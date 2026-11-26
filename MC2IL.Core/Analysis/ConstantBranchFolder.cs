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

    internal static void NormalizeBooleanReturnPaths(MethodAnalysisContext method)
    {
        if (method.ReturnType != method.AppContext.SystemTypes.SystemBooleanType)
            return;

        var graph = method.ControlFlowGraph!;
        var changed = false;
        var loopExits = new List<Block>();
        var earlyReturns = new Dictionary<Block, Block>();
        DominatorInfo? dominance = null;
        foreach (var join in graph.Blocks)
        {
            if (join.Predecessors.Count < 2 || join.Successors is not [var exit] || exit != graph.ExitBlock
                || join.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray() is not
                    [{ OpCode: OpCode.Return, Operands: [LocalVariable result] } sharedReturn]
                || result.Type != method.ReturnType
                || result.Register.Name.StartsWith("stack_", StringComparison.Ordinal))
                continue;

            var returns = new List<(Block Block, Instruction Store, Immediate Value)>();
            foreach (var path in join.Predecessors)
            {
                if (path.Successors is not [var successor] || successor != join)
                    continue;

                var operations = path.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
                if (operations is not [_]
                    && !(operations is [_, { OpCode: OpCode.Jump, Operands: [Block target] }] && target == join))
                    continue;
                if (operations[0] is { OpCode: OpCode.Move, OperandWidth: null,
                        Operands: [LocalVariable destination, Immediate { Value: 0 or 1 } value] } store && destination == result)
                    returns.Add((path, store, value));
            }

            if (returns.Count < 2 || !returns.Any(path => path.Value.Value != returns[0].Value.Value)
                || graph.Instructions.Any(i => i != sharedReturn && DeadCodeEliminator.UsedLocals(i).Contains(result)))
                continue;

            // Compilers share a return epilogue even across independent loop exits. Give
            // each constant its own return so a decompiler can recover the loop condition.
            foreach (var (path, store, value) in returns)
            {
                foreach (var header in path.Predecessors)
                {
                    if (header.BlockType != BlockType.TwoWay || header.Predecessors.Count < 2)
                        continue;

                    dominance ??= new DominatorInfo(graph);
                    if (!header.Predecessors.Any(predecessor => dominance.Dominates(header, predecessor))
                        || !returns.Any(other => other.Block != path && dominance.Dominates(header, other.Block)))
                        continue;

                    loopExits.Add(path);
                    foreach (var (earlyReturn, _, _) in returns)
                        if (earlyReturn != path && dominance.Dominates(header, earlyReturn)
                            && earlyReturn.Predecessors is [var test] && test != header && test.BlockType == BlockType.TwoWay)
                            earlyReturns[earlyReturn] = test;
                    break;
                }

                store.OpCode = OpCode.Return;
                store.SetOperands(value);
                var afterStore = path.Instructions.IndexOf(store) + 1;
                path.Instructions.RemoveRange(afterStore, path.Instructions.Count - afterStore);
                graph.RemoveEdge(path, join);
                path.Successors.Add(exit);
                exit.Predecessors.Add(path);
                path.CalculateBlockType();
            }

            changed = true;
        }

        // Let an early return fall through from its test, with the other branch
        // continuing around the loop.
        foreach (var pair in earlyReturns)
        {
            var earlyReturn = pair.Key;
            var test = pair.Value;
            graph.Blocks.Remove(earlyReturn);
            graph.Blocks.Insert(graph.Blocks.IndexOf(test) + 1, earlyReturn);
        }

        // Lay out the condition's exit after the early returns in the loop body.
        // The C# decompiler uses IL offsets to choose which exit goes after the loop.
        foreach (var exit in loopExits)
        {
            graph.Blocks.Remove(exit);
            graph.Blocks.Add(exit);
        }

        if (changed)
            graph.RemoveUnreachableBlocks();
    }
}
