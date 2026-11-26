using System;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64SharedVirtualDispatchRecovery
{
    private const int PointerSize = 8;

    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        foreach (var shared in graph.Blocks.ToArray())
        {
            if (shared.Predecessors is not [var first, var second] || shared.Instructions.Count == 0
                || first == shared || second == shared
                || InsertionIndex(first, shared) is not { } firstIndex
                || InsertionIndex(second, shared) is not { } secondIndex)
                continue;

            var instructions = shared.Instructions;
            if (shared.Successors is [var exit] && exit == graph.ExitBlock
                && instructions[^1] is { OpCode: OpCode.IndirectJump, Operands: [Register target, ..] }
                && instructions.Take(instructions.Count - 1).All(instruction => IsStackRestore(instruction, target))
                && DispatchSlot(first, firstIndex, target) is { } slot
                && DispatchSlot(second, secondIndex, target) == slot)
            {
                // Keep each path's receiver, arguments and vtable entry together before SSA.
                AppendTail(first, firstIndex, shared, graph);
                AppendTail(second, secondIndex, shared, graph);
                graph.RemoveEdge(shared, exit);
                graph.Blocks.Remove(shared);
            }
            else if (instructions.Count > 3 && HasSharedCallPrefix(shared, out var klass)
                && HasClassLoad(first, firstIndex, klass) && HasClassLoad(second, secondIndex, klass))
            {
                // The join loads the slot and MethodInfo from the path's class pointer.
                // Move only this dispatch sequence; the rest of the block stays shared.
                first.Instructions.InsertRange(firstIndex, instructions.Take(3).Select(Clone));
                second.Instructions.InsertRange(secondIndex, instructions.Take(3).Select(Clone));
                instructions.RemoveRange(0, 3);
                first.CalculateBlockType();
                second.CalculateBlockType();
            }
        }
    }

    private static int? InsertionIndex(Block predecessor, Block shared)
    {
        if (predecessor.Successors is not [var successor] || successor != shared
            || predecessor.Instructions.Count == 0)
            return null;

        var index = predecessor.Instructions.Count - 1;
        var terminator = predecessor.Instructions[index];
        if (terminator is { OpCode: OpCode.Jump, Operands: [Block destination] } && destination == shared)
            return index;
        return terminator.IsFallThrough ? index + 1 : null;
    }

    private static bool IsStackRestore(Instruction instruction, Register target) =>
        instruction is { OpCode: OpCode.Move, Operands: [Register saved, Register slot] }
        && saved != target && slot.Name.StartsWith("stack_", StringComparison.Ordinal);

    private static long? DispatchSlot(Block block, int limit, Register target)
    {
        var index = FindDefinition(block, limit, target);
        if (index < 0 || block.Instructions[index] is not
            { OpCode: OpCode.Move, MemoryAccessSize: PointerSize, Operands: [_, MemoryOperand
                { Base: Register klass, Index: null, Scale: 0, Addend: > 0 } entry] })
            return null;

        return HasClassLoad(block, index, klass) ? entry.Addend : null;
    }

    private static bool HasSharedCallPrefix(Block block, out Register klass)
    {
        klass = default;
        var instructions = block.Instructions;
        if (instructions[0] is not { OpCode: OpCode.Move, MemoryAccessSize: PointerSize, Operands:
                [Register target, MemoryOperand { Base: Register source, Index: null, Scale: 0, Addend: > 0 } entry] }
            || source == target
            || instructions[1] is not { OpCode: OpCode.Move, MemoryAccessSize: PointerSize, Operands:
                [Register methodInfo, MemoryOperand { Base: Register infoClass, Index: null, Scale: 0 } info] }
            || target == methodInfo || infoClass != source || info.Addend != entry.Addend + PointerSize
            || instructions[2] is not { OpCode: OpCode.IndirectCall, Operands: [Register called, _, ..] }
            || called != target)
            return false;

        klass = source;
        return true;
    }

    private static bool HasClassLoad(Block block, int limit, Register klass)
    {
        var index = FindDefinition(block, limit, klass);
        return index >= 0 && block.Instructions[index] is
            { OpCode: OpCode.Move, MemoryAccessSize: PointerSize, Operands: [_, MemoryOperand
                { Base: Register, Index: null, Scale: 0, Addend: 0 }] };
    }

    private static int FindDefinition(Block block, int limit, Register register)
    {
        for (var i = limit - 1; i >= 0; i--)
        {
            var instruction = block.Instructions[i];
            if (instruction.IsCall || instruction.OpCode == OpCode.IndirectCall)
                break;
            if (instruction.Destination is Register written && written == register)
                return i;
        }
        return -1;
    }

    private static void AppendTail(Block predecessor, int index, Block shared, ISILControlFlowGraph graph)
    {
        if (index < predecessor.Instructions.Count)
            predecessor.Instructions.RemoveAt(index);
        predecessor.Instructions.AddRange(shared.Instructions.Select(Clone));

        graph.RemoveEdge(predecessor, shared);
        predecessor.Successors.Add(graph.ExitBlock);
        graph.ExitBlock.Predecessors.Add(predecessor);
        predecessor.CalculateBlockType();
    }

    private static Instruction Clone(Instruction instruction) =>
        new(-1, instruction.OpCode, instruction.Operands.ToList())
        {
            MemoryAccessSize = instruction.MemoryAccessSize,
            OperandWidth = instruction.OperandWidth,
            NativeAddress = instruction.NativeAddress
        };
}
