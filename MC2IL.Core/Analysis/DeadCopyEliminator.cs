using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Removes local-to-local copies whose value is overwritten on every path before it is read.
/// SSA destruction can insert several copies of one phi value along a path; retaining those
/// dead copies creates extra CIL locals and assignments in otherwise ordinary loops.
/// </summary>
public static class DeadCopyEliminator
{
    public static void Run(ISILControlFlowGraph cfg)
    {
        var liveIn = new Dictionary<Block, HashSet<LocalVariable>>();
        var liveOut = new Dictionary<Block, HashSet<LocalVariable>>();

        foreach (var block in cfg.Blocks)
        {
            liveIn.Add(block, []);
            liveOut.Add(block, []);
        }

        var remaining = new Queue<Block>(cfg.Blocks);
        var queued = new HashSet<Block>(cfg.Blocks);

        while (remaining.Count > 0)
        {
            var block = remaining.Dequeue();
            queued.Remove(block);

            var newOut = new HashSet<LocalVariable>();
            foreach (var successor in block.Successors)
                newOut.UnionWith(liveIn[successor]);

            var newIn = TransferBlock(block, newOut);
            var inChanged = !newIn.SetEquals(liveIn[block]);

            liveOut[block] = newOut;
            liveIn[block] = newIn;

            if (!inChanged)
                continue;

            foreach (var predecessor in block.Predecessors)
                if (queued.Add(predecessor))
                    remaining.Enqueue(predecessor);
        }

        var removedAny = false;

        foreach (var block in cfg.Blocks)
        {
            var live = new HashSet<LocalVariable>(liveOut[block]);

            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];
                var destination = instruction.Destination as LocalVariable;

                // Only local copies are removed. Loads, field accesses, calls, and other
                // operations can throw or have side effects even when their result is unused.
                if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable target, LocalVariable] }
                    && !live.Contains(target))
                {
                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                    removedAny = true;
                    continue;
                }

                if (destination != null)
                    live.Remove(destination);

                foreach (var local in UsedLocals(instruction))
                    live.Add(local);
            }
        }

        if (removedAny)
            cfg.RemoveNops();
    }

    private static HashSet<LocalVariable> TransferBlock(Block block, HashSet<LocalVariable> liveOut)
    {
        var live = new HashSet<LocalVariable>(liveOut);

        for (var i = block.Instructions.Count - 1; i >= 0; i--)
        {
            var instruction = block.Instructions[i];

            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, LocalVariable source] })
            {
                if (!live.Contains(destination))
                    continue;

                live.Remove(destination);
                live.Add(source);
                continue;
            }

            if (instruction.Destination is LocalVariable defined)
                live.Remove(defined);

            foreach (var local in UsedLocals(instruction))
                live.Add(local);
        }

        return live;
    }

    private static IEnumerable<LocalVariable> UsedLocals(Instruction instruction)
    {
        var destinationIndex = instruction.DestinationOperandIndex;

        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            var operand = instruction.Operands[i];

            switch (operand)
            {
                case LocalVariable local when i != destinationIndex:
                    yield return local;
                    break;
                case FieldReference field:
                    yield return field.Local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable memoryBase)
                        yield return memoryBase;
                    if (memory.Index is LocalVariable memoryIndex)
                        yield return memoryIndex;
                    break;
                case ArrayAccess array:
                    yield return array.Array;
                    if (array.Index is LocalVariable arrayIndex)
                        yield return arrayIndex;
                    break;
                case ArrayLength length:
                    yield return length.Array;
                    break;
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case AddressOf { Target: FieldReference addressedField }:
                    yield return addressedField.Local;
                    break;
                case AddressOf { Target: ArrayAccess addressedArray }:
                    yield return addressedArray.Array;
                    if (addressedArray.Index is LocalVariable addressedIndex)
                        yield return addressedIndex;
                    break;
                case AddressOf { Target: MemoryOperand addressedMemory }:
                    if (addressedMemory.Base is LocalVariable addressedBase)
                        yield return addressedBase;
                    if (addressedMemory.Index is LocalVariable addressedMemoryIndex)
                        yield return addressedMemoryIndex;
                    break;
            }
        }
    }
}
