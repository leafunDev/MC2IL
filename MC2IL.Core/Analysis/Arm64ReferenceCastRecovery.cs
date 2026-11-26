using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64ReferenceCastRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is NewArmV8InstructionSet)
            Run(method.ControlFlowGraph!);
    }

    internal static void Run(ISILControlFlowGraph graph)
    {
        if (!graph.Blocks.Any(block => block.Instructions.Any(IsInvalidCastThrow)))
            return;

        var changed = false;
        foreach (var guard in graph.Blocks)
        {
            if (TryRecoverReceiverCast(graph, guard))
            {
                changed = true;
                continue;
            }

            if (guard.BlockType != BlockType.TwoWay
                || guard.Instructions.Where(IsOperation).ToArray() is not [.., var nullTest, var branch]
                || nullTest.Operands is not [LocalVariable, LocalVariable { Type: { } sourceType } value, Immediate { Value: 0 }]
                || !IlGenerator.IsManagedReference(sourceType)
                || !TryGetEdges(guard, nullTest, branch, out var nullEdge, out var nonNullEdge))
                continue;

            var check = SkipForwarders(nonNullEdge, guard, out var checkPredecessor);
            if (check.Predecessors is not [var entry] || entry != checkPredecessor
                || check.Instructions.Where(IsOperation).ToArray() is not [var load, var comparison, var conditional]
                || GetTargetType(load, comparison, value) is not { IsSealed: true } targetType
                || !IlGenerator.IsManagedReference(targetType)
                || !TryGetEdges(check, comparison, conditional, out var successEdge, out var failure)
                || failure.Instructions.Where(IsOperation).ToArray() is not [var thrown] || !IsInvalidCastThrow(thrown))
                continue;

            var storeBlock = SkipForwarders(nullEdge, guard, out var nullPredecessor);
            var storePath = storeBlock.Instructions.Where(IsOperation).ToArray();
            if (storePath is not [{ OpCode: OpCode.Move, Operands: [FieldReference { Parent: null } field, var stored] } store, ..]
                || stored != value || field.Field.FieldType != targetType)
                continue;

            var success = SkipForwarders(successEdge, check, out var successPredecessor);
            if (success == storeBlock)
            {
                if (storeBlock.Predecessors.Count != 2 || !storeBlock.Predecessors.Contains(nullPredecessor)
                    || !storeBlock.Predecessors.Contains(successPredecessor) || nullPredecessor == successPredecessor)
                    continue;
            }
            else if (storeBlock.Predecessors is not [var onlyNullPredecessor] || onlyNullPredecessor != nullPredecessor
                || !MatchesDuplicateStore(success, successPredecessor, failure, storeBlock, storePath, field, value, targetType))
            {
                continue;
            }

            var discarded = new HashSet<Instruction>(check.Instructions);
            // Replacing the guard branch also discards its comparison. After SSA removal,
            // that comparison may reuse the type check's condition local.
            discarded.Add(nullTest);
            discarded.Add(branch);
            if (success != storeBlock)
                discarded.UnionWith(success.Instructions);
            var discardedLocals = new HashSet<LocalVariable>(discarded.Select(i => i.Destination).OfType<LocalVariable>());
            if (graph.Instructions.Any(i => !discarded.Contains(i)
                    && DeadCodeEliminator.UsedLocals(i).Any(discardedLocals.Contains)))
                continue;

            // The cast accepts null and performs the same exact class check for a sealed target.
            store.OpCode = OpCode.CastClass;
            store.SetOperands(field, targetType, value);
            branch.OpCode = OpCode.Jump;
            branch.SetOperands(nullEdge);
            graph.RemoveEdge(guard, nonNullEdge);
            guard.CalculateBlockType();
            changed = true;
        }

        if (changed)
        {
            graph.RemoveUnreachableBlocks();
            DeadCodeEliminator.Run(graph);
        }
    }

    private static bool TryRecoverReceiverCast(ISILControlFlowGraph graph, Block guard)
    {
        if (guard.BlockType != BlockType.TwoWay
            || guard.Instructions.Where(IsOperation).ToArray() is not [.., var nullTest, var nullBranch]
            || nullTest.Operands is not [LocalVariable nullCondition, LocalVariable { Type: { } sourceType } value, Immediate { Value: 0 }]
            || !IlGenerator.IsManagedReference(sourceType)
            || !TryGetEdges(guard, nullTest, nullBranch, out var nullEdge, out var nonNullEdge)
            || SkipForwarders(nullEdge, guard, out _).Instructions.Where(IsOperation).ToArray() is not
                [{ OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }] }])
            return false;

        var check = SkipForwarders(nonNullEdge, guard, out var checkPredecessor);
        if (check.Predecessors is not [var entry] || entry != checkPredecessor
            || check.Instructions.Where(IsOperation).ToArray() is not
                [{ OpCode: OpCode.IsInst, Operands: [LocalVariable castResult, TypeAnalysisContext targetType, LocalVariable castSource] } cast,
                    var typeTest, var typeBranch]
            || castSource != value || castResult == value || !MetadataResolver.IsSameType(castResult.Type, targetType)
            || !IlGenerator.IsManagedReference(targetType)
            || typeTest.Operands is not [LocalVariable typeCondition, LocalVariable tested, Immediate { Value: 0 }] || tested != castResult
            || !TryGetEdges(check, typeTest, typeBranch, out var invalidEdge, out var successEdge)
            || SkipForwarders(invalidEdge, check, out _).Instructions.Where(IsOperation).ToArray() is not [var thrown]
            || !IsInvalidCastThrow(thrown))
            return false;

        var success = SkipForwarders(successEdge, check, out var successPredecessor);
        if (success.Predecessors is not [var successEntry] || successEntry != successPredecessor
            || success.Instructions.FirstOrDefault(IsOperation) is not { IsCall: true } call
            || call.Operands[0] is not MethodAnalysisContext { IsStatic: false, DeclaringType: { IsValueType: false } owner } method
            || !MetadataResolver.IsSameType(owner, targetType) || method.Name == ".ctor"
            || call.OpCode == OpCode.Call && call.Destination is not LocalVariable
            || !call.NullCheckReceiver && method.IsVirtual && !method.IsFinal && !owner.IsSealed)
            return false;

        var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
        if (call.Operands.Count <= receiverIndex + method.Parameters.Count || call.Operands[receiverIndex] != value
            || call.Operands.Skip(receiverIndex + 1).Take(method.Parameters.Count)
                .Any(argument => argument is not (LocalVariable or Immediate or StringLiteral or FloatLiteral or DoubleLiteral)))
            return false;

        // The native isinst result belongs only to the guard. Materialize a cast
        // at the call so it cannot affect the reader calls or other argument evaluation.
        if (graph.Instructions.Any(instruction => instruction != typeTest && instruction != typeBranch && instruction != nullBranch
                && DeadCodeEliminator.UsedLocals(instruction).Any(local => local == castResult || local == nullCondition || local == typeCondition)))
            return false;

        check.Instructions.Remove(cast);
        cast.OpCode = OpCode.CastClass;
        cast.OperandWidth = null;
        success.Instructions.Insert(success.Instructions.IndexOf(call), cast);
        call.SetOperand(receiverIndex, castResult);
        call.NullCheckReceiver = true;
        RemoveGuard(graph, guard, nullBranch, nonNullEdge, nullEdge);
        RemoveGuard(graph, check, typeBranch, successEdge, invalidEdge);
        return true;
    }

    private static void RemoveGuard(ISILControlFlowGraph graph, Block guard, Instruction branch, Block success, Block failure)
    {
        branch.OpCode = OpCode.Jump;
        branch.SetOperands(success);
        graph.RemoveEdge(guard, failure);
        guard.CalculateBlockType();
    }

    private static bool MatchesDuplicateStore(Block success, Block predecessor, Block failure, Block nullStore,
        Instruction[] nullPath, FieldReference field, LocalVariable value, TypeAnalysisContext targetType)
    {
        // IL2CPP can emit one store per arm and repeat the class comparison after the non-null store.
        return nullPath is [_] or [_, { OpCode: OpCode.Jump }]
            && nullStore.Successors is [var nullExit]
            && success.Predecessors is [var entry] && entry == predecessor
            && success.Instructions.Where(IsOperation).ToArray() is
                [{ OpCode: OpCode.Move, Operands: [FieldReference { Parent: null } written, var stored] },
                    var load, var compare, var branch]
            && written.Field == field.Field && written.Local == field.Local && written.Offset == field.Offset && stored == value
            && GetTargetType(load, compare, value) == targetType
            && TryGetEdges(success, compare, branch, out var successExit, out var repeatFailure)
            && repeatFailure == failure
            && SkipForwarders(successExit, success, out _) == SkipForwarders(nullExit, nullStore, out _);
    }

    private static TypeAnalysisContext? GetTargetType(Instruction load, Instruction compare, LocalVariable value)
    {
        if (load is not { OpCode: OpCode.Move, MemoryAccessSize: 8,
                Operands: [LocalVariable { Type: RuntimeClassTypeAnalysisContext } klass,
                    MemoryOperand { Base: LocalVariable instance, Index: null, Addend: 0, Scale: 0 }] }
            || instance != value)
            return null;

        return compare.Operands switch
        {
            [LocalVariable, var lhs, TypeAnalysisContext type] when lhs == klass => type,
            [LocalVariable, TypeAnalysisContext type, var rhs] when rhs == klass => type,
            _ => null,
        };
    }

    private static bool TryGetEdges(Block block, Instruction compare, Instruction branch,
        out Block equal, out Block different)
    {
        equal = different = null!;
        if (compare.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || compare.OperandWidth is not (null or 64)
            || branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, var test] }
            || compare.Destination != test || block.Successors.Count != 2 || !block.Successors.Contains(taken))
            return false;

        var fallthrough = block.Successors.First(successor => successor != taken);
        (equal, different) = compare.OpCode == OpCode.CheckEqual ? (taken, fallthrough) : (fallthrough, taken);
        return true;
    }

    private static Block SkipForwarders(Block block, Block previous, out Block predecessor)
    {
        // The limit also protects against malformed cyclic forwarding blocks.
        for (var hops = 0; hops < 8 && block.Predecessors is [var entry] && entry == previous
            && block.Successors is [var next] && block.Instructions.All(i => i.OpCode is OpCode.Nop or OpCode.Jump); hops++)
        {
            previous = block;
            block = next;
        }
        predecessor = previous;
        return block;
    }

    private static bool IsOperation(Instruction instruction) => instruction.OpCode != OpCode.Nop;

    private static bool IsInvalidCastThrow(Instruction instruction) =>
        instruction is { OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.InvalidCastException" }] };
}
