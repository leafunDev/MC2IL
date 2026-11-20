using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class StructReturnFieldRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        var returnType = method.ReturnType;
        if (!returnType.IsValueType || (returnType.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout
            || method.AppContext.InstructionSet.CallingConventionResolver is not Arm64CallingConventionResolver convention
            || convention.HiddenReturnBufferRegister(method) is not { } buffer)
            return;

        var graph = method.ControlFlowGraph!;
        if (graph.ExitBlock.Predecessors is not [var terminal])
            return;
        var operations = terminal.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        if (operations.Length < 2 || operations[^1] is not
                { OpCode: OpCode.Return, Operands: [LocalVariable { Register.Version: -1 } result] }
            || result.Register.Number != buffer.Number || result.Type != returnType)
            return;

        var stores = new Dictionary<FieldAnalysisContext, List<Instruction>>();
        var index = operations.Length - 2;
        for (; index >= 0; index--)
        {
            var store = operations[index];
            if (store is not { OpCode: OpCode.Move, Operands: [FieldReference field, LocalVariable] }
                || !ReferenceEquals(field.Local, result))
                break;

            var root = field.Parent ?? field;
            if (root.Parent != null || root.Field.IsStatic || root.Field.DeclaringType != returnType
                || (root.Field.Attributes & FieldAttributes.InitOnly) != 0)
                return;
            if (!stores.TryGetValue(root.Field, out var group))
                stores.Add(root.Field, group = []);
            group.Add(store);
        }

        if (!stores.Values.Any(group => group.Count >= 2))
            return;

        var tail = new HashSet<Instruction>(operations.Skip(index + 1));
        var definitions = new Dictionary<LocalVariable, Instruction?>();
        var reads = new Dictionary<LocalVariable, int>();
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction.Destination, result))
                return;
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = definitions.ContainsKey(destination) ? null : instruction;
            foreach (var used in DeadCodeEliminator.UsedLocals(instruction))
            {
                // The hidden buffer is represented by a private CIL local. Only fold stores
                // when no code can observe its partially initialized value or take its address.
                if (ReferenceEquals(used, result) && !tail.Contains(instruction))
                    return;
                reads[used] = reads.TryGetValue(used, out var count) ? count + 1 : 1;
            }
        }

        // Each terminal store and the return read the buffer exactly once.
        if (!reads.TryGetValue(result, out var resultUses) || resultUses != tail.Count)
            return;

        var positions = GetLinearPositions(terminal);
        foreach (var group in stores.Values)
            FoldAggregateCopy(group, result, definitions, reads, positions);
    }

    private static void FoldAggregateCopy(List<Instruction> group, LocalVariable result,
        Dictionary<LocalVariable, Instruction?> definitions, Dictionary<LocalVariable, int> reads,
        Dictionary<Instruction, int> positions)
    {
        var store = group[^1]; // The first store in execution order.
        if (store.Operands[0] is not FieldReference { Parent: { } root }
            || Arm64CallingConventionResolver.GetFloatAggregateFields(root.Field.FieldType) is not { Length: >= 2 } fields
            || fields.Length != group.Count)
            return;

        var size = fields[0].FieldType.FullName == "System.Double" ? sizeof(double) : sizeof(float);
        var seen = new HashSet<FieldAnalysisContext>();
        LocalVariable? source = null;
        foreach (var lane in group)
        {
            if (lane is not { MemoryAccessSize: var width, OperandWidth: null,
                    Operands: [FieldReference { Parent: { } parent } destination, LocalVariable component] }
                || width != size || parent.Offset != root.Offset || !fields.Contains(destination.Field)
                || !seen.Add(destination.Field) || destination.Offset != root.Offset + destination.Field.Offset
                || component.Type != destination.Field.FieldType
                || !definitions.TryGetValue(component, out var definition)
                || definition is not { OpCode: OpCode.Move, OperandWidth: null,
                    Operands: [_, FieldReference { Parent: null, Field: var field, Local: var aggregate }] }
                || field != destination.Field || aggregate.Type != root.Field.FieldType
                || (source != null && !ReferenceEquals(source, aggregate))
                || !ComesBefore(definition, lane, positions))
                return;
            source = aggregate;
        }

        // All uses of the original value are scalar projections. The original call result
        // has no second definition, field writes or address escapes.
        if (source == null || !reads.TryGetValue(source, out var count) || count != fields.Length
            || !definitions.TryGetValue(source, out var call)
            || call is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext target, ..] }
            || target.ReturnType != source.Type
            || group.Any(lane => !ComesBefore(call, definitions[(LocalVariable)lane.Operands[1]]!, positions)))
            return;

        store.SetOperands(new FieldReference(root.Field, result, root.Offset), source);
        store.MemoryAccessSize = null;
        foreach (var redundant in group)
            if (redundant != store)
                Remove(redundant);
    }

    private static Dictionary<Instruction, int> GetLinearPositions(Block terminal)
    {
        var positions = new Dictionary<Instruction, int>();
        var visited = new HashSet<Block>();
        var position = 0;
        for (var block = terminal; visited.Add(block);)
        {
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
                positions.Add(block.Instructions[i], --position);
            if (block.Predecessors is not [var predecessor]
                || predecessor.Successors is not [var successor] || successor != block)
                break;
            block = predecessor;
        }
        return positions;
    }

    private static bool ComesBefore(Instruction first, Instruction second, Dictionary<Instruction, int> positions) =>
        positions.TryGetValue(first, out var left) && positions.TryGetValue(second, out var right) && left < right;

    private static void Remove(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }
}
