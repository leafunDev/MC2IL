using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// Points branches whose condition folded to a constant at the block they actually reach.
public static class ConstantBranchFolder
{
    public static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var removedEdge = false;

        foreach (var block in graph.Blocks)
        {
            if (block.Instructions.Count == 0)
                continue;

            var branch = block.Instructions[^1];

            if (branch is not { OpCode: OpCode.ConditionalJump, Operands: [_, Immediate condition] })
                continue;

            if (block.Successors.Count != 2
                || ResolveTarget(branch, graph) is not { } taken
                || !block.Successors.Contains(taken))
                continue;

            var fallthrough = block.Successors.First(s => s != taken);
            var destination = condition.Value != 0 ? taken : fallthrough;
            var discarded = condition.Value != 0 ? fallthrough : taken;

            branch.OpCode = OpCode.Jump;
            branch.SetOperands(destination);
            graph.RemoveEdge(block, discarded);
            block.CalculateBlockType();
            removedEdge = true;
        }

        if (removedEdge)
            graph.RemoveUnreachableBlocks();
    }

    private static Block? ResolveTarget(Instruction branch, ISILControlFlowGraph graph) => branch.Operands[0] switch
    {
        Block block => block,
        Instruction instruction => graph.FindBlockByInstruction(instruction),
        _ => null,
    };
}
