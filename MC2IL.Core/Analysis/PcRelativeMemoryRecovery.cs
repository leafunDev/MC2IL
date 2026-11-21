using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Analysis;

internal static class PcRelativeMemoryRecovery
{
    // Run in SSA so a register reused on another path cannot become the base of this access.
    public static void Run(ISILControlFlowGraph graph)
    {
        var addresses = new Dictionary<LocalVariable, long>();
        foreach (var block in graph.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction is { OpCode: OpCode.Move, IsPcRelativeAddress: true,
                        Operands: [LocalVariable destination, Immediate address] })
                    addresses.Add(destination, address.Value);

        if (addresses.Count == 0)
            return;

        foreach (var block in graph.Blocks)
            foreach (var instruction in block.Instructions)
                for (var index = 0; index < instruction.Operands.Count; index++)
                    if (instruction.Operands[index] is MemoryOperand { Base: LocalVariable local, Index: null, Scale: 0 } memory
                        && addresses.TryGetValue(local, out var address))
                        instruction.SetOperand(index, new MemoryOperand(addend: unchecked(address + memory.Addend)));
    }
}
