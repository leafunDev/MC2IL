using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Removes the IL2CPP runtime-metadata initialization guards the compiler emits near the top of
/// (almost) every method, plus il2cpp_runtime_class_init blocks.
/// </summary>
public static class MetadataInitGuardRemover
{
    private const string InitializeRuntimeMetadata = "il2cpp_codegen_initialize_runtime_metadata";
    private const string InitializeMethod = "il2cpp_codegen_initialize_method";
    private const string ClassInitExport = "il2cpp_runtime_class_init_export";
    private const string ClassInitActual = "il2cpp_runtime_class_init_actual";
    private const string ClassInitCodegen = "il2cpp_codegen_runtime_class_init";

    // Byte holding Il2CppClass's bitfield, of which bit 0 is initialized_and_no_error.
    // TODO this is almost certainly not correct on every version... but which?
    private const long InitialisedFlagOffset64 = 0x135;

    // Other Unity layouts expose the equivalent class-initialization state as cctor_finished.
    private const long CctorFinishedOffset64 = 0xE0;
    private const long CctorFinishedOffset32 = 0x74;

    // Offset of MethodInfo::rgctx_data
    private const long MethodRgctxOffset64 = 0x38;
    private const long MethodRgctxOffset32 = 0x1C;

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var supportsClassReloads = SupportsClassReloads(method);
        var isArmV7 = method.AppContext.InstructionSet is ArmV7InstructionSet;
        var armV7ClassLayout = isArmV7 && method.AppContext.MetadataVersion == 24.5f;
        // Only accept class offsets from known layouts. ARMv7 v29 checks cctor_finished
        // at 0x74 before calling the class initializer.
        var flagOffsets = supportsClassReloads
            ? new[] { InitialisedFlagOffset64, CctorFinishedOffset64 }
            : isArmV7 && method.AppContext.MetadataVersion == 29
                ? new[] { CctorFinishedOffset32 }
                : Array.Empty<long>();
        if (supportsClassReloads)
            RemoveArm64ClassMetadataGuards(method, cfg);
        if (armV7ClassLayout)
            RemoveArmV7CctorGuards(cfg);
        Run(cfg, flagOffsets, keepBareCalls: supportsClassReloads || armV7ClassLayout, dominance: method.DominatorInfo,
            allowStaticFieldReloads: supportsClassReloads && !method.AppContext.Binary.is32Bit);
    }

    private static void RemoveArmV7CctorGuards(ISILControlFlowGraph cfg)
    {
        const int hasCctorFlagOffset = 0xBB;
        const int hasCctorMask = 2;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        var discardedLoads = new HashSet<Instruction>();
        foreach (var guard in cfg.Blocks.ToArray())
        {
            // ARMv7 24.5 calls ClassInit only when has_cctor is set and cctor_finished is zero.
            if (guard.Successors.Count != 2 || guard.Instructions.LastOrDefault() is not
                    { OpCode: OpCode.ConditionalJump, Operands: [Block merge, LocalVariable condition] }
                || !guard.Successors.Contains(merge)
                || !TryGetClassFlagTest(condition, definitions, hasCctorFlagOffset, hasCctorMask,
                    out var flagLoad, out var klass, out var trueWhenSet)
                || trueWhenSet || flagLoad.MemoryAccessSize != 1)
                continue;

            var finishedCheck = guard.Successors.First(successor => successor != merge);
            if (finishedCheck.Predecessors is not [var predecessor] || predecessor != guard
                || finishedCheck.Successors.Count != 2 || !finishedCheck.Successors.Contains(merge)
                || finishedCheck.Instructions.LastOrDefault() is not
                    { OpCode: OpCode.ConditionalJump, Operands: [Block ready, LocalVariable finished] } branch
                || ready != merge
                || !definitions.TryGetValue(finished, out var comparison)
                || comparison is not { OpCode: OpCode.CheckNotEqual, Operands: [_, LocalVariable value, Immediate { Value: 0 }] }
                || !definitions.TryGetValue(value, out var finishedLoad)
                || finishedLoad is not { OpCode: OpCode.Move, MemoryAccessSize: 4, Operands: [_, MemoryOperand
                    { Base: LocalVariable checkedClass, Index: null, Scale: 0, Addend: CctorFinishedOffset32 }] }
                || !ReferenceEquals(klass, checkedClass)
                || !finishedCheck.Instructions.All(instruction => instruction == branch || instruction == finishedLoad
                    || IsSideEffectFree(instruction) && instruction is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand] }))
                continue;

            var slow = finishedCheck.Successors.First(successor => successor != merge);
            if (!TryCollectRegion(cfg, finishedCheck, slow, merge, false, null, null, out var region)
                || !region.SelectMany(block => block.Instructions).Any(i => InitializesClass(i, klass, definitions)))
                continue;

            region.Add(finishedCheck);
            if (HasEscapingValues(cfg, region, merge))
                continue;

            Excise(cfg, guard, finishedCheck, merge, region);
            discardedLoads.Add(flagLoad);
            // The proven runtime guard can reload its class from a metadata slot. Keep the
            // reload if any live operation still needs it, and never discard an object read here.
            if (definitions.TryGetValue(klass, out var reload)
                && reload is { OpCode: OpCode.Move, MemoryAccessSize: 4, Operands: [_, MemoryOperand
                    { Base: LocalVariable slot, Index: null, Scale: 0, Addend: 0 }] }
                && FollowCopies(slot, definitions) is LocalVariable slotSource
                && definitions.TryGetValue(slotSource, out var slotLoad)
                && slotLoad is { OpCode: OpCode.Move, Operands: [_, TypeAnalysisContext
                    and not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext)] })
                discardedLoads.Add(reload);
        }

        if (discardedLoads.Count != 0)
            DeadCodeEliminator.Run(cfg, discardedLoads);
    }

    // The ARM64 metadata initialization slow path returns the same Il2CppClass*
    // as the fast path. Its result is merged before RGCTX and static field reads.
    private static void RemoveArm64ClassMetadataGuards(MethodAnalysisContext method, ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        var discardedLoads = new HashSet<Instruction>();
        foreach (var guard in cfg.Blocks.ToArray())
        {
            if (guard.Successors.Count != 2
                || guard.Instructions.LastOrDefault() is not { OpCode: OpCode.ConditionalJump,
                    Operands: [Block onTrue, LocalVariable condition] }
                || !TryGetClassFlagTest(condition, definitions, 0x132, 1, out var flagLoad, out var klass, out var trueWhenReady))
                continue;

            var onFalse = guard.Successors.FirstOrDefault(successor => successor != onTrue);
            if (onFalse == null)
                continue;
            var slowPath = trueWhenReady ? onFalse : onTrue;
            var fastPath = trueWhenReady ? onTrue : onFalse;
            var merge = fastPath;
            // A setter can end with one void return on each edge, rather than a shared return.
            var terminalGuard = slowPath.Successors.Count == 1 && slowPath.Successors[0] == cfg.ExitBlock
                && slowPath.Instructions.LastOrDefault() is { OpCode: OpCode.Return, Operands: [] }
                && merge.Instructions.LastOrDefault() is { OpCode: OpCode.Return, Operands: [] }
                && merge.Instructions.Take(merge.Instructions.Count - 1).All(instruction => instruction.OpCode == OpCode.Nop);
            if (slowPath.Predecessors.Count != 1 || slowPath.Predecessors[0] != guard
                || slowPath.Successors.Count != 1)
                continue;

            if (!terminalGuard && slowPath.Successors[0] != fastPath)
            {
                if (fastPath.Predecessors.Count != 1 || fastPath.Successors.Count != 1
                    || fastPath.Successors[0] != slowPath.Successors[0])
                    continue;
                merge = fastPath.Successors[0];
            }

            var call = slowPath.Instructions.FirstOrDefault(instruction => instruction.IsCall);
            if (call is not { OpCode: OpCode.Call, Operands: [Immediate address, LocalVariable result, LocalVariable argument, ..] }
                || !SameClassMetadataValue(argument, klass, definitions)
                || slowPath.Instructions.Any(instruction => instruction != call
                    && instruction.OpCode is not (OpCode.Nop or OpCode.Jump)
                    && !(terminalGuard && instruction.OpCode == OpCode.Return)
                    && instruction is not { OpCode: OpCode.Move, OperandWidth: null or 64,
                        Operands: [LocalVariable, LocalVariable] }
                    && (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable reloaded,
                            MemoryOperand or RuntimeClassTypeAnalysisContext] }
                        || (!SameClassMetadataValue(reloaded, klass, definitions)
                            && !SameClassMetadataValue(reloaded, flagLoad.Operands[0], definitions))))
                || !IsArm64ClassMetadataHelper(method, address.UnsignedValue))
                continue;

            HashSet<Block> region = [slowPath];
            if (HasEscapingValues(cfg, region, merge))
                continue;

            if (terminalGuard)
            {
                Excise(cfg, guard, slowPath, merge, region);
                discardedLoads.Add(flagLoad);
                continue;
            }

            var fastIndex = merge.Predecessors.IndexOf(merge == fastPath ? guard : fastPath) + 1;
            var slowIndex = merge.Predecessors.IndexOf(slowPath) + 1;
            if (fastIndex < 1 || slowIndex < 1)
                continue;

            var classPointer = FollowCopies(klass, definitions);
            var phisMatch = merge.Instructions.Where(instruction => instruction.OpCode == OpCode.Phi).All(phi =>
            {
                var fastValue = FollowCopies(phi.Operands[fastIndex], definitions);
                var slowValue = FollowCopies(phi.Operands[slowIndex], definitions);
                return SameClassMetadataValue(fastValue, ReferenceEquals(slowValue, result) ? classPointer : slowValue, definitions);
            });
            if (!phisMatch)
                continue;

            Excise(cfg, guard, slowPath, merge, region, fastPath);
            discardedLoads.Add(flagLoad);
        }

        if (discardedLoads.Count != 0)
            DeadCodeEliminator.Run(cfg, discardedLoads);
    }

    private static bool TryGetClassFlagTest(LocalVariable condition,
        Dictionary<LocalVariable, Instruction> definitions, long flagOffset, long flagMask,
        out Instruction load, out LocalVariable klass, out bool trueWhenSet)
    {
        load = null!;
        klass = null!;
        trueWhenSet = false;

        if (!definitions.TryGetValue(condition, out var test))
            return false;
        var inverted = test is { OpCode: OpCode.Not, Operands: [_, LocalVariable] };
        if (inverted && !definitions.TryGetValue((LocalVariable)test.Operands[1], out test))
            return false;

        if (test!.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || test.Operands is not [_, LocalVariable masked, Immediate { Value: 0 }]
            || !definitions.TryGetValue(masked, out var mask)
            || mask is not { OpCode: OpCode.And, Operands: [_, LocalVariable flag, Immediate constant] }
            || constant.Value != flagMask
            || FollowCopies(flag, definitions) is not LocalVariable flagSource
            || !definitions.TryGetValue(flagSource, out var candidate)
            || candidate is not { OpCode: OpCode.Move, MemoryAccessSize: 1 or 2, Operands: [_, MemoryOperand
                { Base: LocalVariable classPointer, Index: null, Scale: 0, Addend: var offset }] }
            || offset != flagOffset)
            return false;

        klass = classPointer;
        load = candidate;
        trueWhenSet = (test.OpCode == OpCode.CheckNotEqual) != inverted;
        return true;
    }

    private static IOperand FollowCopies(IOperand value, Dictionary<LocalVariable, Instruction> definitions)
    {
        for (var i = 0; i < 16 && value is LocalVariable local
            && definitions.TryGetValue(local, out var definition); i++)
        {
            if (definition is not ({ OpCode: OpCode.Move, OperandWidth: null or 64 } or { OpCode: OpCode.Phi })
                || definition.Operands is not [_, LocalVariable source])
                break;
            value = source;
        }
        return value;
    }

    private static bool SameClassMetadataValue(IOperand left, IOperand right,
        Dictionary<LocalVariable, Instruction> definitions, int depth = 0)
    {
        left = FollowCopies(left, definitions);
        right = FollowCopies(right, definitions);
        if (Equals(left, right))
            return true;

        // A reload of the same class metadata global is represented by a typed operand.
        if (left is LocalVariable leftClass && right is LocalVariable rightClass
            && definitions.TryGetValue(leftClass, out var leftDefinition)
            && definitions.TryGetValue(rightClass, out var rightDefinition)
            && leftDefinition is { OpCode: OpCode.Move, Operands: [_, RuntimeClassTypeAnalysisContext leftType] }
            && rightDefinition is { OpCode: OpCode.Move, Operands: [_, RuntimeClassTypeAnalysisContext rightType] })
            return MetadataResolver.IsSameType(leftType.RepresentedType, rightType.RepresentedType);

        // A slow path may reload MethodInfo::klass and the class flag before rejoining.
        return depth < 3 && left is LocalVariable leftLocal && right is LocalVariable rightLocal
            && definitions.TryGetValue(leftLocal, out var leftLoad)
            && definitions.TryGetValue(rightLocal, out var rightLoad)
            && leftLoad is { OpCode: OpCode.Move, Operands: [_, MemoryOperand
                { Addend: 0x20 or 0x132, Index: null, Scale: 0, Base: LocalVariable leftBase } leftMemory] }
            && rightLoad is { OpCode: OpCode.Move, Operands: [_, MemoryOperand
                { Index: null, Scale: 0, Base: LocalVariable rightBase } rightMemory] }
            && leftMemory.Addend == rightMemory.Addend
            && (leftMemory.Addend == 0x20 ? leftLoad.MemoryAccessSize == 8 : leftLoad.MemoryAccessSize is 1 or 2)
            && leftLoad.MemoryAccessSize == rightLoad.MemoryAccessSize
            && (leftMemory.Addend != 0x20 || FollowCopies(leftBase, definitions) is LocalVariable { IsMethodInfo: true })
            && SameClassMetadataValue(leftBase, rightBase, definitions, depth + 1);
    }

    private static bool IsArm64ClassMetadataHelper(MethodAnalysisContext method, ulong address)
    {
        var binary = method.AppContext.Binary;
        var content = binary.GetRawBinaryContent();
        if (!binary.TryMapVirtualAddressToRaw(address, out var raw) || raw < 0 || raw > content.Length - 44)
            return false;

        var body = content.Slice((int)raw, 44);
        // InitFromCodegen saves x0 in x19, calls the initializer, checks the class's
        // initialization error handle and returns x19. Match the error path as well.
        // Ignore the link displacements, which vary with the native binary layout.
        return BinaryPrimitives.ReadUInt32LittleEndian(body) == 0xA9BF7BF3
            && BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) == 0xAA0003F3
            && IsArm64Call(BinaryPrimitives.ReadUInt32LittleEndian(body[8..]))
            && BinaryPrimitives.ReadUInt32LittleEndian(body[12..]) == 0xB940DA60
            && BinaryPrimitives.ReadUInt32LittleEndian(body[16..]) == 0x35000080
            && BinaryPrimitives.ReadUInt32LittleEndian(body[20..]) == 0xAA1303E0
            && BinaryPrimitives.ReadUInt32LittleEndian(body[24..]) == 0xA8C17BF3
            && BinaryPrimitives.ReadUInt32LittleEndian(body[28..]) == 0xD65F03C0
            && IsArm64Call(BinaryPrimitives.ReadUInt32LittleEndian(body[32..]))
            && BinaryPrimitives.ReadUInt32LittleEndian(body[36..]) == 0xAA1F03E1
            && IsArm64Call(BinaryPrimitives.ReadUInt32LittleEndian(body[40..]));

        static bool IsArm64Call(uint instruction) => (instruction & 0xFC000000) == 0x94000000;
    }

    // Rewrite any metadata init calls we didn't remove into movs.
    public static void RewriteUnguardedInits(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Call || instruction.Operands is not [StringLiteral { Value: InitializeRuntimeMetadata or InitializeMethod }, var result, var handle, ..])
                continue;

            instruction.OpCode = OpCode.Move;
            instruction.SetOperands(result, handle);
        }
    }

    // Removes the lazy-init guards protecting a generic method's inlined RGCTX metadata lookups.
    public static void RunRgctx(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var rgctxOffset = method.AppContext.Binary.is32Bit ? MethodRgctxOffset32 : MethodRgctxOffset64;

        var removedAny = false;

        foreach (var guard in cfg.Blocks.ToList())
            removedAny |= TryRemoveRgctxGuard(cfg, guard, rgctxOffset);

        if (removedAny)
            DeadCodeEliminator.Run(cfg);
    }

    private static bool TryRemoveRgctxGuard(ISILControlFlowGraph cfg, Block guard, long rgctxOffset)
    {
        if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2
            || guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        var isRgctxGuard = guard.Instructions.Any(i =>
            i.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
            && (IsRgctxLoad(i.Operands[1], rgctxOffset) && IsZero(i.Operands[2])
                || IsRgctxLoad(i.Operands[2], rgctxOffset) && IsZero(i.Operands[1])));

        if (!isRgctxGuard)
            return false;

        var first = guard.Successors[0];
        var second = guard.Successors[1];

        return TryExcise(cfg, guard, first, second, true, out _)
            || TryExcise(cfg, guard, second, first, true, out _);
    }

    private static bool IsRgctxLoad(IOperand operand, long rgctxOffset) =>
        operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext } } memory
        && memory.Addend == rgctxOffset;

    private static bool IsZero(IOperand operand) => operand is Immediate { Value: 0 };

    public static void Run(ISILControlFlowGraph cfg, long initialisedFlagOffset)
        => Run(cfg, [initialisedFlagOffset]);

    internal static void Run(ISILControlFlowGraph cfg, IReadOnlyCollection<long> initialisedFlagOffsets,
        bool keepBareCalls = false, DominatorInfo? dominance = null, bool allowStaticFieldReloads = false)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        HashSet<LocalVariable>? redefined = null;
        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable destination)
            {
                if (definitions.ContainsKey(destination))
                    (redefined ??= []).Add(destination);
                definitions[destination] = instruction;
            }

        var removedAny = false;
        var discardedGuardLoads = new HashSet<Instruction>();
        List<(Block Continuation, TypeAnalysisContext Type)>? completedCctors = null;

        foreach (var guard in cfg.Blocks.ToList())
        {
            var established = keepBareCalls && MatchCctorGuard(guard, definitions) is { } match
                && match.Slow.Successors is [var continuation] && continuation == match.Fast ? match : null;
            if (!TryRemoveGuard(cfg, guard, initialisedFlagOffsets, definitions, discardedGuardLoads,
                    IsInitializedFlagValue, allowStaticFieldReloads))
                continue;
            removedAny = true;
            if (established is { } proof && guard.Successors is [var remaining] && remaining == proof.Fast
                && remaining.Predecessors is [var predecessor] && predecessor == guard)
                (completedCctors ??= []).Add((remaining, proof.Type));
        }

        if (completedCctors != null)
            RemoveRepeatedCctorReloads(cfg, definitions, completedCctors, discardedGuardLoads);
        if (!keepBareCalls)
            removedAny |= RemoveBareClassInitCalls(cfg);

        if (removedAny)
            DeadCodeEliminator.Run(cfg, discardedGuardLoads);

        bool IsInitializedFlagValue(Block block, int before, IOperand value)
        {
            // An SSA definition can dominate the store even if the intervening path contains
            // joins or back-edges. Order still matters for copies in the same block.
            for (var remaining = 16; value is LocalVariable local; remaining--)
            {
                if (remaining == 0 || redefined?.Contains(local) == true
                    || !definitions.TryGetValue(local, out var definition)
                    || definition is not { OpCode: OpCode.Move, Operands: [LocalVariable, var source] }
                    || cfg.FindBlockByInstruction(definition) is not { } definitionBlock)
                    return false;

                var index = definitionBlock.Instructions.IndexOf(definition);
                if (definitionBlock == block ? index >= before
                    : !(dominance ??= new DominatorInfo(cfg)).Dominates(definitionBlock, block))
                    return false;

                block = definitionBlock;
                before = index;
                value = source;
            }
            return value is Immediate { Value: 1 };
        }
    }

    private sealed record CctorGuard(TypeAnalysisContext Type, Instruction FlagLoad,
        Block Fast, Block Slow, Instruction Branch);

    private readonly record struct CctorFlag(TypeAnalysisContext Type, Instruction Load, LocalVariable ClassPointer,
        Block Ready, Block Slow, Instruction Branch);

    private static CctorFlag? MatchCctorFlag(Block guard, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (guard.Successors is not [var first, var second] || guard.Instructions.LastOrDefault() is not
                { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] } branch
            || (taken != first && taken != second)
            || !definitions.TryGetValue(condition, out var nonzero)
            || nonzero is not { OpCode: OpCode.Not, Operands: [_, LocalVariable isZero] }
            || !definitions.TryGetValue(isZero, out var comparison)
            || comparison is not { OpCode: OpCode.CheckEqual, Operands: [_, LocalVariable flag, Immediate { Value: 0 }] }
            || !definitions.TryGetValue(flag, out var flagLoad)
            || flagLoad is not { OpCode: OpCode.Move, MemoryAccessSize: 4, Operands: [_, MemoryOperand
                { Base: LocalVariable klass, Index: null, Scale: 0, Addend: CctorFinishedOffset64 }] }
            || FollowCopies(klass, definitions) is not LocalVariable originalClass
            || !definitions.TryGetValue(originalClass, out var classLoad)
            || classLoad is not { OpCode: OpCode.Move, Operands: [_, TypeAnalysisContext classType] })
            return null;

        return new CctorFlag(classType, flagLoad, klass, taken, taken == first ? second : first, branch);
    }

    private static CctorGuard? MatchCctorGuard(Block guard, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (MatchCctorFlag(guard, definitions) is not { } flag
            || flag.Slow.Predecessors is not [var predecessor] || predecessor != guard
            || flag.Slow.Instructions.FirstOrDefault(instruction => instruction.OpCode != OpCode.Nop) is not { } init
            || !InitializesClass(init, flag.ClassPointer, definitions))
            return null;

        return new CctorGuard(flag.Type, flag.Load, flag.Ready, flag.Slow, flag.Branch);
    }

    private static void RemoveRepeatedCctorReloads(ISILControlFlowGraph cfg,
        Dictionary<LocalVariable, Instruction> definitions,
        List<(Block Continuation, TypeAnalysisContext Type)> completedCctors, HashSet<Instruction> discardedLoads)
    {
        // Prefer the cached value when the preceding guard checked the same class
        // and there is no call or store between the two cctor checks.
        var removed = false;
        foreach (var guard in cfg.Blocks.ToArray())
        {
            if (MatchCctorGuard(guard, definitions) is not { } match
                || guard.Predecessors is not [var previous]
                || !completedCctors.Any(proof => proof.Continuation == previous && ReferenceEquals(proof.Type, match.Type))
                || !previous.Instructions.All(IsCctorCheckStep) || !guard.Instructions.All(IsCctorCheckStep)
                || !match.Slow.Instructions.SkipWhile(instruction => instruction.OpCode == OpCode.Nop)
                    .Skip(1).All(IsCctorCheckStep)
                || match.Slow.Successors.Count != 2 || !match.Slow.Successors.Contains(match.Fast)
                || match.Slow.Successors.FirstOrDefault(successor => successor != match.Fast) is not { } failure
                || failure.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                    [{ OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }] }])
                continue;

            HashSet<Block> region = [match.Slow];
            if (HasEscapingValues(cfg, region, match.Fast))
                continue;

            Excise(cfg, guard, match.Slow, match.Fast, region);
            discardedLoads.Add(match.FlagLoad);
            removed = true;
        }

        if (removed)
            cfg.RemoveUnreachableBlocks();

        static bool IsCctorCheckStep(Instruction instruction) => instruction.OpCode is
            OpCode.Nop or OpCode.Jump or OpCode.ConditionalJump or OpCode.Phi or OpCode.Not or OpCode.CheckEqual
            || instruction is { OpCode: OpCode.Move, Destination: LocalVariable };
    }

    internal static void RunClassReloads(MethodAnalysisContext method)
    {
        if (!SupportsClassReloads(method))
            return;

        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;
        if (!instructions.Any(IsClassInitCall))
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        var discardedLoads = new HashSet<Instruction>();
        RemoveSharedCctorGuards(cfg, definitions, discardedLoads);
        foreach (var guard in cfg.Blocks)
            TryKeepManagedReload(guard, definitions, discardedLoads);

        if (RemoveBareClassInitCalls(cfg) || discardedLoads.Count != 0)
            DeadCodeEliminator.Run(cfg, discardedLoads);
    }

    private static bool SupportsClassReloads(MethodAnalysisContext method) =>
        method.AppContext.InstructionSet is NewArmV8InstructionSet && method.AppContext.MetadataVersion == 29;

    // Multiple checks can share a single initializer, passing X0 through a phi. Match every
    // incoming edge before deleting the shared slow path and its inputs to the join.
    private static void RemoveSharedCctorGuards(ISILControlFlowGraph cfg,
        Dictionary<LocalVariable, Instruction> definitions, HashSet<Instruction> discardedLoads)
    {
        var changed = false;
        foreach (var initBlock in cfg.Blocks)
        {
            if (initBlock.Predecessors.Count < 2 || initBlock.Successors is not [var merge])
                continue;

            var init = initBlock.Instructions.FirstOrDefault(IsClassInitCall);
            if (init == null || initBlock.Instructions.Any(instruction => instruction != init
                    && instruction.OpCode is not (OpCode.Phi or OpCode.Nop or OpCode.Jump)))
                continue;

            var argIndex = init.OpCode == OpCode.Call ? 2 : 1;
            if (init.Operands.Count <= argIndex || init.Operands[argIndex] is not LocalVariable argument
                || !definitions.TryGetValue(argument, out var classPhi) || classPhi.OpCode != OpCode.Phi
                || classPhi.Operands.Count != initBlock.Predecessors.Count + 1
                || !initBlock.Instructions.Contains(classPhi))
                continue;

            // The join must immediately use a managed operation on that class. Otherwise removing
            // the guard could move an observable class initialization past unrelated code.
            var next = merge.Instructions.FirstOrDefault(instruction => instruction.OpCode is not (OpCode.Phi or OpCode.Nop)
                && instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable or Immediate or StringLiteral] });
            var declaringType = next switch
            {
                { IsCall: true, Operands: [MethodAnalysisContext { IsStatic: true } target, ..] } => target.DeclaringType,
                { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference { Field.IsStatic: true, Parent: null } field] }
                    => field.Field.DeclaringType,
                _ => null,
            };
            if (declaringType == null)
                continue;

            var initIndex = merge.Predecessors.IndexOf(initBlock);
            if (initIndex < 0)
                continue;

            var mergePhis = merge.Instructions.Where(instruction => instruction.OpCode == OpCode.Phi).ToArray();
            if (mergePhis.Any(phi => phi.Operands.Count != merge.Predecessors.Count + 1))
                continue;

            List<(Block Guard, CctorFlag Flag)> guards = [];
            HashSet<Block> region = [initBlock];
            for (var i = 0; i < initBlock.Predecessors.Count; i++)
            {
                var join = initBlock;
                var guard = initBlock.Predecessors[i];
                while (guard.Successors is [var successor] && successor == join
                       && guard.Predecessors is [var predecessor]
                       && guard.Instructions.All(instruction => instruction.OpCode is OpCode.Nop or OpCode.Jump)
                       && region.Add(guard))
                {
                    join = guard;
                    guard = predecessor;
                }

                if (MatchCctorFlag(guard, definitions) is not { } flag || flag.Ready != merge || flag.Slow != join
                    || !MetadataResolver.IsSameType(declaringType, flag.Type is RuntimeClassTypeAnalysisContext runtimeClass
                        ? runtimeClass.RepresentedType : flag.Type)
                    || !SameClassMetadataValue(classPhi.Operands[i + 1], flag.ClassPointer, definitions))
                    break;

                var readyIndex = merge.Predecessors.IndexOf(guard);
                if (readyIndex < 0 || mergePhis.Any(phi => !HasSameInput(phi, readyIndex, i)))
                    break;

                guards.Add((guard, flag));
            }

            if (guards.Count != initBlock.Predecessors.Count || HasEscapingValues(cfg, region, merge))
                continue;

            foreach (var phi in mergePhis)
                phi.RemoveOperandAt(initIndex + 1);
            merge.Predecessors.RemoveAt(initIndex);
            initBlock.Successors.Remove(merge);

            foreach (var (guard, flag) in guards)
            {
                guard.Successors.Remove(flag.Slow);
                flag.Slow.Predecessors.Remove(guard);
                flag.Branch.OpCode = OpCode.Jump;
                flag.Branch.SetOperands(merge);
                guard.CalculateBlockType();
                discardedLoads.Add(flag.Load);
            }
            changed = true;

            bool HasSameInput(Instruction phi, int readyIndex, int incomingIndex)
            {
                var slowValue = phi.Operands[initIndex + 1];
                if (slowValue is LocalVariable local && definitions.TryGetValue(local, out var definition)
                    && definition.OpCode == OpCode.Phi && initBlock.Instructions.Contains(definition))
                {
                    if (definition.Operands.Count != initBlock.Predecessors.Count + 1)
                        return false;
                    slowValue = definition.Operands[incomingIndex + 1];
                }

                return Equals(FollowCopies(slowValue, definitions), FollowCopies(phi.Operands[readyIndex + 1], definitions));
            }
        }

        if (changed)
            cfg.RemoveUnreachableBlocks();
    }

    // IL2CPP may reuse an earlier static field read when the class is ready and reload
    // it after the class initializer otherwise. In managed IL the reload itself handles
    // class initialization. Keep that read and its null check, and remove the native fork.
    private static void TryKeepManagedReload(Block guard, Dictionary<LocalVariable, Instruction> definitions,
        HashSet<Instruction> discardedLoads)
    {
        if (MatchCctorGuard(guard, definitions) is not (var classType, var flagLoad, var merge, var reload, var branch)
            || reload.Successors.Count != 2 || !reload.Successors.Contains(merge)
            || reload.Instructions.LastOrDefault() is not { OpCode: OpCode.ConditionalJump,
                Operands: [Block failure, LocalVariable isNull] } || failure == merge || !reload.Successors.Contains(failure))
            return;

        if (!definitions.TryGetValue(isNull, out var nullCheck)
            || nullCheck is not { OpCode: OpCode.CheckEqual, Operands: [_, LocalVariable receiver, Immediate { Value: 0 }] }
            || !definitions.TryGetValue(receiver, out var read)
            || read is not { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.IsStatic: true, Parent: null } field] }
            || !MetadataResolver.IsSameType(field.Field.DeclaringType, classType))
            return;

        var operations = reload.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        if (operations.Length < 4
            || operations[operations.Length - 3] != read || operations[operations.Length - 2] != nullCheck)
            return;

        // The only extra values on the initializer edge are metadata handles.
        for (var i = 1; i < operations.Length - 3; i++)
            if (operations[i] is not { OpCode: OpCode.Move, Operands: [LocalVariable, TypeAnalysisContext] })
                return;

        if (failure.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
            [{ OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }] }])
            return;

        var fastIndex = merge.Predecessors.IndexOf(guard);
        var slowIndex = merge.Predecessors.IndexOf(reload);
        if (fastIndex < 0 || slowIndex < 0)
            return;

        var phis = merge.Instructions.Where(instruction => instruction.OpCode == OpCode.Phi).ToArray();
        var mergesReloadedField = false;
        foreach (var phi in phis)
        {
            if (phi.Operands.Count != merge.Predecessors.Count + 1)
                return;

            var fast = FollowCopies(phi.Operands[fastIndex + 1], definitions);
            var slow = FollowCopies(phi.Operands[slowIndex + 1], definitions);
            if (Equals(fast, slow))
                continue;

            if (!ReferenceEquals(slow, receiver)
                || fast is not LocalVariable cached || !definitions.TryGetValue(cached, out var cachedRead)
                || cachedRead is not { OpCode: OpCode.Move, Operands: [_, FieldReference { Parent: null } cachedField] }
                || !ReferenceEquals(cachedField.Field, field.Field))
                return;

            mergesReloadedField = true;
        }

        if (!mergesReloadedField)
            return;

        foreach (var phi in phis)
            phi.RemoveOperandAt(fastIndex + 1);
        merge.Predecessors.RemoveAt(fastIndex);
        guard.Successors.Remove(merge);
        branch.OpCode = OpCode.Jump;
        branch.SetOperands(reload);
        guard.CalculateBlockType();
        discardedLoads.Add(flagLoad);
    }

    // wasm keeps the initialized-flag check inside the class-init function, so callers make bare unguarded
    // calls with no region to excise (just drop the call)
    private static bool RemoveBareClassInitCalls(ISILControlFlowGraph cfg)
    {
        var removedAny = false;

        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (!IsClassInitCall(instruction))
                    continue;

                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
                removedAny = true;
            }
        }

        return removedAny;
    }

    private static bool TryRemoveGuard(ISILControlFlowGraph cfg, Block guard,
        IReadOnlyCollection<long> initialisedFlagOffsets, IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<Instruction> discardedGuardLoads, Func<Block, int, IOperand, bool> isInitializedFlagValue,
        bool allowStaticFieldReloads)
    {
        if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2
            || guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        var condition = guard.Instructions[^1].Operands[1];
        var flagLoad = FindFlagLoad(condition, definitions);
        var metadataFlagAddress = flagLoad?.Operands[1] is
            MemoryOperand { IsConstant: true } metadataFlag ? (long?)metadataFlag.Addend : null;
        var hasKnownClassInitFlag = flagLoad?.Operands[1] is MemoryOperand
            { Index: null, Scale: 0, Base: LocalVariable } classFlag
            && initialisedFlagOffsets.Contains(classFlag.Addend);
        var first = guard.Successors[0];
        var second = guard.Successors[1];

        Block initEntry;
        HashSet<Block> region;
        if (TryCollectRegion(cfg, guard, first, second, false, metadataFlagAddress, isInitializedFlagValue,
                out region, allowStaticFieldReloads)
            && IsRecognizedInitializationRegion(region, hasKnownClassInitFlag, metadataFlagAddress.HasValue))
            initEntry = first;
        else if (TryCollectRegion(cfg, guard, second, first, false, metadataFlagAddress, isInitializedFlagValue,
                     out region, allowStaticFieldReloads)
            && IsRecognizedInitializationRegion(region, hasKnownClassInitFlag, metadataFlagAddress.HasValue))
            initEntry = second;
        else
            return false;

        var merge = initEntry == first ? second : first;
        Excise(cfg, guard, initEntry, merge, region);

        if (metadataFlagAddress.HasValue && flagLoad != null)
            discardedGuardLoads.Add(flagLoad);

        // These reads can be discarded only for the class pointer consumed by the recognized
        // initializer. The same offsets can occur in ordinary managed objects.
        if (flagLoad?.Operands[1] is MemoryOperand
                { Index: null, Scale: 0, Base: LocalVariable classPointer } flag
            && initialisedFlagOffsets.Contains(flag.Addend)
            && region.SelectMany(block => block.Instructions)
                .Any(instruction => InitializesClass(instruction, classPointer, definitions)))
        {
            discardedGuardLoads.Add(flagLoad);

            // Native code can obtain Il2CppClass* by dereferencing a metadata slot. Once
            // this guard is gone, drop that read too if no other instructions use its result.
            if (definitions.TryGetValue(classPointer, out var load)
                && load is { OpCode: OpCode.Move, Operands: [_, MemoryOperand
                    { Base: LocalVariable, Index: null, Scale: 0, Addend: 0 }] })
                discardedGuardLoads.Add(load);
        }

        return true;
    }

    private static Instruction? FindFlagLoad(IOperand condition,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        IOperand? value = condition;
        while (value is LocalVariable local && visited.Add(local)
               && definitions.TryGetValue(local, out var definition))
        {
            if (definition is { OpCode: OpCode.Move, Operands: [_, MemoryOperand] })
                return definition;

            value = definition switch
            {
                { OpCode: OpCode.Move or OpCode.Not, Operands: [_, var moveInput] } => moveInput,
                { OpCode: OpCode.And, Operands: [_, var andInput, Immediate { Value: 1 }] } => andInput,
                { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                    Operands: [_, var compareInput, Immediate { Value: 0 or 1 }] } => compareInput,
                { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                    Operands: [_, Immediate { Value: 0 or 1 }, var reverseCompareInput] } => reverseCompareInput,
                _ => null
            };
        }

        return null;
    }

    private static bool InitializesClass(Instruction instruction, LocalVariable classPointer,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        if (!IsClassInitCall(instruction))
            return false;

        var argumentIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
        if (argumentIndex >= instruction.Operands.Count)
            return false;

        // Class pointer checks often use a scratch register, then copy it into X0 for the call.
        var visited = new HashSet<LocalVariable>();
        var argument = instruction.Operands[argumentIndex];
        while (argument is LocalVariable local && visited.Add(local))
        {
            if (ReferenceEquals(local, classPointer))
                return true;

            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, OperandWidth: null or 64,
                    Operands: [_, LocalVariable source] })
                return false;
            argument = source;
        }

        return false;
    }

    private static bool IsClassInitCall(Instruction instruction) =>
        instruction.IsCall
        && instruction.Operands is [StringLiteral { Value: ClassInitExport or ClassInitActual or ClassInitCodegen }, ..];

    private static bool IsMetadataInitCall(Instruction instruction) =>
        instruction.IsCall
        && instruction.Operands is [StringLiteral { Value: InitializeRuntimeMetadata or InitializeMethod }, ..];

    private static bool IsRecognizedInitializationRegion(HashSet<Block> region, bool hasKnownClassInitFlag,
        bool hasMetadataFlagAddress)
    {
        var instructions = region.SelectMany(block => block.Instructions);
        if (instructions.Any(IsClassInitCall))
            return hasKnownClassInitFlag;

        return hasMetadataFlagAddress && instructions.Any(IsMetadataInitCall);
    }

    private static bool TryExcise(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        bool allowMetadataInitWithoutStore, out HashSet<Block> region)
    {
        if (!TryCollectRegion(cfg, guard, initEntry, merge, allowMetadataInitWithoutStore, null, null, out region))
            return false;

        Excise(cfg, guard, initEntry, merge, region);
        return true;
    }

    private static bool TryCollectRegion(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        bool allowMetadataInitWithoutStore, long? metadataFlagAddress, Func<Block, int, IOperand, bool>? isInitializedFlagValue,
        out HashSet<Block> region, bool allowStaticFieldReloads = false)
    {
        region = [];

        if (initEntry == merge || initEntry == guard || merge == cfg.EntryBlock || merge == cfg.ExitBlock)
            return false;

        var sawMetadataInit = false;
        var sawClassInit = false;
        var sawFlagStore = false;
        var reconverges = false;

        var queue = new Queue<Block>();
        queue.Enqueue(initEntry);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (block == merge)
            {
                reconverges = true;
                continue;
            }

            // The region must not run into the method boundary or loop back through the guard.
            if (block == cfg.EntryBlock || block == cfg.ExitBlock || block == guard)
                return false;

            if (!region.Add(block))
                continue;

            if (!ClassifyBlock(block, metadataFlagAddress, isInitializedFlagValue,
                    ref sawMetadataInit, ref sawClassInit, ref sawFlagStore))
                return false;

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }

        if (!reconverges || !(sawClassInit || sawMetadataInit && (sawFlagStore || allowMetadataInitWithoutStore)))
            return false;

        var collected = region;
        foreach (var block in collected)
        {
            if (block.Predecessors.Any(predecessor => predecessor != guard && !collected.Contains(predecessor)))
                return false;
            if (block.Successors.Any(successor => successor != merge && !collected.Contains(successor)))
                return false;
        }

        return HasOnlyRuntimeMemoryReads(cfg, region, guard, merge,
                allowStaticFieldReloads && metadataFlagAddress.HasValue && sawMetadataInit && sawFlagStore && !sawClassInit)
            && !HasEscapingValues(cfg, region, merge);
    }

    private static bool HasOnlyRuntimeMemoryReads(ISILControlFlowGraph cfg, HashSet<Block> region,
        Block guard, Block merge, bool allowStaticFieldReloads)
    {
        var dynamicLoads = region.SelectMany(block => block.Instructions).Where(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [LocalVariable, MemoryOperand { IsConstant: false }] }).ToList();
        if (dynamicLoads.Count == 0)
            return true;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        var runtimeLoads = new HashSet<Instruction>();
        foreach (var call in region.SelectMany(block => block.Instructions).Where(instruction => instruction.IsCall))
        {
            var argumentIndex = call.OpCode == OpCode.Call ? 2 : 1;
            if (argumentIndex >= call.Operands.Count)
                continue;

            var path = new List<Instruction>();
            if (TraceMetadataSlot(call.Operands[argumentIndex], path, [], 0))
                runtimeLoads.UnionWith(path);
        }

        return dynamicLoads.All(instruction => runtimeLoads.Contains(instruction)
            || allowStaticFieldReloads && IsEquivalentGuardReload(instruction, guard, merge, region, definitions));

        // Accept bounded copies and +0 indirections rooted in an absolute metadata slot. A
        // random object-field read in the same region must retain its possible null exception.
        bool TraceMetadataSlot(IOperand operand, List<Instruction> path, HashSet<LocalVariable> visited, int depth)
        {
            if (depth >= 16 || operand is not LocalVariable local || !visited.Add(local)
                || !definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, var source] })
                return false;

            path.Add(definition);
            return source switch
            {
                MemoryOperand { IsConstant: true } => true,
                TypeAnalysisContext and not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext) => true,
                LocalVariable copy => TraceMetadataSlot(copy, path, visited, depth + 1),
                MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable pointer }
                    => TraceMetadataSlot(pointer, path, visited, depth + 1),
                _ => false,
            };
        }
    }

    private static bool IsEquivalentGuardReload(Instruction reload, Block guard, Block merge,
        HashSet<Block> region, Dictionary<LocalVariable, Instruction> definitions)
    {
        var staticOwner = StaticFieldOwner(reload);
        var receiverField = ReceiverFieldLoad(reload);
        var fastIndex = merge.Predecessors.IndexOf(guard) + 1;
        if (fastIndex == 0 || reload.Destination is not LocalVariable local
            || staticOwner == null && receiverField == null)
            return false;

        // The surviving edge must already supply the same load to each phi. Native
        // metadata initialization can cause the compiler to reload a field of this after
        // the helper call, just as it can reload a class's static storage pointer.
        var replaced = false;
        foreach (var phi in merge.Instructions)
        {
            if (phi.OpCode != OpCode.Phi || phi.Operands.Count != merge.Predecessors.Count + 1)
                continue;
            var usesReload = false;
            for (var i = 1; i < phi.Operands.Count; i++)
                if (region.Contains(merge.Predecessors[i - 1])
                    && ReferenceEquals(FollowCopies(phi.Operands[i], definitions), local))
                    usesReload = true;
            if (!usesReload)
                continue;

            if (FollowCopies(phi.Operands[fastIndex], definitions) is not LocalVariable fastValue
                || !definitions.TryGetValue(fastValue, out var fastLoad)
                || !SameLoad(fastLoad))
                return false;

            replaced = true;
        }
        return replaced;

        bool SameLoad(Instruction fastLoad) => staticOwner != null
            ? MetadataResolver.IsSameType(staticOwner, StaticFieldOwner(fastLoad))
            : receiverField == ReceiverFieldLoad(fastLoad);

        (LocalVariable Receiver, long Offset, int Size, int? Width)? ReceiverFieldLoad(Instruction load)
        {
            if (load is not { OpCode: OpCode.Move, MemoryAccessSize: int size, Operands: [LocalVariable,
                    MemoryOperand { Base: LocalVariable pointer, Index: null, Scale: 0, Addend: > 0 } address] }
                || FollowCopies(pointer, definitions) is not LocalVariable { IsThis: true } receiver)
                return null;

            return (receiver, address.Addend, size, load.OperandWidth);
        }

        TypeAnalysisContext? StaticFieldOwner(Instruction load)
        {
            if (load is not { OpCode: OpCode.Move, MemoryAccessSize: 8, Operands: [LocalVariable,
                    MemoryOperand { Base: LocalVariable klass, Index: null, Scale: 0,
                        Addend: LocalVariables.StaticFieldsOffset64 }] }
                || FollowCopies(klass, definitions) is not LocalVariable originalClass
                || !definitions.TryGetValue(originalClass, out var classLoad)
                || classLoad is not { OpCode: OpCode.Move, Operands: [_, TypeAnalysisContext type] })
                return null;

            return type switch
            {
                RuntimeClassTypeAnalysisContext runtime => runtime.RepresentedType,
                RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext => null,
                _ => type,
            };
        }
    }

    // A local computed inside a discarded region must not become an uninitialized value in the
    // surviving method. Merge-phi inputs from discarded edges are removed by Excise itself.
    private static bool HasEscapingValues(ISILControlFlowGraph cfg, HashSet<Block> region, Block merge)
    {
        var definitions = new HashSet<LocalVariable>(region.SelectMany(block => block.Instructions)
            .Select(instruction => instruction.Destination).OfType<LocalVariable>());
        if (definitions.Count == 0)
            return false;

        foreach (var block in cfg.Blocks)
        {
            if (region.Contains(block))
                continue;

            foreach (var instruction in block.Instructions)
            {
                if (block == merge && instruction.OpCode == OpCode.Phi)
                {
                    if (instruction.Operands.Count != merge.Predecessors.Count + 1)
                        return true;

                    for (var i = 1; i < instruction.Operands.Count; i++)
                    {
                        if (!region.Contains(merge.Predecessors[i - 1])
                            && instruction.Operands[i] is LocalVariable input && definitions.Contains(input))
                            return true;
                    }
                }
                else if (DeadCodeEliminator.UsedLocals(instruction).Any(definitions.Contains))
                    return true;
            }
        }

        return false;
    }

    // A region block is acceptable only if every instruction is intra-region control flow, an init
    // call, the flag store, or otherwise side-effect-free (writes a local, not memory). A managed call
    // or any other store would have an effect we cannot silently drop, so it disqualifies the region.
    private static bool ClassifyBlock(Block block, long? metadataFlagAddress,
        Func<Block, int, IOperand, bool>? isInitializedFlagValue, ref bool sawMetadataInit, ref bool sawClassInit,
        ref bool sawFlagStore)
    {
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            switch (instruction.OpCode)
            {
                case OpCode.Jump:
                    break;

                case OpCode.Call or OpCode.CallVoid:
                    if (instruction.Operands is not [StringLiteral { Value: var name }, ..])
                        return false;

                    if (name is InitializeRuntimeMetadata or InitializeMethod)
                        sawMetadataInit = true;
                    else if (name is ClassInitExport or ClassInitActual or ClassInitCodegen)
                        sawClassInit = true;
                    else
                        return false;

                    break;

                case OpCode.Move when instruction.Operands is [MemoryOperand { IsConstant: true } flag, var value]
                    && metadataFlagAddress == flag.Addend
                    && isInitializedFlagValue?.Invoke(block, index, value) == true:
                    sawFlagStore = true;
                    break;

                default:
                    if (!IsSideEffectFree(instruction))
                        return false;
                    break;
            }
        }

        return true;
    }

    // True for instructions that only compute a value into a local (or do nothing). A store - any
    // instruction whose destination operand is a memory or field reference rather than a local - is
    // excluded, as is anything that transfers control or merges values (phi/return/indirect).
    private static bool IsSideEffectFree(Instruction instruction) =>
        instruction.OpCode switch
        {
            OpCode.Nop => true,
            OpCode.Move when instruction.Operands is [_, FieldReference or ArrayAccess or ArrayLength] => false,
            OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate
                or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned)
                => instruction.Operands is [LocalVariable, ..],
            _ => false,
        };

    internal static void Excise(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        HashSet<Block> region, Block? continuation = null)
    {
        // 1. Repair the merge's phis: drop the inputs from the region's back-edges.
        for (var i = merge.Predecessors.Count - 1; i >= 0; i--)
        {
            if (!region.Contains(merge.Predecessors[i]))
                continue;

            foreach (var phi in merge.Instructions)
                if (phi.OpCode == OpCode.Phi && 1 + i < phi.Operands.Count)
                    phi.RemoveOperandAt(1 + i);

            merge.Predecessors.RemoveAt(i);
        }

        // 2. Fold the guard so it goes straight to the merge.
        guard.Successors.Remove(initEntry);
        initEntry.Predecessors.Remove(guard);

        var terminator = guard.Instructions[^1];
        terminator.OpCode = OpCode.Jump;
        terminator.SetOperands(continuation ?? merge);
        guard.CalculateBlockType();

        // 3. Delete the region.
        foreach (var block in region)
        {
            foreach (var successor in block.Successors)
                successor.Predecessors.Remove(block);
            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
            cfg.Blocks.Remove(block);
        }
    }
}
