using System;
using System.Collections.Generic;
using System.Linq;
using LibMC2IL.Elf;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Recovers float and double literals from typed uses after constant propagation, preserving common
/// IEEE-754 bit patterns and numerically converting ordinary integer immediates.
/// </summary>
public static class FloatLiteralRecovery
{
    internal static void RecoverNativeLoads(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not ElfFile elf || method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        var addresses = new Dictionary<LocalVariable, long>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Move, IsPcRelativeAddress: true,
                    Operands: [LocalVariable destination, Immediate address] })
                addresses.Add(destination, address.Value);

        foreach (var instruction in instructions)
        {
            // The lifter retains the native width, even though S and D share the same Vn local.
            if (instruction is not { OpCode: OpCode.Move, MemoryAccessSize: 4 or 8,
                    Operands: [LocalVariable { Register.Name: var register }, MemoryOperand { Index: null, Scale: 0 } load] }
                || !register.StartsWith("V", StringComparison.Ordinal))
                continue;

            if (load.Base is LocalVariable baseLocal && addresses.TryGetValue(baseLocal, out var baseAddress))
            {
                load = new MemoryOperand(addend: unchecked(baseAddress + load.Addend));
                instruction.SetOperand(1, load);
            }
            if (!load.IsConstant || load.Addend <= 0
                || !elf.TryReadReadOnlyScalar((ulong)load.Addend, instruction.MemoryAccessSize.Value, out var bits))
                continue;

            instruction.SetOperand(1, instruction.MemoryAccessSize == 4
                ? new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits)), 0))
                : new DoubleLiteral(BitConverter.Int64BitsToDouble(unchecked((long)bits))));
            instruction.MemoryAccessSize = null;
        }
    }

    public static void Run(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction is { OpCode: OpCode.Convert, Operands: [var destination, Immediate value, NumericConversion
                    { Saturating: false, FixedPointScale: 0, Kind: var kind }] }
                && FoldIntegerToFloat(value, kind) is { } literal)
            {
                instruction.OpCode = OpCode.Move;
                instruction.OperandWidth = null;
                instruction.SetOperands(destination, literal);
                continue;
            }

            if (instruction.OpCode == OpCode.Move)
            {
                if (instruction.Operands is [FieldReference field, _])
                    RecoverFieldStore(instruction, field);
                else if (instruction.Operands is [LocalVariable { Type: { } localType }, _])
                    TryConvert(instruction, 1, localType);
            }
            else if (instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Modulo
                     && instruction.Destination is LocalVariable { Type: { } resultType }
                     && resultType.FullName is "System.Single" or "System.Double")
            {
                // Float constants can be propagated out of their defining Move before this late
                // recovery pass runs. Recover those bit patterns at the arithmetic use site too.
                // Ambiguous subnormal/non-finite patterns fall back to numeric conversion; this
                // lets `(float)100` become `100f` without turning it into a subnormal value.
                TryConvertArithmeticConstant(instruction, 1, resultType);
                TryConvertArithmeticConstant(instruction, 2, resultType);
            }
            else if (instruction.IsCall && instruction.Operands is [MethodAnalysisContext target, ..])
                ConvertArguments(instruction, target);
        }
    }

    private static void RecoverFieldStore(Instruction store, FieldReference field)
    {
        var fieldType = field.Field.FieldType;
        if (store.Operands[1] is Immediate bits)
        {
            // A width-matched native store already contains the floating-point bit
            // pattern. Preserve even subnormals, infinities and signed zero.
            switch (store.MemoryAccessSize, fieldType.FullName)
            {
                case (4, "System.Single"):
                    store.SetOperand(1, new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits.Value)), 0)));
                    return;
                case (8, "System.Double"):
                    store.SetOperand(1, new DoubleLiteral(BitConverter.Int64BitsToDouble(bits.Value)));
                    return;
            }
        }

        TryConvert(store, 1, fieldType);
    }

    private static void TryConvertArithmeticConstant(Instruction instruction, int operandIndex, TypeAnalysisContext resultType)
    {
        TryConvert(instruction, operandIndex, resultType);
    }

    private static void ConvertArguments(Instruction call, MethodAnalysisContext target)
    {
        var firstArgument = (call.OpCode == OpCode.Call ? 2 : 1) + (target.IsStatic ? 0 : 1);

        for (var i = 0; i < target.Parameters.Count; i++)
        {
            var index = firstArgument + i;

            if (index >= call.Operands.Count)
                break;

            TryConvert(call, index, target.Parameters[i].ParameterType);
        }
    }

    private static void TryConvert(Instruction instruction, int operandIndex, TypeAnalysisContext type)
    {
        if (instruction.Operands[operandIndex] is not Immediate immediate)
            return;

        // TODO FIXME: We have to compare by name, not reference, because a field on a generic type resolves
        // TODO FIXME: to its own Single/Double context instance rather than the canonical one in SystemTypes.
        switch (type.FullName)
        {
            case "System.Single":
                instruction.SetOperand(operandIndex, ToSingleLiteral(immediate));
                break;
            case "System.Double":
                instruction.SetOperand(operandIndex, ToDoubleLiteral(immediate));
                break;
        }
    }

    // A numeric conversion carries signedness and width. Interpret the source as an integer,
    // even when its bits happen to encode a plausible floating-point value.
    private static IOperand? FoldIntegerToFloat(Immediate value, NumericConversionKind kind) => kind switch
    {
        NumericConversionKind.SignedInt32ToFloat32 => new FloatLiteral(unchecked((int)value.Value)),
        NumericConversionKind.UnsignedInt32ToFloat32 => new FloatLiteral(unchecked((uint)value.Value)),
        NumericConversionKind.SignedInt64ToFloat32 => new FloatLiteral(value.Value),
        NumericConversionKind.UnsignedInt64ToFloat32 => new FloatLiteral(value.UnsignedValue),
        NumericConversionKind.SignedInt32ToFloat64 => new DoubleLiteral(unchecked((int)value.Value)),
        NumericConversionKind.UnsignedInt32ToFloat64 => new DoubleLiteral(unchecked((uint)value.Value)),
        NumericConversionKind.SignedInt64ToFloat64 => new DoubleLiteral(value.Value),
        NumericConversionKind.UnsignedInt64ToFloat64 => new DoubleLiteral(value.UnsignedValue),
        _ => null,
    };

    internal static FloatLiteral ToSingleLiteral(Immediate immediate)
    {
        var bits = (uint)immediate.UnsignedValue;
        // Float values loaded through integer registers arrive as their IEEE-754 bit pattern. Plain
        // integer constants such as 100 arrive with bits that are subnormal as a float; in a typed
        // float context those should be numeric conversions, so emit 100f instead of a tiny value.
        // An Immediate alone cannot distinguish a real subnormal payload from such an integer.
        var is32BitPayload = immediate.Value is >= int.MinValue and <= uint.MaxValue;
        var value = is32BitPayload && IsRawSingleLiteral(bits)
            ? BitConverter.ToSingle(BitConverter.GetBytes(bits), 0)
            : (float)immediate.Value;
        return new FloatLiteral(value);
    }

    internal static DoubleLiteral ToDoubleLiteral(Immediate immediate)
    {
        var bits = immediate.UnsignedValue;
        // As above, preserve ordinary finite double bit patterns. Subnormal and non-finite bit
        // patterns are ambiguous without the original conversion opcode, so use numeric conversion.
        var value = IsRawDoubleLiteral(bits)
            ? BitConverter.ToDouble(BitConverter.GetBytes(bits), 0)
            : (double)immediate.Value;
        return new DoubleLiteral(value);
    }

    private static bool IsRawSingleLiteral(uint bits)
    {
        var exponent = bits & 0x7F800000u;
        var magnitude = bits & 0x7FFFFFFFu;
        return magnitude == 0 || exponent != 0 && exponent != 0x7F800000u;
    }

    private static bool IsRawDoubleLiteral(ulong bits)
    {
        var exponent = bits & 0x7FF0000000000000UL;
        var magnitude = bits & 0x7FFFFFFFFFFFFFFFUL;
        return magnitude == 0 || exponent != 0 && exponent != 0x7FF0000000000000UL;
    }
}
