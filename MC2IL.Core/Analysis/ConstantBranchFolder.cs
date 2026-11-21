using System;
using System.Collections.Generic;
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

    // Lowering a phi can leave one identical constant assignment per guard.
    // Share those paths so the C# decompiler can join short-circuit conditions.
    internal static void MergeConstantReturnPaths(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var merged = false;
        foreach (var join in graph.Blocks)
        {
            if (join.Predecessors.Count < 2 || join.Instructions.Count != 1
                || join.Instructions[0] is not { OpCode: OpCode.Return, Operands: [LocalVariable result] })
                continue;

            var paths = new Dictionary<(byte Kind, long Bits), Block>();
            foreach (var path in join.Predecessors.ToArray())
            {
                if (path.Predecessors.Count == 0 || path.Successors is not [var successor] || successor != join
                    || path.Instructions is not
                    [
                        { OpCode: OpCode.Move, OperandWidth: null, Operands: [LocalVariable destination, var value] },
                        { OpCode: OpCode.Jump, Operands: [Block target] }
                    ] || destination != result || target != join)
                    continue;

                var key = value switch
                {
                    Immediate integer => ((byte)1, integer.Value),
                    FloatLiteral single => ((byte)2, (long)BitConverter.ToInt32(BitConverter.GetBytes(single.Value), 0)),
                    DoubleLiteral number => ((byte)3, BitConverter.DoubleToInt64Bits(number.Value)),
                    _ => default
                };
                if (key.Item1 == 0)
                    continue;
                if (!paths.TryGetValue(key, out var shared))
                {
                    paths.Add(key, path);
                    continue;
                }

                if (path.Predecessors.Any(predecessor => predecessor == shared
                    || predecessor.Successors.Contains(shared) || predecessor.Instructions.Count == 0
                    || predecessor.Instructions[^1] is not { OpCode: OpCode.Jump or OpCode.ConditionalJump, Operands: [Block, ..] }))
                    continue;

                foreach (var predecessor in path.Predecessors.ToArray())
                {
                    var branch = predecessor.Instructions[^1];
                    if (ReferenceEquals(branch.Operands[0], path))
                        branch.SetOperand(0, shared);
                    predecessor.Successors[predecessor.Successors.IndexOf(path)] = shared;
                    shared.Predecessors.Add(predecessor);
                }
                path.Predecessors.Clear();
                merged = true;
            }
        }

        if (merged)
            graph.RemoveUnreachableBlocks();
    }
}
