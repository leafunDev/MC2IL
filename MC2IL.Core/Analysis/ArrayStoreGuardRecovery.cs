using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using LibMC2IL;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class ArrayStoreGuardRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return false;

        var cfg = method.ControlFlowGraph!;
        var binary = method.AppContext.Binary;
        var changed = false;

        foreach (var guard in cfg.Blocks)
        {
            if (guard.BlockType != BlockType.TwoWay
                || guard.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray() is not [var comparison, var branch]
                || !TryGetNullEdges(guard, comparison, branch, out var value, out var storePath, out var typeCheck)
                || typeCheck.Predecessors is not [var entry] || entry != guard
                || MatchTypeCheck(typeCheck, value, storePath) is not { } check
                || GetExclusiveThrowPath(typeCheck, check.Failure) is not { } failurePath
                || FindStore(storePath, guard, typeCheck, check.Array, out var unusedLengthLoad) is not { OpCode: OpCode.Move,
                    Operands: [ArrayAccess access, var stored] } || access.Array != check.Array || stored != value)
                continue;

            var instructions = cfg.Instructions;
            if (FindDefinition(guard, comparison, check.Array, instructions) is not { OpCode: OpCode.NewArr,
                    Operands: [_, SzArrayTypeAnalysisContext arrayType, _] }
                || arrayType.ElementType != method.AppContext.SystemTypes.SystemObjectType
                || FindDefinition(guard, comparison, value, instructions) is not { OpCode: OpCode.Call,
                    Operands: [MethodAnalysisContext { DeclaringType: not null } called, ..] }
                || !IlGenerator.IsManagedReference(called.ReturnType)
                || !IsArm64InstanceCheck(binary, check.Target, check.ElementClassOffset))
                continue;

            var removed = new HashSet<Instruction>(typeCheck.Instructions);
            removed.UnionWith(failurePath.SelectMany(block => block.Instructions));
            var removedDefinitions = new HashSet<LocalVariable>(removed.Select(Definition).OfType<LocalVariable>());
            if (instructions.Any(i => !removed.Contains(i)
                    && DeadCodeEliminator.UsedLocals(i).Any(removedDefinitions.Contains)))
                continue;

            // The exact object[] accepts every managed reference. Keep the store, which
            // still performs the managed array checks, and leave allocation/value evaluation in place.
            branch.OpCode = OpCode.Jump;
            branch.SetOperands(storePath);
            cfg.RemoveEdge(guard, typeCheck);
            guard.CalculateBlockType();

            // The length load left behind by a removed bounds guard cannot throw
            // on this new array. Drop it only when no remaining instruction uses it.
            if (unusedLengthLoad?.Destination is LocalVariable length
                && !instructions.Any(i => !removed.Contains(i) && DeadCodeEliminator.UsedLocals(i).Contains(length)))
            {
                unusedLengthLoad.OpCode = OpCode.Nop;
                unusedLengthLoad.SetOperands();
            }
            changed = true;
        }

        if (changed)
        {
            cfg.RemoveUnreachableBlocks();
            DeadCodeEliminator.Run(cfg);
        }
        return changed;
    }

    private sealed record TypeCheck(LocalVariable Array, ulong Target, long ElementClassOffset, Block Failure);

    private static TypeCheck? MatchTypeCheck(Block block, LocalVariable value, Block storePath)
    {
        var instructions = block.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
        if (instructions is not [var classLoad, .., var call, var comparison, var branch]
            || classLoad is not { OpCode: OpCode.Move, MemoryAccessSize: 8,
                Operands: [LocalVariable klass, MemoryOperand { Base: LocalVariable array, Index: null, Scale: 0, Addend: 0 }] }
            || call is not { OpCode: OpCode.Call, Operands: [Immediate { Value: > 0 } target, LocalVariable result,
                var candidate, var elementClass, ..] } || candidate != value
            || !TryGetNullEdges(block, comparison, branch, out var checkedResult, out var fail, out var pass)
            || result != checkedResult || pass != storePath)
            return null;

        IOperand? classField = instructions.Length switch
        {
            4 => elementClass,
            5 when instructions[1] is { OpCode: OpCode.Move, MemoryAccessSize: 8,
                Operands: [LocalVariable loaded, var source] } && loaded == elementClass => source,
            _ => null,
        };
        return classField is MemoryOperand { Index: null, Scale: 0 } memory && memory.Base == klass
            ? new TypeCheck(array, (ulong)target.Value, memory.Addend, fail)
            : null;
    }

    private static List<Block>? GetExclusiveThrowPath(Block predecessor, Block block)
    {
        var path = new List<Block>();
        while (path.Count < 8 && block.Predecessors is [var entry] && entry == predecessor)
        {
            path.Add(block);
            if (block.BlockType == BlockType.Interrupt)
                return path;
            if (block.Successors is not [var next] || block.Instructions.Any(i => i.OpCode is not (OpCode.Nop or OpCode.Jump)))
                break;
            predecessor = block;
            block = next;
        }
        return null;
    }

    private static bool TryGetNullEdges(Block block, Instruction comparison, Instruction branch,
        out LocalVariable value, out Block whenNull, out Block whenNotNull)
    {
        value = null!;
        whenNull = whenNotNull = null!;
        if (comparison.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || comparison.OperandWidth is not (null or 64)
            || comparison.Operands is not [LocalVariable condition, LocalVariable compared, Immediate { Value: 0 }]
            || branch.Operands is not [Block taken, var predicate] || predicate != condition
            || branch.OpCode != OpCode.ConditionalJump || block.Successors.Count != 2
            || !block.Successors.Contains(taken))
            return false;

        var fallthrough = block.Successors.First(b => b != taken);
        value = compared;
        (whenNull, whenNotNull) = comparison.OpCode == OpCode.CheckEqual ? (taken, fallthrough) : (fallthrough, taken);
        return true;
    }

    private static Instruction? FindStore(Block block, Block guard, Block typeCheck, LocalVariable array,
        out Instruction? removableLengthLoad)
    {
        removableLengthLoad = null;
        Instruction? lengthLoad = null;
        var dominated = block.Predecessors.All(predecessor => predecessor == guard || predecessor == typeCheck);
        for (var remaining = 8; remaining > 0; remaining--)
        {
            // Some builds leave the index-zero length guard ahead of the store.
            // Only inspect its successful edge here. The regular bounds pass owns the check itself.
            if (GetNonEmptyArrayPath(block, array) is { } guardedStore)
            {
                dominated &= guardedStore.Predecessors is [var previous] && previous == block;
                block = guardedStore;
                continue;
            }
            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode is OpCode.Nop or OpCode.Jump)
                    continue;
                if (lengthLoad == null && instruction is { OpCode: OpCode.Move,
                    Operands: [LocalVariable, ArrayLength length] } && length.Array == array)
                {
                    lengthLoad = instruction;
                    if (dominated)
                        removableLengthLoad = instruction;
                    continue;
                }
                return instruction;
            }
            if (block.Successors is not [var next])
                break;
            dominated &= next.Predecessors is [var predecessorBlock] && predecessorBlock == block;
            block = next;
        }
        return null;
    }

    private static Block? GetNonEmptyArrayPath(Block block, LocalVariable array)
    {
        if (block.BlockType != BlockType.TwoWay)
            return null;
        var operations = block.Instructions.Where(i => i.OpCode != OpCode.Nop).ToArray();
        if (operations is not [.., var comparison, var branch]
            || comparison.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || comparison.Operands is not [LocalVariable condition, var checkedLength, Immediate { Value: 0 }]
            || branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, var predicate] }
            || predicate != condition || block.Successors.Count != 2 || !block.Successors.Contains(taken))
            return null;

        var length = operations.Length switch
        {
            2 => checkedLength,
            3 when operations[0] is { OpCode: OpCode.Move, Operands: [LocalVariable local, var source] }
                && local == checkedLength => source,
            _ => null,
        };
        if (length is not ArrayLength arrayLength || arrayLength.Array != array)
            return null;

        var fallthrough = block.Successors.First(successor => successor != taken);
        return comparison.OpCode == OpCode.CheckEqual ? fallthrough : taken;
    }

    private static LocalVariable? Definition(Instruction instruction) =>
        instruction.OpCode == OpCode.NewArr ? instruction.Operands[0] as LocalVariable : instruction.Destination as LocalVariable;

    private static Instruction? FindDefinition(Block block, Instruction before, LocalVariable local,
        List<Instruction> instructions)
    {
        if (instructions.Any(i => i.Operands.Any(o => o is AddressOf address && address.Target == local)))
            return null;

        var index = block.Instructions.IndexOf(before);
        for (var remaining = 8; index >= 0 && remaining > 0; remaining--)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];
                if (Definition(instruction) == local)
                    return instructions.Count(other => Definition(other) == local) == 1 ? instruction : null;
            }
            if (block.Predecessors is not [var previous])
                break;
            block = previous;
            index = block.Instructions.Count;
        }
        return null;
    }

    private static bool IsArm64InstanceCheck(Il2CppBinary binary, ulong address, long classOffset)
    {
        // Verify the Il2CppClass layout using the exported element_class accessor.
        var accessor = FollowArm64Branches(binary, binary.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_get_element_class"));
        if (accessor == 0 || !TryReadCode(binary, accessor, 8, out var accessorCode)
            || (Word(accessorCode, 0) & 0xFFC003FF) != 0xF9400000 // ldr x0, [x0, #offset]
            || ((Word(accessorCode, 0) >> 10) & 0xFFF) * 8 != classOffset
            || Word(accessorCode, 4) != 0xD65F03C0)
            return false;

        var assignability = FollowArm64Branches(binary, binary.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_is_assignable_from"));
        var body = FollowArm64Branches(binary, address);
        if (assignability == 0 || body == 0 || !TryReadCode(binary, body, 44, out var code))
            return false;

        // Object::IsInst saves the input, calls IsAssignableFrom(elementClass, value->klass),
        // then returns the original input on success. Match that path through the return,
        // allowing the branches' displacements to vary across builds.
        var call = Word(code, 36);
        var success = Word(code, 40);
        if (Word(code, 0) != 0xD100C3FF || Word(code, 4) != 0xA90153F5 || Word(code, 8) != 0xA9027BF3
            || Word(code, 12) != 0xAA0003F3 || (Word(code, 16) & 0xFF00001F) != 0xB4000000
            || Word(code, 20) != 0xF9400275 || Word(code, 24) != 0xAA0103F4
            || Word(code, 28) != 0xAA0103E0 || Word(code, 32) != 0xAA1503E1
            || (call & 0xFC000000) != 0x94000000 || (success & 0xFFF8001F) != 0x37000000
            || FollowArm64Branches(binary, BranchTarget(body + 36, call, 26, 0)) != assignability)
            return false;

        var epilogue = BranchTarget(body + 40, success, 14, 5);
        return epilogue == BranchTarget(body + 16, Word(code, 16), 19, 5)
            && TryReadCode(binary, epilogue, 20, out var exit)
            && Word(exit, 0) == 0xAA1303E0 && Word(exit, 4) == 0xA9427BF3
            && Word(exit, 8) == 0xA94153F5 && Word(exit, 12) == 0x9100C3FF
            && Word(exit, 16) == 0xD65F03C0;
    }

    private static ulong FollowArm64Branches(Il2CppBinary binary, ulong address)
    {
        for (var remaining = 8; address != 0 && remaining > 0; remaining--)
        {
            if (!TryReadCode(binary, address, 4, out var code))
                break;
            var instruction = Word(code, 0);
            if ((instruction & 0xFC000000) != 0x14000000)
                return address;
            address = BranchTarget(address, instruction, 26, 0);
        }
        return 0;
    }

    private static ulong BranchTarget(ulong address, uint instruction, int width, int shift)
    {
        // Sign-extend the displacement; ARM64 direct branch offsets are measured in words.
        var displacement = (int)((instruction >> shift) << (32 - width)) >> (32 - width);
        return unchecked(address + (ulong)(displacement * 4L));
    }

    private static bool TryReadCode(Il2CppBinary binary, ulong address, int length, out ReadOnlySpan<byte> code)
    {
        var content = binary.GetRawBinaryContent();
        if (binary.TryMapVirtualAddressToRaw(address, out var raw) && raw >= 0 && raw <= content.Length - length)
        {
            code = content.Slice((int)raw, length);
            return true;
        }
        code = default;
        return false;
    }

    private static uint Word(ReadOnlySpan<byte> code, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
}
