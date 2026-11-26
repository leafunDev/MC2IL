using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// IL2CPP reuses one native stack slot for unrelated value-type format receivers.
internal static class PrimitiveFormatReceiverRecovery
{
    internal static bool IsInt32ToString(MethodAnalysisContext method) =>
        !method.IsStatic && method.Name == "ToString" && method.Parameters.Count == 0
        && method.DeclaringType?.FullName == "System.Int32" && method.ReturnType.FullName == "System.String";

    internal static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var calls = new Dictionary<LocalVariable, List<(Block Block, int Index, Instruction Call)>>();
        foreach (var block in graph.Blocks)
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (instruction is not { OpCode: OpCode.Call, Operands:
                    [MethodAnalysisContext format, _, AddressOf { Target: LocalVariable { Type.FullName: "System.Int32" } slot }] }
                || !slot.Register.Name.StartsWith("stack_", StringComparison.Ordinal) || !IsInt32ToString(format))
                continue;

            if (!calls.TryGetValue(slot, out var formats))
                calls.Add(slot, formats = []);
            formats.Add((block, index, instruction));
        }

        if (calls.Count == 0)
            return;

        var instructions = graph.Instructions;
        var reads = instructions.SelectMany(DeadCodeEliminator.UsedLocals).ToLookup(local => local);
        var written = new HashSet<LocalVariable>(instructions.Select(i => i.Destination).OfType<LocalVariable>());
        var addressed = new HashSet<LocalVariable>(instructions.SelectMany(i => i.Operands)
            .OfType<AddressOf>().Select(address => address.Target).OfType<LocalVariable>());
        foreach (var pair in calls)
        {
            var slot = pair.Key;
            var formats = pair.Value;
            // A call taking the address or loading the native slot for any other reason
            // makes it unsafe to give the receivers independent managed storage.
            if (reads[slot].Count() != formats.Count)
                continue;

            var stores = new HashSet<Instruction>();
            var matches = new List<(Block Block, int Index, Instruction Call, LocalVariable Source)>();
            foreach (var (block, index, call) in formats)
            {
                if (FindWriteBefore(block, index, slot) is not { } write
                    || write.Block.Instructions[write.Index] is not
                        { OpCode: OpCode.Move, OperandWidth: null or 32, MemoryAccessSize: null or 4,
                            Operands: [_, LocalVariable { Type.FullName: "System.Int32" } source] } store
                    || !stores.Add(store))
                    break;
                matches.Add((write.Block, write.Index, call, source));
            }
            if (stores.Count != formats.Count)
                continue;

            foreach (var (block, index, call, source) in matches)
            {
                LocalVariable value;
                if (!source.IsThis && method.ParameterLocals.Contains(source) && !written.Contains(source) && !addressed.Contains(source))
                {
                    // Format an unchanged by-value parameter directly.
                    value = source;
                }
                else if (reads[source].Count() == 1 && !source.Register.Name.StartsWith("stack_", StringComparison.Ordinal)
                    && FindWriteBefore(block, index, source) is { } previous
                    && previous.Block.Instructions[previous.Index] is
                        { OpCode: OpCode.Call, Operands: [MethodAnalysisContext { ReturnType.FullName: "System.Int32" }, ..] } producer)
                {
                    // Retarget the getter or other producer at its original location.
                    // Its result no longer needs a separate copy into the native slot.
                    var name = $"formatValue{method.Locals.Count}";
                    value = new LocalVariable(name, new Register(null, name), slot.Type);
                    method.Locals.Add(value);
                    producer.Destination = value;
                }
                else
                {
                    continue;
                }

                call.SetOperand(2, new AddressOf(value));
                block.Instructions[index].OpCode = OpCode.Nop;
                block.Instructions[index].SetOperands();
            }
        }
    }

    private static (Block Block, int Index)? FindWriteBefore(Block block, int index, LocalVariable slot)
    {
        var seen = new HashSet<Block>();
        while (seen.Count < 12 && seen.Add(block))
        {
            for (var i = index - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];
                if (ReferenceEquals(instruction.Destination, slot))
                    return (block, i);
            }
            if (block.Predecessors is not [var predecessor] || predecessor.Successors is not [var next] || next != block)
                return null;
            block = predecessor;
            index = predecessor.Instructions.Count;
        }
        return null;
    }
}
