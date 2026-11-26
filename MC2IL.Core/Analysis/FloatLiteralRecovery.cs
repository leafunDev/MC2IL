using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    internal static void RecoverArmV7FloatAbsoluteValues(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not ArmV7InstructionSet)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        foreach (var instruction in instructions)
        {
            // Soft-float calls return Single in r0. BIC can clear its sign bit before
            // VMOV copies the bits back to VFP. Copy propagation already erased VMOV.
            if (instruction is not { OpCode: OpCode.And, OperandWidth: 32, Operands:
                    [LocalVariable result, LocalVariable { Type.FullName: "System.Single" } input,
                        Immediate { Value: 0x7FFFFFFF }] })
                continue;

            var readers = instructions.Where(use => DeadCodeEliminator.UsedLocals(use).Contains(result)).ToArray();
            if (readers.Length == 0 || instructions.Count(definition => ReferenceEquals(definition.Destination, result)) != 1
                || readers.Any(use => use.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
                    || use.Operands is not [_, var left, var right, FloatingPointPrecision { IsDouble: false }]
                    || !ReferenceEquals(left, result) && !ReferenceEquals(right, result))
                || NewArmV8InstructionSet.FindFloatMathMethod(method.AppContext, "System.Single", "Abs", 1) is not { } abs)
                continue;

            result.Type = input.Type;
            instruction.OpCode = OpCode.Call;
            instruction.SetOperands(abs, result, input);
            instruction.OperandWidth = null;
            instruction.IsLiftedFloatAbs = true;
        }
    }

    // Optimized constructors copy adjacent 32-bit initializers from a constant
    // pool with an LDR/STR of D or Q. The register does not contain a double.
    internal static void RecoverPackedFieldInitializers(MethodAnalysisContext method)
    {
        if (method.Name != ".ctor" || method.DeclaringType is not { IsValueType: false } owner
            || owner.Definition is not { } definition
            || (definition.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout
            || method.AppContext.Binary is not ElfFile elf || method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        var definitions = graph.Instructions.Where(instruction => instruction.Destination is LocalVariable)
            .ToDictionary(instruction => (LocalVariable)instruction.Destination!);

        foreach (var block in graph.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var store = block.Instructions[i];
            if (store is not { OpCode: OpCode.Move, MemoryAccessSize: 8 or 16,
                    Operands: [MemoryOperand { Base: LocalVariable receiver, Index: null, Scale: 0 } target, LocalVariable word] }
                || !word.Register.Name.StartsWith("V", StringComparison.Ordinal)
                || !definitions.TryGetValue(word, out var load)
                || load is not { OpCode: OpCode.Move,
                    Operands: [_, MemoryOperand { Index: null, Scale: 0 } pool] }
                || load.MemoryAccessSize != store.MemoryAccessSize)
                continue;

            var linkCount = 0;
            var offset = target.Addend;
            while (!receiver.IsThis && linkCount++ < 8 && definitions.TryGetValue(receiver, out var copy))
            {
                if (copy is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
                    receiver = source;
                else if (copy is { OpCode: OpCode.Add, OperandWidth: not 32, Operands: [_, LocalVariable baseLocal, Immediate displacement] })
                {
                    receiver = baseLocal;
                    offset += displacement.Value;
                }
                else
                    break;
            }

            var address = pool.Addend;
            if (pool.Base is LocalVariable page
                && definitions.TryGetValue(page, out var pageLoad)
                && pageLoad is { OpCode: OpCode.Move, IsPcRelativeAddress: true, Operands: [_, Immediate pageAddress] })
                address += pageAddress.Value;
            else if (pool.Base != null)
                continue;

            if (!receiver.IsThis || offset < 0 || address <= 0)
                continue;

            var values = new List<(FieldReference Field, IOperand Value)>();
            for (var lane = 0; lane < store.MemoryAccessSize / 4; lane++)
            {
                var fieldOffset = offset + lane * 4;
                var fields = owner.Fields.Where(field => !field.IsStatic && field.BackingData?.FieldOffset == fieldOffset).Take(2).ToArray();
                if (fields.Length != 1 || !elf.TryReadReadOnlyScalar((ulong)address + (uint)(lane * 4), 4, out var bits))
                    break;
                var field = fields[0];
                IOperand? value = field.FieldType.FullName switch
                {
                    "System.Single" => new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits)), 0)),
                    "System.Int32" or "System.UInt32" => new Immediate(unchecked((int)bits)),
                    _ => null
                };
                if (value == null)
                    break;
                values.Add((new FieldReference(field, receiver, (int)fieldOffset), value));
            }

            if (values.Count * 4 != store.MemoryAccessSize)
                continue;

            store.MemoryAccessSize = 4;
            store.SetOperands(values[0].Field, values[0].Value);
            foreach (var (field, value) in values.Skip(1))
                block.Instructions.Insert(++i, new Instruction(store.Index, OpCode.Move, field, value) { MemoryAccessSize = 4 });
        }
    }

    internal static void RecoverTypedBitCopies(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        var loads = new Dictionary<LocalVariable, (string Type, int Size, NumericConversionKind Reinterpret)>();
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable value, FieldReference field] })
                continue;
            var type = field.Field.FieldType.FullName;
            if (type == "System.Single" && instruction.MemoryAccessSize == 4)
                loads[value] = (type, 4, NumericConversionKind.I32ReinterpretFloat32);
            else if (type == "System.Double" && instruction.MemoryAccessSize == 8)
                loads[value] = (type, 8, NumericConversionKind.I64ReinterpretFloat64);
        }

        if (loads.Count == 0)
            return;

        var consumers = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
        foreach (var local in DeadCodeEliminator.UsedLocals(instruction))
        {
            if (!loads.TryGetValue(local, out var load))
                continue;

            // Keep the native word representation if any consumer actually uses
            // the bits as integers, or accesses the value by address.
            if (!(instruction is { OpCode: OpCode.Convert, Operands: [_, var source, NumericConversion conversion] }
                    && ReferenceEquals(source, local) && conversion.Kind == load.Reinterpret
                || instruction is { OpCode: OpCode.Move, Operands: [FieldReference target, var stored] }
                    && ReferenceEquals(stored, local) && instruction.MemoryAccessSize == load.Size
                    && target.Field.FieldType.FullName == load.Type))
            {
                loads.Remove(local);
                continue;
            }

            if (!consumers.TryGetValue(local, out var uses))
                consumers.Add(local, uses = []);
            uses.Add(instruction);
        }

        foreach (var entry in loads)
        {
            var local = entry.Key;
            var load = entry.Value;
            if (!consumers.TryGetValue(local, out var uses) || !uses.Any(instruction => instruction.OpCode == OpCode.Convert))
                continue;
            local.Type = load.Size == 4 ? method.AppContext.SystemTypes.SystemSingleType : method.AppContext.SystemTypes.SystemDoubleType;
            foreach (var instruction in uses)
            {
                if (instruction.OpCode != OpCode.Convert)
                    continue;
                instruction.OpCode = OpCode.Move;
                instruction.OperandWidth = null;
                instruction.RemoveOperandAt(2);
            }
        }
    }

    internal static void RecoverNativeLoads(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not ElfFile elf || method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            // The lifter retains the native width, even though S and D share the same Vn local.
            if (instruction is not { OpCode: OpCode.Move, MemoryAccessSize: 4 or 8,
                    Operands: [LocalVariable { Register.Name: var register }, MemoryOperand { IsConstant: true } load] }
                || !register.StartsWith("V", StringComparison.Ordinal))
                continue;

            if (load.Addend <= 0
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
