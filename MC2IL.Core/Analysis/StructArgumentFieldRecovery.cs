using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class StructArgumentFieldRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not Arm64CallingConventionResolver
            || !method.ParameterLocals.Any(IsByValueStructParameter))
            return;

        var graph = method.ControlFlowGraph!;
        var instructions = graph.Instructions;
        if (!instructions.Any(instruction => instruction.OpCode == OpCode.PackFloatAggregate))
            return;

        var reads = new Dictionary<LocalVariable, int>();
        foreach (var instruction in instructions)
        foreach (var local in DeadCodeEliminator.UsedLocals(instruction))
            reads[local] = reads.TryGetValue(local, out var count) ? count + 1 : 1;

        foreach (var block in graph.Blocks)
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            if (block.Instructions[index] is { OpCode: OpCode.PackFloatAggregate,
                    Operands: [LocalVariable result, TypeAnalysisContext type, ..] } pack
                && Arm64CallingConventionResolver.GetFloatAggregateFields(type) is { Length: >= 2 } fields
                && fields.Length + 2 == pack.Operands.Count)
                TryFoldPack(method, block, index, pack, result, type, fields, reads);
        }
    }

    private static bool IsByValueStructParameter(LocalVariable local) =>
        local is { IsThis: false, Register.Version: -1, Type: { IsValueType: true } type }
        && (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout;

    private static void TryFoldPack(MethodAnalysisContext method, Block block, int index, Instruction pack,
        LocalVariable result, TypeAnalysisContext type, FieldAnalysisContext[] fields, Dictionary<LocalVariable, int> reads)
    {
        var lanes = new Dictionary<LocalVariable, FieldAnalysisContext>();
        for (var i = 0; i < fields.Length; i++)
        {
            if (pack.Operands[i + 2] is not LocalVariable value || value.Type != fields[i].FieldType
                || !reads.TryGetValue(value, out var count) || count != 1 || lanes.ContainsKey(value))
                return;
            lanes.Add(value, fields[i]);
        }

        var size = fields[0].FieldType.FullName == "System.Double" ? sizeof(double) : sizeof(float);
        var loads = new List<Instruction>(fields.Length);
        FieldReference? source = null;
        // Only walk a straight line of scalar loads. A store or call between loads
        // could change what reading the entire argument field would observe.
        var visited = new HashSet<Block>();
        var position = index - 1;
        for (var current = block; visited.Add(current);)
        {
            for (; position >= 0; position--)
            {
                var load = current.Instructions[position];
                if (load.OpCode == OpCode.Nop)
                    continue;

                if (load is not { OpCode: OpCode.Move, OperandWidth: null, MemoryAccessSize: var width,
                        Operands: [LocalVariable lane, FieldReference { Parent: { Parent: null } parent } field] }
                    || width != size || !lanes.TryGetValue(lane, out var expected) || field.Field != expected
                    || field.Offset != parent.Offset + expected.Offset || parent.Field.IsStatic
                    || parent.Field.FieldType != type || !IsByValueStructParameter(field.Local)
                    || parent.Field.DeclaringType != field.Local.Type || !method.ParameterLocals.Contains(field.Local)
                    || source != null && (!ReferenceEquals(source.Local, field.Local)
                        || source.Field != parent.Field || source.Offset != parent.Offset))
                    return;

                source ??= new FieldReference(parent.Field, field.Local, parent.Offset);
                lanes.Remove(lane);
                loads.Add(load);
                if (lanes.Count == 0)
                {
                    pack.OpCode = OpCode.Move;
                    pack.SetOperands(result, source);
                    foreach (var scalarLoad in loads)
                    {
                        scalarLoad.OpCode = OpCode.Nop;
                        scalarLoad.SetOperands();
                    }
                    return;
                }
            }

            if (current.Predecessors is not [var predecessor]
                || predecessor.Successors is not [var successor] || successor != current)
                return;
            position = predecessor.Instructions.Count - 1;
            if (position >= 0 && predecessor.Instructions[position] is
                { OpCode: OpCode.Jump, Operands: [Block target] } && target == current)
                position--;
            current = predecessor;
        }
    }
}
