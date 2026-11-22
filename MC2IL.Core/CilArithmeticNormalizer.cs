using System.Collections.Generic;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace MC2IL.Core;

/// <summary>
/// Rewrites negative constants on the right side of addition into subtraction.
/// </summary>
internal static class CilArithmeticNormalizer
{
    public static void Run(CilMethodBody body)
    {
        var instructions = body.Instructions;
        if (instructions.Count < 2)
            return;

        var branchTargets = new HashSet<CilInstruction>(CilBranchCleanup.InstructionIdentity.Instance);
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is CilInstructionLabel { Instruction: { } target })
                branchTargets.Add(target);
            else if (instruction.Operand is IEnumerable<ICilLabel> labels)
                foreach (var label in labels)
                    if (label is CilInstructionLabel { Instruction: { } switchTarget })
                        branchTargets.Add(switchTarget);
        }

        for (var index = 1; index < instructions.Count; index++)
        {
            var add = instructions[index];
            if (add.OpCode != CilOpCodes.Add || branchTargets.Contains(add))
                continue;

            var constant = instructions[index - 1];
            if (constant.OpCode == CilOpCodes.Ldc_R4 && constant.Operand is float single && single < 0f)
            {
                constant.Operand = System.Math.Abs(single);
                add.OpCode = CilOpCodes.Sub;
            }
            else if (constant.OpCode == CilOpCodes.Ldc_R8 && constant.Operand is double number && number < 0d)
            {
                constant.Operand = System.Math.Abs(number);
                add.OpCode = CilOpCodes.Sub;
            }
            else if (TryMakeIntegerConstantPositive(constant))
            {
                add.OpCode = CilOpCodes.Sub;
            }
        }
    }

    private static bool TryMakeIntegerConstantPositive(CilInstruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case CilCode.Ldc_I4_M1:
                instruction.OpCode = CilOpCodes.Ldc_I4_1;
                instruction.Operand = null;
                return true;
            case CilCode.Ldc_I4_S when instruction.Operand is sbyte small && small < 0:
                if (small == sbyte.MinValue)
                {
                    instruction.OpCode = CilOpCodes.Ldc_I4;
                    instruction.Operand = -(int)small;
                }
                else
                {
                    instruction.Operand = (sbyte)-small;
                }
                return true;
            case CilCode.Ldc_I4 when instruction.Operand is int intValue && intValue < 0 && intValue != int.MinValue:
                instruction.Operand = -intValue;
                return true;
            case CilCode.Ldc_I8 when instruction.Operand is long longValue && longValue < 0 && longValue != long.MinValue:
                instruction.Operand = -longValue;
                return true;
            default:
                return false;
        }
    }
}
