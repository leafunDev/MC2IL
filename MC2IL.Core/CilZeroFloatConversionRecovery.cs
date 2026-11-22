using System.Collections.Generic;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace MC2IL.Core;

/// <summary>
/// Replaces unknown locals that only carry an integer zero into float conversions with typed zero literals.
/// </summary>
internal static class CilZeroFloatConversionRecovery
{
    public static void Run(CilMethodBody body)
    {
        if (body.ExceptionHandlers.Count != 0)
            return;

        var instructions = body.Instructions;
        if (instructions.Count < 4)
            return;

        var branchTargets = new HashSet<CilInstruction>(CilBranchCleanup.InstructionIdentity.Instance);
        var accesses = new Dictionary<CilLocalVariable, LocalAccesses>();
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.Operand is CilInstructionLabel { Instruction: { } target })
                branchTargets.Add(target);
            else if (instruction.Operand is IEnumerable<ICilLabel> labels)
                foreach (var label in labels)
                    if (label is CilInstructionLabel { Instruction: { } switchTarget })
                        branchTargets.Add(switchTarget);

            if (instruction.Operand is not CilLocalVariable local)
                continue;

            if (!accesses.TryGetValue(local, out var access))
                accesses.Add(local, access = new LocalAccesses());

            switch (instruction.OpCode.Code)
            {
                case CilCode.Stloc:
                case CilCode.Stloc_S:
                    access.Stores.Add(index);
                    break;
                case CilCode.Ldloc:
                case CilCode.Ldloc_S:
                    access.Loads.Add(index);
                    break;
                default:
                    access.HasOtherAccess = true;
                    break;
            }
        }

        foreach (var entry in accesses)
        {
            var access = entry.Value;
            if (access.HasOtherAccess || access.Stores is not [var storeIndex] || access.Loads.Count == 0
                || storeIndex < 2 || !IsNativeIntegerConversion(instructions[storeIndex - 1])
                || !IsIntegerZero(instructions[storeIndex - 2])
                || branchTargets.Contains(instructions[storeIndex - 1])
                || branchTargets.Contains(instructions[storeIndex]))
                continue;

            var conversions = new List<(int Load, int Conversion)>();
            var safe = true;
            foreach (var loadIndex in access.Loads)
            {
                if (loadIndex + 1 >= instructions.Count
                    || instructions[loadIndex + 1].OpCode.Code is not (CilCode.Conv_R4 or CilCode.Conv_R8)
                    || branchTargets.Contains(instructions[loadIndex + 1]))
                {
                    safe = false;
                    break;
                }
                conversions.Add((loadIndex, loadIndex + 1));
            }

            if (!safe)
                continue;

            foreach (var (loadIndex, conversionIndex) in conversions)
            {
                var load = instructions[loadIndex];
                var conversion = instructions[conversionIndex];
                if (conversion.OpCode == CilOpCodes.Conv_R4)
                {
                    load.OpCode = CilOpCodes.Ldc_R4;
                    load.Operand = 0f;
                }
                else
                {
                    load.OpCode = CilOpCodes.Ldc_R8;
                    load.Operand = 0d;
                }
                conversion.OpCode = CilOpCodes.Nop;
                conversion.Operand = null;
            }

            instructions[storeIndex - 2].OpCode = CilOpCodes.Nop;
            instructions[storeIndex - 2].Operand = null;
            instructions[storeIndex - 1].OpCode = CilOpCodes.Nop;
            instructions[storeIndex - 1].Operand = null;
            instructions[storeIndex].OpCode = CilOpCodes.Nop;
            instructions[storeIndex].Operand = null;
        }
    }

    private static bool IsIntegerZero(CilInstruction instruction) => instruction.OpCode.Code switch
    {
        CilCode.Ldc_I4_0 => true,
        CilCode.Ldc_I4 => instruction.Operand is int intValue && intValue == 0,
        CilCode.Ldc_I4_S => instruction.Operand is sbyte shortValue && shortValue == 0,
        _ => false,
    };

    private static bool IsNativeIntegerConversion(CilInstruction instruction) => instruction.OpCode.Code is
        CilCode.Conv_I or CilCode.Conv_U;

    private sealed class LocalAccesses
    {
        public readonly List<int> Stores = [];
        public readonly List<int> Loads = [];
        public bool HasOtherAccess;
    }
}
