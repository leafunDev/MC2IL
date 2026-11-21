using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class ValueTypeReceiverRecovery
{
    internal static void ResolveRefArgumentFieldAddresses(MethodAnalysisContext method)
    {
        if (method.IsStatic || method.DeclaringType is not { IsValueType: false } owner
            || owner.GenericParameters.Count != 0
            || method.AppContext.InstructionSet is not NewArmV8InstructionSet
            || method.ParameterLocals.FirstOrDefault(local => local.IsThis) is not { } self)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        Dictionary<LocalVariable, FieldAnalysisContext>? fieldsByAddress = null;
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Add, OperandWidth: null or 64,
                    Operands: [LocalVariable address, LocalVariable receiver, Immediate { Value: > 0 and <= int.MaxValue } offset] }
                || !ReferenceEquals(receiver, self)
                || FindUniqueField(owner, (int)offset.Value) is not { } field
                || (field.Attributes & FieldAttributes.InitOnly) != 0
                || (field.DeclaringType != owner && field.Visibility != FieldAttributes.Public))
                continue;

            (fieldsByAddress ??= []).Add(address, field);
        }

        if (fieldsByAddress == null)
            return;

        // In SSA the same address definition can reach a call after other calls and
        // argument preparation. Taking the address of a field on this is non-throwing.
        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext target)
                continue;
            var firstArgument = (call.OpCode == OpCode.Call ? 2 : 1) + (target.IsStatic ? 0 : 1);
            for (var i = 0; i < target.Parameters.Count && firstArgument + i < call.Operands.Count; i++)
            {
                if (target.Parameters[i].ParameterType is ByRefTypeAnalysisContext { ElementType: var elementType }
                    && call.Operands[firstArgument + i] is LocalVariable address
                    && fieldsByAddress.TryGetValue(address, out var field)
                    && MetadataResolver.IsSameType(field.FieldType, elementType))
                    call.SetOperand(firstArgument + i, new AddressOf(new FieldReference(field, self, field.Offset)));
            }
        }
    }

    internal static void ResolveStaticFieldAddresses(MethodAnalysisContext method)
    {
        if (!method.Locals.Exists(local => local.Type is StaticFieldStorageTypeAnalysisContext))
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        Dictionary<LocalVariable, LocalVariable>? copies = null;
        var pointerFitsInWord = method.AppContext.Binary.is32Bit;
        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext
                { IsStatic: false, DeclaringType: { IsValueType: true } valueType })
                continue;

            var receiverIndex = call.OpCode == OpCode.Call ? 2 : 1;
            if (call.Operands.Count <= receiverIndex
                || call.Operands[receiverIndex] is not LocalVariable receiver
                || FindStorage(receiver) is not
                    { Type: StaticFieldStorageTypeAnalysisContext { OwnerType: var owner } } storage
                || owner is ReferencedTypeAnalysisContext || owner.GenericParameters.Count != 0
                || FindUniqueField(owner, 0, isStatic: true) is not { } field
                || !MetadataResolver.IsSameType(field.FieldType, valueType)
                || (field.DeclaringType != method.DeclaringType && field.Visibility != FieldAttributes.Public))
                continue;

            // A direct pointer to the static storage block is the address of its offset-zero field.
            call.SetOperand(receiverIndex, new AddressOf(new FieldReference(field, storage, 0)));
        }

        LocalVariable? FindStorage(LocalVariable receiver)
        {
            if (receiver.Type is StaticFieldStorageTypeAnalysisContext)
                return receiver;

            // A call can type its receiver before inference reaches the native register copy.
            // Trace only full-width SSA copies back to the known storage load.
            if (copies == null)
            {
                copies = [];
                foreach (var instruction in instructions)
                    if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, LocalVariable source] }
                        && (instruction.OperandWidth != 32 || pointerFitsInWord))
                        copies[destination] = source;
            }
            for (var depth = 0; depth < 16 && copies.TryGetValue(receiver, out var source); depth++)
            {
                if (source.Type is StaticFieldStorageTypeAnalysisContext)
                    return source;
                receiver = source;
            }
            return null;
        }
    }

    internal static void BindStackConstructorReceivers(MethodAnalysisContext method)
    {
        if (method.Name != ".cctor" || method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        foreach (var block in method.ControlFlowGraph!.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var call = block.Instructions[i];
            if (call is not { OpCode: OpCode.CallVoid, Operands: [Immediate target, Register receiver, ..] }
                || !method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var candidates)
                || candidates is not [{ Name: ".ctor", IsStatic: false, DeclaringType: { IsValueType: true } valueType }])
                continue;

            var size = TypeSizes.UnboxedSize(valueType, 8);
            if (size is not (4 or 8))
                continue;

            // Bind the argument before SSA so a subsequent stack read sees the result
            // written by the constructor, rather than the preceding zero fill.
            Dictionary<Register, Instruction>? lastWrites = null;
            for (var j = i - 1; j >= 0; j--)
            {
                var previous = block.Instructions[j];
                if (previous.OpCode == OpCode.Nop)
                    continue;
                if (previous is not { OpCode: OpCode.Move, Operands: [Register destination, var source] })
                    break;
                if (destination == receiver)
                {
                    if (source is AddressOf { Target: Register slot }
                        && slot.Name.StartsWith("stack_-", StringComparison.Ordinal)
                        && lastWrites != null && lastWrites.TryGetValue(slot, out var initialization)
                        && initialization.MemoryAccessSize == size && initialization.Operands[1] is Immediate { Value: 0 }
                        && !UsesAddressInOtherArguments(call, receiver))
                        call.SetOperand(1, source);
                    break;
                }
                if (source is not (Register or Immediate) || source is Register alias && alias == receiver)
                    break;
                lastWrites ??= [];
                if (!lastWrites.ContainsKey(destination))
                    lastWrites.Add(destination, previous);
            }
        }
    }

    private static bool UsesAddressInOtherArguments(Instruction call, Register receiver)
    {
        for (var i = 2; i < call.Operands.Count; i++)
            if (call.Operands[i] is Register argument && argument == receiver)
                return true;
        return false;
    }

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

    private static FieldAnalysisContext? FindUniqueField(TypeAnalysisContext owner, int offset, bool isStatic = false)
    {
        FieldAnalysisContext? match = null;
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (type.IsGenericInstance)
                return null;

            foreach (var field in type.Fields)
            {
                if (field.IsStatic != isStatic || (field.Attributes & FieldAttributes.Literal) != 0
                    || field.BackingData?.FieldOffset != offset)
                    continue;
                if (match != null)
                    return null;
                match = field;
            }

            // Each type has a separate block for its static fields.
            if (isStatic)
                break;
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
