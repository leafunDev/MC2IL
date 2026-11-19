using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibMC2IL.BinaryStructures;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class InlinedListAddRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet || method.AppContext.MetadataVersion != 29)
            return;

        var graph = method.ControlFlowGraph!;
        var changed = false;
        foreach (var block in graph.Blocks)
            changed |= TryRecover(block, graph);

        if (changed)
            graph.RemoveUnreachableBlocks();
    }

    private static bool TryRecover(Block entry, ISILControlFlowGraph graph)
    {
        if (entry.Successors.Count != 2 || entry.Instructions.LastOrDefault() is not
                { OpCode: OpCode.ConditionalJump, Operands: [Block onNull, LocalVariable] }
            || onNull.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                [{ OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }] }])
            return false;

        // An instantiated Add method handle supplies the runtime generic context for
        // the capacity-growth branch, even when the whole public method was inlined.
        if (Operations(entry) is not
            [
                { OpCode: OpCode.Move, Operands: [LocalVariable addHandle, RuntimeMethodInfoAnalysisContext
                    { RepresentedMethod: ConcreteGenericMethodAnalysisContext add }] },
                { OpCode: OpCode.Move, Operands: [LocalVariable version, FieldReference versionField] },
                { OpCode: OpCode.Move, Operands: [LocalVariable items, FieldReference itemsField] },
                { OpCode: OpCode.Move, Operands: [LocalVariable addInfo, MemoryOperand infoRead] },
                var incrementVersion,
                { OpCode: OpCode.Move, Operands: [FieldReference versionStore, LocalVariable nextVersion] },
                { OpCode: OpCode.CheckEqual, Operands: [LocalVariable isNull, LocalVariable testedItems, Immediate { Value: 0 }] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block, LocalVariable nullCondition] }
            ]
            || add is not { DefaultName: "Add", IsStatic: false, IsVoid: true, Parameters.Count: 1,
                Visibility: MethodAttributes.Public, DeclaringType: GenericInstanceTypeAnalysisContext list }
            || list.GenericType is not { FullName: "System.Collections.Generic.List`1" } definition
            || definition.DeclaringAssembly != add.AppContext.SystemTypes.SystemObjectType.DeclaringAssembly
            || versionField.Local.Type is not GenericInstanceTypeAnalysisContext receiverType
            || !MetadataResolver.IsSameType(receiverType, list)
            || !IsField(versionField, versionField.Local, definition, "_version")
            || !IsField(versionStore, versionField.Local, definition, "_version")
            || !IsField(itemsField, versionField.Local, definition, "_items")
            || !IsRead(infoRead, addHandle, 0)
            || !IsIncrement(incrementVersion, nextVersion, version)
            || testedItems != items || nullCondition != isNull)
            return false;

        var receiver = versionField.Local;
        var capacity = entry.Successors.FirstOrDefault(block => block != onNull);
        if (capacity == null || capacity.Predecessors is not [var previous] || previous != entry
            || capacity.Successors.Count != 2 || Operations(capacity) is not
            [
                { OpCode: OpCode.Move, Operands: [LocalVariable size, FieldReference sizeField] },
                { OpCode: OpCode.CheckGreaterOrEqualUnsigned,
                    Operands: [LocalVariable full, LocalVariable compareSize, ArrayLength length] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block grow, LocalVariable fullCondition] }
            ]
            || !IsField(sizeField, receiver, definition, "_size")
            || fullCondition != full || compareSize != size || length.Array != items)
            return false;

        var append = capacity.Successors.FirstOrDefault(block => block != grow);
        if (append == null || append.Predecessors is not [var appendParent] || appendParent != capacity
            || grow.Predecessors is not [var growParent] || growParent != capacity
            || append.Successors is not [var next] || grow.Successors is not [var otherNext] || next != otherNext
            || Operations(append) is not
            [
                var incrementSize,
                { OpCode: OpCode.Move, Operands: [FieldReference sizeStore, LocalVariable nextSize] },
                { OpCode: OpCode.Move, Operands: [ArrayAccess write, var item] }
            ]
            || !IsIncrement(incrementSize, nextSize, size)
            || !IsField(sizeStore, receiver, definition, "_size")
            || write.Array != items || !ReferenceEquals(write.Index, size)
            || !MatchesAddWithResize(Operations(grow), definition, addInfo, receiver, item))
            return false;

        // Never remove a local definition used by code beyond the matched branches.
        var replaced = new HashSet<Block> { entry, capacity, append, grow };
        var removed = new HashSet<LocalVariable>(replaced.SelectMany(block => block.Instructions)
            .Select(instruction => instruction.Destination).OfType<LocalVariable>());
        if (removed.Contains(receiver) || item is not LocalVariable and not Immediate
            || item is LocalVariable itemLocal && removed.Contains(itemLocal)
            || graph.Blocks.Where(block => !replaced.Contains(block)).SelectMany(block => block.Instructions)
                .SelectMany(DeadCodeEliminator.UsedLocals).Any(removed.Contains))
            return false;

        var index = entry.Instructions[0].Index;
        entry.Instructions =
        [
            new Instruction(index, OpCode.CallVoid, add, receiver, item) { NullCheckReceiver = true },
            new Instruction(index, OpCode.Jump, next)
        ];
        foreach (var successor in entry.Successors)
            successor.Predecessors.Remove(entry);
        entry.Successors = [next];
        next.Predecessors.Add(entry);
        entry.CalculateBlockType();
        return true;
    }

    private static bool MatchesAddWithResize(Instruction[] operations, TypeAnalysisContext definition,
        LocalVariable addInfo, LocalVariable receiver, IOperand item)
    {
        if (operations is not
            [
                { OpCode: OpCode.Move, Operands: [LocalVariable klass, MemoryOperand klassRead] },
                { OpCode: OpCode.Move, Operands: [LocalVariable table, MemoryOperand tableRead] },
                { OpCode: OpCode.Move, Operands: [LocalVariable resizeInfo, MemoryOperand slot] },
                { OpCode: OpCode.IndirectCall, Operands: [MemoryOperand target, _, var self, var value, MemoryOperand methodInfo, ..] }
            ]
            || !ReferenceEquals(self, receiver) || !Equals(value, item)
            || !IsRead(klassRead, addInfo, 0x20) || !IsRead(tableRead, klass, 0xC0)
            || slot.Base != table || slot.Index != null || slot.Scale != 0 || slot.Addend < 0 || slot.Addend % 8 != 0
            || !IsRead(methodInfo, table, slot.Addend) || !IsRead(target, resizeInfo, 8)
            || definition.Definition is not { } list)
            return false;

        // The slot number is build dependent. Resolve it from the actual collection metadata.
        var entries = list.RgctXs;
        var index = slot.Addend / 8;
        return index < entries.Length && entries[index].type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
            && entries[index].MethodSpec.MethodDefinition is { Name: "AddWithResize" } resize
            && definition.Methods.Any(method => method.Definition == resize && !method.IsStatic
                && method.IsVoid && method.Parameters.Count == 1 && method.Visibility == MethodAttributes.Private);
    }

    private static Instruction[] Operations(Block block)
    {
        var operations = block.Instructions.Where(instruction => instruction.OpCode is not (OpCode.Nop or OpCode.Jump)).ToArray();
        // Phi lowering may leave synthetic copies on the outgoing edges. The use check
        // rejects the match if any of their destinations are still read outside it.
        var length = operations.Length;
        while (length > 0 && operations[length - 1] is
               { Index: < 0, OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable] })
            length--;
        return length == operations.Length ? operations : operations[..length];
    }

    private static bool IsRead(MemoryOperand operand, LocalVariable address, long offset) =>
        ReferenceEquals(operand.Base, address) && operand.Index == null && operand.Scale == 0 && operand.Addend == offset;

    private static bool IsIncrement(Instruction instruction, LocalVariable result, LocalVariable source) =>
        instruction is { OpCode: OpCode.Add, Operands: [LocalVariable destination, LocalVariable input, Immediate { Value: 1 }] }
        && destination == result && input == source;

    private static bool IsField(FieldReference reference, LocalVariable receiver, TypeAnalysisContext owner, string name) =>
        reference.Parent == null && reference.Local == receiver
        && reference.Field is ConcreteGenericFieldAnalysisContext { BaseFieldContext: { } field }
        && field.DeclaringType == owner && !field.IsStatic && field.DefaultName == name;
}
