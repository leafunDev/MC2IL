using System;
using System.Collections.Generic;
using System.Reflection;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Recovers zero stores spanning several floating point fields. A native STR or STP
/// may write through an aggregate's first field, or even across two adjacent aggregates.
/// </summary>
internal static class Arm64ZeroStoreRecovery
{
    private readonly record struct Lane(FieldAnalysisContext Field, int Offset, int Size,
        FieldAnalysisContext? Aggregate, int AggregateSize);

    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        Dictionary<TypeAnalysisContext, Dictionary<int, Lane>?>? layouts = null;
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            var instructions = block.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is { OpCode: OpCode.Move, MemoryAccessSize: > 0,
                        Operands: [FieldReference { Parent: null, Field.IsStatic: true } staticField, var constant] } staticStore
                    && IsZero(constant)
                    && staticField.Offset == staticField.Field.Offset
                    && Arm64CallingConventionResolver.GetFloatAggregateFields(staticField.Field.FieldType) is { } members
                    && staticStore.MemoryAccessSize == members.Length
                        * (members[0].FieldType.FullName == "System.Single" ? sizeof(float) : sizeof(double)))
                {
                    staticStore.SetOperand(1, new DefaultValue(staticField.Field.FieldType));
                    staticStore.MemoryAccessSize = null;
                    staticStore.OperandWidth = null;
                    continue;
                }

                if (!TryGetWrite(instructions[i], out var target, out var size))
                    continue;

                var owner = target.Local.Type is ByRefTypeAnalysisContext byRef ? byRef.ElementType : target.Local.Type;
                if (owner == null)
                    continue;
                layouts ??= [];
                if (!layouts.TryGetValue(owner, out var layout))
                    layouts.Add(owner, layout = BuildLayout(owner));
                if (layout == null || !CoversAggregate(layout, target.Offset, size))
                    continue;

                var covered = new HashSet<int>();
                var ordered = new List<Lane>();
                var end = i;
                // Only combine consecutive writes on the same receiver. Calls, different
                // receivers, and even a nonzero write on this receiver stop the scan.
                for (; end < instructions.Count; end++)
                {
                    if (instructions[end].OpCode == OpCode.Nop)
                        continue;
                    if (!TryGetWrite(instructions[end], out var next, out var nextSize)
                        || !ReferenceEquals(next.Local, target.Local)
                        || !CoversAggregate(layout, next.Offset, nextSize))
                        break;

                    for (var offset = next.Offset; offset < next.Offset + nextSize; offset += layout[offset].Size)
                        if (covered.Add(offset))
                            ordered.Add(layout[offset]);
                }

                var writes = new List<(FieldReference Target, IOperand Value)>();
                var emitted = new HashSet<FieldAnalysisContext>();
                foreach (var lane in ordered)
                {
                    FieldReference? parent = null;
                    var aggregate = lane.Aggregate;
                    if (aggregate != null)
                    {
                        var offset = aggregate.BackingData!.FieldOffset;
                        parent = new FieldReference(aggregate, target.Local, offset);
                        var fullCoverage = true;
                        for (var member = offset; member < offset + lane.AggregateSize; member += lane.Size)
                            fullCoverage &= covered.Contains(member);
                        if (fullCoverage)
                        {
                            if (emitted.Add(aggregate))
                                writes.Add((parent, new DefaultValue(aggregate.FieldType)));
                            continue;
                        }
                    }

                    var field = new FieldReference(lane.Field, target.Local, lane.Offset, parent);
                    writes.Add((field, lane.Size == sizeof(float) ? new FloatLiteral(0f) : new DoubleLiteral(0d)));
                }

                var written = 0;
                for (var j = i; j < end; j++)
                {
                    var original = instructions[j];
                    if (original.OpCode == OpCode.Nop)
                        continue;
                    original.MemoryAccessSize = null;
                    original.OperandWidth = null;
                    if (written < writes.Count)
                    {
                        var (field, value) = writes[written++];
                        original.SetOperands(field, value);
                    }
                    else
                    {
                        original.OpCode = OpCode.Nop;
                        original.SetOperands();
                    }
                }
                while (written < writes.Count)
                {
                    var (field, value) = writes[written++];
                    instructions.Insert(end++, new Instruction(-1, OpCode.Move, field, value));
                }
                i = end - 1;
            }
        }
    }

    private static bool TryGetWrite(Instruction instruction, out FieldReference target, out int size)
    {
        if (instruction is { OpCode: OpCode.Move, MemoryAccessSize: 4 or 8 or 16,
                Operands: [FieldReference { Field.IsStatic: false, Offset: >= 0 and <= int.MaxValue - 16 } field, var value] }
            && IsZero(value))
        {
            target = field;
            size = instruction.MemoryAccessSize.Value;
            return true;
        }
        target = null!;
        size = 0;
        return false;
    }

    private static bool IsZero(IOperand value) => value is Immediate { Value: 0 }
        || value is FloatLiteral single && BitConverter.DoubleToInt64Bits(single.Value) == 0
        || value is DoubleLiteral wide && BitConverter.DoubleToInt64Bits(wide.Value) == 0;

    private static bool CoversAggregate(Dictionary<int, Lane> layout, int offset, int size)
    {
        var end = offset + size;
        var touchesAggregate = false;
        while (offset < end)
        {
            if (!layout.TryGetValue(offset, out var lane) || lane.Size > end - offset)
                return false;
            touchesAggregate |= lane.Aggregate != null;
            offset += lane.Size;
        }
        return touchesAggregate;
    }

    private static Dictionary<int, Lane>? BuildLayout(TypeAnalysisContext owner)
    {
        var result = new Dictionary<int, Lane>();
        var hasAggregate = false;
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (type is GenericInstanceTypeAnalysisContext || type.GenericParameters.Count != 0
                || (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return null;

            foreach (var field in type.Fields)
            {
                if (field.IsStatic || field.BackingData?.FieldOffset is not { } offset
                    || offset is < 0 or > int.MaxValue - 32)
                    continue;

                var fieldType = field.FieldType;
                switch (fieldType.FullName)
                {
                    case "System.Single":
                    case "System.Double":
                        var scalarSize = fieldType.FullName == "System.Single" ? sizeof(float) : sizeof(double);
                        if (result.ContainsKey(offset))
                            return null;
                        result.Add(offset, new Lane(field, offset, scalarSize, null, 0));
                        break;
                    default:
                        if (Arm64CallingConventionResolver.GetFloatAggregateFields(fieldType) is not { } members)
                            break;
                        var laneSize = members[0].FieldType.FullName == "System.Single" ? sizeof(float) : sizeof(double);
                        for (var i = 0; i < members.Length; i++)
                        {
                            var laneOffset = offset + i * laneSize;
                            if (result.ContainsKey(laneOffset))
                                return null;
                            result.Add(laneOffset, new Lane(members[i], laneOffset, laneSize, field, members.Length * laneSize));
                        }
                        hasAggregate = true;
                        break;
                }
            }
        }
        return hasAggregate ? result : null;
    }
}
