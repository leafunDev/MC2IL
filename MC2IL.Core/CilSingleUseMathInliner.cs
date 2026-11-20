using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.ISIL;

namespace MC2IL.Core;

internal static class CilSingleUseMathInliner
{
    private sealed class Slot
    {
        public int Store = -1;
        public int Load = -1;
        public bool MultipleAccesses;
        public CilOpCode StorageConversion;
    }

    public static void Run(CilMethodBody body, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = body.Instructions;
        if (body.ExceptionHandlers.Count != 0 || instructions.Count == 0)
            return;

        var slots = new Dictionary<CilLocalVariable, Slot>();
        foreach (var pair in locals)
            if (pair.Key.Type?.FullName is "System.Single" or "System.Double")
                slots.Add(pair.Value, new Slot { StorageConversion = pair.Key.Type.FullName == "System.Single" ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8 });
        if (slots.Count == 0)
            return;

        var position = new Dictionary<CilInstruction, int>(CilBranchCleanup.InstructionIdentity.Instance);
        for (var i = 0; i < instructions.Count; i++)
            position.Add(instructions[i], i);

        var starts = new bool[instructions.Count];
        starts[0] = true;
        var addressedArguments = new HashSet<object>();
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.Operand is ICilLabel label)
            {
                if (label is not CilInstructionLabel { Instruction: { } target }
                    || !position.TryGetValue(target, out var index))
                    return;
                starts[index] = true;
            }
            else if (instruction.Operand is IEnumerable<ICilLabel>)
            {
                return;
            }

            if (instruction.OpCode.FlowControl is not (CilFlowControl.Next or CilFlowControl.Call)
                && i + 1 < instructions.Count)
                starts[i + 1] = true;

            if (instruction.OpCode.Code is CilCode.Ldarga or CilCode.Ldarga_S or CilCode.Starg or CilCode.Starg_S
                && instruction.Operand is { } argument)
                addressedArguments.Add(argument);

            if (instruction.Operand is not CilLocalVariable local || !slots.TryGetValue(local, out var slot))
                continue;
            switch (instruction.OpCode.Code)
            {
                case CilCode.Stloc when slot.Store == -1:
                    slot.Store = i;
                    break;
                case CilCode.Ldloc when slot.Load == -1:
                    slot.Load = i;
                    break;
                default:
                    slot.MultipleAccesses = true;
                    break;
            }
        }

        var block = 0;
        var enclosingBlock = new int[instructions.Count];
        for (var i = 0; i < starts.Length; i++)
        {
            if (starts[i])
                block = i;
            enclosingBlock[i] = block;
        }

        var suppressed = new bool[instructions.Count];
        var expansions = new Dictionary<int, List<CilInstruction>>();
        var candidates = slots.Values.Where(slot => !slot.MultipleAccesses && slot.Store >= 0 && slot.Load > slot.Store)
            .OrderBy(slot => slot.Store);
        foreach (var slot in candidates)
        {
            var first = enclosingBlock[slot.Store];
            // Do not move even pure arithmetic through branches, calls or writes to
            // escaping storage. Both sides must be in the same straight-line region.
            if (first != enclosingBlock[slot.Load] || slot.Load - slot.Store > 128)
                continue;

            var scratchStores = new HashSet<CilLocalVariable>();
            var safe = true;
            for (var i = slot.Store + 1; i < slot.Load; i++)
            {
                if (suppressed[i])
                    continue;
                var instruction = instructions[i];
                if (instruction.OpCode == CilOpCodes.Stloc && instruction.Operand is CilLocalVariable stored)
                    scratchStores.Add(stored);
                // Producer loads are checked against those stores below. Calls and indirect
                // writes are excluded because they can modify an input through an alias.
                else if (!IsNonWriting(instruction.OpCode.Code))
                {
                    safe = false;
                    break;
                }
            }
            if (!safe)
                continue;

            var depth = 1;
            var expression = new List<CilInstruction>();
            var consumed = new List<int>();
            for (var i = slot.Store - 1; i >= first && slot.Store - i <= 128; i--)
            {
                if (suppressed[i])
                    continue;
                var original = instructions[i];
                if (original.OpCode == CilOpCodes.Nop)
                    continue;

                var operations = expansions.TryGetValue(i, out var nested) ? nested : null;
                if (operations == null)
                {
                    if (!Consume(original))
                        break;
                }
                else
                {
                    for (var j = operations.Count - 1; j >= 0; j--)
                        if (!Consume(operations[j]))
                        {
                            safe = false;
                            break;
                        }
                    if (!safe)
                        break;
                }
                consumed.Add(i);
                if (depth != 0)
                    continue;

                if (!IsFloatingExpression(expression))
                    break;

                expression.Insert(0, new CilInstruction(slot.StorageConversion));
                expression.Reverse();
                foreach (var index in consumed)
                    suppressed[index] = true;
                suppressed[slot.Store] = true;
                expansions.Add(slot.Load, expression);
                break;
            }

            bool Consume(CilInstruction instruction)
            {
                if (expression.Count >= 64)
                    return false;
                var code = instruction.OpCode.Code;
                if (code == CilCode.Ldloc && instruction.Operand is CilLocalVariable local && scratchStores.Contains(local)
                    || code == CilCode.Ldarg && instruction.Operand is { } argument && addressedArguments.Contains(argument))
                    return false;
                if (code == CilCode.Ldloca
                    && (instruction.Operand is not CilLocalVariable addressed || !addressed.VariableType.IsValueType
                        || scratchStores.Contains(addressed)))
                    return false;
                var inputs = ScalarInputs(code);
                if (inputs < 0)
                    return false;
                depth += inputs - 1;
                expression.Add(new CilInstruction(instruction.OpCode, instruction.Operand));
                return depth >= 0;
            }
        }

        if (expansions.Count == 0)
            return;

        var result = new List<CilInstruction>(instructions.Count);
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (suppressed[i])
            {
                if (starts[i])
                {
                    // Keep a label in its original block even when the expression moves.
                    instruction.OpCode = CilOpCodes.Nop;
                    instruction.Operand = null;
                    result.Add(instruction);
                }
                continue;
            }
            if (!expansions.TryGetValue(i, out var expression))
            {
                result.Add(instruction);
                continue;
            }

            // Preserve the use instruction itself (labels may refer to its identity).
            var copies = expression;
            instruction.OpCode = copies[0].OpCode;
            instruction.Operand = copies[0].Operand;
            result.Add(instruction);
            result.AddRange(copies.Skip(1));
        }

        instructions.Clear();
        instructions.AddRange(result);
    }

    private static bool IsFloatingExpression(List<CilInstruction> reversed)
    {
        var types = new Stack<ValueKind>();
        for (var i = reversed.Count - 1; i >= 0; i--)
        {
            var instruction = reversed[i];
            var code = instruction.OpCode.Code;
            switch (ScalarInputs(code))
            {
                case 0:
                    types.Push(code == CilCode.Ldloca ? ValueKind.StructAddress
                        : code is CilCode.Ldc_R4 or CilCode.Ldc_R8
                          || instruction.Operand is CilLocalVariable { VariableType.ElementType: ElementType.R4 or ElementType.R8 }
                          || instruction.Operand is Parameter { ParameterType.ElementType: ElementType.R4 or ElementType.R8 }
                            ? ValueKind.Float : ValueKind.Other);
                    break;
                case 1 when types.Count >= 1:
                    var value = types.Pop();
                    if (code == CilCode.Ldfld)
                    {
                        if (value != ValueKind.StructAddress || instruction.Operand is not IFieldDescriptor
                            { Signature.FieldType.ElementType: ElementType.R4 or ElementType.R8 })
                            return false;
                        types.Push(ValueKind.Float);
                    }
                    else
                    {
                        if (value == ValueKind.StructAddress)
                            return false;
                        types.Push(code switch
                        {
                            CilCode.Neg => value,
                            CilCode.Conv_I4 or CilCode.Conv_U4 => ValueKind.Other,
                            _ => ValueKind.Float
                        });
                    }
                    break;
                case 2 when types.Count >= 2:
                    var right = types.Pop();
                    var left = types.Pop();
                    if (left != ValueKind.Float || right != ValueKind.Float)
                        return false;
                    types.Push(ValueKind.Float);
                    break;
                default:
                    return false;
            }
        }
        return types.Count == 1 && types.Peek() == ValueKind.Float;
    }

    private enum ValueKind { Other, Float, StructAddress }

    private static bool IsNonWriting(CilCode code) => ScalarInputs(code) >= 0
        || code is CilCode.Nop or CilCode.Ldfld or CilCode.Ldloca or CilCode.Ldarg_0 or CilCode.Ldarg_1
            or CilCode.Ldarg_2 or CilCode.Ldarg_3 or CilCode.Ldarga or CilCode.Ldnull
            or CilCode.Ceq or CilCode.Cgt or CilCode.Cgt_Un or CilCode.Clt or CilCode.Clt_Un;

    private static int ScalarInputs(CilCode code) => code switch
    {
        CilCode.Ldloc or CilCode.Ldloca or CilCode.Ldarg or CilCode.Ldc_R4 or CilCode.Ldc_R8
            or CilCode.Ldc_I4 or CilCode.Ldc_I4_M1 or CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1
            or CilCode.Ldc_I4_2 or CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4 or CilCode.Ldc_I4_5
            or CilCode.Ldc_I4_6 or CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8 or CilCode.Ldc_I8 => 0,
        CilCode.Conv_I4 or CilCode.Conv_U4 or CilCode.Conv_R4 or CilCode.Conv_R8 or CilCode.Conv_R_Un or CilCode.Neg or CilCode.Ldfld => 1,
        CilCode.Add or CilCode.Sub or CilCode.Mul or CilCode.Div or CilCode.Rem => 2,
        _ => -1
    };
}
