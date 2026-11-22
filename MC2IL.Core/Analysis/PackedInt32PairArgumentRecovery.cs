using System;
using System.Linq;
using System.Reflection;
using Disarm;
using Disarm.InternalDisassembly;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// A two-int value type is passed in one ARM64 X register. W-register reads see the
// low field, while an LSR by 32 extracts the high field. Keep those reads typed
// instead of treating the entire struct local as an integer operand.
internal static class PackedInt32PairArgumentRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet
            || !method.AppContext.Binary.IsLittleEndian
            || method.ParameterLocals.Count == 0)
            return;

        var candidates = method.ParameterLocals
            .Where(local => !local.IsThis && !local.IsMethodInfo && local.Register.Version == -1
                && local.Register.Name != null && TryGetInt32Pair(GetParameterType(method, local), out _)
                && TryGetXRegister(local.Register.Name, out _))
            .Select(local => (Local: local, Register: local.Register.Name!, Fields: GetInt32Pair(GetParameterType(method, local))!))
            .ToArray();
        if (candidates.Length == 0)
            return;
        Logger.VerboseNewline($"{method.FullName}: found {candidates.Length} packed Int32-pair parameters.", "PackedInt32PairArgumentRecovery");

        Arm64Instruction[] native;
        try
        {
            native = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(method.AppContext, method.UnderlyingPointer).ToArray();
        }
        catch
        {
            return;
        }

        var byAddress = native.ToDictionary(instruction => instruction.Address);
        var recoveredLow = 0;
        var recoveredHigh = 0;
        foreach (var candidate in candidates)
        {
            if (!TryGetXRegister(candidate.Register, out var argumentRegister))
                continue;

            var lowField = new FieldReference(candidate.Fields[0], candidate.Local, candidate.Fields[0].Offset);
            var highField = new FieldReference(candidate.Fields[1], candidate.Local, candidate.Fields[1].Offset);

            foreach (var instruction in method.ControlFlowGraph!.Instructions)
            {
                if (instruction.NativeAddress is not { } address || !byAddress.TryGetValue(address, out var machine))
                    continue;

                // LSR Xd, Xn, #32 is the ABI-level projection of the second Int32 field.
                if (machine.Mnemonic == Arm64Mnemonic.LSR
                    && machine.Op1Kind == Arm64OperandKind.Register && machine.Op1Reg == argumentRegister
                    && machine.Op2Kind == Arm64OperandKind.Immediate && machine.Op2Imm == 32
                    && instruction is { OpCode: OpCode.ShiftRightUnsigned,
                        Operands: [LocalVariable destination, LocalVariable source, Immediate { Value: 32 }] }
                    && ReferenceEquals(source, candidate.Local))
                {
                    instruction.OpCode = OpCode.Move;
                    instruction.SetOperands(destination, highField);
                    recoveredHigh++;
                    continue;
                }

                // Wn reads are the low 32-bit field. Operand zero is a source only
                // for comparisons/tests and stores; other supported forms read 1..3.
                if (!ReadsWordRegister(machine, argumentRegister))
                    continue;

                for (var operandIndex = 1; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (ReferenceEquals(instruction.Operands[operandIndex], candidate.Local))
                    {
                        instruction.SetOperand(operandIndex, lowField);
                        recoveredLow++;
                    }
                }
            }
        }

        Logger.VerboseNewline($"{method.FullName}: projected {recoveredLow} low-field reads and {recoveredHigh} high-field reads.", "PackedInt32PairArgumentRecovery");
    }

    private static bool ReadsWordRegister(Arm64Instruction instruction, Arm64Register xRegister)
    {
        var wRegister = (Arm64Register)((int)Arm64Register.W0 + (int)xRegister - (int)Arm64Register.X0);
        var firstSource = instruction.Mnemonic is Arm64Mnemonic.CMP or Arm64Mnemonic.CMN or Arm64Mnemonic.TST
            ? 0
            : instruction.Mnemonic is Arm64Mnemonic.STR or Arm64Mnemonic.STUR or Arm64Mnemonic.STP
                ? 0
                : 1;

        return IsSourceRegister(instruction, firstSource, wRegister)
            || IsSourceRegister(instruction, firstSource + 1, wRegister)
            || firstSource == 1 && IsSourceRegister(instruction, 3, wRegister);
    }

    private static bool IsSourceRegister(Arm64Instruction instruction, int operand, Arm64Register register) => operand switch
    {
        0 => instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg == register,
        1 => instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg == register,
        2 => instruction.Op2Kind == Arm64OperandKind.Register && instruction.Op2Reg == register,
        3 => instruction.Op3Kind == Arm64OperandKind.Register && instruction.Op3Reg == register,
        _ => false
    };

    private static bool TryGetXRegister(string name, out Arm64Register register)
    {
        register = default;
        if (name.Length < 2 || name[0] != 'X' || !int.TryParse(name.Substring(1), out var index) || index is < 0 or > 30)
            return false;

        register = (Arm64Register)((int)Arm64Register.X0 + index);
        return true;
    }

    private static FieldAnalysisContext[]? GetInt32Pair(TypeAnalysisContext? type)
    {
        if (type == null || !type.IsValueType || type.IsEnumType || type.Definition == null
            || (type.Attributes & TypeAttributes.LayoutMask) is not (TypeAttributes.SequentialLayout or TypeAttributes.ExplicitLayout)
            || TypeSizes.UnboxedSize(type, type.AppContext.Binary.PointerSizeBytes) != 8)
            return null;

        var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
        if (fields.Length != 2 || fields.Any(field => field.FieldType.FullName != "System.Int32"
                || field.Offset is not (0 or 4)
                || (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
            || fields.Select(field => field.Offset).Distinct().Count() != 2)
            return null;

        return fields.OrderBy(field => field.Offset).ToArray();
    }

    private static bool TryGetInt32Pair(TypeAnalysisContext? type, out FieldAnalysisContext[]? fields)
    {
        fields = GetInt32Pair(type);
        return fields != null;
    }

    private static TypeAnalysisContext? GetParameterType(MethodAnalysisContext method, LocalVariable local)
    {
        var operandOffset = method.IsStatic ? 0 : 1;
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Count; parameterIndex++)
        {
            var operandIndex = parameterIndex + operandOffset;
            if (operandIndex >= method.ParameterOperands.Count || method.ParameterOperands[operandIndex] is not Register register)
                continue;
            if (register.Number == local.Register.Number && local.Register.Version == -1)
                return method.Parameters[parameterIndex].ParameterType;
        }

        return local.Type;
    }
}
