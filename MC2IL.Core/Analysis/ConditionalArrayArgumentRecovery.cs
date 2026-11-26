using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class ConditionalArrayArgumentRecovery
{
    private sealed record Arm(Block Join, Instruction Load, ArrayAccess Access, Immediate Index,
        Instruction Call, MethodAnalysisContext Setter, LocalVariable Receiver);

    internal static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        ILookup<LocalVariable, LocalVariable>? uses = null;
        var changed = false;

        foreach (var block in graph.Blocks)
        {
            if (block.Successors.Count != 2 || block.Instructions.LastOrDefault() is not
                    { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable { Type.FullName: "System.Boolean" } test] }
                || !block.Successors.Contains(taken)
                || ReadArm(block, taken) is not { } whenTrue
                || ReadArm(block, block.Successors.First(next => next != taken)) is not { } whenFalse
                || whenTrue.Join != whenFalse.Join || whenTrue.Join.Predecessors.Count != 2
                || whenTrue.Setter != whenFalse.Setter || whenTrue.Receiver != whenFalse.Receiver
                || whenTrue.Call.NullCheckReceiver != whenFalse.Call.NullCheckReceiver
                || whenTrue.Access.Array != whenFalse.Access.Array)
                continue;

            uses ??= graph.Instructions.SelectMany(DeadCodeEliminator.UsedLocals).ToLookup(local => local);
            if (whenTrue.Load.Destination is not LocalVariable trueValue || whenFalse.Load.Destination is not LocalVariable falseValue
                || uses[trueValue].Count() != (trueValue == falseValue ? 2 : 1)
                || uses[falseValue].Count() != (trueValue == falseValue ? 2 : 1)
                || whenTrue.Call.Destination is LocalVariable trueResult && uses.Contains(trueResult)
                || whenFalse.Call.Destination is LocalVariable falseResult && uses.Contains(falseResult))
                continue;

            var trueIndex = whenTrue.Index;
            var falseIndex = whenFalse.Index;
            var producer = block.Instructions.Take(block.Instructions.Count - 1).LastOrDefault(i => i.OpCode != OpCode.Nop);
            if (producer is { OpCode: OpCode.Not,
                    Operands: [LocalVariable negated, LocalVariable { Type.FullName: "System.Boolean" } original] }
                && negated == test && original != negated)
            {
                test = original;
                (trueIndex, falseIndex) = (falseIndex, trueIndex);
            }

            // Both paths do exactly one checked array read and the same setter call.
            // Select the index first so the managed read keeps the original exceptions.
            var name = $"selectedIndex{method.Locals.Count}";
            var index = new LocalVariable(name, new Register(null, name), method.AppContext.SystemTypes.SystemInt32Type);
            method.Locals.Add(index);
            whenTrue.Load.SetOperand(1, new ArrayAccess(whenTrue.Access.Array, index));
            block.Instructions.RemoveAt(block.Instructions.Count - 1);
            block.Instructions.Add(new Instruction(-1, OpCode.Select, index, test, trueIndex, falseIndex));
            block.Instructions.Add(whenTrue.Load);
            block.Instructions.Add(whenTrue.Call);
            block.Instructions.Add(new Instruction(-1, OpCode.Jump, whenTrue.Join));
            foreach (var successor in block.Successors.ToArray())
                graph.RemoveEdge(block, successor);
            block.Successors.Add(whenTrue.Join);
            whenTrue.Join.Predecessors.Add(block);
            block.CalculateBlockType();
            changed = true;
        }

        if (changed)
            graph.RemoveUnreachableBlocks();
    }

    private static Arm? ReadArm(Block split, Block entry)
    {
        var seen = new HashSet<Block> { split };
        var previous = split;
        var block = entry;
        var operations = new List<Instruction>(2);
        while (seen.Count <= 12 && seen.Add(block) && block.Predecessors is [var predecessor] && predecessor == previous
               && block.Successors is [var next])
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode == OpCode.Nop
                    || instruction is { OpCode: OpCode.Jump, Operands: [Block target] } && target == next)
                    continue;

                if (operations.Count == 2)
                    return null;
                operations.Add(instruction);
            }

            if (next.Predecessors.Count > 1 && !seen.Contains(next)
                && operations is [
                    { OpCode: OpCode.Move, OperandWidth: null, Operands: [LocalVariable value, ArrayAccess access] } load,
                    { IsCall: true } call]
                && access.Index is Immediate { Value: >= 0 and <= int.MaxValue } index
                && call.Operands[0] is MethodAnalysisContext { IsVoid: true, IsStatic: false, Parameters.Count: 1 } setter
                && setter.Name.StartsWith("set_", System.StringComparison.Ordinal)
                && access.Array.Type is SzArrayTypeAnalysisContext arrayType
                && MetadataResolver.IsSameType(arrayType.ElementType, setter.Parameters[0].ParameterType))
            {
                var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
                if (call.Operands.Count == receiverIndex + 2
                    && call.Operands[receiverIndex] is LocalVariable receiver
                    && call.Operands[receiverIndex + 1] == value)
                    return new Arm(next, load, access, index, call, setter, receiver);
            }
            previous = block;
            block = next;
        }

        return null;
    }
}
