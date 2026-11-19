using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// Remove null and bounds checks which are explicit in il2cpp but implicit in IL
public static class InjectedCheckRemover
{
    public static void Run(MethodAnalysisContext method) => Run(method, true, true);

    public static void Run(MethodAnalysisContext method, bool removeNullChecks, bool removeBoundsChecks) =>
        Run(method.ControlFlowGraph!, removeNullChecks, removeBoundsChecks, method.ParameterLocals);

    public static void Run(ISILControlFlowGraph cfg) => Run(cfg, true, true);

    private static void Run(ISILControlFlowGraph cfg, bool removeNullChecks, bool removeBoundsChecks,
        IEnumerable<LocalVariable>? parameters = null)
    {
        var defOf = BuildDefMap(cfg, parameters, out var stableParameters);
        var ignoredExceptionConstructions = FindUnusedExceptionConstructions(cfg);
        var removedAny = false;

        foreach (var block in cfg.Blocks)
        {
            if (block.BlockType != BlockType.TwoWay || block.Instructions.Count == 0)
                continue;

            var terminator = block.Instructions[^1];

            if (terminator.OpCode != OpCode.ConditionalJump)
                continue;

            if (terminator.Operands[0] is not Block trueTarget)
                continue;

            var falseTarget = block.Successors.FirstOrDefault(successor => successor != trueTarget);
            if (falseTarget == null)
                continue;

            var trueThrow = GetInjectedThrow(trueTarget, ignoredExceptionConstructions);
            var falseThrow = GetInjectedThrow(falseTarget, ignoredExceptionConstructions);

            // The same null guard can branch to the throw on either the true edge or the
            // fallthrough edge, depending on how the native compare/branch pair was lifted.
            // If both edges throw (or neither does), there is no safe check to remove.
            if ((trueThrow == null) == (falseThrow == null))
                continue;

            var throwOnTrue = trueThrow != null;
            var throwTarget = throwOnTrue ? trueTarget : falseTarget!;
            var thrown = throwOnTrue ? trueThrow! : falseThrow!;

            if (terminator.Operands[1] is not LocalVariable condition
                || !defOf.TryGetValue(condition, out var definition)
                || !IsInjectedCheck(block, definition, thrown, throwOnTrue, defOf, stableParameters, throwOnTrue ? falseTarget : trueTarget,
                    removeNullChecks, removeBoundsChecks))
                continue;

            RemoveThrowEdge(block, throwTarget, throwOnTrue ? falseTarget : trueTarget);
            removedAny = true;
        }

        if (!removedAny)
            return;

        // delete any throw blocks
        cfg.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(cfg);
    }

    private static void RemoveThrowEdge(Block block, Block throwTarget, Block continuation)
    {
        var terminator = block.Instructions[^1];
        terminator.OpCode = OpCode.Jump;
        terminator.SetOperands(continuation);
        block.Successors.Remove(throwTarget);
        throwTarget.Predecessors.Remove(block);
        block.CalculateBlockType();
    }

    private static bool IsInjectedCheck(Block checkedBlock, Instruction definition, InjectedThrow thrown, bool throwOnTrue,
        Dictionary<LocalVariable, Instruction> defOf, HashSet<LocalVariable> stableParameters, Block continuation,
        bool removeNullChecks, bool removeBoundsChecks)
    {
        if (IsNullComparison(definition, out var receiver) && IsNullEdge(definition.OpCode, throwOnTrue)
            && (thrown.ExceptionType == "System.NullReferenceException"
                || thrown.Operand != null && SameValue(receiver, thrown.Operand, defOf, [])
                || thrown.ExceptionType == "System.IndexOutOfRangeException" && removeBoundsChecks))
        {
            var knownNonNullThis = thrown.ExceptionType == "System.NullReferenceException"
                ? GetCheckedReceiverOwner(checkedBlock, definition, receiver, defOf)
                : null;

            // Some IL2CPP builds share the failure block between the null and length checks.
            return (removeNullChecks || thrown.ExceptionType == "System.IndexOutOfRangeException")
                && (HasImplicitNullCheck(continuation, receiver, defOf,
                        arraysOnly: thrown.ExceptionType == "System.IndexOutOfRangeException",
                        allowFloatComputations: thrown.ExceptionType == "System.NullReferenceException" && receiver is LocalVariable,
                        knownNonNullThis: knownNonNullThis)
                    || thrown.ExceptionType == "System.NullReferenceException"
                    && (IsObjectAllocationCheck(checkedBlock, definition, continuation, receiver, defOf)
                        || HasEarlierNonNullCheck(checkedBlock, definition, receiver, defOf, stableParameters)));
        }

        return removeBoundsChecks && thrown.ExceptionType == "System.IndexOutOfRangeException"
            && IsArrayBoundsCheck(definition, throwOnTrue, continuation, defOf, removeNullChecks);
    }

    private static bool IsObjectAllocationCheck(Block checkedBlock, Instruction comparison, Block continuation,
        IOperand receiver, Dictionary<LocalVariable, Instruction> defOf)
    {
        // newobj either returns an instance or throws. Only remove IL2CPP's check of the
        // immediately preceding allocation when the surviving path initializes that object.
        if (receiver is not LocalVariable local || !defOf.TryGetValue(local, out var allocation)
            || allocation is not { OpCode: OpCode.Newobj, Operands: [_, TypeAnalysisContext { IsValueType: false } allocatedType] })
            return false;

        // The allocation helper ends its native basic block. The null test can be the
        // first operation of the next block, provided that edge has no alternate entry.
        if (!ReferenceEquals(InstructionsBefore(checkedBlock, comparison)
                .FirstOrDefault(instruction => instruction.OpCode is not (OpCode.Nop or OpCode.Jump)), allocation))
            return false;

        if (FirstManagedOperation(continuation, defOf, out var singleEntryPath) is not { } constructorCall
            || !singleEntryPath
            || !constructorCall.IsCall || constructorCall.Operands[0] is not MethodAnalysisContext
                { Name: ".ctor", IsStatic: false, DeclaringType: { } owner } constructor)
            return false;

        var receiverIndex = constructorCall.OpCode == OpCode.CallVoid ? 1 : 2;
        if (constructorCall.Operands.Count < receiverIndex + 1 + constructor.Parameters.Count
            || !ReferenceEquals(constructorCall.Operands[receiverIndex], local))
            return false;

        if (!IlGenerator.IsDelegateType(allocatedType))
            return MetadataResolver.IsSameType(owner, allocatedType);

        return IlGenerator.IsDelegateConstructor(constructor) && IlGenerator.MatchesDelegateAllocation(constructor, allocatedType)
            && IlGenerator.ResolveDelegateMethodOperand(constructorCall.Operands[receiverIndex + 2],
                pointer => defOf.TryGetValue(pointer, out var def) ? def : null, []) != null;
    }

    private static bool HasEarlierNonNullCheck(Block block, Instruction comparison, IOperand receiver,
        Dictionary<LocalVariable, Instruction> defOf, HashSet<LocalVariable> stableParameters)
    {
        if (comparison.Operands.Count != 3
            || receiver is not LocalVariable { Type: { } type } local || !IlGenerator.IsManagedReference(type)
            || !defOf.ContainsKey(local) && !stableParameters.Contains(local))
            return false;

        var index = block.Instructions.IndexOf(comparison);
        if (index < 0)
            return false;

        // Follow only paths with no alternate entry to the current guard. A reload,
        // assignment, or addressable local can invalidate an earlier null check.
        var visited = new HashSet<Block>();
        for (var remaining = 12; remaining > 0 && visited.Add(block); remaining--)
        {
            for (var i = index - 1; i >= 0; i--)
                if (ReferenceEquals(block.Instructions[i].Destination, local))
                    return false;

            if (block.Predecessors is not [var previous] || visited.Contains(previous))
                return false;

            if (previous.Successors.Count == 2 && previous.Successors.Contains(block)
                && previous.Instructions.LastOrDefault() is
                    { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] }
                && previous.Successors.Contains(taken)
                && IsNonNullEdge(previous, condition, taken == block, local, defOf))
                return true;

            block = previous;
            index = block.Instructions.Count;
        }

        return false;
    }

    private static bool IsNonNullEdge(Block block, LocalVariable condition, bool truth, LocalVariable receiver,
        Dictionary<LocalVariable, Instruction> defOf)
    {
        // Evaluate definitions in this block in execution order. Never infer a fact
        // from another branch or from a receiver overwritten after the comparison.
        for (var i = block.Instructions.Count - 2; i >= 0; i--)
        {
            var instruction = block.Instructions[i];
            if (ReferenceEquals(instruction.Destination, receiver))
                return false;

            if (!defOf.TryGetValue(condition, out var definition) || instruction != definition)
                continue;

            if (instruction is { OpCode: OpCode.Not, Operands: [_, LocalVariable { Type.FullName: "System.Boolean" } inner] })
            {
                condition = inner;
                truth = !truth;
                continue;
            }

            if (instruction.Operands.Count == 3
                && IsNullComparison(instruction, out var compared) && ReferenceEquals(compared, receiver))
                return !IsNullEdge(instruction.OpCode, truth);

            if (instruction is { OpCode: OpCode.Call, Operands: [MethodAnalysisContext called, _, var left, ..] })
            {
                if (EqualityBranchInverter.IsUnityObjectBooleanConversion(called))
                    return truth && ReferenceEquals(left, receiver);
                if (EqualityBranchInverter.IsUnityObjectComparison(called) && instruction.Operands.Count == 4)
                {
                    var right = instruction.Operands[3];
                    return truth == (called.Name == "op_Inequality")
                        && (ReferenceEquals(left, receiver) && right is Immediate { Value: 0 }
                            || ReferenceEquals(right, receiver) && left is Immediate { Value: 0 });
                }
            }

            return false;
        }

        return false;
    }

    private static LocalVariable? GetCheckedReceiverOwner(Block block, Instruction comparison, IOperand receiver,
        Dictionary<LocalVariable, Instruction> defOf)
    {
        if (receiver is not LocalVariable local || !defOf.ContainsKey(local))
            return null;

        // A previous field read on the same path proves the reference in the this slot
        // nonnull even when the tested local came from a call, rather than from that read.
        foreach (var instruction in InstructionsBefore(block, comparison))
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference
                { Parent: null, Local: { IsThis: true, Type.IsValueType: false } owner, Field.IsStatic: false }] })
                return owner;

        return null;
    }

    private static IEnumerable<Instruction> InstructionsBefore(Block block, Instruction instruction)
    {
        var index = block.Instructions.IndexOf(instruction);
        if (index < 0)
            yield break;

        var visited = new HashSet<Block>();
        // Looking further is optional. Bound the work per guard in very large methods.
        for (var remaining = 12; remaining > 0 && visited.Add(block); remaining--)
        {
            for (var i = index - 1; i >= 0; i--)
                yield return block.Instructions[i];

            if (block.Predecessors is not [var predecessor] || predecessor.BlockType == BlockType.Entry)
                yield break;

            block = predecessor;
            index = block.Instructions.Count;
        }
    }

    private static bool HasImplicitNullCheck(Block continuation, IOperand receiver,
        Dictionary<LocalVariable, Instruction> defOf, bool arraysOnly, bool allowFloatComputations,
        LocalVariable? knownNonNullThis)
    {
        if (FirstManagedOperation(continuation, defOf, out var singleEntryPath, allowFloatComputations, knownNonNullThis) is not { } operation)
            return false;

        var operand = FirstAccess(operation);
        if (operand is ArrayLength length && SameValue(receiver, length.Array, defOf, [])
            || AsArrayAccess(operand) is { } access && SameValue(receiver, access.Array, defOf, []))
            return true;

        if (arraysOnly)
            return false;

        if ((operand is AddressOf { Target: FieldReference addressedField } ? addressedField : operand)
            is FieldReference { Field.IsStatic: false } field
            && SameValue(receiver, field.Local, defOf, []))
            return true;

        // A direct CIL call can enter a nonvirtual instance method on a null receiver. Use
        // callvirt for its null check only when it cannot change the method being invoked.
        if (!singleEntryPath || !operation.IsCall
            || operation.Operands[0] is not MethodAnalysisContext { IsStatic: false, DeclaringType: { IsValueType: false } owner } target
            || target.Name == ".ctor"
            || !operation.NullCheckReceiver && target.IsVirtual && !target.IsFinal && !owner.IsSealed)
            return false;

        var receiverIndex = operation.OpCode == OpCode.CallVoid ? 1 : 2;
        if (operation.Operands.Count <= receiverIndex
            || !SameValue(receiver, operation.Operands[receiverIndex], defOf, [])
            || operation.Operands.Skip(receiverIndex + 1).Take(target.Parameters.Count)
                .Any(arg => !IsSimpleValue(arg) && !IsSafeFieldRead(arg, knownNonNullThis)))
            return false;

        operation.NullCheckReceiver = true;
        return true;
    }

    private static Instruction? FirstManagedOperation(Block continuation,
        Dictionary<LocalVariable, Instruction> defOf, out bool singleEntryPath, bool allowFloatComputations = false,
        LocalVariable? knownNonNullThis = null)
    {
        var visited = new HashSet<Block>();
        var current = continuation;
        var crossedArgumentEvaluation = false;
        singleEntryPath = true;
        while (visited.Add(current))
        {
            singleEntryPath &= current.Predecessors.Count == 1;
            if (crossedArgumentEvaluation && !singleEntryPath)
                return null;
            foreach (var instruction in current.Instructions)
            {
                if (instruction.OpCode is OpCode.Nop or OpCode.Jump)
                    continue;
                if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, var source] }
                    && defOf.ContainsKey(destination) && (IsSimpleValue(source) || source is RuntimeMethodInfoAnalysisContext))
                    continue;
                if (singleEntryPath && (allowFloatComputations && IsNonThrowingFloatComputation(instruction, defOf)
                    || instruction is { OpCode: OpCode.Move, Operands: [LocalVariable fieldValue, var fieldSource] }
                    && defOf.ContainsKey(fieldValue) && IsSafeFieldRead(fieldSource, knownNonNullThis)))
                {
                    crossedArgumentEvaluation = true;
                    continue;
                }
                return instruction;
            }

            if (current.Successors is not [var next])
                break;
            current = next;
        }

        return null;
    }

    private static bool IsNonThrowingFloatComputation(Instruction instruction,
        Dictionary<LocalVariable, Instruction> defOf)
    {
        // The lifter turns FABS into a call for C# output. An actual managed call, even one
        // with a similar name, might have side effects and must stop the search.
        return instruction switch
        {
            { OpCode: OpCode.Call, IsLiftedFloatAbs: true, Operands: [MethodAnalysisContext { IsStatic: true },
                LocalVariable destination, var input] } => defOf.ContainsKey(destination) && IsFloatValue(input),
            { OpCode: OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide,
                Operands: [LocalVariable destination, var left, var right, FloatingPointPrecision] } =>
                defOf.ContainsKey(destination) && IsFloatValue(left) && IsFloatValue(right),
            { OpCode: OpCode.PackFloatAggregate, Destination: LocalVariable destination } =>
                defOf.ContainsKey(destination) && FloatAggregateRecovery.IsLiteralPack(instruction),
            _ => false,
        };
    }

    private static bool IsFloatValue(IOperand operand) => operand is FloatLiteral or DoubleLiteral
        or LocalVariable { Type.FullName: "System.Single" or "System.Double" };

    private static bool IsSimpleValue(IOperand operand) =>
        operand is LocalVariable or Immediate or StringLiteral or FloatLiteral or DoubleLiteral;

    private static bool IsSafeFieldRead(IOperand operand, LocalVariable? knownNonNullThis) =>
        knownNonNullThis != null
        && operand is FieldReference { Parent: null, Field.IsStatic: false } field
        && ReferenceEquals(field.Local, knownNonNullThis)
        && field.Field.FieldType is not CustomModifierTypeAnalysisContext;

    private static IOperand? FirstAccess(Instruction operation)
    {
        if (operation is { OpCode: OpCode.Move, Operands: [var destination, var source] })
            // Stores evaluate the value before the destination address.
            return IsSimpleValue(source) ? destination : source;

        if (operation is { OpCode: OpCode.Return, Operands: [var value] })
            return value;

        if (operation.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned
            && operation.Operands is [_, var left, var right])
            return IsSimpleValue(left) ? right : left;

        if (operation.IsCall && operation.Operands[0] is MethodAnalysisContext target)
        {
            var start = operation.OpCode == OpCode.CallVoid ? 1 : 2;
            return operation.Operands.Skip(start).Take(target.Parameters.Count + (target.IsStatic ? 0 : 1))
                .FirstOrDefault(arg => !IsSimpleValue(arg));
        }

        return null;
    }

    private static ArrayAccess? AsArrayAccess(IOperand? operand) => operand switch
    {
        ArrayAccess access => access,
        AddressOf { Target: ArrayAccess access } => access,
        _ => null,
    };

    // Only erase a bounds guard after array recovery has produced both its length and the
    // corresponding element access. Otherwise the generated IL would have no operation left
    // to enforce the same bounds check.
    private static bool IsArrayBoundsCheck(Instruction definition, bool throwOnTrue, Block continuation,
        Dictionary<LocalVariable, Instruction> defOf, bool removeNullChecks)
    {
        if (definition.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned
            || definition.Operands.Count < 3)
            return false;

        var operation = FirstManagedOperation(continuation, defOf, out _);
        var access = operation != null ? AsArrayAccess(FirstAccess(operation)) : null;
        var receiverGuard = access == null && removeNullChecks ? MatchGuardedArrayArgument(continuation) : null;
        access ??= receiverGuard?.Access;
        if (access == null)
            return false;

        for (var lengthIndex = 1; lengthIndex <= 2; lengthIndex++)
        {
            var index = definition.Operands[3 - lengthIndex];
            if (TryGetArrayLength(definition.Operands[lengthIndex], defOf, [], out var array)
                && SameValue(access.Array, array, defOf, [])
                && SameValue(access.Index, index, defOf, [])
                && IsBoundsFailure(definition.OpCode, lengthIndex == 1, throwOnTrue,
                    access.Index is Immediate { Value: 0 }))
            {
                if (receiverGuard is { } receiver)
                {
                    receiver.Call.NullCheckReceiver = true;
                    RemoveThrowEdge(continuation, receiver.Failure, receiver.Continuation);
                }
                return true;
            }
        }

        return false;
    }

    private sealed record GuardedArrayArgument(ArrayAccess Access, Instruction Call, Block Failure, Block Continuation);

    private static GuardedArrayArgument? MatchGuardedArrayArgument(Block guard)
    {
        // IL2CPP can check the array bounds, load a receiver from this, check the receiver,
        // and only then load the previously checked element. The equivalent CIL loads the
        // argument first (checking bounds), then saves the receiver null check in callvirt.
        if (guard.Predecessors.Count != 1 || guard.Successors.Count != 2
            || guard.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray() is not
                [ { OpCode: OpCode.Move, Operands: [LocalVariable receiver,
                      FieldReference { Parent: null, Local.IsThis: true, Field.IsStatic: false }] },
                  var comparison,
                  { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] } ]
            || !ReferenceEquals(comparison.Destination, condition)
            || !IsNullComparison(comparison, out var tested) || !ReferenceEquals(receiver, tested))
            return null;

        var other = guard.Successors.FirstOrDefault(block => block != taken);
        if (other == null || !guard.Successors.Contains(taken))
            return null;
        var failure = comparison.OpCode == OpCode.CheckEqual ? taken : other;
        var continuation = failure == taken ? other : taken;
        if (GetInjectedThrow(failure, []) is not { ExceptionType: "System.NullReferenceException" }
            || continuation.Predecessors.Count != 1
            || continuation.Instructions.Where(i => i.OpCode != OpCode.Nop).Take(2).ToArray() is not
                [ { OpCode: OpCode.Move, Operands: [LocalVariable argument, ArrayAccess access] }, var call ]
            || !call.IsCall || call.Operands[0] is not MethodAnalysisContext
                { IsStatic: false, DeclaringType: { IsValueType: false } owner, Parameters.Count: 1 } target
            || target.Name == ".ctor" || !call.NullCheckReceiver && target.IsVirtual && !target.IsFinal && !owner.IsSealed)
            return null;

        var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
        return call.Operands.Count > receiverIndex + 1
            && ReferenceEquals(call.Operands[receiverIndex], receiver)
            && ReferenceEquals(call.Operands[receiverIndex + 1], argument)
                ? new GuardedArrayArgument(access, call, failure, continuation) : null;
    }

    private static bool IsBoundsFailure(OpCode comparison, bool lengthOnLeft, bool throwOnTrue, bool indexIsZero)
    {
        // For array[0], an equality comparison with zero is equivalent to the bounds check.
        if (indexIsZero && comparison is OpCode.CheckEqual or OpCode.CheckNotEqual)
            return comparison == OpCode.CheckEqual ? throwOnTrue : !throwOnTrue;

        return (lengthOnLeft, throwOnTrue) switch
        {
            (true, true) => comparison == OpCode.CheckLessOrEqualUnsigned,
            (true, false) => comparison == OpCode.CheckGreaterUnsigned,
            (false, true) => comparison == OpCode.CheckGreaterOrEqualUnsigned,
            _ => comparison == OpCode.CheckLessUnsigned,
        };
    }

    private static bool TryGetArrayLength(IOperand operand, Dictionary<LocalVariable, Instruction> defOf,
        HashSet<LocalVariable> seen, out LocalVariable array)
    {
        if (operand is ArrayLength length)
        {
            array = length.Array;
            return true;
        }

        if (operand is LocalVariable local && seen.Add(local)
            && defOf.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands: [_, var source] }
            && source is IOperand sourceOperand
            && TryGetArrayLength(sourceOperand, defOf, seen, out array))
            return true;

        array = null!;
        return false;
    }

    private static bool IsNullComparison(Instruction instruction, out IOperand testedOperand)
    {
        testedOperand = null!;
        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || instruction.Operands.Count < 3)
            return false;

        var leftIsNull = instruction.Operands[1] is Immediate { Value: 0 };
        var rightIsNull = instruction.Operands[2] is Immediate { Value: 0 };
        if (leftIsNull == rightIsNull)
            return false;

        testedOperand = leftIsNull ? instruction.Operands[2] : instruction.Operands[1];
        return testedOperand is LocalVariable or FieldReference or MemoryOperand or Register;
    }

    private static bool IsNullEdge(OpCode comparison, bool throwOnTrue) =>
        comparison == OpCode.CheckEqual ? throwOnTrue : !throwOnTrue;

    private static bool SameValue(IOperand left, IOperand right,
        Dictionary<LocalVariable, Instruction> defOf, HashSet<LocalVariable> seen)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is Immediate leftImmediate && right is Immediate rightImmediate)
            return leftImmediate == rightImmediate;

        // A checked field is often first loaded into a temporary, while the throw block
        // contains a separately materialized FieldReference for the same receiver/field.
        if (left is LocalVariable local && seen.Add(local)
            && defOf.TryGetValue(local, out var definition)
            && definition.OpCode == OpCode.Move && definition.Operands.Count == 2
            && IsSimpleValue(definition.Operands[1]))
            return SameValue(definition.Operands[1], right, defOf, seen);

        if (right is LocalVariable otherLocal && seen.Add(otherLocal)
            && defOf.TryGetValue(otherLocal, out var otherDefinition)
            && otherDefinition.OpCode == OpCode.Move && otherDefinition.Operands.Count == 2
            && IsSimpleValue(otherDefinition.Operands[1]))
            return SameValue(left, otherDefinition.Operands[1], defOf, seen);

        if (left is FieldReference leftField && right is FieldReference rightField)
            return SameField(leftField, rightField, defOf, seen);

        if (left is MemoryOperand leftMemory && right is MemoryOperand rightMemory)
            return leftMemory.Addend == rightMemory.Addend && leftMemory.Scale == rightMemory.Scale
                && SameOptionalValue(leftMemory.Base, rightMemory.Base, defOf, seen)
                && SameOptionalValue(leftMemory.Index, rightMemory.Index, defOf, seen);

        return false;
    }

    private static bool SameField(FieldReference left, FieldReference right,
        Dictionary<LocalVariable, Instruction> defOf, HashSet<LocalVariable> seen)
    {
        var sameField = ReferenceEquals(left.Field, right.Field)
            || left.Field.Token != 0 && left.Field.Token == right.Field.Token
                && left.Field.DeclaringType.FullName == right.Field.DeclaringType.FullName
                && left.Field.DeclaringType.DeclaringAssembly.Name == right.Field.DeclaringType.DeclaringAssembly.Name;
        if (!sameField || left.Offset != right.Offset)
            return false;

        if (left.Parent == null || right.Parent == null)
            return left.Parent == null && right.Parent == null
                && SameValue(left.Local, right.Local, defOf, seen);

        return SameField(left.Parent, right.Parent, defOf, seen);
    }

    private static bool SameOptionalValue(IOperand? left, IOperand? right,
        Dictionary<LocalVariable, Instruction> defOf, HashSet<LocalVariable> seen) =>
        left == null || right == null ? left == null && right == null : SameValue(left, right, defOf, seen);

    private sealed record InjectedThrow(string? ExceptionType, IOperand? Operand);

    // Exception checks can share a native block with a dead allocation of another exception
    // type. Ignore only an unused parameterless NullReferenceException/IndexOutOfRangeException
    // construction; any object that is returned, forwarded, or thrown remains observable here.
    private static HashSet<Instruction> FindUnusedExceptionConstructions(ISILControlFlowGraph cfg)
    {
        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in cfg.Instructions)
        {
            foreach (var local in DeadCodeEliminator.UsedLocals(instruction))
            {
                if (!uses.TryGetValue(local, out var consumers))
                    uses[local] = consumers = [];
                consumers.Add(instruction);
            }
        }

        var ignored = new HashSet<Instruction>();
        var instructions = cfg.Instructions;
        foreach (var allocation in instructions)
        {
            if (allocation.OpCode != OpCode.Newobj
                || allocation.Operands is not [LocalVariable result,
                    TypeAnalysisContext { FullName: "System.NullReferenceException" or "System.IndexOutOfRangeException" } allocatedType])
                continue;

            if (!uses.TryGetValue(result, out var consumers) || consumers.Count == 0)
            {
                ignored.Add(allocation);
                continue;
            }

            if (consumers.Count != 1
                || consumers[0] is not { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext
                    { Name: ".ctor", Parameters.Count: 0, DeclaringType: { } constructorType }, _, ..] } constructorCall
                || constructorType.FullName != allocatedType.FullName
                || !ReferenceEquals(constructorCall.Operands[1], result))
                continue;

            ignored.Add(allocation);
            ignored.Add(constructorCall);
        }

        return ignored;
    }

    // Describes the throw performed by a block containing no other observable operation.
    private static InjectedThrow? GetInjectedThrow(Block block, HashSet<Instruction> ignoredExceptionConstructions)
    {
        InjectedThrow? thrown = null;

        foreach (var instruction in block.Instructions)
        {
            if (ignoredExceptionConstructions.Contains(instruction))
                continue;

            switch (instruction.OpCode)
            {
                case OpCode.Nop or OpCode.Interrupt:
                case OpCode.Return when thrown != null:
                    continue;

                case OpCode.Throw when thrown == null
                    && instruction.Operands is [TypeAnalysisContext { FullName: "System.NullReferenceException" or "System.IndexOutOfRangeException" } exception]:
                    thrown = new InjectedThrow(exception.FullName, null);
                    continue;

                case OpCode.Throw when thrown == null
                    && instruction.Operands is [LocalVariable or FieldReference or MemoryOperand or Register]:
                    thrown = new InjectedThrow(null, instruction.Operands.First());
                    continue;

                default:
                    return null;
            }
        }

        return thrown;
    }

    private static Dictionary<LocalVariable, Instruction> BuildDefMap(ISILControlFlowGraph cfg,
        IEnumerable<LocalVariable>? parameters, out HashSet<LocalVariable> stableParameters)
    {
        var defs = new Dictionary<LocalVariable, Instruction>();
        stableParameters = parameters != null ? new HashSet<LocalVariable>(parameters) : [];
        var unstable = new HashSet<LocalVariable>(stableParameters);

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.Destination is LocalVariable local)
            {
                stableParameters.Remove(local);
                if (defs.ContainsKey(local))
                    unstable.Add(local);
                else
                    defs.Add(local, instruction);
            }
            foreach (var operand in instruction.Operands)
            {
                if (operand is AddressOf { Target: LocalVariable addressed })
                {
                    unstable.Add(addressed);
                    stableParameters.Remove(addressed);
                }
            }
        }

        foreach (var local in unstable)
            defs.Remove(local);

        return defs;
    }
}
