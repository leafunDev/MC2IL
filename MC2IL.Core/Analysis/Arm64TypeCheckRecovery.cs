using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>Recovers IL2CPP's inlined class hierarchy tests as managed isinst operations.</summary>
internal static class Arm64TypeCheckRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        // These offsets describe the verified 64-bit metadata-v29 Il2CppClass layout.
        if (method.AppContext.InstructionSet is NewArmV8InstructionSet && method.AppContext.MetadataVersion == 29)
            Run(method.ControlFlowGraph!);
    }

    internal static void Run(ISILControlFlowGraph graph)
    {
        var changed = false;
        foreach (var guard in graph.Blocks.ToArray())
        {
            if (guard.BlockType != BlockType.TwoWay
                || guard.Instructions.Where(IsOperation).ToArray() is not
                    [var classLoad, var targetLoad, var sourceDepthLoad, var targetDepthLoad, var depthCheck, var depthBranch]
                || classLoad is not { OpCode: OpCode.Move, MemoryAccessSize: 8,
                    Operands: [LocalVariable { Type: RuntimeClassTypeAnalysisContext } klass,
                        MemoryOperand { Base: LocalVariable { Type: { } sourceType } value, Index: null, Addend: 0, Scale: 0 }] }
                || classLoad.OperandWidth is not (null or 64)
                || !IlGenerator.IsManagedReference(sourceType)
                || targetLoad is not { OpCode: OpCode.Move,
                    Operands: [LocalVariable { Type: RuntimeClassTypeAnalysisContext } targetClass, TypeAnalysisContext targetType] }
                || targetLoad.OperandWidth is not (null or 64)
                || !IlGenerator.IsManagedReference(targetType) || targetType.IsInterface
                || targetType is SzArrayTypeAnalysisContext or ArrayTypeAnalysisContext or GenericParameterTypeAnalysisContext
                || !IsLoad(sourceDepthLoad, klass, 0x12C, 1, out var sourceDepth)
                || !IsLoad(targetDepthLoad, targetClass, 0x12C, 1, out var targetDepth)
                || depthCheck.OperandWidth != 32
                || depthCheck.Operands is not [LocalVariable condition, var lhsDepth, var rhsDepth]
                || lhsDepth != sourceDepth || rhsDepth != targetDepth
                || !TryGetEdges(guard, depthCheck, depthBranch,
                    OpCode.CheckGreaterOrEqualUnsigned, OpCode.CheckLessUnsigned, out var lookup, out var failure)
                || lookup.Predecessors is not [var predecessor] || predecessor != guard
                || lookup.Instructions.Where(IsOperation).ToArray() is not
                    [var tableLoad, var shift, var add, var ancestorLoad, var typeCheck, var typeBranch]
                || !IsLoad(tableLoad, klass, 0xC8, 8, out var table)
                || shift is not { OpCode: OpCode.ShiftLeft, OperandWidth: 64,
                    Operands: [LocalVariable offset, var shiftedDepth, Immediate { Value: 3 }] }
                || shiftedDepth != targetDepth
                || add is not { OpCode: OpCode.Add, Operands: [LocalVariable slot, var baseTable, var indexOffset] }
                || add.OperandWidth is not (null or 64)
                || baseTable != table || indexOffset != offset
                || !IsLoad(ancestorLoad, slot, -8, 8, out var ancestor)
                || typeCheck.OperandWidth is not (null or 64)
                || typeCheck.Operands is not [LocalVariable, var lhsType, var rhsType]
                || !(lhsType == ancestor && IsTarget(rhsType, targetType, targetClass)
                    || rhsType == ancestor && IsTarget(lhsType, targetType, targetClass))
                || !TryGetEdges(lookup, typeCheck, typeBranch,
                    OpCode.CheckEqual, OpCode.CheckNotEqual, out var success, out var typeFailure)
                || failure != typeFailure || success == guard || success == lookup
                || !HasNonNullEvidence(guard, value))
                continue;

            var discarded = new HashSet<Instruction>(guard.Instructions.Concat(lookup.Instructions));
            var temporaries = new HashSet<LocalVariable>(discarded.Select(i => i.Destination).OfType<LocalVariable>());
            // After SSA removal a local can have several definitions. Retyping it or deleting its
            // producer is safe only when the entire temporary belongs to this native type test.
            if (temporaries.Contains(value) || graph.Instructions.Any(i => !discarded.Contains(i)
                    && (i.Destination is LocalVariable written && temporaries.Contains(written)
                        || DeadCodeEliminator.UsedLocals(i).Any(temporaries.Contains))))
                continue;

            // isinst returns the original reference on success and null on failure. The preceding
            // null guard or managed dereference proves the removed header read cannot throw.
            klass.Type = targetType;
            classLoad.OpCode = OpCode.IsInst;
            classLoad.SetOperands(klass, targetType, value);
            classLoad.MemoryAccessSize = null;
            depthCheck.OpCode = OpCode.CheckEqual;
            depthCheck.SetOperands(condition, klass, new Immediate(0));
            depthCheck.OperandWidth = 64;
            depthBranch.SetOperands(failure, condition);
            foreach (var instruction in new[] { targetLoad, sourceDepthLoad, targetDepthLoad })
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
            }

            guard.Successors[guard.Successors.IndexOf(lookup)] = success;
            lookup.Predecessors.Remove(guard);
            if (!success.Predecessors.Contains(guard))
                success.Predecessors.Add(guard);
            guard.CalculateBlockType();
            changed = true;
        }

        if (changed)
        {
            graph.RemoveUnreachableBlocks();
            DeadCodeEliminator.Run(graph);
        }
    }

    private static bool IsLoad(Instruction instruction, LocalVariable address, long offset, int size,
        out LocalVariable result)
    {
        result = null!;
        if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable destination,
                MemoryOperand { Base: var source, Index: null, Scale: 0, Addend: var addend }] }
            || source != address || addend != offset || instruction.MemoryAccessSize != size)
            return false;
        result = destination;
        return true;
    }

    private static bool IsTarget(IOperand operand, TypeAnalysisContext type, LocalVariable klass) =>
        operand == type || operand == klass;

    private static bool TryGetEdges(Block block, Instruction comparison, Instruction branch,
        OpCode positive, OpCode negative, out Block success, out Block failure)
    {
        success = failure = null!;
        if (comparison.OpCode != positive && comparison.OpCode != negative
            || branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, var condition] }
            || comparison.Destination != condition || block.Successors.Count != 2 || !block.Successors.Contains(taken))
            return false;
        var fallthrough = block.Successors.First(block => block != taken);
        (success, failure) = comparison.OpCode == positive ? (taken, fallthrough) : (fallthrough, taken);
        return true;
    }

    private static bool HasNonNullEvidence(Block guard, LocalVariable value)
    {
        var block = guard;
        var visited = new HashSet<Block> { guard };
        while (block.Predecessors is [var predecessor] && visited.Add(predecessor))
        {
            var instructions = predecessor.Instructions.Where(IsOperation).ToArray();
            if (instructions is [.., var comparison, var branch]
                && comparison.Operands is [LocalVariable, var tested, Immediate { Value: 0 }] && tested == value
                && comparison.OperandWidth is null or 64
                && TryGetEdges(predecessor, comparison, branch,
                    OpCode.CheckNotEqual, OpCode.CheckEqual, out var nonNull, out _) && nonNull == block)
                return true;

            foreach (var instruction in instructions.Reverse())
            {
                if (instruction.Destination == value)
                    return false;
                if (instruction.NullCheckReceiver
                    && (instruction is { OpCode: OpCode.Call, Operands: [MethodAnalysisContext { IsStatic: false }, _, var receiver, ..] } && receiver == value
                        || instruction is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { IsStatic: false }, var voidReceiver, ..] } && voidReceiver == value))
                    return true;
            }
            block = predecessor;
        }
        return false;
    }

    private static bool IsOperation(Instruction instruction) => instruction.OpCode != OpCode.Nop;
}
