using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Removes pure instructions whose result is never used. This eliminates, among other things, the
/// dead flag/temporary computations the x86 lifter emits eagerly for every comparison - a single
/// <c>cmp</c>/<c>test</c> produces all of CF/OF/SF/ZF/PF plus scratch temporaries, but the branch
/// that follows only consumes one of them.
///
/// Marks values reachable from observable instructions before pruning pure definitions. This
/// removes dead phi/increment cycles as well as ordinary unused chains. Instructions are turned
/// into nops in place.
/// </summary>
public static class DeadCodeEliminator
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg) => Run(cfg, null);

    internal static void Run(ISILControlFlowGraph cfg, HashSet<Instruction>? discardableLoads,
        bool discardUnusedCallResults = false)
    {
        var removable = new HashSet<Instruction>();
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction.Destination is not LocalVariable destination
                    || !IsRemovable(instruction) && discardableLoads?.Contains(instruction) != true)
                    continue;

                removable.Add(instruction);
                if (!definitions.TryGetValue(destination, out var writes))
                    definitions[destination] = writes = [];
                writes.Add(instruction);
            }
        }

        var stackSlotVersions = new Dictionary<string, List<LocalVariable>>();
        foreach (var local in definitions.Keys)
        {
            if (!local.Register.Name.StartsWith("stack_", System.StringComparison.Ordinal))
                continue;

            if (!stackSlotVersions.TryGetValue(local.Register.Name, out var versions))
                stackSlotVersions[local.Register.Name] = versions = [];
            versions.Add(local);
        }

        var live = new HashSet<LocalVariable>();
        var pending = new Stack<LocalVariable>();
        foreach (var instruction in cfg.Instructions)
        {
            if (removable.Contains(instruction))
                continue;

            foreach (var used in UsedLocals(instruction))
                Keep(used);
        }

        while (pending.Count != 0)
        {
            var local = pending.Pop();
            if (!definitions.TryGetValue(local, out var writes))
                continue;

            foreach (var write in writes)
                foreach (var used in UsedLocals(write))
                    Keep(used);
        }

        foreach (var instruction in removable)
            if (instruction.Destination is LocalVariable destination && !live.Contains(destination))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
            }

        if (discardUnusedCallResults)
        {
            var usedLocals = new HashSet<LocalVariable>();
            foreach (var instruction in cfg.Instructions)
                foreach (var used in UsedLocals(instruction))
                    usedLocals.Add(used);

            foreach (var block in cfg.Blocks)
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext, LocalVariable result, ..] }
                    || usedLocals.Contains(result))
                    continue;

                // Preserve the call and its arguments. CallVoid tells IL generation to
                // pop a non-void return value instead of storing it in an unused local.
                instruction.RemoveOperandAt(1);
                instruction.OpCode = OpCode.CallVoid;
            }
        }

        void Keep(LocalVariable local)
        {
            if (live.Add(local))
                pending.Push(local);

            // SSA gives each write to a stack location a different LocalVariable, but an
            // AddressOf formed before a write still aliases the same physical slot. Keep
            // every version's write when any version of that slot is observed through an
            // address, then follow the ordinary source dependencies above.
            if (local.Register.Name.StartsWith("stack_", System.StringComparison.Ordinal)
                && stackSlotVersions.TryGetValue(local.Register.Name, out var versions))
                foreach (var version in versions)
                    if (live.Add(version))
                        pending.Push(version);
        }
    }

    /// <summary>
    /// Every local read by the instruction. The single write position - a plain local destination -
    /// is excluded. Memory and field operands always contribute their address/object locals as
    /// reads, even when they are the destination of a store.
    /// </summary>
    internal static IEnumerable<LocalVariable> UsedLocals(Instruction instruction)
    {
        var destinationIndex = instruction.Destination is not LocalVariable
            ? -1
            : instruction.DestinationOperandIndex;

        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            switch (instruction.Operands[i])
            {
                case LocalVariable local when i != destinationIndex:
                    yield return local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable baseLocal)
                        yield return baseLocal;
                    if (memory.Index is LocalVariable indexLocal)
                        yield return indexLocal;
                    break;
                // A static field access doesn't read the storage pointer it was resolved from, so that
                // pointer (and the class load feeding it) is free to die.
                case FieldReference field when !IsStaticFieldPath(field):
                    yield return field.Local;
                    break;
                // Handing out a slot's address is a read of it as far as we can tell, whatever the callee then does with it.
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case AddressOf { Target: ArrayAccess addressedElement }:
                    foreach (var used in ArrayAccessLocals(addressedElement))
                        yield return used;
                    break;
                case AddressOf { Target: FieldReference addressedField } when !IsStaticFieldPath(addressedField):
                    yield return addressedField.Local;
                    break;
                case ArrayAccess access:
                    foreach (var used in ArrayAccessLocals(access))
                        yield return used;
                    break;
                case ArrayLength { Array: { } lengthArray }:
                    yield return lengthArray;
                    break;
            }
        }
    }

    private static bool IsStaticFieldPath(FieldReference field)
    {
        for (var current = field; current != null; current = current.Parent)
            if (current.Field.IsStatic)
                return true;

        return false;
    }

    private static IEnumerable<LocalVariable> ArrayAccessLocals(ArrayAccess access)
    {
        yield return access.Array;

        if (access.Index is LocalVariable index)
            yield return index;
    }

    /// <summary>
    /// Opcodes with no side effects, so removing a never-read result is safe. Calls, stores,
    /// returns and branches are intentionally excluded.
    /// </summary>
    private static bool IsRemovable(Instruction instruction) =>
        instruction.OpCode switch
        {
            // Native memory loads are still represented as Move-to-local operations. If nobody
            // consumes that local, retaining the load only creates a bogus unmanaged-memory
            // diagnostic in generated C#; an absolute image/global read has no managed null or
            // bounds behavior to preserve. Base-relative reads can still throw for a null object,
            // so leave those in the graph even when their result is unused.
            OpCode.Move when instruction.Operands is [_, FieldReference
                { Parent: null, Field.IsStatic: false, Local: { IsThis: false,
                    Type: { IsValueType: true } and not ByRefTypeAnalysisContext } }] => true,
            OpCode.Move when instruction.Operands is [_, MemoryOperand { IsConstant: false }
                or FieldReference or ArrayAccess or ArrayLength] => false,
            OpCode.Move => true,
            OpCode.Convert when instruction.Operands is [_, _, NumericConversion
                { Kind: NumericConversionKind.I64ExtendI32Signed or NumericConversionKind.I64ExtendI32Unsigned }] => true,
            OpCode.Phi
                or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate => true,
            >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned => true,
            _ => false
        };
}
