using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class ClassHierarchyTestRecovery
{
    private const int HierarchyDepthOffset = 0x12C;
    private const int HierarchyOffset = 0xC8;
    private const int PointerSize = 8;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext is not { MetadataVersion: 29, InstructionSet: NewArmV8InstructionSet })
            return;

        var graph = method.ControlFlowGraph!;
        foreach (var block in graph.Blocks)
            if (block.BlockType == BlockType.TwoWay)
                RecoverGuard(graph, block);
    }

    private static void RecoverGuard(ISILControlFlowGraph graph, Block depthBlock)
    {
        var depthInstructions = depthBlock.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
        if (depthInstructions is not [var objectClassLoad, var targetClassLoad, var objectDepthLoad,
                var targetDepthLoad, var depthComparison, var depthBranch]
            || LoadBase(objectClassLoad, 0, PointerSize) is not { Type: { } receiverType } receiver
            || !IlGenerator.IsManagedReference(receiverType)
            || targetClassLoad is not { OpCode: OpCode.Move,
                Operands: [LocalVariable targetClass, TypeAnalysisContext targetType] }
            || targetClassLoad.MemoryAccessSize is not (null or PointerSize)
            || targetType is not { Definition: not null, IsInterface: false, IsValueType: false, GenericParameters.Count: 0 }
            || targetType.IsDelegate
            || !ReferenceEquals(LoadBase(objectDepthLoad, HierarchyDepthOffset, 1), objectClassLoad.Destination)
            || !ReferenceEquals(LoadBase(targetDepthLoad, HierarchyDepthOffset, 1), targetClass)
            || targetDepthLoad.Destination is not LocalVariable targetDepth
            || depthComparison is not { OpCode: OpCode.CheckLessUnsigned or OpCode.CheckGreaterOrEqualUnsigned, OperandWidth: 32,
                Operands: [LocalVariable depthCondition, LocalVariable actualDepth, LocalVariable requiredDepth] }
            || actualDepth != objectDepthLoad.Destination || requiredDepth != targetDepth
            || depthBranch is not { OpCode: OpCode.ConditionalJump, Operands: [Block depthBranchTarget, LocalVariable depthTest] }
            || depthTest != depthCondition || depthBlock.Successors.Count != 2 || !depthBlock.Successors.Contains(depthBranchTarget))
            return;

        // Native checks can be emitted with either polarity: branch on depth < target
        // to the failure block, or branch on depth >= target to the membership test.
        var branchTargetsMembership = depthComparison.OpCode == OpCode.CheckGreaterOrEqualUnsigned;
        var membershipBlock = branchTargetsMembership
            ? depthBranchTarget
            : depthBlock.Successors.First(block => block != depthBranchTarget);
        var failure = branchTargetsMembership
            ? depthBlock.Successors.First(block => block != depthBranchTarget)
            : depthBranchTarget;
        var membershipInstructions = membershipBlock.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
        if (membershipBlock.Predecessors is not [var predecessor] || predecessor != depthBlock
            || membershipBlock.Successors.Count != 2
            || membershipInstructions is not [var hierarchyLoad, var scaleDepth, var addAddress,
                var ancestorLoad, var compareClass, var membershipBranch]
            || !ReferenceEquals(LoadBase(hierarchyLoad, HierarchyOffset, PointerSize), objectClassLoad.Destination)
            || scaleDepth is not { OpCode: OpCode.ShiftLeft, OperandWidth: 64,
                Operands: [LocalVariable scaled, LocalVariable depth, Immediate { Value: 3 }] } || depth != targetDepth
            || addAddress is not { OpCode: OpCode.Add, OperandWidth: null or 64,
                Operands: [LocalVariable ancestorAddress, LocalVariable hierarchy, LocalVariable offset] }
            || hierarchy != hierarchyLoad.Destination || offset != scaled
            || LoadBase(ancestorLoad, -PointerSize, PointerSize) != ancestorAddress
            || compareClass is not { OpCode: OpCode.CheckNotEqual, OperandWidth: null or 64,
                Operands: [LocalVariable condition, LocalVariable ancestor, var comparedType] }
            || ancestor != ancestorLoad.Destination
            || !(ReferenceEquals(comparedType, targetClass)
                || comparedType is TypeAnalysisContext compared && MetadataResolver.IsSameType(compared, targetType))
            || membershipBranch is not { OpCode: OpCode.ConditionalJump, Operands: [Block miss, LocalVariable classTest] }
            || !membershipBlock.Successors.Contains(miss) || classTest != condition || !HasCheckedReceiver(depthBlock, receiver))
            return;

        var replaced = new HashSet<Instruction>(depthInstructions.Concat(membershipInstructions));
        var nativeLocals = new HashSet<LocalVariable>(replaced.Select(i => i.Destination).OfType<LocalVariable>());
        if (graph.Instructions.Any(i => !replaced.Contains(i)
                && (DeadCodeEliminator.UsedLocals(i).Any(nativeLocals.Contains)
                    || i.Operands.Any(o => o is AddressOf address && ReferenceEquals(address.Target, receiver)))))
            return;

        // A class is assignable when its hierarchy is deep enough and the ancestor
        // at targetDepth - 1 matches the target. Emit both conditions as one isinst.
        var match = new LocalVariable("matchedType", ancestor.Register, targetType);
        compareClass.OpCode = OpCode.CheckEqual;
        compareClass.OperandWidth = null;
        compareClass.SetOperands(condition, match, new Immediate(0));
        membershipBlock.Instructions = [new Instruction(-1, OpCode.IsInstance, [match, receiver, targetType]),
            compareClass, membershipBranch];

        depthBranch.OpCode = OpCode.Jump;
        depthBranch.SetOperands(membershipBlock);
        depthBlock.Instructions = [depthBranch];
        graph.RemoveEdge(depthBlock, failure);
        depthBlock.CalculateBlockType();

        var success = membershipBlock.Successors.First(block => block != failure);
        if (success.Predecessors is [var test] && test == membershipBlock)
            foreach (var instruction in success.Instructions)
            {
                if (ReferenceEquals(instruction.Destination, receiver))
                    break;
                if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, LocalVariable value] }
                    && value == receiver && MetadataResolver.IsSameType(field.Field.FieldType, targetType))
                    instruction.SetOperand(1, match);
            }
    }

    private static LocalVariable? LoadBase(Instruction instruction, long offset, int size) =>
        instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, MemoryOperand
            { Base: LocalVariable source, Index: null, Scale: 0, Addend: var displacement }] }
        && instruction.MemoryAccessSize == size && displacement == offset ? source : null;

    private static bool HasCheckedReceiver(Block block, LocalVariable receiver)
    {
        var visited = new HashSet<Block> { block };
        while (block.Predecessors is [var predecessor] && visited.Add(predecessor))
        {
            // A preceding callvirt on this same value already throws for null.
            // A later assignment to the receiver invalidates that proof.
            for (var i = predecessor.Instructions.Count - 1; i >= 0; i--)
            {
                var instruction = predecessor.Instructions[i];
                if (ReferenceEquals(instruction.Destination, receiver))
                    return false;
                if (ReferenceEquals(instruction, predecessor.Instructions.LastOrDefault())
                    && IsNonNullBranchEdge(predecessor, block, receiver))
                    return true;
                if (!instruction.NullCheckReceiver || !instruction.IsCall
                    || instruction.Operands[0] is not MethodAnalysisContext { IsStatic: false })
                    continue;
                var receiverIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
                if (receiverIndex < instruction.Operands.Count && ReferenceEquals(instruction.Operands[receiverIndex], receiver))
                    return true;
            }
            block = predecessor;
        }
        return false;
    }

    private static bool IsNonNullBranchEdge(Block predecessor, Block continuation, LocalVariable receiver)
    {
        if (predecessor.Successors.Count != 2
            || !predecessor.Successors.Contains(continuation)
            || predecessor.Instructions.LastOrDefault() is not
                { OpCode: OpCode.ConditionalJump, Operands: [Block trueTarget, LocalVariable condition] })
            return false;

        var comparison = predecessor.Instructions.Take(predecessor.Instructions.Count - 1)
            .LastOrDefault(instruction => ReferenceEquals(instruction.Destination, condition));
        if (comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                Operands: [_, var left, var right] })
            return false;

        var comparesReceiverToZero = ReferenceEquals(left, receiver) && IsZero(right)
            || ReferenceEquals(right, receiver) && IsZero(left);
        if (!comparesReceiverToZero)
            return false;

        var branchIsTaken = ReferenceEquals(trueTarget, continuation);
        var comparisonIsTrueForNonNull = comparison.OpCode == OpCode.CheckNotEqual;
        return branchIsTaken == comparisonIsTrueForNonNull;
    }

    private static bool IsZero(IOperand operand) => operand is Immediate { Value: 0 } or DefaultValue;
}
