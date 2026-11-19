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
    private const long InitialisedFlagOffset32 = 0xBD;

    // Other Unity layouts expose the equivalent class-initialization state as cctor_finished.
    private const long CctorFinishedOffset64 = 0xE0;
    private const long CctorFinishedOffset32 = 0x74;

    // Offset of MethodInfo::rgctx_data
    private const long MethodRgctxOffset64 = 0x38;
    private const long MethodRgctxOffset32 = 0x1C;

    public static void Run(MethodAnalysisContext method)
    {
        var flagOffsets = method.AppContext.Binary.is32Bit
            ? new[] { InitialisedFlagOffset32, CctorFinishedOffset32 }
            : new[] { InitialisedFlagOffset64, CctorFinishedOffset64 };
        var cfg = method.ControlFlowGraph!;
        if (method.AppContext.InstructionSet is NewArmV8InstructionSet && method.AppContext.MetadataVersion == 29)
            RemoveArm64ClassMetadataGuards(method, cfg);
        Run(cfg, flagOffsets);
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
                || !TryGetArm64ClassFlag(condition, definitions, out var flagLoad, out var klass, out var trueWhenReady))
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

    private static bool TryGetArm64ClassFlag(LocalVariable condition,
        Dictionary<LocalVariable, Instruction> definitions,
        out Instruction load, out LocalVariable klass, out bool trueWhenReady)
    {
        load = null!;
        klass = null!;
        trueWhenReady = false;

        if (!definitions.TryGetValue(condition, out var test))
            return false;
        var inverted = test is { OpCode: OpCode.Not, Operands: [_, LocalVariable] };
        if (inverted && !definitions.TryGetValue((LocalVariable)test.Operands[1], out test))
            return false;

        if (test!.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || test.Operands is not [_, LocalVariable masked, Immediate { Value: 0 }]
            || !definitions.TryGetValue(masked, out var mask)
            || mask is not { OpCode: OpCode.And, Operands: [_, LocalVariable flag, Immediate { Value: 1 }] }
            || FollowCopies(flag, definitions) is not LocalVariable flagSource
            || !definitions.TryGetValue(flagSource, out var candidate)
            || candidate is not { OpCode: OpCode.Move, MemoryAccessSize: 1 or 2, Operands: [_, MemoryOperand
                { Base: LocalVariable classPointer, Index: null, Scale: 0, Addend: 0x132 }] })
            return false;

        klass = classPointer;
        load = candidate;
        trueWhenReady = (test.OpCode == OpCode.CheckNotEqual) != inverted;
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

    private static void Run(ISILControlFlowGraph cfg, IReadOnlyCollection<long> initialisedFlagOffsets)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var removedAny = false;
        var discardedGuardLoads = new HashSet<Instruction>();

        foreach (var guard in cfg.Blocks.ToList())
            removedAny |= TryRemoveGuard(cfg, guard, initialisedFlagOffsets, definitions, discardedGuardLoads);

        removedAny |= RemoveBareClassInitCalls(cfg);

        if (removedAny)
            DeadCodeEliminator.Run(cfg, discardedGuardLoads);
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
        HashSet<Instruction> discardedGuardLoads)
    {
        if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2
            || guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        var condition = guard.Instructions[^1].Operands[1];
        var flagLoad = FindFlagLoad(condition, definitions);
        var metadataFlagAddress = flagLoad?.Operands[1] is
            MemoryOperand { IsConstant: true } metadataFlag ? (long?)metadataFlag.Addend : null;
        var first = guard.Successors[0];
        var second = guard.Successors[1];

        if (!TryExcise(cfg, guard, first, second, false, out var region, metadataFlagAddress)
            && !TryExcise(cfg, guard, second, first, false, out region, metadataFlagAddress))
            return false;

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

    private static bool TryExcise(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        bool allowMetadataInitWithoutStore, out HashSet<Block> region, long? metadataFlagAddress = null)
    {
        if (!TryCollectRegion(cfg, guard, initEntry, merge, allowMetadataInitWithoutStore, metadataFlagAddress, out region))
            return false;

        Excise(cfg, guard, initEntry, merge, region);
        return true;
    }

    private static bool TryCollectRegion(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        bool allowMetadataInitWithoutStore, long? metadataFlagAddress, out HashSet<Block> region)
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

            if (!ClassifyBlock(block, metadataFlagAddress, ref sawMetadataInit, ref sawClassInit, ref sawFlagStore))
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

        return HasOnlyRuntimeMemoryReads(cfg, region) && !HasEscapingValues(cfg, region, merge);
    }

    private static bool HasOnlyRuntimeMemoryReads(ISILControlFlowGraph cfg, HashSet<Block> region)
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

        return dynamicLoads.All(runtimeLoads.Contains);

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
        ref bool sawMetadataInit, ref bool sawClassInit, ref bool sawFlagStore)
    {
        foreach (var instruction in block.Instructions)
        {
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
                    && (value is Immediate { Value: 1 } || value is LocalVariable flagValue
                        && block.Instructions.Any(candidate => candidate is
                            { OpCode: OpCode.Move, Operands: [LocalVariable destination, Immediate { Value: 1 }] }
                            && ReferenceEquals(destination, flagValue))):
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
