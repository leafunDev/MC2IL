using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// Turns the raw Il2CppArray layout (header, then length, then inline elements) back into array ops
public static class ArrayRecovery
{
    private static readonly HashSet<string> ArrayNewFunctions =
    [
        "SzArrayNew",
        "il2cpp_vm_array_new_specific",
        "il2cpp_array_new_specific",
    ];

    // Il2CppArray is {Il2CppObject obj; void* bounds; il2cpp_array_size_t max_length;} then the elements, on all versions(?)
    private static long LengthOffset(int pointerSize) => 3L * pointerSize;
    private static long ElementsOffset(int pointerSize) => 4L * pointerSize;

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;
        RecoverAccesses(method, instructions);
        RecoverStructElementAddresses(method, instructions);
        GroupInitialisers(cfg);
    }

    internal static void MergeReloadedLoopArrayLengths(ISILControlFlowGraph cfg)
    {
        // Native loops may load the array on the initial edge and reload it at the latch.
        // When both edges only carry that field to ldlen, put the read back in the loop test.
        // It still happens on every iteration, after the body can have changed the field.
        var headers = cfg.Blocks.Where(block => block.Predecessors.Count == 2
            && block.BlockType == BlockType.TwoWay
            && block.Instructions.FirstOrDefault(i => i.OpCode != OpCode.Nop) is
                { OpCode: OpCode.Move, Operands: [LocalVariable, ArrayLength] }).ToArray();
        if (headers.Length == 0)
            return;

        var instructions = cfg.Instructions;
        var definitions = instructions.Where(i => i.Destination is LocalVariable)
            .ToLookup(i => (LocalVariable)i.Destination!);
        var uses = instructions.SelectMany(DeadCodeEliminator.UsedLocals).ToLookup(local => local);
        var dominators = new DominatorInfo(cfg);
        foreach (var header in headers)
        {
            var length = header.Instructions.First(i => i.OpCode != OpCode.Nop);
            if (length.Operands[1] is not ArrayLength { Array: var array }
                || uses[array].Count() != 1 || definitions[array].Count() != 2
                || header.Predecessors.Count(predecessor => dominators.Dominates(header, predecessor)) != 1
                || FindEdgeRead(header.Predecessors[0], header, array) is not { } entry
                || FindEdgeRead(header.Predecessors[1], header, array) is not { } backedge
                || !ReferenceEquals(entry.Field.Field, backedge.Field.Field)
                || !ReferenceEquals(entry.Field.Local, backedge.Field.Local)
                || entry.Removed.Any(backedge.Removed.Contains))
                continue;

            foreach (var instruction in entry.Removed.Concat(backedge.Removed))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
            }
            header.Instructions.Insert(header.Instructions.IndexOf(length),
                new Instruction(-1, OpCode.Move, [array, entry.Field]));
        }

        (FieldReference Field, List<Instruction> Removed)? FindEdgeRead(Block current, Block next, LocalVariable value)
        {
            var seen = new HashSet<Block> { next };
            var removed = new List<Instruction>();
            while (seen.Add(current) && current.Successors is [var successor] && successor == next)
            {
                for (var i = current.Instructions.Count - 1; i >= 0; i--)
                {
                    var instruction = current.Instructions[i];
                    if (ReferenceEquals(instruction.Destination, value))
                    {
                        removed.Add(instruction);
                        if (instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Parent: null,
                            Local: { IsThis: true, Type.IsValueType: false } receiver, Field.IsStatic: false,
                            Field.FieldType: SzArrayTypeAnalysisContext } field] } && !definitions.Contains(receiver))
                            return (field, removed);

                        if (instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable copied] }
                            && uses[copied].Count() == 1 && definitions[copied].Count() == 1)
                        {
                            value = copied;
                            continue;
                        }
                        return null;
                    }
                    if (instruction.OpCode is OpCode.Nop or OpCode.Jump
                        || instruction is { OpCode: OpCode.Move, Operands: [LocalVariable { Type.FullName: "System.Int32" }, Immediate] }
                        || instruction is { OpCode: OpCode.Add, Operands: [LocalVariable { Type.FullName: "System.Int32" },
                            LocalVariable { Type.FullName: "System.Int32" }, Immediate { Value: 1 }] })
                        continue;
                    return null;
                }
                if (current.Predecessors is not [var predecessor])
                    break;
                next = current;
                current = predecessor;
            }
            return null;
        }
    }

    // IL2CPP may compute the indexed base in one instruction and load/store through it in
    // another. Resolve that address in SSA, while its array and index still denote single values.
    public static void RecoverSplitElementAccesses(MethodAnalysisContext method)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;
        var definitions = SingleDefinitions(instructions);
        DominatorInfo? dominators = null;
        var changed = false;

        TypeBoundedLoopIndices(method, cfg, definitions, ref dominators);

        foreach (var instruction in instructions)
        {
            var isMemoryMove = instruction is { OpCode: OpCode.Move, MemoryAccessSize: > 0, Operands.Count: 2 };
            var isMemoryComparison = instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned;
            if (!isMemoryMove && !instruction.IsCall && !isMemoryComparison)
                continue;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand memory)
                    continue;

                // Some AArch64 sequences keep the array header bias in the index register
                // (for example, address index = logical index + 4) while using the array
                // object itself as the base. Recover the logical index only when the matching
                // array-length guard proves that exact biased relationship.
                if (memory.Base is LocalVariable directArray
                    && ResolveArrayType(directArray, definitions, [], 0) is { } directArrayType
                    && (isMemoryMove
                        ? instruction.MemoryAccessSize == ElementSize(directArrayType.ElementType, pointerSize)
                        : instruction.IsCall || isMemoryComparison)
                    && TryRecoverBiasedArrayIndex(memory, directArray, directArrayType, pointerSize,
                        instruction, cfg, instructions, definitions, ref dominators) is { } checkedIndex)
                {
                    directArray.Type ??= directArrayType;
                    TypeProvenArrayIndex(method, checkedIndex, memory.Index, directArray, pointerSize,
                        instruction, cfg, instructions, definitions, ref dominators);
                    instruction.SetOperand(i, new ArrayAccess(directArray, checkedIndex));
                    if (instruction.OpCode == OpCode.Move && i == 1
                        && instruction.Destination is LocalVariable directDestination)
                        LocalVariables.PropagateProducedType(directDestination, directArrayType.ElementType);
                    changed = true;
                    continue;
                }

                if (!isMemoryMove
                    || memory.Base is not LocalVariable address
                    || SplitArrayAddress(address, definitions, 0) is not { } split
                    || ReferenceEquals(split.Array, address)
                    || split.Array.Type is not SzArrayTypeAnalysisContext { ElementType: var elementType }
                    || elementType is GenericParameterTypeAnalysisContext)
                    continue;

                var displacement = Sum(split.Displacement, new Affine(null, 0, memory.Addend));
                if (memory.Index is LocalVariable index)
                {
                    if (memory.Scale <= 0 || !IsArrayIndex(index))
                        continue;
                    displacement = Sum(displacement, new Affine(index, memory.Scale, 0));
                }
                else if (memory.Index != null || memory.Scale != 0)
                    continue;

                var stride = ElementSize(elementType, pointerSize);
                if (stride <= 0 || instruction.MemoryAccessSize != stride
                    || displacement is not { } offset || offset.Offset < ElementsOffset(pointerSize))
                    continue;

                var dataOffset = offset.Offset - ElementsOffset(pointerSize);
                IOperand elementIndex;
                if (offset.Root is { } dynamicIndex)
                {
                    if (offset.Multiplier != stride || dataOffset != 0)
                        continue;
                    elementIndex = FindEquivalentCheckedIndex(dynamicIndex, split.Array, pointerSize,
                        instruction, cfg, instructions, definitions, ref dominators) ?? dynamicIndex;
                }
                else
                {
                    if (dataOffset % stride != 0 || dataOffset / stride > int.MaxValue)
                        continue;
                    elementIndex = new Immediate(dataOffset / stride);
                }

                instruction.SetOperand(i, new ArrayAccess(split.Array, elementIndex));
                if (instruction.OpCode == OpCode.Move && i == 1
                    && instruction.Destination is LocalVariable destination)
                    LocalVariables.PropagateProducedType(destination, elementType);
                changed = true;
            }
        }

        if (!changed)
            return;

        DeadCodeEliminator.Run(cfg);
        // Element types can be the missing input for field and virtual-call resolution.
        LocalVariables.ResolveTypesAndFields(method);
    }

    private static void TypeBoundedLoopIndices(MethodAnalysisContext method, ISILControlFlowGraph cfg,
        Dictionary<LocalVariable, Instruction?> definitions, ref DominatorInfo? dominators)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var int32 = method.AppContext.SystemTypes.SystemInt32Type;
        foreach (var guard in cfg.Blocks)
        {
            if (guard.Successors is not [var firstSuccessor, var secondSuccessor]
                || guard.Instructions.LastOrDefault() is not
                    { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] }
                || taken != firstSuccessor && taken != secondSuccessor
                || !definitions.TryGetValue(condition, out var comparison)
                || comparison is not { OpCode: OpCode.CheckGreaterOrEqual or OpCode.CheckLess
                    or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessUnsigned,
                    Operands: [_, LocalVariable { Type: null } index, var length] }
                || !definitions.TryGetValue(index, out var phi)
                || phi is not { OpCode: OpCode.Phi, Operands: [_, var firstInput, var secondInput] }
                || cfg.FindBlockByInstruction(phi) is not { Predecessors: [var firstPredecessor, var secondPredecessor] } header)
                continue;

            var array = length switch
            {
                ArrayLength direct => direct.Array,
                LocalVariable local when definitions.TryGetValue(local, out var load) => load switch
                {
                    { OpCode: OpCode.Move, Operands: [_, ArrayLength arrayLength] } => arrayLength.Array,
                    { OpCode: OpCode.Move, MemoryAccessSize: 4, Operands: [_, MemoryOperand
                        { Base: LocalVariable arrayBase, Index: null, Scale: 0 } memory] }
                        when memory.Addend == LengthOffset(pointerSize) => arrayBase,
                    _ => null,
                },
                _ => null,
            };
            if (array == null || ResolveArrayType(array, definitions, [], 0) == null)
                continue;

            var inRange = comparison.OpCode is OpCode.CheckLess or OpCode.CheckLessUnsigned
                ? taken : taken == firstSuccessor ? secondSuccessor : firstSuccessor;
            if (inRange.Predecessors is not [var previous] || previous != guard)
                continue;
            var dominance = dominators ??= new DominatorInfo(cfg);
            if (!dominance.Dominates(header, guard))
                continue;

            // The native counter remains in Int32 range even if it uses a 64-bit
            // register: the back-edge increments only after i < array.Length. The
            // array check can be inside a loop whose exit tests a different bound.
            if (TryTypeIndex(firstInput, secondInput, firstPredecessor, secondPredecessor)
                || TryTypeIndex(secondInput, firstInput, secondPredecessor, firstPredecessor))
                index.Type = int32;

            bool TryTypeIndex(IOperand seed, IOperand backEdge, Block seedBlock, Block latch)
            {
                if (seed is not Immediate { Value: >= 0 and <= int.MaxValue }
                    || backEdge is not LocalVariable next || next.Type != null && next.Type != int32
                    || !definitions.TryGetValue(next, out var increment)
                    || increment is not { OpCode: OpCode.Add, Operands.Count: 3 }
                    || !IsIncrementOf(increment, index)
                    || cfg.FindBlockByInstruction(increment) is not { } incrementBlock
                    || !dominance.Dominates(incrementBlock, latch)
                    || dominance.Dominates(header, seedBlock) || !dominance.Dominates(inRange, incrementBlock))
                    return false;

                next.Type = int32;
                return true;
            }
        }
    }

    private static LocalVariable? FindEquivalentCheckedIndex(LocalVariable index, LocalVariable array, int pointerSize,
        Instruction access, ISILControlFlowGraph cfg, List<Instruction> instructions,
        Dictionary<LocalVariable, Instruction?> definitions, ref DominatorInfo? dominators)
    {
        if (cfg.FindBlockByInstruction(access) is not { } accessBlock)
            return null;

        foreach (var branch in instructions)
        {
            if (branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] }
                || !definitions.TryGetValue(condition, out var test)
                || test is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, OperandWidth: 32,
                    Operands: [_, LocalVariable left, LocalVariable right] })
                continue;

            var equivalent = ReferenceEquals(left, index) ? right : ReferenceEquals(right, index) ? left : null;
            if (equivalent == null || !IsArrayIndex(equivalent) || cfg.FindBlockByInstruction(branch) is not { } guard)
                continue;
            var equal = test.OpCode == OpCode.CheckEqual ? taken : guard.Successors.FirstOrDefault(block => block != taken);
            if (equal?.Predecessors is not [var predecessor] || predecessor != guard)
                continue;

            dominators ??= new DominatorInfo(cfg);
            if (dominators.Dominates(equal, accessBlock)
                && IsCheckedArrayIndex(equivalent, array, pointerSize, access, cfg, instructions, definitions,
                    requireInt32: true, ref dominators))
                return equivalent;
        }
        return null;
    }

    private static IOperand? TryRecoverBiasedArrayIndex(MemoryOperand memory, LocalVariable array,
        SzArrayTypeAnalysisContext arrayType, int pointerSize, Instruction access, ISILControlFlowGraph cfg,
        List<Instruction> instructions, Dictionary<LocalVariable, Instruction?> definitions,
        ref DominatorInfo? dominators)
    {
        if (memory.Index is not LocalVariable addressIndex)
            return null;

        var stride = ElementSize(arrayType.ElementType, pointerSize);
        if (stride <= 0 || memory.Scale != stride)
            return null;

        long bias;
        try
        {
            var offset = checked(memory.Addend - ElementsOffset(pointerSize));
            if (offset % stride != 0)
                return null;
            bias = offset / stride;
        }
        catch (OverflowException)
        {
            return null;
        }

        if (bias == 0)
            return null;

        foreach (var candidate in definitions)
        {
            if (!HasIndexBias(candidate.Key, addressIndex, bias, definitions, [], 0)
                || !IsCheckedArrayIndex(candidate.Key, array, pointerSize, access, cfg, instructions, definitions,
                    requireInt32: false, ref dominators))
                continue;

            return candidate.Key;
        }

        return null;
    }

    private static void TypeProvenArrayIndex(MethodAnalysisContext method, IOperand index, IOperand? addressIndex,
        LocalVariable array, int pointerSize, Instruction access, ISILControlFlowGraph cfg,
        List<Instruction> instructions, Dictionary<LocalVariable, Instruction?> definitions,
        ref DominatorInfo? dominators)
    {
        if (index is not LocalVariable checkedIndex
            || !IsCheckedArrayIndex(checkedIndex, array, pointerSize, access, cfg, instructions, definitions,
                requireInt32: true, ref dominators))
            return;

        if ((checkedIndex.Type != null && checkedIndex.Type.FullName != "System.Int32")
            || addressIndex is not LocalVariable counter
            || !HasUnconvertedIndexBias(checkedIndex, counter, definitions, [], 0)
            || !definitions.TryGetValue(counter, out var phi)
            || phi is not { OpCode: OpCode.Phi, Operands.Count: 3 })
            return;

        // Optimized ARM64 loops can keep a word-sized array index biased by a constant in an
        // X register (e.g. start at 4 and access counter - 4). Recover an Int32 counter only
        // when an in-range 32-bit guard dominates the access and the phi is a bounded +1 induction.

        var phiFirstInput = phi.Operands[1];
        var phiSecondInput = phi.Operands[2];
        var next = phiFirstInput as LocalVariable ?? phiSecondInput as LocalVariable;
        var seed = phiFirstInput is Immediate ? phiFirstInput : phiSecondInput;
        if (next == null || !IsInt32Seed(seed, definitions)
            || !definitions.TryGetValue(next, out var increment)
            || increment is not { OpCode: OpCode.Add, Operands.Count: 3 }
            || !IsIncrementOf(increment, counter))
            return;

        if ((counter.Type != null && counter.Type.FullName != "System.Int32")
            || (next.Type != null && next.Type.FullName != "System.Int32"))
            return;

        var int32 = method.AppContext.SystemTypes.SystemInt32Type;
        checkedIndex.Type ??= int32;
        counter.Type ??= int32;
        next.Type ??= int32;
    }

    private static bool IsInt32Seed(IOperand seed, Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (seed is Immediate immediate)
            return immediate.Value is >= int.MinValue and <= int.MaxValue;

        return seed is LocalVariable local
            && definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands: [_, Immediate value] }
            && value.Value is >= int.MinValue and <= int.MaxValue;
    }

    private static bool HasUnconvertedIndexBias(LocalVariable candidate, LocalVariable counter,
        Dictionary<LocalVariable, Instruction?> definitions, HashSet<LocalVariable> seen, int depth)
    {
        if (depth > 8 || !seen.Add(candidate))
            return false;
        if (ReferenceEquals(candidate, counter))
            return true;
        if (!definitions.TryGetValue(candidate, out var definition) || definition == null)
            return false;

        if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
            return HasUnconvertedIndexBias(source, counter, definitions, seen, depth + 1);
        if (definition is { OpCode: OpCode.Add, Operands: [_, LocalVariable addLeft, Immediate] })
            return HasUnconvertedIndexBias(addLeft, counter, definitions, seen, depth + 1);
        if (definition is { OpCode: OpCode.Add, Operands: [_, Immediate, LocalVariable addRight] })
            return HasUnconvertedIndexBias(addRight, counter, definitions, seen, depth + 1);
        if (definition is { OpCode: OpCode.Subtract, Operands: [_, LocalVariable subtractLeft, Immediate] })
            return HasUnconvertedIndexBias(subtractLeft, counter, definitions, seen, depth + 1);

        return false;
    }

    private static bool IsIncrementOf(Instruction instruction, LocalVariable counter)
    {
        var left = instruction.Operands[1];
        var right = instruction.Operands[2];
        return left is LocalVariable leftLocal && ReferenceEquals(leftLocal, counter)
                && right is Immediate { Value: 1 }
            || right is LocalVariable rightLocal && ReferenceEquals(rightLocal, counter)
                && left is Immediate { Value: 1 };
    }

    private static SzArrayTypeAnalysisContext? ResolveArrayType(IOperand operand,
        Dictionary<LocalVariable, Instruction?> definitions, HashSet<LocalVariable> seen, int depth)
    {
        if (depth > 12)
            return null;

        if (operand is SzArrayTypeAnalysisContext arrayType)
            return arrayType;
        if (operand is FieldReference { Field.FieldType: SzArrayTypeAnalysisContext fieldArrayType })
            return fieldArrayType;
        if (operand is not LocalVariable local)
            return null;
        if (local.Type is SzArrayTypeAnalysisContext localArrayType)
            return localArrayType;
        if (!seen.Add(local) || !definitions.TryGetValue(local, out var definition) || definition == null)
            return null;

        if (definition is { OpCode: OpCode.Move, Operands: [_, var source] })
            return ResolveArrayType(source, definitions, seen, depth + 1);

        if (definition.OpCode != OpCode.Phi)
            return null;

        SzArrayTypeAnalysisContext? resolved = null;
        foreach (var phiSource in definition.Operands.Skip(1))
        {
            if (phiSource is Immediate { Value: 0 })
                continue;

            var sourceArray = ResolveArrayType(phiSource, definitions, seen, depth + 1);
            if (sourceArray == null)
                return null;

            if (resolved != null && (resolved.FullName != sourceArray.FullName
                || resolved.DeclaringAssembly.Name != sourceArray.DeclaringAssembly.Name))
                return null;

            resolved = sourceArray;
        }

        return resolved;
    }

    private static bool HasIndexBias(LocalVariable candidate, LocalVariable addressIndex, long bias,
        Dictionary<LocalVariable, Instruction?> definitions, HashSet<LocalVariable> seen, int depth)
    {
        if (depth > 8 || !seen.Add(candidate))
            return false;
        if (ReferenceEquals(candidate, addressIndex))
            return bias == 0;
        if (!definitions.TryGetValue(candidate, out var definition) || definition == null)
            return false;

        if (definition is { OpCode: OpCode.Move or OpCode.Convert, Operands: [_, LocalVariable source] })
            return HasIndexBias(source, addressIndex, bias, definitions, seen, depth + 1);

        if (definition is { OpCode: OpCode.Add, Operands: [_, LocalVariable addLeft, Immediate addRight] })
            return HasIndexBias(addLeft, addressIndex, bias - addRight.Value, definitions, seen, depth + 1);
        if (definition is { OpCode: OpCode.Add, Operands: [_, Immediate reverseAddLeft, LocalVariable reverseAddRight] })
            return HasIndexBias(reverseAddRight, addressIndex, bias - reverseAddLeft.Value, definitions, seen, depth + 1);
        if (definition is { OpCode: OpCode.Subtract, Operands: [_, LocalVariable subtractLeft, Immediate subtractRight] })
            return HasIndexBias(subtractLeft, addressIndex, bias + subtractRight.Value, definitions, seen, depth + 1);

        return false;
    }

    private static bool IsCheckedArrayIndex(LocalVariable candidate, LocalVariable array, int pointerSize,
        Instruction access, ISILControlFlowGraph cfg, List<Instruction> instructions,
        Dictionary<LocalVariable, Instruction?> definitions, bool requireInt32, ref DominatorInfo? dominators)
    {
        var accessBlock = cfg.FindBlockByInstruction(access);
        if (accessBlock == null)
            return false;

        foreach (var branch in instructions)
        {
            if (branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] })
                continue;

            var inverted = false;
            var conditionValue = condition;
            Instruction? comparison = null;
            for (var depth = 0; depth < 8; depth++)
            {
                if (!definitions.TryGetValue(conditionValue, out var definition) || definition == null)
                    break;

                if (definition is { OpCode: OpCode.Not, Operands: [_, LocalVariable inner] })
                {
                    inverted = !inverted;
                    conditionValue = inner;
                    continue;
                }

                comparison = definition;
                break;
            }

            if (comparison is not { Operands.Count: >= 3 }
                || requireInt32 && comparison.OperandWidth != 32
                || !TryGetInRangeTruth(comparison, candidate, array, pointerSize, definitions, out var trueIsInRange))
                continue;

            var branchBlock = cfg.FindBlockByInstruction(branch);
            if (branchBlock == null || branchBlock.Successors.Count < 2)
                continue;

            var fallthrough = branchBlock.Successors.FirstOrDefault(successor => successor != taken);
            if (fallthrough == null)
                continue;

            var inRangeBlock = trueIsInRange != inverted ? taken : fallthrough;
            dominators ??= new DominatorInfo(cfg);
            if (inRangeBlock.Predecessors is [var predecessor] && predecessor == branchBlock
                && dominators.Dominates(inRangeBlock, accessBlock))
                return true;
        }

        return false;
    }

    private static bool TryGetInRangeTruth(Instruction comparison, LocalVariable candidate, LocalVariable array,
        int pointerSize, Dictionary<LocalVariable, Instruction?> definitions, out bool trueIsInRange)
    {
        trueIsInRange = false;
        var left = comparison.Operands[1];
        var right = comparison.Operands[2];

        var candidateOnLeft = ReferenceEquals(left, candidate);
        var candidateOnRight = ReferenceEquals(right, candidate);
        var leftIsLength = IsLengthOf(left, array, pointerSize, definitions, [], 0);
        var rightIsLength = IsLengthOf(right, array, pointerSize, definitions, [], 0);

        if (candidateOnLeft && rightIsLength)
        {
            if (comparison.OpCode is OpCode.CheckLess or OpCode.CheckLessUnsigned)
            {
                trueIsInRange = true;
                return true;
            }

            if (comparison.OpCode is OpCode.CheckGreaterOrEqual or OpCode.CheckGreaterOrEqualUnsigned)
                return true;
        }

        if (leftIsLength && candidateOnRight)
        {
            if (comparison.OpCode is OpCode.CheckGreater or OpCode.CheckGreaterUnsigned)
            {
                trueIsInRange = true;
                return true;
            }

            if (comparison.OpCode is OpCode.CheckLessOrEqual or OpCode.CheckLessOrEqualUnsigned)
                return true;
        }

        return false;
    }

    private static bool IsLengthOf(IOperand operand, LocalVariable array, int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions, HashSet<LocalVariable> seen, int depth)
    {
        if (depth > 8)
            return false;

        if (operand is ArrayLength length)
            return ReferenceEquals(length.Array, array);

        if (operand is MemoryOperand { Base: LocalVariable baseArray, Index: null, Scale: 0, Addend: var offset })
            return ReferenceEquals(baseArray, array) && offset == LengthOffset(pointerSize);

        if (operand is LocalVariable local && seen.Add(local)
            && definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands: [_, var source] }
            && source is IOperand sourceOperand)
            return IsLengthOf(sourceOperand, array, pointerSize, definitions, seen, depth + 1);

        return false;
    }

    private static (LocalVariable Array, Affine Displacement)? SplitArrayAddress(
        IOperand operand, Dictionary<LocalVariable, Instruction?> definitions, int depth)
    {
        if (depth > 8 || operand is not LocalVariable local)
            return null;

        definitions.TryGetValue(local, out var definition);
        if (definition is { OpCode: OpCode.Add, OperandWidth: not 32, Operands: [_, var left, var right] })
            return ExtendAddress(left, right) ?? ExtendAddress(right, left);
        if (local.Type is SzArrayTypeAnalysisContext)
            return (local, new Affine(null, 0, 0));
        return definition is { OpCode: OpCode.Move, OperandWidth: not 32, Operands: [_, LocalVariable source] }
            ? SplitArrayAddress(source, definitions, depth + 1)
            : null;

        (LocalVariable Array, Affine Displacement)? ExtendAddress(IOperand baseOperand, IOperand displacementOperand)
        {
            if (SplitArrayAddress(baseOperand, definitions, depth + 1) is not { } arrayBase
                || Sum(arrayBase.Displacement, ElementDisplacement(displacementOperand, definitions)) is not { } displacement)
                return null;

            return (arrayBase.Array, displacement);
        }
    }

    private static Affine? ElementDisplacement(IOperand operand, Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (operand is Immediate immediate)
            return new Affine(null, 0, immediate.Value);
        if (operand is not LocalVariable local)
            return null;

        long multiplier = 1;
        if (definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.ShiftLeft, OperandWidth: 64,
                Operands: [_, LocalVariable unscaled, Immediate { Value: > 0 and < 32 } shift] })
        {
            multiplier = 1L << (int)shift.Value;
            local = unscaled;
        }

        if (definitions.TryGetValue(local, out definition)
            && definition is { OpCode: OpCode.Convert, Operands: [_, LocalVariable narrowed,
                NumericConversion { Kind: NumericConversionKind.I64ExtendI32Signed or NumericConversionKind.I64ExtendI32Unsigned }] })
            local = narrowed;

        return IsArrayIndex(local) ? new Affine(local, multiplier, 0) : null;
    }

    private static bool IsArrayIndex(LocalVariable local) => local.Type?.FullName is "System.Int32" or "System.UInt32";

    private static void RecoverAccesses(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        HashSet<LocalVariable>? returnedLocals = null;

        foreach (var instruction in instructions)
        {
            RecoverAllocation(method, instruction, instructions, ref returnedLocals);

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand memory
                    || memory.Base is not LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array)
                    continue;

                if (memory.Index == null && memory.Scale == 0 && memory.Addend == LengthOffset(pointerSize))
                {
                    instruction.SetOperand(i, new ArrayLength(array));

                    // Array recovery runs after the main type-propagation fixpoint. A length
                    // load therefore needs to type its Move destination here, or that local can
                    // reach IL generation without the System.Int32 type produced by ldlen/conv.i4.
                    if (instruction.OpCode == OpCode.Move && i == 1
                        && instruction.Destination is LocalVariable destination)
                        destination.Type ??= method.AppContext.SystemTypes.SystemInt32Type;

                    continue;
                }

                if (ElementIndex(memory, arrayType, pointerSize) is { } index)
                    instruction.SetOperand(i, new ArrayAccess(array, index));
            }
        }
    }

    // Group initializers after an array allocation together so ILSpy decompiles them better
    private static void GroupInitialisers(ISILControlFlowGraph cfg)
    {
        var movedAny = false;

        foreach (var block in cfg.Blocks.ToList())
        {
            foreach (var allocation in block.Instructions.ToList())
            {
                if (allocation.OpCode != OpCode.NewArr || allocation.Operands[0] is not LocalVariable array)
                    continue;

                var stores = new List<(Block Block, Instruction Instruction)>();
                var current = block;
                var index = current.Instructions.IndexOf(allocation) + 1;
                var visitedBlocks = new HashSet<Block> { block };

                while (true)
                {
                    if (index >= current.Instructions.Count)
                    {
                        // only a straight-line run can be regrouped without changing what runs when
                        if (current.Successors.Count != 1 || current.Successors[0].Predecessors.Count != 1)
                            break;

                        var successor = current.Successors[0];
                        if (!visitedBlocks.Add(successor))
                            break;

                        current = successor;
                        index = 0;
                        continue;
                    }

                    var instruction = current.Instructions[index];

                    if (IsElementStore(instruction, array))
                    {
                        stores.Add((current, instruction));
                        index++;
                        continue;
                    }

                    // Moving an initializer across any other instruction can change the
                    // stored value or exception order. Nops have no observable effects.
                    if (instruction.OpCode == OpCode.Nop)
                    {
                        index++;
                        continue;
                    }

                    if (!ReadsArray(instruction, array))
                        break;

                    // Found the first read. Move the allocation and its stores immediately in front, so the whole array is built in one chain with the elements already computed.
                    if (stores.Count > 1)
                    {
                        foreach (var (storeBlock, store) in stores)
                            storeBlock.Instructions.Remove(store);

                        block.Instructions.Remove(allocation);

                        var moved = new List<Instruction> { allocation };
                        moved.AddRange(stores.Select(s => s.Instruction));

                        current.Instructions.InsertRange(current.Instructions.IndexOf(instruction), moved);
                        movedAny = true;
                    }

                    break;
                }
            }
        }

        // Emptying a block out entirely leaves branches pointing at nothing to jump to
        if (movedAny)
            cfg.RemoveEmptyBlocks();
    }

    private static bool IsElementStore(Instruction instruction, LocalVariable array) =>
        instruction.OpCode == OpCode.Move && instruction.Operands[0] is ArrayAccess { Index: Immediate } stored
                                          && ReferenceEquals(stored.Array, array)
                                          && !ReadsArray(instruction, array);

    private static bool ReadsArray(Instruction instruction, LocalVariable array)
    {
        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            if (i == 0 && instruction.OpCode == OpCode.Move)
                continue;

            var reads = instruction.Operands[i] switch
            {
                LocalVariable local => ReferenceEquals(local, array),
                ArrayAccess access => ReferenceEquals(access.Array, array),
                ArrayLength length => ReferenceEquals(length.Array, array),
                MemoryOperand memory => ReferenceEquals(memory.Base, array) || ReferenceEquals(memory.Index, array),
                AddressOf { Target: LocalVariable addressed } => ReferenceEquals(addressed, array),
                _ => false
            };

            if (reads)
                return true;
        }

        return false;
    }

    private static void RecoverAllocation(MethodAnalysisContext method, Instruction instruction,
        List<Instruction> instructions, ref HashSet<LocalVariable>? returnedLocals)
    {
        // Call "SzArrayNew", result, typeof(T[]), length, ...
        if (!instruction.IsCall || instruction.Operands.Count < 4
            || instruction.Operands[0] is not StringLiteral { Value: var name }
            || !ArrayNewFunctions.Contains(name)
            || instruction.Operands[1] is not LocalVariable result)
            return;

        // Depending on which resolver recognized the metadata load, the class argument can be
        // either the array type itself or an Il2CppClass* typed as RuntimeClassTypeAnalysisContext.
        // Some newer layouts leave that argument unresolved; the allocation result often already
        // has its array type from a later field store, so use it as a safe fallback.
        var arrayType = ResolveArrayType(instruction.Operands[2]) ?? result.Type as SzArrayTypeAnalysisContext;
        if (arrayType == null && (returnedLocals ??= CollectReturnedLocals(instructions)).Contains(result))
            arrayType = method.ReturnType as SzArrayTypeAnalysisContext;

        if (arrayType == null)
            return;

        var length = instruction.Operands[3];
        instruction.OpCode = OpCode.NewArr;
        instruction.SetOperands(result, arrayType, length);
        result.Type = arrayType;
    }

    private static HashSet<LocalVariable> CollectReturnedLocals(List<Instruction> instructions)
    {
        var returnedLocals = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Return, Operands: [LocalVariable value] })
                returnedLocals.Add(value);

        return returnedLocals;
    }

    private static SzArrayTypeAnalysisContext? ResolveArrayType(IOperand operand) => operand switch
    {
        SzArrayTypeAnalysisContext arrayType => arrayType,
        RuntimeClassTypeAnalysisContext { RepresentedType: SzArrayTypeAnalysisContext arrayType } => arrayType,
        LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } => arrayType,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: SzArrayTypeAnalysisContext arrayType } } => arrayType,
        _ => null,
    };

    private static IOperand? ElementIndex(MemoryOperand memory, SzArrayTypeAnalysisContext arrayType, int pointerSize)
    {
        var elementSize = ElementSize(arrayType.ElementType, pointerSize);
        long offset;
        try
        {
            offset = checked(memory.Addend - ElementsOffset(pointerSize));
        }
        catch (OverflowException)
        {
            return null;
        }

        if (offset < 0 || elementSize == 0 || offset % elementSize != 0)
            return null;

        if (memory.Index == null)
            return memory.Scale == 0 ? new Immediate(offset / elementSize) : null;

        return memory.Scale == elementSize && offset == 0 ? memory.Index : null;
    }

    private static long ElementSize(TypeAnalysisContext elementType, int pointerSize)
    {
        if (!elementType.IsValueType)
            return pointerSize;

        if (elementType.IsEnumType)
            return elementType.EnumUnderlyingType is { } underlyingType
                ? ElementSize(underlyingType, pointerSize)
                : 0;

        return elementType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => 1,
            "System.Int16" or "System.UInt16" or "System.Char" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" => 8,
            "System.IntPtr" or "System.UIntPtr" => pointerSize,
            _ => 0 // struct arrays are handled by the element-address path, which sizes them from metadata
        };
    }

    // Recovers &array[i] over struct arrays. Struct elements are never loaded outright, the compiler
    // computes their address via lea chains and calls through it. We solve the index as a linear function
    // of a local and demand an exact hit on the metadata stride, so a real load can't match by accident.
    private static void RecoverStructElementAddresses(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;

        var definitions = SingleDefinitions(instructions);
        var uses = CollectUses(instructions);

        foreach (var instruction in instructions)
        {
            if (instruction.IsCall)
            {
                for (var i = 1; i < instruction.Operands.Count; i++)
                {
                    if (MatchElementAddress(instruction.Operands[i], pointerSize, definitions) is { } inlined)
                        instruction.SetOperand(i, inlined);
                }

                continue;
            }

            // a Move from memory could be a load or a lifted lea, and only the uses tell them apart
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable destination, MemoryOperand] })
                continue;

            if (definitions.TryGetValue(destination, out var single) && single == null)
                continue;

            if (!uses.TryGetValue(destination, out var destinationUses) || destinationUses.Count == 0
                || !destinationUses.All(u => u.Instruction.IsCall || IsMemoryBase(u.Instruction.Operands[u.OperandIndex], destination)))
                continue;

            if (MatchElementAddress(instruction.Operands[1], pointerSize, definitions) is { } address)
                instruction.SetOperand(1, address);
        }
    }

    private static AddressOf? MatchElementAddress(IOperand operand, int pointerSize, Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (operand is not MemoryOperand memory
            || memory.Base is not LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array)
            return null;

        var elementType = arrayType.ElementType;
        if (!elementType.IsValueType || ElementSize(elementType, pointerSize) != 0)
            return null;

        var elementSize = MetadataElementSize(elementType, pointerSize);
        if (elementSize <= 0)
            return null;

        return StructElementIndex(memory, array, elementSize, pointerSize, definitions) is { } index
            ? new AddressOf(new ArrayAccess(array, index))
            : null;
    }

    private static bool IsMemoryBase(IOperand operand, LocalVariable local)
        => operand is MemoryOperand { Base: LocalVariable baseLocal } && ReferenceEquals(baseLocal, local);

    private static long MetadataElementSize(TypeAnalysisContext elementType, int pointerSize)
        => TypeSizes.UnboxedSize(elementType, pointerSize);

    private static IOperand? StructElementIndex(MemoryOperand memory, LocalVariable array, long elementSize, int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions)
    {
        var indexAffine = memory.Index is LocalVariable indexLocal
            ? ScaleBy(Evaluate(indexLocal, definitions, 0), Math.Max(memory.Scale, 1))
            : new Affine(null, 0, 0);

        if (Sum(indexAffine, new Affine(null, 0, memory.Addend)) is not { } address)
            return null;

        if (ReferenceEquals(address.Root, array))
            return null;

        long offset;
        try
        {
            offset = checked(address.Offset - ElementsOffset(pointerSize));
        }
        catch (OverflowException)
        {
            return null;
        }

        if (address.Root != null)
            return address.Multiplier == elementSize && offset == 0 ? address.Root : null;

        return offset >= 0 && offset % elementSize == 0 ? new Immediate(offset / elementSize) : null;
    }

    // value = Multiplier * Root + Offset (a null Root means it's just a constant)
    private readonly record struct Affine(LocalVariable? Root, long Multiplier, long Offset);

    private static Affine? Evaluate(IOperand operand, Dictionary<LocalVariable, Instruction?> definitions, int depth)
    {
        if (depth > 8)
            return null;

        switch (operand)
        {
            case Immediate { Value: var value }:
                return new Affine(null, 0, value);

            case LocalVariable local:
            {
                if (!definitions.TryGetValue(local, out var definition) || definition == null)
                    return new Affine(local, 1, 0);

                return definition switch
                {
                    { OpCode: OpCode.Move, Operands: [_, MemoryOperand lea] } => EvaluateLea(lea, definitions, depth + 1),
                    { OpCode: OpCode.Move, Operands: [_, var source] } => Evaluate(source, definitions, depth + 1),
                    { OpCode: OpCode.Add, Operands: [_, var left, var right] } => Sum(Evaluate(left, definitions, depth + 1), Evaluate(right, definitions, depth + 1)),
                    { OpCode: OpCode.ShiftLeft, Operands: [_, var left, Immediate { Value: >= 0 and < 32 } shift] } => ScaleBy(Evaluate(left, definitions, depth + 1), 1L << (int)shift.Value),
                    { OpCode: OpCode.Multiply, Operands: [_, var left, Immediate factor] } => ScaleBy(Evaluate(left, definitions, depth + 1), factor.Value),
                    _ => new Affine(local, 1, 0)
                };
            }

            default:
                return null;
        }
    }

    private static Affine? EvaluateLea(MemoryOperand lea, Dictionary<LocalVariable, Instruction?> definitions, int depth)
    {
        var result = (Affine?)new Affine(null, 0, lea.Addend);

        if (lea.Base != null)
            result = Sum(result, Evaluate(lea.Base, definitions, depth));

        if (lea.Index != null)
            result = Sum(result, ScaleBy(Evaluate(lea.Index, definitions, depth), Math.Max(lea.Scale, 1)));

        return result;
    }

    private static Affine? Sum(Affine? left, Affine? right)
    {
        if (left is not { } l || right is not { } r)
            return null;

        if (l.Root != null && r.Root != null && !ReferenceEquals(l.Root, r.Root))
            return null;

        try
        {
            return new Affine(l.Root ?? r.Root, checked(l.Multiplier + r.Multiplier), checked(l.Offset + r.Offset));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Affine? ScaleBy(Affine? value, long factor)
    {
        if (value is not { } affine)
            return null;

        try
        {
            return new Affine(affine.Root, checked(affine.Multiplier * factor), checked(affine.Offset * factor));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    // null means the local has more than one definition
    private static Dictionary<LocalVariable, Instruction?> SingleDefinitions(List<Instruction> instructions)
    {
        var definitions = new Dictionary<LocalVariable, Instruction?>();

        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = definitions.ContainsKey(destination) ? null : instruction;

        return definitions;
    }

    private static Dictionary<LocalVariable, List<(Instruction Instruction, int OperandIndex)>> CollectUses(List<Instruction> instructions)
    {
        var uses = new Dictionary<LocalVariable, List<(Instruction, int)>>();

        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (ReferenceEquals(operand, instruction.Destination))
                    continue;

                foreach (var local in OperandLocals(operand))
                {
                    if (!uses.TryGetValue(local, out var sites))
                        uses[local] = sites = [];
                    sites.Add((instruction, i));
                }
            }
        }

        return uses;
    }

    private static IEnumerable<LocalVariable> OperandLocals(IOperand operand)
    {
        switch (operand)
        {
            case LocalVariable direct:
                yield return direct;
                break;
            case MemoryOperand memory:
                if (memory.Base is LocalVariable baseLocal)
                    yield return baseLocal;
                if (memory.Index is LocalVariable indexLocal)
                    yield return indexLocal;
                break;
            case AddressOf { Target: LocalVariable addressed }:
                yield return addressed;
                break;
        }
    }
}
