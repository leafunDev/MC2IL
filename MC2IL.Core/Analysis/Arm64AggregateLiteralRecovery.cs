using System;
using System.Linq;
using LibMC2IL.Elf;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class Arm64AggregateLiteralRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        var definitions = graph.Instructions.Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);
        foreach (var block in graph.Blocks)
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (instruction is { OpCode: OpCode.Move, MemoryAccessSize: var loadSize,
                    Operands: [LocalVariable { Type: { } type } value, MemoryOperand { IsConstant: true, Addend: > 0 } load] }
                && Arm64CallingConventionResolver.GetFloatAggregateFields(type) is { } loadedFields
                && loadSize == loadedFields.Length * Width(loadedFields[0].FieldType)
                && method.AppContext.Binary is ElfFile elf)
            {
                var lanes = new IOperand[loadedFields.Length];
                for (var lane = 0; lane < lanes.Length; lane++)
                {
                    var width = Width(loadedFields[lane].FieldType);
                    if (!elf.TryReadReadOnlyScalar((ulong)load.Addend + (ulong)loadedFields[lane].Offset, width, out var bits))
                        break;
                    lanes[lane] = width == sizeof(float)
                        ? new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((uint)bits)), 0))
                        : new DoubleLiteral(BitConverter.Int64BitsToDouble(unchecked((long)bits)));
                }
                if (lanes.Any(lane => lane == null))
                    continue;
                instruction.OpCode = OpCode.PackFloatAggregate;
                instruction.SetOperands([value, type, .. lanes]);
                instruction.MemoryAccessSize = null;
            }
            else if (instruction is { OpCode: OpCode.Move, MemoryAccessSize: var storeSize,
                         Operands: [FieldReference target, LocalVariable source] }
                && Arm64CallingConventionResolver.GetFloatAggregateFields(target.Field.FieldType) is { } fields
                && storeSize == fields.Length * Width(fields[0].FieldType)
                && definitions[source].Count() == 1
                && SplatValue(definitions[source].Single(), source, fields.Length, fields[0].FieldType) is { } repeated)
            {
                var aggregate = new LocalVariable($"aggregateLiteral{method.Locals.Count}",
                    new Register(null, $"TEMP_AGGREGATE_LITERAL{method.Locals.Count}"), target.Field.FieldType);
                method.Locals.Add(aggregate);
                var pack = new Instruction(-1, OpCode.PackFloatAggregate, [aggregate, target.Field.FieldType,
                    .. Enumerable.Repeat(repeated, fields.Length)]);
                block.Instructions.Insert(index++, pack);
                instruction.SetOperand(1, aggregate);
                instruction.MemoryAccessSize = null;
            }
        }
    }

    private static IOperand? SplatValue(Instruction definition, LocalVariable source, int count, TypeAnalysisContext elementType)
    {
        if (definition.VectorElementCount != count)
            return null;
        if (definition is { OpCode: OpCode.Move, Operands: [_, var literal] }
            && (Width(elementType) == sizeof(float) ? literal is FloatLiteral : literal is DoubleLiteral))
            return literal;
        if (definition is { OpCode: OpCode.Convert, Operands: [_, _, NumericConversion { Kind: var conversion }] }
            && (Width(elementType) == sizeof(float) ? conversion == NumericConversionKind.I32ReinterpretFloat32
                : conversion == NumericConversionKind.I64ReinterpretFloat64))
        {
            source.Type = elementType;
            return source;
        }
        return null;
    }

    private static int Width(TypeAnalysisContext type) => type.FullName == "System.Double" ? sizeof(double) : sizeof(float);
}
