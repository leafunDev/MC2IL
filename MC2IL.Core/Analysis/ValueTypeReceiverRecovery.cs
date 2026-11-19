using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class ValueTypeReceiverRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var call = block.Instructions[i];
                if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext
                    { IsStatic: false, Parameters.Count: 0, Name: not ".ctor",
                        DeclaringType: { IsValueType: true } valueType })
                    continue;

                var receiverIndex = call.OpCode == OpCode.Call ? 2 : 1;
                if (call.Operands.Count <= receiverIndex
                    || call.Operands[receiverIndex] is not LocalVariable address
                    || FindPrevious(block, i) is not { OpCode: OpCode.Add, OperandWidth: null or 64,
                        Operands: [LocalVariable destination, LocalVariable { Type: { IsValueType: false } owner } instance,
                            Immediate { Value: > 0 and <= int.MaxValue } offset] } add
                    || destination != address || destination == instance
                    || owner is ReferencedTypeAnalysisContext || owner.GenericParameters.Count != 0
                    || FindUniqueField(owner, (int)offset.Value) is not { } field
                    || !MetadataResolver.IsSameType(field.FieldType, valueType)
                    || HasOtherReads(graph, address, call))
                    continue;

                call.SetOperand(receiverIndex, new AddressOf(new FieldReference(field, instance, (int)offset.Value)));
                add.OpCode = OpCode.Nop;
                add.SetOperands();
            }
        }
    }

    private static Instruction? FindPrevious(Block block, int before)
    {
        HashSet<Block>? visited = null;
        while (true)
        {
            for (var i = before - 1; i >= 0; i--)
                if (block.Instructions[i].OpCode != OpCode.Nop)
                    return block.Instructions[i];

            if (block.Predecessors is not [var parent] || parent.BlockType == BlockType.Entry
                || parent.Successors is not [var next] || next != block)
                return null;

            visited ??= [block];
            if (!visited.Add(parent))
                return null;

            block = parent;
            before = parent.Instructions.Count;
            while (before > 0 && parent.Instructions[before - 1].OpCode == OpCode.Nop)
                before--;
            if (before > 0 && parent.Instructions[before - 1] is
                { OpCode: OpCode.Jump, Operands: [Block target] } && target == next)
                before--;
        }
    }

    private static FieldAnalysisContext? FindUniqueField(TypeAnalysisContext owner, int offset)
    {
        FieldAnalysisContext? match = null;
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (type.IsGenericInstance)
                return null;

            foreach (var field in type.Fields)
            {
                if (field.IsStatic || field.BackingData?.FieldOffset != offset)
                    continue;
                if (match != null)
                    return null;
                match = field;
            }
        }

        return match;
    }

    private static bool HasOtherReads(ISILControlFlowGraph graph, LocalVariable address, Instruction call)
    {
        var reads = 0;
        foreach (var instruction in graph.Instructions)
        {
            foreach (var used in DeadCodeEliminator.UsedLocals(instruction))
                if (used == address && (instruction != call || ++reads > 1))
                    return true;
        }

        return false;
    }
}
