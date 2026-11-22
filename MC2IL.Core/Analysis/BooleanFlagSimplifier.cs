using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Fixes the compare-a-flag-against-zero pairs the we get for every conditional: <c>bool != 0</c>
/// is the bool itself, and <c>bool == 0</c> is its negation.
/// </summary>
public static class BooleanFlagSimplifier
{
    public static void Run(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        FoldTypedBitwiseOperations(method);

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
                continue;

            var value = IsZeroConstant(instruction.Operands[2])
                ? instruction.Operands[1]
                : IsZeroConstant(instruction.Operands[1])
                    ? instruction.Operands[2]
                    : null;

            if (value is not LocalVariable local
                || !TryGetBooleanBit(local, definitions, booleanType, out var booleanValue, out var inverted))
                continue;

            // Native code often materializes a bool as (condition ? 1 : 0), flips it with
            // XOR 1, then compares the integer with zero. Collapse the whole proven 0/1
            // expression back to a bool copy or logical negation before emitting CIL.
            inverted ^= instruction.OpCode == OpCode.CheckEqual;
            instruction.OpCode = inverted ? OpCode.Not : OpCode.Move;
            instruction.SetOperands(instruction.Operands[0], booleanValue);
        }
    }

    // This can also run after SSA removal: it relies only on the operation's resolved bool type
    // and immediate mask, not on which definition reaches a particular use.
    internal static void FoldTypedBitwiseOperations(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            TryFoldBooleanBitwiseIdentity(instruction, booleanType);
    }

    private static bool TryFoldBooleanBitwiseIdentity(Instruction instruction, TypeAnalysisContext booleanType)
    {
        if (instruction.Operands is not [LocalVariable { Type: { } resultType } destination, var left, var right]
            || resultType != booleanType)
            return false;

        if (instruction.OpCode == OpCode.And)
        {
            if (IsZeroConstant(left) || IsZeroConstant(right))
                return ToBooleanConstant(instruction, destination, 0);
            if (IsBoolean(left, booleanType) && IsOne(right))
                return ToBooleanMove(instruction, destination, left);
            if (IsBoolean(right, booleanType) && IsOne(left))
                return ToBooleanMove(instruction, destination, right);
        }
        else if (instruction.OpCode == OpCode.Or)
        {
            if (IsBoolean(left, booleanType) && IsZeroConstant(right))
                return ToBooleanMove(instruction, destination, left);
            if (IsBoolean(right, booleanType) && IsZeroConstant(left))
                return ToBooleanMove(instruction, destination, right);
            if (IsBoolean(left, booleanType) && IsOne(right)
                || IsBoolean(right, booleanType) && IsOne(left))
                return ToBooleanConstant(instruction, destination, 1);
        }
        else if (instruction.OpCode == OpCode.Xor)
        {
            if (IsBoolean(left, booleanType) && IsZeroConstant(right))
                return ToBooleanMove(instruction, destination, left);
            if (IsBoolean(right, booleanType) && IsZeroConstant(left))
                return ToBooleanMove(instruction, destination, right);
            if (IsBoolean(left, booleanType) && IsOne(right))
                return ToBooleanNot(instruction, destination, left);
            if (IsBoolean(right, booleanType) && IsOne(left))
                return ToBooleanNot(instruction, destination, right);
        }

        return false;
    }

    private static bool IsBoolean(IOperand operand, TypeAnalysisContext booleanType)
        => operand is LocalVariable { Type: { } type } && type == booleanType;

    private static bool ToBooleanMove(Instruction instruction, IOperand destination, IOperand value)
    {
        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(destination, value);
        return true;
    }

    private static bool ToBooleanNot(Instruction instruction, IOperand destination, IOperand value)
    {
        instruction.OpCode = OpCode.Not;
        instruction.SetOperands(destination, value);
        return true;
    }

    private static bool ToBooleanConstant(Instruction instruction, IOperand destination, long value)
    {
        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(destination, new Immediate(value));
        return true;
    }

    private static bool TryGetBooleanBit(LocalVariable value,
        Dictionary<LocalVariable, Instruction> definitions, TypeAnalysisContext booleanType,
        out LocalVariable booleanValue, out bool inverted, int depth = 0)
    {
        booleanValue = null!;
        inverted = false;
        if (depth >= 16)
            return false;

        if (!definitions.TryGetValue(value, out var definition))
        {
            if (value.Type != booleanType)
                return false;

            booleanValue = value;
            return true;
        }

        switch (definition)
        {
            case { OpCode: OpCode.Move, Operands: [_, LocalVariable source] }:
                return TryGetBooleanBit(source, definitions, booleanType, out booleanValue, out inverted, depth + 1);

            case { OpCode: OpCode.Not, Operands: [_, LocalVariable source] }:
                if (!TryGetBooleanBit(source, definitions, booleanType, out booleanValue, out inverted, depth + 1))
                    return false;
                inverted = !inverted;
                return true;

            case { OpCode: OpCode.Xor, Operands: [_, var left, var right] }
                when IsOne(right) && left is LocalVariable leftLocal:
                if (!TryGetBooleanBit(leftLocal, definitions, booleanType, out booleanValue, out inverted, depth + 1))
                    return false;
                inverted = !inverted;
                return true;

            case { OpCode: OpCode.Xor, Operands: [_, var left, var right] }
                when IsOne(left) && right is LocalVariable rightLocal:
                if (!TryGetBooleanBit(rightLocal, definitions, booleanType, out booleanValue, out inverted, depth + 1))
                    return false;
                inverted = !inverted;
                return true;

            case { OpCode: OpCode.And, Operands: [_, var left, var right] }
                when IsOne(right) && left is LocalVariable leftLocal:
                return TryGetBooleanBit(leftLocal, definitions, booleanType, out booleanValue, out inverted, depth + 1);

            case { OpCode: OpCode.And, Operands: [_, var left, var right] }
                when IsOne(left) && right is LocalVariable rightLocal:
                return TryGetBooleanBit(rightLocal, definitions, booleanType, out booleanValue, out inverted, depth + 1);

            default:
                if (value.Type != booleanType)
                    return false;

                booleanValue = value;
                return true;
        }
    }

    private static bool IsOne(IOperand operand) => operand is Immediate { Value: 1 };

    // After SSA propagation a boolean return can have the form (phi(flag, 1) & 1).
    // The untyped merge would otherwise become an object local when the phi is lowered.
    internal static void TypeMaskedReturnMerges(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        if (method.ReturnType != booleanType)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        var returned = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Return, Operands: [LocalVariable result] })
                returned.Add(result);

        if (returned.Count == 0)
            return;

        Dictionary<LocalVariable, Instruction>? candidates = null;
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.And, Operands: [LocalVariable result, var left, var right] }
                || !returned.Contains(result))
                continue;

            if ((right is Immediate { Value: 1 } ? left : left is Immediate { Value: 1 } ? right : null)
                is LocalVariable { Type: null } merged)
                (candidates ??= [])[merged] = instruction;
        }

        if (candidates == null)
            return;

        var phis = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Phi, Destination: LocalVariable { Type: null } destination })
                phis[destination] = instruction;

        if (phis.Count == 0)
            return;

        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
            foreach (var input in DeadCodeEliminator.UsedLocals(instruction))
                if (phis.ContainsKey(input))
                {
                    if (!uses.TryGetValue(input, out var readers))
                        uses[input] = readers = [];
                    readers.Add(instruction);
                }

        foreach (var candidate in candidates)
        {
            var proven = new HashSet<LocalVariable>();
            var visiting = new HashSet<LocalVariable>();
            if (!IsBit(candidate.Key, 0))
                continue;

            // Only retype merges whose uses stay inside this Boolean expression. A merge
            // also used as a wider integer, memory address or ref argument keeps its type.
            if (proven.Any(local => uses[local].Any(reader => reader != candidate.Value
                    && !(reader.OpCode == OpCode.Phi && reader.Destination is LocalVariable result && proven.Contains(result)))))
                continue;

            foreach (var local in proven)
                local.Type = booleanType;

            bool IsBit(IOperand input, int depth)
            {
                if (input is Immediate { Value: 0 or 1 } || input is LocalVariable typed && typed.Type == booleanType)
                    return true;

                if (input is not LocalVariable { Type: null } local)
                    return false;
                if (proven.Contains(local))
                    return true;
                if (depth >= 64 || !phis.TryGetValue(local, out var phi) || phi.Operands.Count < 2 || !visiting.Add(local))
                    return false;

                for (var i = 1; i < phi.Operands.Count; i++)
                    if (!IsBit(phi.Operands[i], depth + 1))
                        return false;

                visiting.Remove(local);
                proven.Add(local);
                return true;
            }
        }
    }

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
}
