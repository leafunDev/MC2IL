using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;
using MC2IL.Core.Utils.AsmResolver;

namespace MC2IL.Core;

public static class IlGenerator
{
    private const string HelpersNamespace = "MC2ILInjected";
    private const string HelpersTypeName = "MC2ILHelpers";
    private const string NoteIssueMethodName = "NoteDecompilerIssue";

    public static void InjectHelpersType(ApplicationAnalysisContext appContext)
    {
        var helpersType = appContext.InjectTypeIntoAllAssemblies(
            HelpersNamespace,
            HelpersTypeName,
            null,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        helpersType.InjectMethodToAllAssemblies(
            NoteIssueMethodName,
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [appContext.SystemTypes.SystemStringType]);
    }

    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        var graph = context.ControlFlowGraph!;
        var assembly = context.DeclaringType!.DeclaringAssembly;
        var module = definition.DeclaringModule!;
        var factory = module.CorLibTypeFactory;

        var noteIssueContext = assembly
            .GetTypeByFullName($"{HelpersNamespace}.{HelpersTypeName}")?.Methods.FirstOrDefault(m => m.Name == NoteIssueMethodName);

        var writeLine = noteIssueContext != null
            ? noteIssueContext.ToMethodDescriptor()
            : factory.CorLibScope
                .CreateTypeReference("System", "Console")
                .CreateMemberReference("WriteLine", MethodSignature.CreateStatic(factory.Void, [factory.String]));

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = false // Set after all CIL rewrites and branch fixups.
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in graph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            var elementOperand = operand is AddressOf { Target: ArrayAccess elementAddress } ? elementAddress : operand;

            if (elementOperand is ArrayAccess arrayAccess)
            {
                local = arrayAccess.Array;

                if (arrayAccess.Index is LocalVariable index && !context.Locals.Contains(index))
                    context.Locals.Add(index);
            }

            if (operand is ArrayLength arrayLength)
                local = arrayLength.Array;

            if (operand is AddressOf { Target: LocalVariable addressed })
                local = addressed;

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        // An unresolved native load is not a managed object just because type recovery failed.
        // Infer its machine-width scalar/pointer type only after all semantic recovery passes ran,
        // and only when every remaining definition is a raw memory load.
        foreach (var local in context.Locals)
            if (local.Type == null && InferUnknownMemoryLoadType(local, context) is { } inferredType)
                local.Type = inferredType;

        foreach (var local in context.Locals)
            if (local.Type == null && IsUnknownNativeAddress(local, context))
                local.Type = context.AppContext.SystemTypes.SystemIntPtrType;

        // Map ISIL locals to IL
        Dictionary<LocalVariable, CilLocalVariable> locals = [];
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // Use object if type couldn't be determined, or if it's void, which no locals sig can hold
            if (local.Type != null && local.Type != context.AppContext.SystemTypes.SystemVoidType)
                ilType = local.Type.ToTypeSignature();
            else
                ilType = module.CorLibTypeFactory.Object;

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        /* foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            body.Instructions.Add(CilOpCodes.Ldstr, instruction.ToString());
            body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!));
        }
        body.Instructions.Add(CilOpCodes.Ldstr, "-------------------------------------------------------------------------");
        body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!)); */

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];
        HashSet<LocalVariable>? fieldReadLocals = null;
        var graphUsedLocals = new HashSet<LocalVariable>();
        foreach (var graphInstruction in graph.Instructions)
            graphUsedLocals.UnionWith(DeadCodeEliminator.UsedLocals(graphInstruction));

        var isIteratorMoveNext = context.Name == "MoveNext" && context.DeclaringType is { } declaringType
            && IteratorFactoryRecovery.IsIteratorType(declaringType);
        foreach (var block in GetCilBlockOrder(graph, isIteratorMoveNext))
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals, writeLine,
                    instructionMap, graphUsedLocals);
                instructionMap.Add(instruction, generated);

                if (instruction is { OpCode: OpCode.Move, Operands:
                    [LocalVariable { Type: { } localType } destination, FieldReference { Field.IsStatic: false }] }
                    && IsManagedReference(localType))
                    (fieldReadLocals ??= []).Add(destination);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions[^1];
            Block? fallthrough = null;
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, graph);
                // Both outcomes can share a successor in the CFG.
                fallthrough = block.Successors.FirstOrDefault(s => s != trueTarget)
                    ?? block.Successors.FirstOrDefault() ?? graph.ExitBlock;
            }
            else if (lastInstruction.OpCode is not (OpCode.Jump or OpCode.Return or OpCode.IndirectJump or OpCode.Throw))
            {
                fallthrough = block.Successors.FirstOrDefault() ?? graph.ExitBlock;
            }

            if (fallthrough != null)
            {
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                body.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, fallthrough));
            }
        }

        // Keep simple temporaries out of logging calls, conditions, and returns. Run this after
        // generating every block so a single-use value can also be traced across a call's fallthrough.
        // Recover direct field expressions when IL2CPP kept one field value alive across
        // a null guard and an immediate member read. Calls and writes form barriers.
        InlineRepeatedFieldReads(graph, instructionMap, locals, body);
        RemoveDominatedStackSlotInitializers(graph, instructionMap);
        var inlineLocals = FindSingleUseCilLocals(body, locals);
        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            for (var i = 0; i < block.Instructions.Count; i++)
            {
                InlineReferenceComparisonInputs(block, i, graph, instructionMap, inlineLocals, body);
                // Keep the field and ldlen together so the comparison can inline the full expression.
                InlineSingleUseArrayLengthReceiver(block, i, graph, instructionMap, inlineLocals, body);
                InlineSingleUseFirstInput(block, i, graph, instructionMap, inlineLocals);
                InlineSingleUseComparisonInput(block, i, graph, instructionMap, inlineLocals, body);
                InlineSingleUseCallArguments(block, i, graph, instructionMap, inlineLocals, body);
                if (fieldReadLocals != null)
                    InlineSingleUseFieldReceiver(block, i, graph, fieldReadLocals, instructionMap, inlineLocals, body);
                InlineSingleUseLogArgument(block, i, graph, instructionMap, inlineLocals);
                InlineSingleUseBranchCondition(block, i, graph, instructionMap, inlineLocals);
                InlineSingleUseReturnValue(block, i, graph, instructionMap, inlineLocals);
            }
        }

        var exitEntry = new CilInstruction(CilOpCodes.Ret);
        var unresolvedEntry = new CilInstruction(CilOpCodes.Ldstr, "MC2IL could not recover the control flow.");
        blockEntryMap[graph.ExitBlock] = context.IsVoid ? exitEntry : unresolvedEntry;
        var needsExit = body.Instructions.Count == 0 && context.IsVoid;
        var needsUnresolved = body.Instructions.Count == 0 && !context.IsVoid;

        void SetBranchTarget(CilInstruction branch, CilInstruction? target)
        {
            target ??= unresolvedEntry;
            needsExit |= ReferenceEquals(target, exitEntry);
            needsUnresolved |= ReferenceEquals(target, unresolvedEntry);
            branch.Operand = new CilInstructionLabel(target);
        }

        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                var target = instruction.Operands[0] switch
                {
                    Block block => ResolveBlockEntryInstruction(block, blockEntryMap),
                    Instruction targetInstruction when instructionMap.TryGetValue(targetInstruction, out var targetIl)
                        && targetIl.Count != 0 => targetIl[0],
                    _ => null,
                };
                if (target == null)
                    context.AddWarning($"Unable to resolve branch target: {instruction}");
                SetBranchTarget(ilBranch, target);
            }
        }

        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
                context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
            SetBranchTarget(branchInstruction, target);
        }

        if (body.Instructions.Count > 0)
        {
            var entry = ResolveBlockEntryInstruction(graph.EntryBlock, blockEntryMap);
            if (!ReferenceEquals(entry, body.Instructions[0]))
            {
                var transfer = new CilInstruction(CilOpCodes.Br);
                SetBranchTarget(transfer, entry);
                body.Instructions.Insert(0, transfer);
            }
        }

        // Add analysis warnings
        var instructions = body.Instructions;
        foreach (var warning in context.AnalysisWarnings)
        {
            instructions.Add(CilOpCodes.Ldstr, Diagnostic("Warning: " + warning));
            instructions.Add(CilOpCodes.Call, writeLine);
        }

        if (needsExit)
            instructions.Add(exitEntry);
        if (needsUnresolved)
        {
            instructions.Add(unresolvedEntry);
            EmitUnrecoveredReturn(context, definition, writeLine);
        }

        CilSingleUseMathInliner.Run(body, locals);
        CilBranchCleanup.Run(body);
        // The expression passes can leave their original constant stack-slot initializer unread.
        RemoveUnreadSimpleLocalStores(body);
        RemoveDeadLocalAssignments(body, instructionMap, locals);
        SetRecoveredMaxStack(body);
    }

    private static Dictionary<LocalVariable, CilLocalVariable> FindSingleUseCilLocals(CilMethodBody body,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        // Constructor fusion moves arguments into newobj and suppresses their original ISIL call.
        // Count the emitted reads as well, so a captured receiver's initialization survives.
        var readOnce = new HashSet<CilLocalVariable>();
        var ineligible = new HashSet<CilLocalVariable>();
        foreach (var instruction in body.Instructions)
        {
            if (instruction.Operand is not CilLocalVariable local)
                continue;
            if (instruction.OpCode.Code is CilCode.Ldloca or CilCode.Ldloca_S
                || instruction.OpCode.Code is CilCode.Ldloc or CilCode.Ldloc_S && !readOnce.Add(local))
                ineligible.Add(local);
        }

        // Each rewrite transfers the existing loads of other locals, so the initial counts
        // are a safe upper bound throughout this pass.
        return locals.Where(pair => readOnce.Contains(pair.Value) && !ineligible.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static void RemoveDeadLocalAssignments(CilMethodBody body,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var readLocals = new HashSet<CilLocalVariable>();
        foreach (var instruction in body.Instructions)
        {
            if (instruction.Operand is CilLocalVariable local
                && instruction.OpCode.Code is CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloca or CilCode.Ldloca_S)
                readLocals.Add(local);
            else
            {
                var localIndex = instruction.OpCode.Code switch
                {
                    CilCode.Ldloc_0 => 0,
                    CilCode.Ldloc_1 => 1,
                    CilCode.Ldloc_2 => 2,
                    CilCode.Ldloc_3 => 3,
                    _ => -1,
                };
                if (localIndex >= 0 && localIndex < body.LocalVariables.Count)
                    readLocals.Add(body.LocalVariables[localIndex]);
            }
        }

        foreach (var pair in instructionMap)
        {
            var source = pair.Key;
            var generated = pair.Value;
            if (source is not { OpCode: OpCode.Move, Operands: [LocalVariable destination, var value] }
                || !locals.TryGetValue(destination, out var cilLocal)
                || readLocals.Contains(cilLocal))
                continue;

            var pureValue = value is LocalVariable or Immediate or StringLiteral or FloatLiteral or DoubleLiteral or TypeAnalysisContext;
            var diagnosticCall = value is MemoryOperand
                ? generated.FindLastIndex(emitted => emitted.OpCode.Code is CilCode.Call or CilCode.Callvirt)
                : -1;
            if (!pureValue && diagnosticCall < 0)
                continue;

            // Keep any unsupported-read diagnostic, but drop its dead typed placeholder. Pure
            // native-only handle stores can be erased entirely; nops preserve branch labels.
            var firstInstructionToRemove = diagnosticCall >= 0 ? diagnosticCall + 1 : 0;
            for (var i = firstInstructionToRemove; i < generated.Count; i++)
            {
                generated[i].OpCode = CilOpCodes.Nop;
                generated[i].Operand = null;
            }
        }
    }

    private static void InlineRepeatedFieldReads(ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        foreach (var producer in graph.Instructions.ToArray())
        {
            if (producer is not { OpCode: OpCode.Move, Operands:
                    [LocalVariable { Type: { } valueType } value, FieldReference { Parent: null } field] }
                || !(field.Field.IsStatic && field.Field.Name == "Instance"
                    || !field.Field.IsStatic && field.Local.IsThis)
                || !IsManagedReference(valueType)
                || !MetadataResolver.IsSameType(valueType, field.Field.FieldType)
                || CountLocalUses(graph, value) != 2
                || graph.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, value)) != 1
                || graph.Instructions.Any(instruction => instruction.Destination is FieldReference destination
                    && destination.Field == field.Field))
                continue;

            var uses = graph.Instructions.Where(instruction => ReadsLocal(instruction, value)).ToArray();
            if (uses.Length < 2)
                continue;

            var nullChecks = uses.Where(instruction => instruction is
                    { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                        Operands: [_, LocalVariable checkedValue, Immediate { Value: 0 }] }
                    && checkedValue == value)
                .ToArray();
            var memberReads = uses.Where(instruction => IsInstanceMemberRead(instruction, value)).ToArray();
            if (nullChecks.Length != 1 || memberReads.Length != 1)
                continue;

            var nullCheck = nullChecks[0];
            var memberRead = memberReads[0];
            var hiddenReads = uses.Where(instruction => instruction != nullCheck && instruction != memberRead).ToArray();
            var dominates = InstructionDominates(graph, nullCheck, memberRead);
            var safeProducer = CanRepeatFieldReadBetween(graph, producer, nullCheck);
            var safeUse = CanRepeatFieldReadBetween(graph, nullCheck, memberRead);
            if (nullCheck == memberRead || !dominates || !safeProducer || !safeUse
                || hiddenReads.Any(instruction => !IsDiscardableInstanceHeaderRead(graph, instruction, value)))
                continue;

            if (!locals.TryGetValue(value, out var ilLocal)
                || !instructionMap.TryGetValue(producer, out var producerIl)
                || !TryGetFieldLoadExpression(field, producerIl, ilLocal, out var fieldLoads))
                continue;

            var sourceUses = new[] { nullCheck, memberRead };
            var localLoads = sourceUses.SelectMany(use => instructionMap.TryGetValue(use, out var useIl)
                    ? useIl.Select((instruction, index) => (UseIl: useIl, Index: index, Instruction: instruction))
                    : [])
                .Where(item => item.Instruction.OpCode is var opcode
                    && (opcode == CilOpCodes.Ldloc || opcode == CilOpCodes.Ldloc_S)
                    && ReferenceEquals(item.Instruction.Operand, ilLocal))
                .ToArray();
            if (localLoads.Length != sourceUses.Sum(use => CountInstructionLocalReads(use, value)))
                continue;

            foreach (var item in localLoads)
            {
                var bodyIndex = body.Instructions.IndexOf(item.Instruction);
                if (bodyIndex < 0)
                    continue;

                item.Instruction.OpCode = fieldLoads[0].OpCode;
                item.Instruction.Operand = fieldLoads[0].Operand;
                for (var i = 1; i < fieldLoads.Length; i++)
                {
                    var additionalLoad = new CilInstruction(fieldLoads[i].OpCode, fieldLoads[i].Operand);
                    body.Instructions.Insert(bodyIndex + i, additionalLoad);
                    item.UseIl.Insert(item.Index + i, additionalLoad);
                }
            }
            // A resolved ARM64 virtual call may leave an unused class-header read. It has no
            // managed consumer after its dispatch loads have been removed by analysis.
            foreach (var hiddenRead in hiddenReads)
                SuppressGeneratedInstructions(hiddenRead, instructionMap);
            SuppressGeneratedInstructions(producer, instructionMap);
        }
    }

    private static bool TryGetFieldLoadExpression(FieldReference field, List<CilInstruction> producerIl,
        CilLocalVariable ilLocal, out CilInstruction[] fieldLoads)
    {
        var fieldDescriptor = field.Field.ToFieldDescriptor();
        fieldLoads = field.Field.IsStatic
            ? [new CilInstruction(CilOpCodes.Ldsfld, fieldDescriptor)]
            : [new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldfld, fieldDescriptor)];
        var emitted = producerIl.Where(instruction => instruction.OpCode != CilOpCodes.Nop).ToArray();
        if (emitted.Length != fieldLoads.Length + 1
            || emitted[^1].OpCode != CilOpCodes.Stloc || emitted[^1].Operand != ilLocal)
            return false;

        for (var i = 0; i < fieldLoads.Length; i++)
            if (emitted[i].OpCode != fieldLoads[i].OpCode || emitted[i].Operand != fieldLoads[i].Operand)
                return false;
        return true;
    }

    private static bool ReadsLocal(Instruction instruction, LocalVariable local)
    {
        var destinationIndex = instruction.DestinationOperandIndex;
        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            var operand = instruction.Operands[i];
            if (i == destinationIndex && operand is LocalVariable)
                continue;
            if (CountLocalReads(operand, local) > 0)
                return true;
        }
        return false;
    }

    private static int CountInstructionLocalReads(Instruction instruction, LocalVariable local)
    {
        var destinationIndex = instruction.DestinationOperandIndex;
        var count = 0;
        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            var operand = instruction.Operands[i];
            if (i != destinationIndex || operand is not LocalVariable)
                count += CountLocalReads(operand, local);
        }
        return count;
    }

    private static bool IsDiscardableInstanceHeaderRead(ISILControlFlowGraph graph, Instruction instruction,
        LocalVariable local) => instruction is
        { OpCode: OpCode.Move, Operands: [LocalVariable { Type: RuntimeClassTypeAnalysisContext } header,
            MemoryOperand { Base: LocalVariable receiver, Index: null, Scale: 0, Addend: 0 }] }
        && receiver == local && CountLocalUses(graph, header) == 0;

    private static bool IsInstanceMemberRead(Instruction instruction, LocalVariable local) => instruction switch
    {
        { OpCode: OpCode.Call, Operands: [MethodAnalysisContext { IsStatic: false }, _, LocalVariable receiver, ..] }
            => receiver == local,
        { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { IsStatic: false }, LocalVariable receiver, ..] }
            => receiver == local,
        { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.IsStatic: false, Local: var receiver }] }
            => receiver == local,
        _ => false
    };

    private static bool CanRepeatFieldReadBetween(ISILControlFlowGraph graph, Instruction producer,
        Instruction use)
    {
        Block? producerBlock = null;
        Block? useBlock = null;
        var producerIndex = -1;
        var useIndex = -1;
        foreach (var block in graph.Blocks)
        {
            var index = block.Instructions.IndexOf(producer);
            if (index >= 0)
                (producerBlock, producerIndex) = (block, index);
            index = block.Instructions.IndexOf(use);
            if (index >= 0)
                (useBlock, useIndex) = (block, index);
        }
        if (producerBlock == null || useBlock == null || producerIndex < 0 || useIndex < 0)
            return false;

        var canReachUse = new HashSet<Block> { useBlock };
        var reverse = new Stack<Block>();
        reverse.Push(useBlock);
        while (reverse.Count > 0)
        {
            var block = reverse.Pop();
            foreach (var predecessor in block.Predecessors)
                if (canReachUse.Add(predecessor))
                    reverse.Push(predecessor);
        }

        var active = new HashSet<Block>();
        var completed = new HashSet<Block>();
        return CheckPath(producerBlock, producerIndex + 1);

        bool CheckPath(Block block, int startIndex)
        {
            if (block == useBlock)
                return useIndex >= startIndex
                    && block.Instructions.Skip(startIndex).Take(useIndex - startIndex)
                        .All(IsSafeBetweenFieldReads);
            if (completed.Contains(block))
                return true;
            if (!active.Add(block))
                return false;

            if (block.Instructions.Skip(startIndex).Any(instruction => !IsSafeBetweenFieldReads(instruction)))
                return false;

            var successors = block.Successors.Where(canReachUse.Contains).ToArray();
            if (successors.Length == 0 || successors.Any(successor => !CheckPath(successor, 0)))
                return false;

            active.Remove(block);
            completed.Add(block);
            return true;
        }
    }

    private static bool InstructionDominates(ISILControlFlowGraph graph, Instruction first, Instruction second)
    {
        Block? firstBlock = null;
        Block? secondBlock = null;
        var firstIndex = -1;
        var secondIndex = -1;
        foreach (var block in graph.Blocks)
        {
            var index = block.Instructions.IndexOf(first);
            if (index >= 0)
                (firstBlock, firstIndex) = (block, index);
            index = block.Instructions.IndexOf(second);
            if (index >= 0)
                (secondBlock, secondIndex) = (block, index);
        }

        if (firstBlock == null || secondBlock == null || firstIndex < 0 || secondIndex < 0)
            return false;
        return firstBlock == secondBlock
            ? firstIndex < secondIndex
            : new DominatorInfo(graph).Dominates(firstBlock, secondBlock);
    }

    private static bool IsSafeBetweenFieldReads(Instruction instruction)
    {
        if (instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall)
        {
            if (instruction.OpCode != OpCode.Call
                || instruction.Operands.FirstOrDefault() is not MethodAnalysisContext method
                || !InlinedFieldGetterRecovery.IsVerifiedDirectGetter(method)
                    && !IsPrimitiveToString(method))
                return false;
        }

        if (instruction.OpCode is OpCode.Newobj or OpCode.NewArr or OpCode.Throw or OpCode.Return or OpCode.IndirectJump
            or OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned
            || instruction.OpCode == OpCode.Divide && !IsNonThrowingFloatDivision(instruction)
            || instruction.Destination is FieldReference or ArrayAccess or MemoryOperand
            || instruction.Operands.Any(operand => operand is ArrayAccess))
            return false;

        return true;
    }

    private static bool IsPrimitiveToString(MethodAnalysisContext method) =>
        !method.IsStatic && method.Name == "ToString" && method.Parameters.Count == 0
        && method.DeclaringType?.FullName == "System.Int32";

    private static bool IsNonThrowingFloatDivision(Instruction instruction) =>
        instruction.Destination is LocalVariable { Type.FullName: "System.Single" or "System.Double" };

    private static void SetRecoveredMaxStack(CilMethodBody body)
    {
        try
        {
            body.MaxStack = body.ComputeMaxStack();
        }
        catch (StackImbalanceException)
        {
            // Keep partially recovered bodies available for inspection. In a balanced CIL
            // method each loop has zero net stack growth, so the total push count is an
            // upper bound. This fallback sets the header only; it cannot balance the CIL.
            var upperBound = 0;
            foreach (var instruction in body.Instructions)
            {
                upperBound += instruction.GetStackPushCount();
                if (upperBound >= ushort.MaxValue)
                {
                    upperBound = ushort.MaxValue;
                    break;
                }
            }

            body.MaxStack = upperBound;
        }
    }

    /// <summary>
    /// Removes MC2IL's no-op issue markers from recovered assemblies after all method bodies have
    /// been generated. They are useful during analysis, but are not part of the game's logic.
    /// </summary>
    public static int StripInjectedDiagnosticScaffolding(ApplicationAnalysisContext appContext,
        IReadOnlyCollection<AssemblyDefinition> assemblies)
    {
        var removedCalls = 0;
        var removedStores = 0;

        foreach (var module in assemblies.SelectMany(assembly => assembly.Modules))
        foreach (var type in EnumerateTypes(module))
        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody is not { } body)
                continue;

            var instructions = body.Instructions;
            var removedCallsBeforeBody = removedCalls;
            for (var i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if (!IsInjectedDiagnosticCall(instruction)
                    || i == 0
                    || instructions[i - 1].OpCode.Code != CilCode.Ldstr)
                    continue;

                // The string and the call only feed MC2IL's injected void helper. Removing both
                // leaves the evaluation stack exactly as it was before the diagnostic sequence.
                instructions[i - 1].OpCode = CilOpCodes.Nop;
                instructions[i - 1].Operand = null;
                instruction.OpCode = CilOpCodes.Nop;
                instruction.Operand = null;
                removedCalls++;
            }

            // Unresolved operations can keep input locals alive until the diagnostic is lowered.
            // With the marker gone, those stores can still interrupt an expression in ILSpy.
            if (removedCalls != removedCallsBeforeBody)
                removedStores += RemoveUnreadSimpleLocalStores(body);
        }

        var removedHelperTypes = 0;
        foreach (var helperContext in appContext.Assemblies
                     .SelectMany(assembly => assembly.Types)
                     .OfType<InjectedTypeAnalysisContext>()
                     .Where(type => type.FullName == $"{HelpersNamespace}.{HelpersTypeName}"))
        {
            var helperDefinition = helperContext.GetExtraData<TypeDefinition>("AsmResolverType");
            if (helperDefinition?.DeclaringType != null
                || helperDefinition?.DeclaringModule is not { } declaringModule
                || assemblies.SelectMany(assembly => assembly.Modules)
                    .SelectMany(EnumerateTypes)
                    .SelectMany(type => type.Methods)
                    .Where(method => method.CilMethodBody != null)
                    .SelectMany(method => method.CilMethodBody!.Instructions)
                    .Any(IsInjectedDiagnosticCall))
                continue;

            if (declaringModule.TopLevelTypes.Remove(helperDefinition))
                removedHelperTypes++;
        }

        if (removedCalls > 0 || removedHelperTypes > 0)
            Logger.InfoNewline(
                $"Removed {removedCalls} injected decompiler diagnostic calls, {removedStores} unread simple local stores, "
                + $"and {removedHelperTypes} unused helper types from recovered output.",
                "IlGenerator");

        return removedCalls;
    }

    internal static int RemoveUnreadSimpleLocalStores(CilMethodBody body)
    {
        if (body.LocalVariables.Count == 0 || body.ExceptionHandlers.Count != 0)
            return 0;

        var reads = new HashSet<CilLocalVariable>();
        var targets = new HashSet<CilInstruction>();
        foreach (var instruction in body.Instructions)
        {
            if (instruction.IsLdloc())
                reads.Add(instruction.GetLocalVariable(body.LocalVariables));
            else if (instruction.OpCode.Code is CilCode.Ldloca or CilCode.Ldloca_S)
            {
                if (instruction.Operand is not CilLocalVariable addressed)
                    return 0;

                reads.Add(addressed);
            }

            if (instruction.Operand is ICilLabel label)
            {
                if (label is not CilInstructionLabel { Instruction: { } target })
                    return 0;

                targets.Add(target);
            }
            else if (instruction.OpCode.OperandType == CilOperandType.InlineSwitch)
            {
                if (instruction.Operand is not IEnumerable<ICilLabel> labels)
                    return 0;

                foreach (var entry in labels)
                {
                    if (entry is not CilInstructionLabel { Instruction: { } target })
                        return 0;

                    targets.Add(target);
                }
            }
        }

        var removed = 0;
        var instructions = body.Instructions;
        for (var i = 1; i < instructions.Count; i++)
        {
            var store = instructions[i];
            var load = instructions[i - 1];
            if (!store.IsStloc() || reads.Contains(store.GetLocalVariable(body.LocalVariables)) || targets.Contains(store)
                || i >= 2 && instructions[i - 2].OpCode.OpCodeType == CilOpCodeType.Prefix
                || !(load.IsLdloc() || load.IsLdarg() || load.IsLdcI4()
                    || load.OpCode.Code is CilCode.Ldc_I8 or CilCode.Ldc_R4 or CilCode.Ldc_R8 or CilCode.Ldnull))
                continue;

            load.ReplaceWithNop();
            store.ReplaceWithNop();
            removed++;
        }

        return removed;
    }

    private static bool IsInjectedDiagnosticCall(CilInstruction instruction) =>
        instruction.OpCode.Code == CilCode.Call
        && instruction.Operand is IMethodDescriptor method
        && method.Name == NoteIssueMethodName
        && method.DeclaringType?.FullName == $"{HelpersNamespace}.{HelpersTypeName}";

    private static IEnumerable<TypeDefinition> EnumerateTypes(ModuleDefinition module)
    {
        foreach (var type in module.TopLevelTypes)
        foreach (var nested in EnumerateTypeAndNested(type))
            yield return nested;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypeAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var descendant in EnumerateTypeAndNested(nested))
            yield return descendant;
    }

    // Limit so we don't run into the 16mb limit (see AsmResolver issue #775)
    private static string Diagnostic(string message)
        => message.Length <= 250 ? message : message[..250] + "…";

    private static void InlineSingleUseLogArgument(Block block, int logIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var logCall = block.Instructions[logIndex];
        if (logCall is not { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext method, LocalVariable argument, ..] }
            || method.DeclaringType?.FullName != "UnityEngine.Debug"
            || method.Name is not ("Log" or "LogWarning" or "LogError" or "LogException"))
            return;

        // Only inline a value whose sole use is this log argument. That keeps evaluation count and
        // observable behavior unchanged, including when the value comes from another method call.
        InlineSingleUseTemporary(block, logIndex, logCall, argument, graph, instructionMap, locals);
    }

    private static void InlineSingleUseFirstInput(Block block, int consumerIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var consumer = block.Instructions[consumerIndex];
        if (!instructionMap.TryGetValue(consumer, out var consumerIl)
            || consumerIl.Count == 0
            || consumerIl[0] is not { OpCode: var loadOpcode, Operand: CilLocalVariable inputLocal }
            || loadOpcode != CilOpCodes.Ldloc)
            return;

        foreach (var localEntry in locals)
        {
            if (!ReferenceEquals(localEntry.Value, inputLocal))
                continue;

            // Keep a receiver on the stack while the following field load evaluates another
            // argument. The field load still runs in its original place.
            var isReceiver = consumer.IsCall && consumer.Operands[0] is MethodAnalysisContext { IsStatic: false }
                && consumer.Operands.Count > ConstructorReceiverIndex(consumer)
                && ReferenceEquals(consumer.Operands[ConstructorReceiverIndex(consumer)], localEntry.Key);
            Func<Instruction, bool>? canCross = isReceiver
                ? candidate => IsStackNeutralArgumentProducer(candidate, consumer, instructionMap, locals)
                : candidate => IsStackNeutralThisFieldRead(candidate, instructionMap, locals);
            InlineSingleUseTemporary(block, consumerIndex, consumer, localEntry.Key, graph, instructionMap, locals,
                canCross);
            return;
        }
    }

    private static bool IsStackNeutralThisFieldRead(Instruction candidate,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (candidate is not { OpCode: OpCode.Move, Operands: [LocalVariable destination,
                FieldReference { Field.IsStatic: false, Local.IsThis: true }] }
            || !instructionMap.TryGetValue(candidate, out var emitted))
            return false;

        var meaningful = emitted.Where(instruction => instruction.OpCode != CilOpCodes.Nop).ToArray();
        if (meaningful.Length == 0)
            return true;

        if (!locals.TryGetValue(destination, out var ilLocal))
            return false;

        return meaningful is
            [{ OpCode: var receiverOpcode }, { OpCode: var fieldOpcode, Operand: var field },
                { OpCode: var storeOpcode, Operand: var storedLocal }]
            && receiverOpcode == CilOpCodes.Ldarg_0 && fieldOpcode == CilOpCodes.Ldfld
            && field is IFieldDescriptor && storeOpcode == CilOpCodes.Stloc
            && ReferenceEquals(storedLocal, ilLocal);
    }

    private static void RemoveDominatedStackSlotInitializers(ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        foreach (var initializer in graph.Instructions.ToArray())
        {
            if (initializer is not { OpCode: OpCode.Move, Operands:
                    [LocalVariable { Register.Name: var name } local, Immediate { Value: 0 }] }
                || !name.StartsWith("stack_", StringComparison.Ordinal))
                continue;

            var uses = graph.Instructions.Where(instruction => ReadsLocal(instruction, local)).ToArray();
            if (!graph.Instructions.Any(definition => definition != initializer
                    && ReferenceEquals(definition.Destination, local)
                    && uses.All(use => InstructionDominates(graph, definition, use))))
                continue;

            SuppressGeneratedInstructions(initializer, instructionMap);
        }
    }

    private static void InlineReferenceComparisonInputs(Block block, int comparisonIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        var comparison = block.Instructions[comparisonIndex];
        if (comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, OperandWidth: null or 64,
                Operands: [_, LocalVariable { Type: { } leftType } left, LocalVariable { Type: { } rightType } right] }
            || !IsManagedReference(leftType) || !IsManagedReference(rightType)
            || !locals.ContainsKey(left) || !locals.ContainsKey(right)
            || !TryFindSingleUseProducer(block, comparisonIndex, right, out var rightBlock, out var rightIndex, out var rightProducer)
            || rightProducer is not { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.IsStatic: false }] }
            || !instructionMap.TryGetValue(rightProducer, out var rightIl)
            || !HasStandaloneStackEffect(rightIl, body, rightIl.Count, 0)
            || !TryFindSingleUseProducer(rightBlock, rightIndex, left, out _, out _, out var leftProducer)
            || CountLocalUses(graph, left) != 1 || CountLocalUses(graph, right) != 1)
            return;

        // Both field and call reads keep their evaluation order. The first reference
        // remains on the stack while the second is loaded before ceq.
        if (TryInlineStackValue(leftProducer, comparison, left, instructionMap, locals))
            TryInlineStackValue(rightProducer, comparison, right, instructionMap, locals);
    }

    private static bool IsStackNeutralArgumentProducer(Instruction candidate, Instruction consumer,
        Dictionary<Instruction, List<CilInstruction>> instructionMap, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (candidate.Destination is not LocalVariable argument
            || !consumer.Operands.Skip(ConstructorReceiverIndex(consumer) + 1).Any(value => ReferenceEquals(value, argument))
            || !locals.TryGetValue(argument, out var slot)
            || !instructionMap.TryGetValue(candidate, out var emitted)
            || emitted.Count == 0 || emitted[^1].OpCode != CilOpCodes.Stloc
            || !ReferenceEquals(emitted[^1].Operand, slot))
            return false;

        // It is safe to leave another value beneath the scratch value on the evaluation stack.
        if (FloatAggregateRecovery.IsLiteralPack(candidate))
            return emitted.Count >= 3 && emitted[0].OpCode == CilOpCodes.Ldloca && emitted[^2].OpCode == CilOpCodes.Ldloc;

        return candidate is { OpCode: OpCode.Move, Operands: [_, FieldReference
                { Parent: null, Local.IsThis: true, Field.IsStatic: false }] }
            && emitted is [var load, var field, _]
            && load.OpCode == CilOpCodes.Ldarg_0 && field.OpCode == CilOpCodes.Ldfld;
    }

    private static void InlineSingleUseComparisonInput(Block block, int index, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        var comparison = block.Instructions[index];
        if (comparison is not { OpCode: >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned,
                Operands: [_, LocalVariable or Immediate, LocalVariable bound] }
            || bound.Type?.FullName != "System.Int32"
            || !locals.TryGetValue(bound, out var slot)
            || !instructionMap.TryGetValue(comparison, out var consumerIl))
            return;

        // A Count/Length load in the loop header should be part of the condition.
        // Loading the already computed index first does not affect the dereference.
        var load = consumerIl.FindIndex(il => il.OpCode == CilOpCodes.Ldloc && ReferenceEquals(il.Operand, slot));
        if (load <= 0 || !HasOnlyPureLoadsBefore(consumerIl, load)
            || CountLocalUses(graph, bound) != 1
            || !TryFindSingleUseProducer(block, index, bound, out var producerBlock, out _, out var producer)
            || producerBlock != block
            || producer is not { OpCode: OpCode.Move, Operands: [_, FieldReference or ArrayLength] }
            || !instructionMap.TryGetValue(producer, out var producerIl)
            || producerIl.Count < 2 || producerIl[^1].OpCode != CilOpCodes.Stloc
            || !ReferenceEquals(producerIl[^1].Operand, slot)
            || !ProducesStandaloneStackValue(producerIl, body))
            return;

        RelocateSingleUseExpression(producer, producerIl, consumerIl, load, instructionMap, body);
    }

    private static void InlineSingleUseCallArguments(Block block, int consumerIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        var consumer = block.Instructions[consumerIndex];
        if (!consumer.IsCall || consumer.Operands[0] is not MethodAnalysisContext calledMethod
            || !instructionMap.TryGetValue(consumer, out var consumerIl))
            return;

        var firstArgument = consumer.OpCode == OpCode.CallVoid
            ? (calledMethod.IsStatic ? 1 : 2)
            : (calledMethod.IsStatic ? 2 : 3);

        var lastArgument = Math.Min(firstArgument + calledMethod.Parameters.Count, consumer.Operands.Count);
        for (var operandIndex = firstArgument; operandIndex < lastArgument; operandIndex++)
        {
            if (consumer.Operands[operandIndex] is not LocalVariable argument
                || HasNarrowLocalStorage(argument)
                || CountLocalUses(graph, argument) != 1
                || !locals.TryGetValue(argument, out var ilLocal))
                continue;

            var loadIndex = consumerIl.FindIndex(cil => cil.OpCode == CilOpCodes.Ldloc
                && ReferenceEquals(cil.Operand, ilLocal));
            if (loadIndex < 0)
                continue;

            // An unresolved absolute global read that survived type analysis has already been
            // represented as a typed default in its producer. Inline that reference default at
            // its sole call use and drop the injected diagnostic/local noise with it.
            if (TryFindUnresolvedReferenceLoad(block, consumerIndex, argument, out var loadProducer)
                && instructionMap.TryGetValue(loadProducer, out var defaultIl)
                && defaultIl is [var message, var diagnostic, var nullLoad, var store]
                && message.OpCode == CilOpCodes.Ldstr && diagnostic.OpCode == CilOpCodes.Call
                && nullLoad.OpCode == CilOpCodes.Ldnull && store.OpCode == CilOpCodes.Stloc
                && ReferenceEquals(store.Operand, ilLocal))
            {
                consumerIl[loadIndex].OpCode = CilOpCodes.Ldnull;
                consumerIl[loadIndex].Operand = null;
                SuppressGeneratedInstructions(loadProducer, instructionMap);
                continue;
            }

            var delegateParameter = IsDelegateType(calledMethod.Parameters[operandIndex - firstArgument].ParameterType);
            if ((!TryFindSingleUseProducer(block, consumerIndex, argument, out _, out _, out var producer)
                    && !(delegateParameter && TryFindDelegateProducer(block, consumerIndex, argument, out producer)))
                || !instructionMap.TryGetValue(producer, out var producerIl)
                || producerIl.Count == 0
                || producerIl[^1] is not { OpCode: var storeOpcode, Operand: var storedLocal }
                || storeOpcode != CilOpCodes.Stloc
                || !ReferenceEquals(storedLocal, ilLocal))
                continue;

            // An argument produced immediately before the call can stay on the stack
            // above a receiver whose load was already inlined.
            if (IsStackNeutralArgumentProducer(producer, consumer, instructionMap, locals)
                && TryInlineStackValue(producer, consumer, argument, instructionMap, locals))
                continue;

            var purePrefix = HasOnlyPureLoadsBefore(consumerIl, loadIndex);
            var canInlineCallResult = producer.OpCode == OpCode.Call
                && purePrefix && !HasAddressedArgumentLoad(body.Instructions, consumerIl, loadIndex);
            var canInlinePureExpression = IsStableCallArgumentExpression(producer, producerIl)
                && (purePrefix || HasOnlyStableLoadsBefore(consumerIl, loadIndex));
            // Keep the array access at its original evaluation point, after only
            // ordinary local/argument loads. Moving it past another field access,
            // call or conversion that can throw could change which exception wins.
            var canInlineArrayElement = producer is { OpCode: OpCode.Move,
                    Operands: [_, ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array }] }
                && MetadataResolver.IsSameType(argument.Type, array.ElementType)
                && purePrefix;
            var canInlineValueField = producer is { OpCode: OpCode.Move, OperandWidth: null,
                    Operands: [_, FieldReference { Parent: null, Field.IsStatic: false } field] }
                && MetadataResolver.IsSameType(argument.Type, field.Field.FieldType)
                && (field.Local.IsThis
                    // A complete struct field can follow other direct loads from this,
                    // including the receiver of a property setter.
                    ? field.Field.FieldType.IsValueType && (purePrefix || HasOnlyStableLoadsBefore(consumerIl, loadIndex))
                    : purePrefix && field.Local.Type is { IsValueType: true } and not ByRefTypeAnalysisContext);
            var canInlineDelegate = delegateParameter && producer.OpCode == OpCode.Newobj
                && purePrefix && IsBoundDelegateExpression(producerIl);
            // Moving an allocation to its sole call argument keeps its evaluation order when
            // the producer is immediately before the call (as proven above) and the CIL stack
            // prefix contains only loads. This avoids a throwaway local for `Try(new T(...))`.
            var canInlineObjectCreation = !delegateParameter
                && producer is { OpCode: OpCode.Newobj, Operands: [_, TypeAnalysisContext allocatedType] }
                && argument.Type is { } argumentType
                && MetadataResolver.IsSameType(argumentType, allocatedType)
                && MetadataResolver.IsSameType(
                    calledMethod.Parameters[operandIndex - firstArgument].ParameterType, allocatedType)
                && purePrefix;
            if ((!canInlineCallResult && !canInlinePureExpression && !canInlineArrayElement
                    && !canInlineValueField && !canInlineDelegate && !canInlineObjectCreation)
                || !ProducesStandaloneStackValue(producerIl, body))
                continue;

            RelocateSingleUseExpression(producer, producerIl, consumerIl, loadIndex, instructionMap, body);
        }
    }

    private static void InlineSingleUseArrayLengthReceiver(Block block, int consumerIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        var consumer = block.Instructions[consumerIndex];
        if (consumer is not { OpCode: OpCode.Move, Operands: [_, ArrayLength { Array: var array }] }
            || !instructionMap.TryGetValue(consumer, out var consumerIl)
            || !locals.TryGetValue(array, out var ilArray)
            || CountLocalUses(graph, array) != 1
            || !TryFindSingleUseProducer(block, consumerIndex, array, out _, out _, out var producer)
            || producer is not { OpCode: OpCode.Move, Operands: [_, FieldReference
                { Field.FieldType: SzArrayTypeAnalysisContext fieldArrayType }] }
            || array.Type is not SzArrayTypeAnalysisContext arrayType
            || !MetadataResolver.IsSameType(arrayType.ElementType, fieldArrayType.ElementType)
            || !instructionMap.TryGetValue(producer, out var producerIl)
            || producerIl.Count == 0 || producerIl[^1].OpCode != CilOpCodes.Stloc
            || !ReferenceEquals(producerIl[^1].Operand, ilArray))
            return;

        // A field assignment can put its receiver on the stack before loading the
        // array. We can move the array load past that prefix if it only loads values.
        var index = consumerIl.FindIndex(il => il.OpCode == CilOpCodes.Ldlen);
        if (index <= 0 || consumerIl[index - 1].OpCode != CilOpCodes.Ldloc
            || !ReferenceEquals(consumerIl[index - 1].Operand, ilArray)
            || !HasOnlyPureLoadsBefore(consumerIl, index - 1)
            || !ProducesStandaloneStackValue(producerIl, body))
            return;

        RelocateSingleUseExpression(producer, producerIl, consumerIl, index - 1, instructionMap, body);
    }

    private static void InlineSingleUseFieldReceiver(Block block, int consumerIndex, ISILControlFlowGraph graph,
        HashSet<LocalVariable> fieldReadLocals, Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, CilMethodBody body)
    {
        var consumer = block.Instructions[consumerIndex];
        var receiver = consumer switch
        {
            { OpCode: OpCode.Call, Operands: [MethodAnalysisContext { IsStatic: false }, _, LocalVariable instance, ..] }
                => instance,
            { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { IsStatic: false }, LocalVariable instance, ..] }
                => instance,
            { OpCode: OpCode.Move, Operands: [FieldReference { Field.IsStatic: false } destination, _] }
                => destination.Local,
            { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.IsStatic: false } source] }
                => source.Local,
            _ => null
        };

        if (receiver?.Type is not { } receiverType || !fieldReadLocals.Contains(receiver)
            || !locals.TryGetValue(receiver, out var ilLocal)
            || !instructionMap.TryGetValue(consumer, out var consumerIl)
            || consumerIl.Count == 0 || consumerIl[0].OpCode != CilOpCodes.Ldloc
            || !ReferenceEquals(consumerIl[0].Operand, ilLocal)
            || !TryFindSingleUseProducer(block, consumerIndex, receiver, out _, out _, out var producer, CanCross)
            || producer is not { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.IsStatic: false } field] }
            || !MetadataResolver.IsSameType(receiverType, field.Field.FieldType)
            || CountLocalUses(graph, receiver) != 1)
            return;

        // Leave the field read at its original evaluation point. Argument preparation and calls
        // may throw or change that field. The saved reference can stay below their stack values.
        TryInlineStackValue(producer, consumer, receiver, instructionMap, locals);

        bool CanCross(Instruction instruction) =>
            instructionMap.TryGetValue(instruction, out var generated)
            && HasStandaloneStackEffect(generated, body, generated.Count, 0);
    }

    private static void RelocateSingleUseExpression(Instruction producer, List<CilInstruction> producerIl,
        List<CilInstruction> consumerIl, int loadIndex,
        Dictionary<Instruction, List<CilInstruction>> instructionMap, CilMethodBody body)
    {
        // Preserve the load as a branch anchor and update the actual method body.
        var load = consumerIl[loadIndex];
        var bodyIndex = body.Instructions.IndexOf(load);
        if (bodyIndex < 0)
            return;

        var expression = producerIl.Take(producerIl.Count - 1).Where(il => il.OpCode != CilOpCodes.Nop).ToList();
        load.OpCode = expression[0].OpCode;
        load.Operand = expression[0].Operand;
        var remaining = expression.Skip(1).Select(il => new CilInstruction(il.OpCode, il.Operand)).ToList();
        body.Instructions.InsertRange(bodyIndex + 1, remaining);
        consumerIl.InsertRange(loadIndex + 1, remaining);
        SuppressGeneratedInstructions(producer, instructionMap);
    }

    private static bool TryFindDelegateProducer(Block block, int consumerIndex, LocalVariable argument, out Instruction producer)
    {
        var seen = new HashSet<Block>();
        var index = consumerIndex - 1;
        while (seen.Add(block))
        {
            for (; index >= 0; index--)
            {
                var instruction = block.Instructions[index];
                if (instruction.OpCode == OpCode.Nop)
                    continue;
                producer = instruction;
                return instruction.OpCode == OpCode.Newobj && ReferenceEquals(instruction.Destination, argument);
            }

            if (block.Predecessors is not [var parent] || parent.Successors is not [var next] || next != block)
                break;

            block = parent;
            index = block.Instructions.Count - 1;
            if (index >= 0 && block.Instructions[index] is { OpCode: OpCode.Jump, Operands: [Block target] }
                && target == next)
                index--;
        }

        producer = null!;
        return false;
    }

    private static bool IsBoundDelegateExpression(List<CilInstruction> instructions)
    {
        var sequence = instructions.Where(instruction => instruction.OpCode != CilOpCodes.Nop).ToArray();
        return sequence is
            [var receiver, var target, var constructor, _]
            && (receiver.OpCode == CilOpCodes.Ldnull || receiver.OpCode == CilOpCodes.Ldarg_0
                || receiver is { Operand: CilLocalVariable } && receiver.OpCode == CilOpCodes.Ldloc)
            && target.OpCode == CilOpCodes.Ldftn && constructor.OpCode == CilOpCodes.Newobj
            || sequence is [var boundReceiver, var duplicate, var virtualTarget, var virtualConstructor, _]
            && boundReceiver.OpCode == CilOpCodes.Ldarg_0 && duplicate.OpCode == CilOpCodes.Dup
            && virtualTarget.OpCode == CilOpCodes.Ldvirtftn && virtualConstructor.OpCode == CilOpCodes.Newobj;
    }

    private static bool TryFindUnresolvedReferenceLoad(Block consumerBlock, int consumerIndex,
        LocalVariable local, out Instruction producer)
    {
        producer = null!;
        if (local.Type is not { } type || !IsManagedReference(type))
            return false;

        var definitions = consumerBlock.Instructions.Take(consumerIndex)
            .Where(instruction => ReferenceEquals(instruction.Destination, local))
            .ToList();
        if (definitions.Count != 1
            || definitions[0] is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand
                { Base: null, Index: null, Scale: 0 }] } move)
            return false;

        producer = move;
        return true;
    }

    internal static bool IsManagedReference(TypeAnalysisContext type) =>
        !type.IsValueType && type is not (GenericParameterTypeAnalysisContext or ByRefTypeAnalysisContext
            or PointerTypeAnalysisContext or RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext
            or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext
            or StaticFieldStorageTypeAnalysisContext);

    private static bool HasOnlyPureLoadsBefore(List<CilInstruction> instructions, int index)
    {
        for (var i = 0; i < index; i++)
        {
            var opcode = instructions[i].OpCode;
            if (opcode == CilOpCodes.Nop || opcode == CilOpCodes.Ldnull || opcode == CilOpCodes.Ldstr
                || opcode == CilOpCodes.Ldloc || opcode == CilOpCodes.Ldarg || opcode == CilOpCodes.Ldarg_0
                || opcode == CilOpCodes.Ldarg_1 || opcode == CilOpCodes.Ldarg_2 || opcode == CilOpCodes.Ldarg_3
                || opcode == CilOpCodes.Ldc_I4 || opcode == CilOpCodes.Ldc_I4_0 || opcode == CilOpCodes.Ldc_I4_1
                || opcode == CilOpCodes.Ldc_I4_2 || opcode == CilOpCodes.Ldc_I4_3 || opcode == CilOpCodes.Ldc_I4_4
                || opcode == CilOpCodes.Ldc_I4_5 || opcode == CilOpCodes.Ldc_I4_6 || opcode == CilOpCodes.Ldc_I4_7
                || opcode == CilOpCodes.Ldc_I4_8 || opcode == CilOpCodes.Ldc_I4_M1 || opcode == CilOpCodes.Ldc_I8
                || opcode == CilOpCodes.Ldc_R4 || opcode == CilOpCodes.Ldc_R8)
                continue;

            return false;
        }

        return true;
    }

    private static bool ProducesStandaloneStackValue(List<CilInstruction> producerIl, CilMethodBody body)
    {
        // Stack forwarding may have removed a load from the producer. It can only be
        // relocated if the remaining instructions still compute a value on their own.
        return HasStandaloneStackEffect(producerIl, body, producerIl.Count - 1, 1);
    }

    private static bool HasStandaloneStackEffect(List<CilInstruction> instructions, CilMethodBody body,
        int count, int expectedDepth)
    {
        var depth = 0;
        for (var i = 0; i < count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode.FlowControl is not (CilFlowControl.Next or CilFlowControl.Call))
                return false;

            depth -= instruction.GetStackPopCount(body);
            if (depth < 0)
                return false;
            depth += instruction.GetStackPushCount();
        }
        return depth == expectedDepth;
    }

    private static bool HasAddressedArgumentLoad(CilInstructionCollection body, List<CilInstruction> consumerIl,
        int loadIndex)
    {
        // A call can mutate a slot through an alias created earlier in the method.
        for (var i = 0; i < loadIndex; i++)
        {
            var load = consumerIl[i];
            if (load.OpCode == CilOpCodes.Ldloc
                && body.Any(cil => cil.OpCode == CilOpCodes.Ldloca && ReferenceEquals(cil.Operand, load.Operand)))
                return true;
            if (load.OpCode == CilOpCodes.Ldarg
                && body.Any(cil => cil.OpCode == CilOpCodes.Ldarga && Equals(cil.Operand, load.Operand)))
                return true;
        }

        return false;
    }

    private static bool HasOnlyStableLoadsBefore(List<CilInstruction> instructions, int index)
    {
        for (var i = 0; i < index; i++)
        {
            var opcode = instructions[i].OpCode;
            if (opcode == CilOpCodes.Nop || opcode == CilOpCodes.Ldnull || opcode == CilOpCodes.Ldstr
                || opcode == CilOpCodes.Ldloc || opcode == CilOpCodes.Ldarg || opcode == CilOpCodes.Ldarg_0
                || opcode == CilOpCodes.Ldarg_1 || opcode == CilOpCodes.Ldarg_2 || opcode == CilOpCodes.Ldarg_3
                || opcode == CilOpCodes.Ldc_I4 || opcode == CilOpCodes.Ldc_I4_0 || opcode == CilOpCodes.Ldc_I4_1
                || opcode == CilOpCodes.Ldc_I4_2 || opcode == CilOpCodes.Ldc_I4_3 || opcode == CilOpCodes.Ldc_I4_4
                || opcode == CilOpCodes.Ldc_I4_5 || opcode == CilOpCodes.Ldc_I4_6 || opcode == CilOpCodes.Ldc_I4_7
                || opcode == CilOpCodes.Ldc_I4_8 || opcode == CilOpCodes.Ldc_I4_M1 || opcode == CilOpCodes.Ldc_I8
                || opcode == CilOpCodes.Ldc_R4 || opcode == CilOpCodes.Ldc_R8)
                continue;

            // The expression moved after this receiver load uses only non-throwing stack operations.
            // This covers the field loads generated for common Unity property setters.
            if (opcode == CilOpCodes.Ldfld && i > 0 && instructions[i - 1].OpCode == CilOpCodes.Ldarg_0)
                continue;

            return false;
        }

        return true;
    }

    private static bool IsStableCallArgumentExpression(Instruction producer, List<CilInstruction> producerIl)
    {
        if (producer.OpCode is OpCode.Divide or OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned
            && producer.Destination is not LocalVariable { Type.FullName: "System.Single" or "System.Double" })
            return false;

        for (var i = 0; i < producerIl.Count - 1; i++)
        {
            var opcode = producerIl[i].OpCode;
            if (opcode == CilOpCodes.Nop || opcode == CilOpCodes.Ldnull || opcode == CilOpCodes.Ldstr
                || opcode == CilOpCodes.Ldloc || opcode == CilOpCodes.Ldarg || opcode == CilOpCodes.Ldarg_0
                || opcode == CilOpCodes.Ldarg_1 || opcode == CilOpCodes.Ldarg_2 || opcode == CilOpCodes.Ldarg_3
                || opcode == CilOpCodes.Ldc_I4 || opcode == CilOpCodes.Ldc_I4_0 || opcode == CilOpCodes.Ldc_I4_1
                || opcode == CilOpCodes.Ldc_I4_2 || opcode == CilOpCodes.Ldc_I4_3 || opcode == CilOpCodes.Ldc_I4_4
                || opcode == CilOpCodes.Ldc_I4_5 || opcode == CilOpCodes.Ldc_I4_6 || opcode == CilOpCodes.Ldc_I4_7
                || opcode == CilOpCodes.Ldc_I4_8 || opcode == CilOpCodes.Ldc_I4_M1 || opcode == CilOpCodes.Ldc_I8
                || opcode == CilOpCodes.Ldc_R4 || opcode == CilOpCodes.Ldc_R8
                || opcode == CilOpCodes.Conv_I4 || opcode == CilOpCodes.Conv_U4
                || opcode == CilOpCodes.Conv_R4 || opcode == CilOpCodes.Conv_R8
                || opcode == CilOpCodes.Add || opcode == CilOpCodes.Sub || opcode == CilOpCodes.Mul
                || opcode == CilOpCodes.Div || opcode == CilOpCodes.Div_Un || opcode == CilOpCodes.Rem
                || opcode == CilOpCodes.Rem_Un || opcode == CilOpCodes.And || opcode == CilOpCodes.Or
                || opcode == CilOpCodes.Xor || opcode == CilOpCodes.Not || opcode == CilOpCodes.Neg
                || opcode == CilOpCodes.Ceq || opcode == CilOpCodes.Cgt || opcode == CilOpCodes.Cgt_Un
                || opcode == CilOpCodes.Clt || opcode == CilOpCodes.Clt_Un)
                continue;

            return false;
        }

        return true;
    }

    private static void SuppressGeneratedInstructions(Instruction instruction,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        if (!instructionMap.TryGetValue(instruction, out var generated))
            return;

        foreach (var cilInstruction in generated)
        {
            cilInstruction.OpCode = CilOpCodes.Nop;
            cilInstruction.Operand = null;
        }
    }

    private static void InlineSingleUseBranchCondition(Block block, int branchIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var branch = block.Instructions[branchIndex];
        if (branch is not { OpCode: OpCode.ConditionalJump, Operands: [_, LocalVariable condition] }
            || condition.Type?.FullName != "System.Boolean")
            return;

        // Let brtrue consume the value produced by the condition directly. Follow local-to-local
        // copies as well, so a getter result does not survive as an intermediate bool local.
        var consumer = branch;
        var consumerBlock = block;
        var consumerIndex = branchIndex;
        var local = condition;

        while (CountLocalUses(graph, local) == 1
            && TryFindSingleUseProducer(consumerBlock, consumerIndex, local, out var producerBlock, out var producerIndex, out var producer)
            && TryInlineStackValue(producer, consumer, local, instructionMap, locals))
        {
            if (producer.OpCode != OpCode.Move
                || producer.Operands.Count < 2
                || producer.Operands[1] is not LocalVariable source)
                break;

            consumer = producer;
            consumerBlock = producerBlock;
            consumerIndex = producerIndex;
            local = source;
        }
    }

    private static void InlineSingleUseReturnValue(Block block, int returnIndex, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var ret = block.Instructions[returnIndex];
        if (ret is not { OpCode: OpCode.Return, Operands: [LocalVariable value] })
            return;

        InlineSingleUseTemporary(block, returnIndex, ret, value, graph, instructionMap, locals);
    }

    private static void InlineSingleUseTemporary(Block block, int consumerIndex, Instruction consumer,
        LocalVariable local, ISILControlFlowGraph graph,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals, Func<Instruction, bool>? canCross = null)
    {
        if (CountLocalUses(graph, local) != 1
            || !TryFindSingleUseProducer(block, consumerIndex, local, out _, out _, out var producer, canCross))
            return;

        TryInlineStackValue(producer, consumer, local, instructionMap, locals);
    }

    private static bool TryFindSingleUseProducer(Block consumerBlock, int consumerIndex, LocalVariable local,
        out Block producerBlock, out int producerIndex, out Instruction producer,
        Func<Instruction, bool>? canCross = null)
    {
        producerBlock = consumerBlock;
        producerIndex = consumerIndex - 1;
        producer = null!;
        HashSet<Block>? visited = null;
        while (true)
        {
            for (; producerIndex >= 0; producerIndex--)
            {
                var candidate = producerBlock.Instructions[producerIndex];
                if (candidate.OpCode == OpCode.Nop)
                    continue;
                if (ReferenceEquals(candidate.Destination, local))
                {
                    producer = candidate;
                    return true;
                }
                if (canCross?.Invoke(candidate) != true)
                    return false;
            }

            // A folded null guard leaves an unconditional jump between a field read and its
            // use. Keep the value on the evaluation stack across edges with no other path.
            if (producerBlock.Predecessors is not [var predecessor]
                || predecessor.BlockType == BlockType.Entry
                || predecessor.Successors is not [var successor] || successor != producerBlock)
                return false;

            visited ??= [consumerBlock];
            if (!visited.Add(predecessor))
                return false;

            producerBlock = predecessor;
            producerIndex = predecessor.Instructions.Count - 1;
            while (producerIndex >= 0 && predecessor.Instructions[producerIndex].OpCode == OpCode.Nop)
                producerIndex--;
            if (producerIndex >= 0 && predecessor.Instructions[producerIndex] is
                { OpCode: OpCode.Jump, Operands: [Block target] } && target == successor)
                producerIndex--;
        }
    }

    private static bool TryInlineStackValue(Instruction producer, Instruction consumer, LocalVariable local,
        Dictionary<Instruction, List<CilInstruction>> instructionMap,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (HasNarrowLocalStorage(local)
            || !locals.TryGetValue(local, out var ilLocal)
            || !instructionMap.TryGetValue(producer, out var producerIl)
            || producerIl.Count == 0
            || producerIl[^1] is not { OpCode: var storeOpcode, Operand: var storedLocal }
            || storeOpcode != CilOpCodes.Stloc
            || !ReferenceEquals(storedLocal, ilLocal)
            || !instructionMap.TryGetValue(consumer, out var consumerIl)
            || consumerIl.FirstOrDefault(instruction => instruction.OpCode != CilOpCodes.Nop)
                is not { OpCode: var loadOpcode, Operand: var loadedLocal } load
            || loadOpcode != CilOpCodes.Ldloc || !ReferenceEquals(loadedLocal, ilLocal))
            return false;

        // The producer's result is already on the stack. Remove its store and the matching load so
        // the consumer can use the value directly.
        producerIl[^1].OpCode = CilOpCodes.Nop;
        producerIl[^1].Operand = null;
        load.OpCode = CilOpCodes.Nop;
        load.Operand = null;
        return true;
    }

    private static bool HasNarrowLocalStorage(LocalVariable local)
    {
        // stloc/ldloc can truncate and extend a small integer. A direct stack value can be wider.
        var storageType = local.Type is { IsEnumType: true } enumType ? enumType.EnumUnderlyingType : local.Type;
        return storageType?.FullName is "System.SByte" or "System.Byte" or "System.Char"
            or "System.Int16" or "System.UInt16";
    }

    private static int CountLocalUses(ISILControlFlowGraph graph, LocalVariable local)
    {
        var count = 0;

        foreach (var instruction in graph.Instructions)
        {
            var destinationIndex = instruction.DestinationOperandIndex;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];
                // Skip only the destination operand slot. The same local may also appear in a
                // source slot (for example `x = M(x)`) and must then count as a real use.
                if (i == destinationIndex && operand is LocalVariable)
                    continue;
                count += CountLocalReads(operand, local);
                if (count > 1)
                    return count;
            }
        }

        return count;
    }

    private static int CountLocalReads(IOperand operand, LocalVariable local) => operand switch
    {
        LocalVariable candidate => ReferenceEquals(candidate, local) ? 1 : 0,
        MemoryOperand memory => (memory.Base is { } baseOperand ? CountLocalReads(baseOperand, local) : 0)
            + (memory.Index is { } indexOperand ? CountLocalReads(indexOperand, local) : 0),
        FieldReference field => !field.Field.IsStatic && ReferenceEquals(field.Local, local) ? 1 : 0,
        ArrayAccess array => (ReferenceEquals(array.Array, local) ? 1 : 0) + CountLocalReads(array.Index, local),
        ArrayLength length => ReferenceEquals(length.Array, local) ? 1 : 0,
        AddressOf address => CountLocalReads(address.Target, local),
        _ => 0
    };

    private static IEnumerable<Block> GetCilBlockOrder(ISILControlFlowGraph graph, bool isIteratorMoveNext)
    {
        // Analysis can move the entry's first real block to the end when it splits
        // edges. Start there, while preserving the relative layout of other blocks.
        var first = graph.EntryBlock;
        var visited = new HashSet<Block>();
        while (first.Instructions.Count == 0 && first.Successors is [var successor] && visited.Add(first))
            first = successor;

        if (first != graph.EntryBlock && first != graph.ExitBlock && graph.Blocks.Contains(first))
        {
            yield return first;
            // Metadata initialization can leave a trampoline at the entry of MoveNext.
            // Keep the state dispatch at the front where C# decompilers look for it.
            while (isIteratorMoveNext && first.Successors is [var next] && next != graph.ExitBlock
                && first.Instructions.All(instruction => instruction.OpCode is OpCode.Nop or OpCode.Jump)
                && visited.Add(first) && !visited.Contains(next))
            {
                first = next;
                yield return first;
            }
        }

        foreach (var block in graph.Blocks)
            if (block != first && (!isIteratorMoveNext || !visited.Contains(block)))
                yield return block;
    }

    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    private static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        var visited = new HashSet<Block>();
        while (block.Successors.Count == 1 && visited.Add(block))
        {
            block = block.Successors[0];
            if (blockEntryMap.TryGetValue(block, out target))
                return target;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine,
        Dictionary<Instruction, List<CilInstruction>> instructionMap, HashSet<LocalVariable> graphUsedLocals)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;

        var module = method.DeclaringModule!;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Invalid instruction: {instruction}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.NotImplemented:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Not implemented instruction: {instruction.Operands[0]}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.Interrupt:
            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    if (!field.Field.IsStatic)
                        LoadFieldReceiver(field, method, locals);

                    if (instruction.Operands[1] is DefaultValue { Type: var zeroType })
                    {
                        instructions.Add(field.Field.IsStatic ? CilOpCodes.Ldsflda : CilOpCodes.Ldflda,
                            field.Field.ToFieldDescriptor());
                        instructions.Add(CilOpCodes.Initobj, zeroType.ToTypeSignature().ToTypeDefOrRef());
                        break;
                    }

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, field.Field.FieldType);
                    EmitMoveWidthConversion(instruction, instructions);
                    instructions.Add(field.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, field.Field.ToFieldDescriptor());
                    break;
                }

                // stelem needs array and index before the value, so like stfld it can't go through LoadOperand/StoreToOperand.
                // This also lets ILSpy handle it as a proper array initializer
                if (instruction.Operands[0] is ArrayAccess { Array.Type: SzArrayTypeAnalysisContext { ElementType: { } stored } } target)
                {
                    LoadLocal(target.Array, method, locals);
                    LoadOperand(target.Index, method, locals, writeLine);
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stored);
                    EmitMoveWidthConversion(instruction, instructions);
                    instructions.Add(CilOpCodes.Stelem, stored.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }

                // stobj needs the pointer below the value. Load the by-ref local first, so
                // ordinary assignments through ref/out do not need a scratch value local.
                if (instruction.Operands[0] is MemoryOperand { Index: null, Scale: 0, Addend: 0,
                    Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } referent } } address })
                {
                    LoadLocal(address, method, locals);
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, referent);
                    EmitMoveWidthConversion(instruction, instructions);
                    instructions.Add(CilOpCodes.Stobj, referent.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }

                LoadOperand(instruction.Operands[1], method, locals, writeLine, OperandType(instruction.Operands[0]));
                EmitMoveWidthConversion(instruction, instructions);
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Convert:
                if (instruction.Operands is [var convertedDestination, var conversionSource, NumericConversion { Kind: var conversionKind } conversion])
                {
                    var types = context.AppContext.SystemTypes;
                    var (sourceType, resultType) = NumericConversionTypes.Get(conversionKind, types);

                    if (sourceType == null || resultType == null)
                    {
                        instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unsupported numeric conversion: {conversionKind}"));
                        instructions.Add(CilOpCodes.Call, writeLine);
                        PushDefaultOf(types.SystemObjectType, method);
                    }
                    else if (conversionKind is NumericConversionKind.Float32ReinterpretI32
                        or NumericConversionKind.Float64ReinterpretI64
                        or NumericConversionKind.I32ReinterpretFloat32
                        or NumericConversionKind.I64ReinterpretFloat64)
                    {
                        LoadOperand(conversionSource, method, locals, writeLine, sourceType);
                        EmitBitReinterpretation(module, instructions, conversionKind);
                    }
                    else
                    {
                        if (conversionKind == NumericConversionKind.Float64ToFloat32
                            && conversionSource is DoubleLiteral)
                        {
                            // Round this compile-time constant now and emit an R4 literal rather
                            // than an R8 literal followed by conv.r4, which decompiles as
                            // the noisy `(float)3.0` form.
                            LoadOperand(conversionSource, method, locals, writeLine, resultType);
                            StoreToOperand(convertedDestination, method, locals, writeLine);
                            break;
                        }

                        LoadOperand(conversionSource, method, locals, writeLine, sourceType);

                        if (conversion.Saturating)
                        {
                            instructions.Add(CilOpCodes.Conv_R8);
                            instructions.Add(CilOpCodes.Call, Arm64ConversionHelpers.GetConversion(module, conversion));
                        }
                        else
                        {
                            switch (conversionKind)
                            {
                            case NumericConversionKind.Float32ToFloat64:
                                instructions.Add(CilOpCodes.Conv_R8);
                                break;
                            case NumericConversionKind.Float64ToFloat32:
                                instructions.Add(CilOpCodes.Conv_R4);
                                break;
                            case NumericConversionKind.SignedInt32ToFloat32:
                                instructions.Add(CilOpCodes.Conv_I4);
                                instructions.Add(CilOpCodes.Conv_R4);
                                break;
                            case NumericConversionKind.SignedInt32ToFloat64:
                                instructions.Add(CilOpCodes.Conv_I4);
                                instructions.Add(CilOpCodes.Conv_R8);
                                break;
                            case NumericConversionKind.SignedInt64ToFloat32:
                                instructions.Add(CilOpCodes.Conv_I8);
                                instructions.Add(CilOpCodes.Conv_R4);
                                break;
                            case NumericConversionKind.SignedInt64ToFloat64:
                                instructions.Add(CilOpCodes.Conv_I8);
                                instructions.Add(CilOpCodes.Conv_R8);
                                break;
                            case NumericConversionKind.UnsignedInt32ToFloat32:
                                instructions.Add(CilOpCodes.Conv_U4);
                                instructions.Add(CilOpCodes.Conv_R_Un);
                                instructions.Add(CilOpCodes.Conv_R4);
                                break;
                            case NumericConversionKind.UnsignedInt32ToFloat64:
                                instructions.Add(CilOpCodes.Conv_U4);
                                instructions.Add(CilOpCodes.Conv_R_Un);
                                instructions.Add(CilOpCodes.Conv_R8);
                                break;
                            case NumericConversionKind.UnsignedInt64ToFloat32:
                                instructions.Add(CilOpCodes.Conv_U8);
                                instructions.Add(CilOpCodes.Conv_R_Un);
                                instructions.Add(CilOpCodes.Conv_R4);
                                break;
                            case NumericConversionKind.UnsignedInt64ToFloat64:
                                instructions.Add(CilOpCodes.Conv_U8);
                                instructions.Add(CilOpCodes.Conv_R_Un);
                                instructions.Add(CilOpCodes.Conv_R8);
                                break;
                            case NumericConversionKind.I32WrapI64:
                                instructions.Add(CilOpCodes.Conv_I4);
                                break;
                            case NumericConversionKind.I64ExtendI32Signed:
                                // ARM64 W registers share storage with X registers in ISIL.
                                // Narrow first so stale upper bits cannot change the sign.
                                instructions.Add(CilOpCodes.Conv_I4);
                                instructions.Add(CilOpCodes.Conv_I8);
                                break;
                            case NumericConversionKind.I64ExtendI32Unsigned:
                                instructions.Add(CilOpCodes.Conv_U8);
                                break;
                            case NumericConversionKind.Float32ToInt32Signed:
                            case NumericConversionKind.Float64ToInt32Signed:
                                instructions.Add(CilOpCodes.Conv_Ovf_I4);
                                break;
                            case NumericConversionKind.Float32ToInt32Unsigned:
                            case NumericConversionKind.Float64ToInt32Unsigned:
                                instructions.Add(CilOpCodes.Conv_Ovf_U4);
                                break;
                            case NumericConversionKind.Float32ToInt64Signed:
                            case NumericConversionKind.Float64ToInt64Signed:
                                instructions.Add(CilOpCodes.Conv_Ovf_I8);
                                break;
                            case NumericConversionKind.Float32ToInt64Unsigned:
                            case NumericConversionKind.Float64ToInt64Unsigned:
                                instructions.Add(CilOpCodes.Conv_Ovf_U8);
                                break;
                            case NumericConversionKind.I32Extend8Signed:
                                instructions.Add(CilOpCodes.Conv_I1);
                                break;
                            case NumericConversionKind.I32Extend16Signed:
                                instructions.Add(CilOpCodes.Conv_I2);
                                break;
                            case NumericConversionKind.I64Extend8Signed:
                                instructions.Add(CilOpCodes.Conv_I1);
                                instructions.Add(CilOpCodes.Conv_I8);
                                break;
                            case NumericConversionKind.I64Extend16Signed:
                                instructions.Add(CilOpCodes.Conv_I2);
                                instructions.Add(CilOpCodes.Conv_I8);
                                break;
                            case NumericConversionKind.I64Extend32Signed:
                                instructions.Add(CilOpCodes.Conv_I4);
                                instructions.Add(CilOpCodes.Conv_I8);
                                break;
                            case NumericConversionKind.Float32ToInt32Truncate:
                            case NumericConversionKind.Float64ToInt32Truncate:
                                instructions.Add(CilOpCodes.Conv_I4);
                                break;
                            case NumericConversionKind.Float32ToInt32UnsignedTruncate:
                            case NumericConversionKind.Float64ToInt32UnsignedTruncate:
                                instructions.Add(CilOpCodes.Conv_U4);
                                break;
                            case NumericConversionKind.Float32ToInt64Truncate:
                            case NumericConversionKind.Float64ToInt64Truncate:
                                instructions.Add(CilOpCodes.Conv_I8);
                                break;
                            case NumericConversionKind.Float32ToInt64UnsignedTruncate:
                            case NumericConversionKind.Float64ToInt64UnsignedTruncate:
                                instructions.Add(CilOpCodes.Conv_U8);
                                break;
                            default:
                                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unsupported numeric conversion: {conversionKind}"));
                                instructions.Add(CilOpCodes.Call, writeLine);
                                instructions.Add(CilOpCodes.Pop);
                                PushDefaultOf(resultType, method);
                                break;
                            }
                        }

                        if (conversion.FixedPointScale > 0)
                        {
                            var multiplier = Math.Pow(2, -conversion.FixedPointScale);
                            if (conversionKind is NumericConversionKind.SignedInt32ToFloat32 or NumericConversionKind.UnsignedInt32ToFloat32
                                or NumericConversionKind.SignedInt64ToFloat32 or NumericConversionKind.UnsignedInt64ToFloat32)
                            {
                                instructions.Add(CilOpCodes.Ldc_R4, (float)multiplier);
                                instructions.Add(CilOpCodes.Mul);
                            }
                            else
                            {
                                instructions.Add(CilOpCodes.Ldc_R8, multiplier);
                                instructions.Add(CilOpCodes.Mul);
                            }
                        }
                    }

                    StoreToOperand(convertedDestination, method, locals, writeLine);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Invalid conversion instruction: {instruction}"));
                    instructions.Add(CilOpCodes.Call, writeLine);
                }
                break;

            case OpCode.NewArr:
                if (instruction.Operands is [_, SzArrayTypeAnalysisContext { ElementType: { } newArrayElement }, { } length])
                {
                    LoadOperand(length, method, locals, writeLine);
                    instructions.Add(CilOpCodes.Newarr, newArrayElement.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    instructions.Add(CilOpCodes.Ldnull);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.PackFloatAggregate:
            {
                var aggregateType = (TypeAnalysisContext)instruction.Operands[1];
                var fields = Arm64CallingConventionResolver.GetFloatAggregateFields(aggregateType)!;
                if (fields.Any(f => (f.Attributes & (FieldAttributes.FieldAccessMask | FieldAttributes.InitOnly)) != FieldAttributes.Public))
                {
                    context.AddWarning($"Cannot reconstruct {aggregateType.FullName} argument from inaccessible fields.");
                    PushDefaultOf(aggregateType, method);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                    break;
                }

                var signature = aggregateType.ToTypeSignature();
                var aggregate = new CilLocalVariable(signature);
                body.LocalVariables.Add(aggregate);
                instructions.Add(CilOpCodes.Ldloca, aggregate);
                instructions.Add(CilOpCodes.Initobj, signature.ToTypeDefOrRef());
                for (var i = 0; i < fields.Length; i++)
                {
                    var aggregateField = fields[i];
                    instructions.Add(CilOpCodes.Ldloca, aggregate);
                    var lane = instruction.Operands[i + 2];
                    LoadOperand(lane, method, locals, writeLine, aggregateField.FieldType);
                    instructions.Add(aggregateField.FieldType.FullName == "System.Single" ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
                    instructions.Add(CilOpCodes.Stfld, aggregateField.ToFieldDescriptor());
                }
                instructions.Add(CilOpCodes.Ldloc, aggregate);
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;
            }

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj.
                // If we can't, just fall back to an Ldnull.
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    // Operands run [ctor, newObject, arguments..., methodInfo], so take only as many as
                    // the constructor declares (i.e. drop methodInfo)
                    var constructorArgs = constructorCall.Operands.Skip(ConstructorReceiverIndex(constructorCall) + 1).Take(constructor.Parameters.Count).ToList();

                    if (IsDelegateConstructor(constructor) && constructorArgs.Count == 2)
                    {
                        if (instruction.Operands[1] is GenericInstanceTypeAnalysisContext allocatedDelegate
                            && MatchesDelegateAllocation(constructor, allocatedDelegate)
                            && !SameType(constructor.DeclaringType!, allocatedDelegate))
                        {
                            var definition = (constructor as ConcreteGenericMethodAnalysisContext)?.BaseMethodContext ?? constructor;
                            constructor = new ConcreteGenericMethodAnalysisContext(definition, allocatedDelegate.GenericArguments, []);
                        }

                        var sources = new List<(LocalVariable Local, Instruction Definition)>();
                        var delegateTarget = ResolveDelegateMethodOperand(constructorArgs[1], local =>
                        {
                            var definitions = context.ControlFlowGraph!.Instructions
                                .Where(candidate => ReferenceEquals(candidate.Destination, local)).Take(2).ToArray();
                            return definitions is [var unique] ? unique : null;
                        }, sources);

                        if (delegateTarget == null)
                            context.AddWarning($"Could not resolve the target of delegate constructor {constructor.FullName}.");
                        else
                        {
                            constructorArgs[1] = delegateTarget;
                            var deadSources = sources.TakeWhile(source =>
                                CountLocalUses(context.ControlFlowGraph!, source.Local) == 1).ToArray();
                            foreach (var (_, definition) in deadSources)
                                SuppressInstruction(definition, instructionMap);
                        }
                    }

                    if (IsDelegateConstructor(constructor) && constructorArgs is
                        [var receiver, RuntimeMethodInfoAnalysisContext { IsVirtualDelegateTarget: true } virtualTarget])
                    {
                        LoadOperand(receiver, method, locals, writeLine, constructor.Parameters[0].ParameterType);
                        instructions.Add(CilOpCodes.Dup);
                        instructions.Add(CilOpCodes.Ldvirtftn, virtualTarget.RepresentedMethod.ToMethodDescriptor());
                    }
                    else
                    {
                        for (var i = 0; i < constructorArgs.Count; i++)
                            LoadOperand(constructorArgs[i], method, locals, writeLine, constructor.Parameters[i].ParameterType);
                    }

                    instructions.Add(CilOpCodes.Newobj, constructor.ToMethodDescriptor());
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    // Layout order may emit the constructor's block before the allocation's.
                    // Suppress any CIL it already produced as well as the pending ISIL call.
                    SuppressInstruction(constructorCall, instructionMap);
                }
                else if (instruction.Operands is [_, TypeAnalysisContext allocatedType] && allocatedType.Methods.FirstOrDefault(m => m is { Name: ".ctor", Parameters.Count: 0 }) is { } parameterlessCtor)
                {
                    // Nothing to fuse with, so the allocation was self-contained. The type is still right, so construct it bare.
                    instructions.Add(CilOpCodes.Newobj, parameterlessCtor.ToMethodDescriptor());
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                break;

            case OpCode.Box:
                if (instruction.Operands is [_, TypeAnalysisContext boxedType, var boxedValue])
                {
                    // il2cpp_value_box takes the value by address, but IL boxes it by value
                    LoadOperand(boxedValue is AddressOf { Target: LocalVariable byRef } ? byRef : boxedValue, method, locals, writeLine, boxedType);
                    instructions.Add(CilOpCodes.Box, boxedType.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    instructions.Add(CilOpCodes.Ldnull);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.IsInstance:
                var targetType = (TypeAnalysisContext)instruction.Operands[2];
                LoadOperand(instruction.Operands[1], method, locals, writeLine, context.AppContext.SystemTypes.SystemObjectType);
                instructions.Add(CilOpCodes.Isinst, targetType.ToTypeSignature().ToTypeDefOrRef());
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType]
                    && exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0) is { } exceptionCtor)
                    instructions.Add(CilOpCodes.Newobj, exceptionCtor.ToMethodDescriptor());
                else if (instruction.Operands is [LocalVariable or FieldReference])
                    LoadOperand(instruction.Operands[0], method, locals, writeLine); // an already-constructed exception
                else
                    instructions.Add(CilOpCodes.Ldnull);

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Phi opcodes should not exist at this point in decompilation ({instruction})"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                {
                    if (instruction.Operands[0] is Immediate targetAddress)
                        instructions.Add(CilOpCodes.Ldstr, $"Method not found @{targetAddress.UnsignedValue:X}");
                    else // Probably key function. Just the target, the full operand dump is huge and blows the 16MB #US heap limit
                        instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unknown call target operand: {instruction.Operands[0]}"));

                    instructions.Add(CilOpCodes.Call, writeLine);

                    // A missing direct-call target can still have a result local used by later
                    // instructions. Initialize it with a typed placeholder instead of letting
                    // decompiled C# read an unassigned local.
                    if (instruction.OpCode == OpCode.Call
                        && instruction.Operands.Count > 1
                        && instruction.Operands[1] is LocalVariable unresolvedCallResult)
                    {
                        var resultType = unresolvedCallResult.Type ?? context.AppContext.SystemTypes.SystemObjectType;
                        PushDefaultOf(resultType, method);
                        StoreToOperand(unresolvedCallResult, method, locals, writeLine);
                    }

                    break;
                }

                // IL2CPP can place an implicit base constructor call after the derived
                // constructor body. Keeping it there makes ILSpy emit `base..ctor()` instead of
                // treating it as C# constructor boilerplate.
                if (ShouldOmitImplicitBaseConstructorCall(instruction, context, targetMethod))
                {
                    instructions.Add(CilOpCodes.Nop);
                    break;
                }

                var importedMethod = targetMethod.ToMethodDescriptor();

                // A call can write straight into a private struct return buffer field.
                var resultField = instruction is { OpCode: OpCode.Call, Operands: [_, FieldReference fieldResult, ..] }
                    ? fieldResult : null;
                if (resultField is { Field.IsStatic: false })
                    LoadFieldReceiver(resultField, method, locals);

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    if ((instruction.Operands.Count - 1) >= thisParamIndex)
                    {
                        var receiver = instruction.Operands[thisParamIndex];
                        if (IsOptimizedCurrentInstanceReceiver(receiver, context, targetMethod))
                            instructions.Add(CilOpCodes.Ldarg_0);
                        else
                            LoadOperand(receiver, method, locals, writeLine,
                                targetMethod.DeclaringType, isReceiver: true);
                    }
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Non static method called without 'this' param ({instruction})"));
                        instructions.Add(CilOpCodes.Call, writeLine);
                        instructions.Add(CilOpCodes.Ldnull);
                    }
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // A call whose target was only identified after lifting still carries the operands the
                // unknown-callee convention gave it, which may be fewer than the method actually takes.
                // The stack still has to match the signature, so anything missing gets a placeholder.
                var availableArgs = instruction.Operands.Count - callParamIndex;
                for (var i = 0; i < targetMethod.Parameters.Count; i++)
                {
                    var parameterType = targetMethod.Parameters[i].ParameterType;

                    if (i < availableArgs)
                    {
                        var argument = instruction.Operands[callParamIndex + i];
                        if (argument is UnrecoveredCallArgument)
                            context.AddWarning($"Stack argument {i + 1} for {targetMethod.FullName} was not recovered; a default value is emitted.");

                        LoadOperand(argument, method, locals, writeLine, parameterType);
                    }
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr,
                            Diagnostic($"Missing argument {i} ({parameterType.FullName}) for {targetMethod.FullName}"));
                        instructions.Add(CilOpCodes.Call, writeLine);
                        PushDefaultOf(parameterType, method);
                    }
                }

                instructions.Add(instruction.NullCheckReceiver ? CilOpCodes.Callvirt : CilOpCodes.Call, importedMethod);

                // the lifter's guess at whether the callee returns anything can disagree with the
                // signature we later resolved, so go by the signature and balance the stack
                if (!targetMethod.IsVoid)
                {
                    if (resultField != null)
                        instructions.Add(resultField.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld,
                            resultField.Field.ToFieldDescriptor());
                    else if (instruction.OpCode == OpCode.Call)
                        StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                    else
                        instructions.Add(CilOpCodes.Pop);
                }

                break;

            case OpCode.IndirectCall:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Indirect call: {instruction.Operands[0]} (should have been resolved before IL gen)"));
                instructions.Add(CilOpCodes.Call, writeLine);

                // Keep the IL stack and local assignment valid when the return register has a
                // known destination but call-target recovery failed. Without this, callers can
                // decompile to an unassigned local followed by a return or condition on that local.
                if (instruction.Destination is LocalVariable indirectResult && graphUsedLocals.Contains(indirectResult))
                {
                    var resultType = indirectResult.Type ?? context.AppContext.SystemTypes.SystemObjectType;
                    PushDefaultOf(resultType, method);
                    StoreToOperand(indirectResult, method, locals, writeLine);
                }
                break;

            case OpCode.Return:
                if (!context.IsVoid)
                {
                    if (instruction.Operands.Count == 1)
                        LoadOperand(instruction.Operands[0], method, locals, writeLine, context.ReturnType);
                    else if (context.ReturnType is ByRefTypeAnalysisContext)
                    {
                        instructions.Add(CilOpCodes.Ldstr, "MC2IL could not recover the reference returned by this method.");
                        EmitUnsupportedOperationThrow(instructions, module);
                        break;
                    }
                    else
                        PushDefaultOf(context.ReturnType, method);
                }
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals, writeLine);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.IndirectJump:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unresolved indirect jump: {instruction.Operands[0]}"));
                EmitUnrecoveredReturn(context, method, writeLine);
                break;

            case OpCode.ShiftStack:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Stack shift: {instruction} (stack analysis should have removed these)"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:
            case OpCode.CheckGreaterUnsigned:
            case OpCode.CheckLessUnsigned:
            case OpCode.CheckGreaterOrEqualUnsigned:
            case OpCode.CheckLessOrEqualUnsigned:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:
            case OpCode.DivideUnsigned:
            case OpCode.Modulo:
            case OpCode.ModuloUnsigned:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.ShiftRightUnsigned:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                if (instruction is { OpCode: OpCode.Or, OperandWidth: 64,
                        Operands: [var packedResult, var packedInput, Immediate packedMask] })
                {
                    var inputType = OperandType(packedInput);
                    var resultType = OperandType(packedResult) ?? context.AppContext.SystemTypes.SystemUInt64Type;
                    if ((packedInput is Immediate || IntegerOperandType(inputType) != null)
                        && resultType.FullName is "System.Int64" or "System.UInt64")
                    {
                        LoadOperand(packedInput, method, locals, writeLine,
                            context.AppContext.SystemTypes.SystemUInt64Type);
                        if (packedInput is not Immediate && inputType?.FullName is not ("System.Int64" or "System.UInt64"))
                            instructions.Add(CilOpCodes.Conv_U8);
                        instructions.Add(CilOpCodes.Ldc_I8, packedMask.Value);
                        instructions.Add(CilOpCodes.Or);
                    }
                    else
                    {
                        // Numeric conversion of a managed reference or float does not preserve
                        // register bits. Keep the failure explicit and the CIL stack typed.
                        context.AddWarning($"Could not recover the packed ARM64 ORR input or result: {instruction}");
                        PushDefaultOf(resultType, method);
                    }
                    StoreToOperand(packedResult, method, locals, writeLine);
                    break;
                }

                // klass pointer read => GetType
                if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    && (TryEmitExactTypeComparison(instruction, method, locals, writeLine)
                        || TryEmitOverloadedReferenceEquals(instruction, context, method, locals, writeLine)))
                    break;

                var leftOperand = instruction.Operands[1];
                var rightOperand = instruction.Operands[2];
                var isReferenceEquality = instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual;
                var isComparison = instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    or OpCode.CheckGreater or OpCode.CheckLess or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
                    or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
                    or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned;
                var floatConversion = FloatOperandConversion(instruction, isComparison);
                var isFloatComparison = isComparison && floatConversion != null;
                var isBooleanLogic = (isComparison || instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor)
                    && (IsBoolean(leftOperand) || IsBoolean(rightOperand))
                    && IsBooleanOrBooleanConstant(leftOperand)
                    && IsBooleanOrBooleanConstant(rightOperand);
                var isManagedReferenceEquality = isReferenceEquality && context.AppContext.InstructionSet is ArmV7InstructionSet
                    && (ReferenceComparisonType(leftOperand) != null || IsZeroConstant(leftOperand))
                    && (ReferenceComparisonType(rightOperand) != null || IsZeroConstant(rightOperand));
                var apply32BitIntegerWidth = instruction.OperandWidth == 32
                    && !isBooleanLogic && !isFloatComparison && !isManagedReferenceEquality;
                var comparisonType = isComparison
                    ? IntegerOperandType(OperandType(leftOperand) ?? OperandType(rightOperand))
                    : null;
                var operationType = floatConversion == null
                    ? IntegerOperandType(OperandType(instruction.Operands[0]))
                    : null;

                var floatType = FloatExpectedType(floatConversion, context);
                var leftExpectedType = floatType
                    ?? (isReferenceEquality && IsZeroConstant(leftOperand)
                    ? ReferenceComparisonType(rightOperand)
                    : IntegerOperandType(OperandType(leftOperand)) ?? comparisonType ?? operationType);
                var rightExpectedType = floatType
                    ?? (isReferenceEquality && IsZeroConstant(rightOperand)
                    ? ReferenceComparisonType(leftOperand)
                    : IntegerOperandType(OperandType(rightOperand)) ?? comparisonType ?? operationType);

                // CIL shift counts are int32 even when the shifted value is 64-bit. Immediate
                // counts need this hint so large encodings are narrowed before `shl`/`shr`.
                var isShift = instruction.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned;
                if (isShift)
                    rightExpectedType = context.AppContext.SystemTypes.SystemInt32Type;

                LoadOperand(leftOperand, method, locals, writeLine, leftExpectedType);
                if (apply32BitIntegerWidth)
                {
                    if (!isComparison || !IsInt32StackValue(leftOperand, leftExpectedType))
                        instructions.Add(Integer32InputConversion(instruction.OpCode));
                }
                else if (isShift && instruction.OperandWidth == 64)
                    instructions.Add(leftExpectedType?.FullName is "System.UInt32" or "System.UInt64" or "System.UIntPtr"
                        ? CilOpCodes.Conv_U8
                        : CilOpCodes.Conv_I8);
                else if (floatConversion is { } conv1 && NeedsFloatArithmeticConversion(leftOperand, conv1))
                    instructions.Add(conv1);
                LoadOperand(rightOperand, method, locals, writeLine, rightExpectedType);
                if (apply32BitIntegerWidth)
                {
                    if (!isComparison || !IsInt32StackValue(rightOperand, rightExpectedType))
                        instructions.Add(isShift ? CilOpCodes.Conv_I4 : Integer32InputConversion(instruction.OpCode));
                }
                else if (floatConversion is { } conv2 && NeedsFloatArithmeticConversion(rightOperand, conv2))
                    instructions.Add(conv2);

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;
                    case OpCode.CheckGreaterUnsigned: instructions.Add(CilOpCodes.Cgt_Un); break;
                    case OpCode.CheckLessUnsigned: instructions.Add(CilOpCodes.Clt_Un); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // For floats, ordered >= must be false on NaN, so negate clt.un.
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(isFloatComparison ? CilOpCodes.Clt_Un : CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(isFloatComparison ? CilOpCodes.Cgt_Un : CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // For floats, .un comparisons include unordered (NaN) as well.
                    case OpCode.CheckGreaterOrEqualUnsigned:
                        instructions.Add(isFloatComparison ? CilOpCodes.Clt : CilOpCodes.Clt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    case OpCode.CheckLessOrEqualUnsigned:
                        instructions.Add(isFloatComparison ? CilOpCodes.Cgt : CilOpCodes.Cgt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;
                    case OpCode.DivideUnsigned: instructions.Add(CilOpCodes.Div_Un); break;
                    case OpCode.Modulo: instructions.Add(CilOpCodes.Rem); break;
                    case OpCode.ModuloUnsigned: instructions.Add(CilOpCodes.Rem_Un); break;

                    case OpCode.ShiftLeft: instructions.Add(CilOpCodes.Shl); break;
                    case OpCode.ShiftRight: instructions.Add(CilOpCodes.Shr); break;
                    case OpCode.ShiftRightUnsigned: instructions.Add(CilOpCodes.Shr_Un); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                if (apply32BitIntegerWidth && !isComparison)
                    EmitZeroExtend32Result(instruction.Operands[0], instructions);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Select:
            {
                var startFalse = new CilInstruction(CilOpCodes.Nop);
                var store = new CilInstruction(CilOpCodes.Nop);
                var type = OperandType(instruction.Operands[0]);
                LoadOperand(instruction.Operands[1], method, locals, writeLine, context.AppContext.SystemTypes.SystemBooleanType);
                instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(startFalse));
                LoadOperand(instruction.Operands[2], method, locals, writeLine, type);
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel(store));
                instructions.Add(startFalse);
                LoadOperand(instruction.Operands[3], method, locals, writeLine, type);
                instructions.Add(store);
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;
            }
            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, OperandType(instruction.Operands[0]));

                if (instruction.OperandWidth == 32 && !IsBoolean(instruction.Operands[1]))
                    instructions.Add(CilOpCodes.Conv_I4);

                if (instruction.OpCode == OpCode.Negate)
                    instructions.Add(CilOpCodes.Neg);
                else if (IsBoolean(instruction.Operands[1]))
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Ceq);
                }
                else
                    instructions.Add(CilOpCodes.Not);

                if (instruction.OperandWidth == 32)
                    EmitZeroExtend32Result(instruction.Operands[0], instructions);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Unknown instruction: {instruction}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                break;
        }

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }

    private static void EmitUnrecoveredReturn(MethodAnalysisContext context, MethodDefinition method, IMethodDescriptor writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;
        if (context.ReturnType is ByRefTypeAnalysisContext)
        {
            EmitUnsupportedOperationThrow(instructions, method.DeclaringModule!);
            return;
        }

        instructions.Add(CilOpCodes.Call, writeLine);
        if (!context.IsVoid)
            PushDefaultOf(context.ReturnType, method);
        instructions.Add(CilOpCodes.Ret);
    }

    private static void EmitBitReinterpretation(ModuleDefinition module, CilInstructionCollection instructions,
        NumericConversionKind kind)
    {
        var factory = module.CorLibTypeFactory;
        var bitConverter = factory.CorLibScope.CreateTypeReference("System", "BitConverter");
        if (kind == NumericConversionKind.Float64ReinterpretI64)
        {
            instructions.Add(CilOpCodes.Call, bitConverter.CreateMemberReference("DoubleToInt64Bits",
                MethodSignature.CreateStatic(factory.Int64, [factory.Double])));
            return;
        }

        if (kind == NumericConversionKind.I64ReinterpretFloat64)
        {
            instructions.Add(CilOpCodes.Call, bitConverter.CreateMemberReference("Int64BitsToDouble",
                MethodSignature.CreateStatic(factory.Double, [factory.Int64])));
            return;
        }

        // Use the byte-array overloads available in older Unity corlibs as well.
        var integerToFloat = kind == NumericConversionKind.I32ReinterpretFloat32;
        var bytes = factory.Byte.MakeSzArrayType();
        instructions.Add(CilOpCodes.Call, bitConverter.CreateMemberReference("GetBytes",
            MethodSignature.CreateStatic(bytes, [integerToFloat ? factory.Int32 : factory.Single])));
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Call, bitConverter.CreateMemberReference(integerToFloat ? "ToSingle" : "ToInt32",
            MethodSignature.CreateStatic(integerToFloat ? factory.Single : factory.Int32, [bytes, factory.Int32])));
    }

    private static void EmitUnsupportedOperationThrow(CilInstructionCollection instructions, ModuleDefinition module)
    {
        var factory = module.CorLibTypeFactory;
        var constructor = factory.CorLibScope.CreateTypeReference("System", "NotSupportedException")
            .CreateMemberReference(".ctor", MethodSignature.CreateInstance(factory.Void, [factory.String]));
        instructions.Add(CilOpCodes.Newobj, constructor);
        instructions.Add(CilOpCodes.Throw);
    }

    private static bool ShouldOmitImplicitBaseConstructorCall(Instruction instruction,
        MethodAnalysisContext context, MethodAnalysisContext targetMethod)
    {
        if (instruction.OpCode != OpCode.CallVoid
            || context.Name != ".ctor"
            || targetMethod is not { Name: ".ctor", IsStatic: false, Parameters.Count: 0 }
            || targetMethod.DeclaringType is not { } targetType
            || context.DeclaringType is not { BaseType: { } baseType } declaringType
            // C# decompilers recognize the trivial closure constructor by its base call.
            || TrivialConstructorRecovery.IsDisplayClass(declaringType)
            || !SameType(baseType, targetType))
            return false;

        return instruction.Operands.Count > 1
            && (instruction.Operands[1] switch
            {
                LocalVariable { IsThis: true } => true,
                AddressOf { Target: LocalVariable { IsThis: true } } => true,
                _ => false
            });
    }

    private static bool SameType(TypeAnalysisContext first, TypeAnalysisContext second) =>
        ReferenceEquals(first, second)
        || first.FullName == second.FullName
        && first.DeclaringAssembly.CleanAssemblyName == second.DeclaringAssembly.CleanAssemblyName;

    private static bool IsOptimizedCurrentInstanceReceiver(IOperand receiver,
        MethodAnalysisContext caller, MethodAnalysisContext target)
    {
        if (receiver is not TypeAnalysisContext receiverType
            || receiverType is RuntimeClassTypeAnalysisContext or RuntimeMethodInfoAnalysisContext
                or RuntimeFieldInfoAnalysisContext or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            || caller.IsStatic
            || caller.DeclaringType is not { } callerType
            || target.DeclaringType is not { } targetType
            || !SameType(receiverType, targetType))
            return false;

        // IL2CPP can leave a class metadata pointer in the receiver register when an
        // instance method never reads `this`. In that case, use the caller's live `this`
        // only when it is assignable to the target method's declaring type.
        for (var current = callerType; current != null; current = current.BaseType)
        {
            if (SameType(current, targetType))
                return true;
        }

        return false;
    }

    private static int ConstructorReceiverIndex(Instruction constructorCall) => constructorCall.OpCode == OpCode.CallVoid ? 1 : 2;

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];
        var graph = context.ControlFlowGraph!;

        if (newobj.Operands[1] is TypeAnalysisContext allocatedType
            && (IsDelegateType(allocatedType) || IteratorFactoryRecovery.IsIteratorType(allocatedType))
            && FindConstructorOnSuccessorPath(graph, newobj, allocatedType) is { } pathConstructor)
            return pathConstructor;

        // The allocation and the constructor call routinely end up in different blocks
        var instructions = graph.Instructions;
        var index = instructions.IndexOf(newobj);

        if (index < 0)
            return null;

        for (var i = index + 1; i < instructions.Count; i++)
        {
            var candidate = instructions[i];

            if (candidate is not { OpCode: OpCode.Call or OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, ..] })
                continue;

            var receiver = ConstructorReceiverIndex(candidate);

            if (candidate.Operands.Count > receiver && ReferenceEquals(candidate.Operands[receiver], newObject))
                return candidate;
        }

        return null;
    }

    private static Instruction? FindConstructorOnSuccessorPath(ISILControlFlowGraph graph, Instruction allocation,
        TypeAnalysisContext allocatedType)
    {
        var block = graph.FindBlockByInstruction(allocation);
        if (block == null)
            return null;

        var index = block.Instructions.IndexOf(allocation) + 1;
        var visited = new HashSet<Block>();
        while (visited.Add(block))
        {
            for (; index < block.Instructions.Count; index++)
            {
                var candidate = block.Instructions[index];
                if (candidate.OpCode is OpCode.Nop or OpCode.Jump)
                    continue;
                if (!candidate.IsCall || candidate.Operands[0] is not MethodAnalysisContext
                        { Name: ".ctor", IsStatic: false, DeclaringType: { } owner } constructor
                    || !(IsDelegateType(allocatedType)
                        ? IsDelegateConstructor(constructor) && MatchesDelegateAllocation(constructor, allocatedType)
                        : SameType(owner, allocatedType)))
                    return null;

                var receiver = ConstructorReceiverIndex(candidate);
                return candidate.Operands.Count > receiver + constructor.Parameters.Count
                    && ReferenceEquals(candidate.Operands[receiver], allocation.Operands[0]) ? candidate : null;
            }

            if (block.Successors is not [var next] || next.Predecessors is not [var predecessor] || predecessor != block)
                break;
            block = next;
            index = 0;
        }

        return null;
    }

    internal static bool IsDelegateType(TypeAnalysisContext? type) =>
        type != null && (type.IsDelegate || type.BaseType?.FullName == "System.MulticastDelegate");

    internal static bool IsDelegateConstructor(MethodAnalysisContext method) =>
        method is { Name: ".ctor", IsStatic: false, Parameters.Count: 2 }
        && method.Parameters[0].ParameterType.FullName == "System.Object"
        && method.Parameters[1].ParameterType.FullName == "System.IntPtr"
        && IsDelegateType(method.DeclaringType);

    internal static bool MatchesDelegateAllocation(MethodAnalysisContext constructor, TypeAnalysisContext allocatedType)
    {
        var owner = constructor.DeclaringType!;
        var ownerDefinition = (owner as GenericInstanceTypeAnalysisContext)?.GenericType ?? owner;
        var allocatedDefinition = (allocatedType as GenericInstanceTypeAnalysisContext)?.GenericType ?? allocatedType;
        return SameType(ownerDefinition, allocatedDefinition);
    }

    internal static RuntimeMethodInfoAnalysisContext? ResolveDelegateMethodOperand(IOperand operand,
        Func<LocalVariable, Instruction?> findDefinition, List<(LocalVariable Local, Instruction Definition)> sources)
    {
        var seen = new HashSet<LocalVariable>();
        var dereferenced = false;
        while (true)
        {
            if (operand is RuntimeMethodInfoAnalysisContext methodInfo)
                return methodInfo;

            // A native delegate can take the function pointer loaded from the start of
            // MethodInfo, or load its handle through an ELF metadata slot. A zero-offset
            // load from a local whose definition names the method preserves its identity.
            if (!dereferenced && operand is MemoryOperand
                { Base: LocalVariable baseLocal, Index: null, Addend: 0, Scale: 0 })
            {
                operand = baseLocal;
                dereferenced = true;
            }

            if (operand is not LocalVariable local || !seen.Add(local)
                || findDefinition(local) is not { OpCode: OpCode.Move, Operands: [_, var source] } definition)
                return null;

            sources.Add((local, definition));
            operand = source;
        }
    }

    private static void SuppressInstruction(Instruction instruction,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        if (instructionMap.TryGetValue(instruction, out var generated))
        {
            foreach (var cilInstruction in generated)
            {
                cilInstruction.OpCode = CilOpCodes.Nop;
                cilInstruction.Operand = null;
            }
        }

        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }

    private static CilOpCode? FloatOperandConversion(Instruction instruction, bool isComparison)
    {
        if (instruction.Operands.Count == 4 && instruction.Operands[3] is FloatingPointPrecision precision)
            return precision.IsDouble ? CilOpCodes.Conv_R8 : CilOpCodes.Conv_R4;

        if (isComparison)
        {
            var left = FloatingType(instruction.Operands[1]);
            var right = FloatingType(instruction.Operands[2]);
            if (left == "System.Double" || right == "System.Double")
                return CilOpCodes.Conv_R8;
            if (left == "System.Single" || right == "System.Single")
                return CilOpCodes.Conv_R4;
            return null;
        }

        if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Modulo))
            return null;

        return (instruction.Operands[0] as LocalVariable)?.Type?.FullName switch
        {
            "System.Single" => CilOpCodes.Conv_R4,
            "System.Double" => CilOpCodes.Conv_R8,
            _ => null,
        };
    }

    private static string? FloatingType(IOperand operand) => operand switch
    {
        FloatLiteral => "System.Single",
        DoubleLiteral => "System.Double",
        _ => OperandType(operand)?.FullName,
    };

    private static bool NeedsFloatArithmeticConversion(IOperand operand, CilOpCode conversion)
    {
        // Literals are emitted using the expected arithmetic type already. For typed locals and
        // fields, skip conv.r4/conv.r8 when the value is already that exact C# type.
        if (operand is Immediate or FloatLiteral or DoubleLiteral)
            return false;

        var expectedType = conversion == CilOpCodes.Conv_R4 ? "System.Single" : "System.Double";
        return OperandType(operand)?.FullName != expectedType;
    }

    private static TypeAnalysisContext? FloatExpectedType(CilOpCode? conversion, MethodAnalysisContext context)
        => conversion == CilOpCodes.Conv_R4 ? context.AppContext.SystemTypes.SystemSingleType
            : conversion == CilOpCodes.Conv_R8 ? context.AppContext.SystemTypes.SystemDoubleType : null;

    private static CilOpCode Integer32InputConversion(OpCode opCode) => opCode switch
    {
        OpCode.DivideUnsigned or OpCode.ModuloUnsigned or OpCode.ShiftRightUnsigned
            or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
            or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned
            => CilOpCodes.Conv_U4,
        _ => CilOpCodes.Conv_I4
    };

    private static void EmitMoveWidthConversion(Instruction move, CilInstructionCollection instructions)
    {
        if (move.OperandWidth != 32)
            return;

        var destination = move.Operands[0];
        // A native word load/store can copy an entire small struct. Once both ends are
        // typed as that struct, CIL must preserve the value without a numeric conversion.
        if (OperandType(destination) is { IsValueType: true, IsEnumType: false } valueType
            && valueType.Type is LibMC2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE
                or LibMC2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST
            && MetadataResolver.IsSameType(valueType, OperandType(move.Operands[1])))
            return;

        // CIL uses the same stack width for both signed and unsigned 32-bit values.
        // An extra conv.u4 hides direct parameter and constant stores from iterator recovery.
        if (OperandType(destination)?.FullName is "System.Int32" or "System.UInt32"
            && (move.Operands[1] is ArrayLength or Immediate
                || OperandType(move.Operands[1])?.FullName is "System.Int32" or "System.UInt32"))
            return;

        EmitZeroExtend32Result(destination, instructions);
    }

    private static void EmitZeroExtend32Result(IOperand destination, CilInstructionCollection instructions)
    {
        // A boolean is already the logical 0/1 result of comparisons and condition materialization.
        // Converting it to uint32 makes ILSpy reconstruct `(condition ? 1 : 0)` at every use.
        // A recovered float literal is already an FP stack value, even when IL2CPP originally
        // stored its bits with an integer register (STR Wn).
        var type = OperandType(destination);
        if (type?.FullName is "System.Boolean" or "System.Single" or "System.Double")
            return;

        instructions.Add(CilOpCodes.Conv_U4);

        var primitiveType = type is { IsEnumType: true }
            ? type.EnumUnderlyingType?.FullName
            : type?.FullName;

        switch (primitiveType)
        {
            case "System.Int64":
            case "System.UInt64":
                instructions.Add(CilOpCodes.Conv_U8);
                break;
            case "System.IntPtr":
            case "System.UIntPtr":
                instructions.Add(CilOpCodes.Conv_U);
                break;
        }
    }

    private static void LoadOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine,
        TypeAnalysisContext? expectedType = null, bool isReceiver = false)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;

        // A null reference reaches us as an integer zero, which would otherwise be emitted as a literal 0
        // and read back as a cast from a number.
        if ((expectedType is RuntimeClassTypeAnalysisContext or RuntimeMethodInfoAnalysisContext
                or RuntimeFieldInfoAnalysisContext or StaticFieldStorageTypeAnalysisContext
                or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext)
            && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Conv_I);
            return;
        }

        if (expectedType is { IsValueType: false } && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        // Integer constants must have the same evaluation-stack width as their typed destination.
        // A typed Int32/UInt32 immediate has already been narrowed to its low 32 bits.
        if (operand is Immediate typedImmediate)
        {
            var expectedPrimitiveType = expectedType is { IsEnumType: true }
                ? expectedType.EnumUnderlyingType?.FullName
                : expectedType?.FullName;

            void LoadIntegerImmediate(long value)
            {
                if (value is >= int.MinValue and <= int.MaxValue)
                    instructions.Add(CilOpCodes.Ldc_I4, (int)value);
                else
                    instructions.Add(CilOpCodes.Ldc_I8, value);
            }

            switch (expectedPrimitiveType)
            {
                case "System.Single":
                    instructions.Add(CilOpCodes.Ldc_R4, FloatLiteralRecovery.ToSingleLiteral(typedImmediate).Value);
                    return;
                case "System.Double":
                    instructions.Add(CilOpCodes.Ldc_R8, FloatLiteralRecovery.ToDoubleLiteral(typedImmediate).Value);
                    return;
                case "System.Int64":
                case "System.UInt64":
                    instructions.Add(CilOpCodes.Ldc_I8, typedImmediate.Value);
                    return;

                case "System.Int32":
                case "System.UInt32":
                    instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)typedImmediate.Value));
                    return;

                case "System.SByte":
                    LoadIntegerImmediate(typedImmediate.Value);
                    if (typedImmediate.Value is < sbyte.MinValue or > sbyte.MaxValue)
                        instructions.Add(CilOpCodes.Conv_I1);
                    return;

                case "System.Byte":
                    LoadIntegerImmediate(typedImmediate.Value);
                    if (typedImmediate.Value is < byte.MinValue or > byte.MaxValue)
                        instructions.Add(CilOpCodes.Conv_U1);
                    return;

                case "System.Int16":
                    LoadIntegerImmediate(typedImmediate.Value);
                    if (typedImmediate.Value is < short.MinValue or > short.MaxValue)
                        instructions.Add(CilOpCodes.Conv_I2);
                    return;

                case "System.UInt16":
                case "System.Char":
                    LoadIntegerImmediate(typedImmediate.Value);
                    if (typedImmediate.Value is < ushort.MinValue or > ushort.MaxValue)
                        instructions.Add(CilOpCodes.Conv_U2);
                    return;

                case "System.Boolean":
                    LoadIntegerImmediate(typedImmediate.Value);
                    if (typedImmediate.Value is < int.MinValue or > int.MaxValue)
                        instructions.Add(CilOpCodes.Conv_I4);
                    return;

                case "System.IntPtr":
                case "System.UIntPtr":
                    LoadIntegerImmediate(typedImmediate.Value);
                    instructions.Add(expectedPrimitiveType == "System.IntPtr" ? CilOpCodes.Conv_I : CilOpCodes.Conv_U);
                    return;
            }
        }

        switch (operand)
        {
            case DefaultValue defaultValue:
                PushDefaultOf(defaultValue.Type, method);
                break;
            case Immediate { Value: >= int.MinValue and <= int.MaxValue } immediate:
                instructions.Add(CilOpCodes.Ldc_I4, (int)immediate.Value);
                break;
            case Immediate immediate:
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
                break;
            case FloatLiteral f when expectedType?.FullName == "System.Double":
                instructions.Add(CilOpCodes.Ldc_R8, (double)f.Value);
                break;
            case FloatLiteral f:
                instructions.Add(CilOpCodes.Ldc_R4, f.Value);
                break;
            case DoubleLiteral d when expectedType?.FullName == "System.Single":
                // Round the constant exactly as conv.r4 would, but emit a native R4 literal so
                // the C# decompiler can render it with an f suffix.
                instructions.Add(CilOpCodes.Ldc_R4, (float)d.Value);
                break;
            case DoubleLiteral d:
                instructions.Add(CilOpCodes.Ldc_R8, d.Value);
                break;
            case UnrecoveredCallArgument missingArgument:
                PushDefaultOf(missingArgument.ExpectedType, method);
                break;
            case StringLiteral s:
                instructions.Add(CilOpCodes.Ldstr, s.Value);
                break;
            case LocalVariable local:
                LoadLocal(local, method, locals);
                break;
            case AddressOf { Target: LocalVariable addressed } when !isReceiver
                && expectedType is { IsValueType: true } and not ByRefTypeAnalysisContext
                && addressed.Type is { } addressedType
                && MetadataResolver.IsSameType(addressedType, expectedType):
                // ARM64 passes some IL2CPP value-type arguments through a temporary address
                // even though their managed signature is by value. Load that typed temporary
                // as the argument value; explicit ref/out parameters and value-type receivers
                // keep the address path below.
                LoadLocal(addressed, method, locals);
                break;
            case ArrayLength arrayLength:
                LoadLocal(arrayLength.Array, method, locals);
                instructions.Add(CilOpCodes.Ldlen);
                instructions.Add(CilOpCodes.Conv_I4);
                break;
            case AddressOf { Target: LocalVariable addressed }:
                if (addressed.IsThis || addressed.Type is ByRefTypeAnalysisContext)
                {
                    // Value-type `this` and by-ref locals already hold a managed pointer; taking
                    // the address of their synthetic local would instead pass a pointer to a copy.
                    LoadLocal(addressed, method, locals);
                }
                else if (method.Parameters.FirstOrDefault(p => p.Name == addressed.Name) is { } parameter)
                {
                    // Parameters are represented in the local map for data-flow purposes, but
                    // their storage is an argument slot, not the synthetic local in that map.
                    instructions.Add(CilOpCodes.Ldarga, parameter);
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldloca, locals[addressed]);
                }
                break;
            case AddressOf { Target: ArrayAccess elementAddress }:
                LoadLocal(elementAddress.Array, method, locals);
                LoadOperand(elementAddress.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldelema,
                    ((SzArrayTypeAnalysisContext)elementAddress.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case AddressOf { Target: FieldReference fieldAddress }:
                if (fieldAddress.Parent == null && fieldAddress.Field.IsStatic)
                    instructions.Add(CilOpCodes.Ldsflda, fieldAddress.Field.ToFieldDescriptor());
                else
                {
                    LoadFieldReceiver(fieldAddress, method, locals);
                    instructions.Add(CilOpCodes.Ldflda, fieldAddress.Field.ToFieldDescriptor());
                }
                break;
            case ArrayAccess arrayAccess:
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldelem,
                    ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case FieldReference field:
                if (StringLengthGetter(field.Field) is { } stringLengthGetter)
                {
                    LoadFieldReceiver(field, method, locals);
                    instructions.Add(CilOpCodes.Callvirt, stringLengthGetter.ToMethodDescriptor());
                    break;
                }

                if (FrameworkPropertyRecovery.GetGetterForRead(field, method) is { } listCountGetter)
                {
                    LoadFieldReceiver(field, method, locals);
                    instructions.Add(field.Field.DeclaringType.IsValueType ? CilOpCodes.Call : CilOpCodes.Callvirt, listCountGetter);
                    break;
                }

                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor());
                    break;
                }

                LoadFieldReceiver(field, method, locals);
                instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor());
                break;
            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    LoadLocal(local2, method, locals);

                    // A load through a managed pointer (byref) dereferences it to yield the referent.
                    if (local2.Type is ByRefTypeAnalysisContext { ElementType: { } referent })
                        // A generic parameter may be instantiated as either a reference or a
                        // value type. ldind.ref would lose a value-type instantiation, while
                        // ldobj with the generic signature preserves both possibilities.
                        instructions.Add(referent.IsValueType || referent is GenericParameterTypeAnalysisContext
                            ? new CilInstruction(CilOpCodes.Ldobj, referent.ToTypeSignature().ToTypeDefOrRef())
                            : new CilInstruction(CilOpCodes.Ldind_Ref));
                    break;
                }
                instructions.Add(CilOpCodes.Ldstr, Diagnostic("Unmanaged memory load: " + operand));
                instructions.Add(CilOpCodes.Call, writeLine);
                if (expectedType is { } knownExpectedType)
                    PushDefaultOf(knownExpectedType, method);
                else
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Conv_I);
                }
                break;
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, runtimeMethod.RepresentedMethod.ToMethodDescriptor());
                    break;
                }

                //Not fully implemented, these basically shouldn't actually ever exist in the final IL.
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case RuntimeFieldInfoAnalysisContext runtimeField:
                // fieldof(F), e.g. the handle InitializeArray takes.
                if (expectedType?.FullName == "System.RuntimeFieldHandle")
                {
                    instructions.Add(CilOpCodes.Ldtoken, runtimeField.RepresentedField.ToFieldDescriptor());
                    break;
                }

                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext:
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case TypeAnalysisContext type:
                //typeof(T)
                var corLibScope = module.CorLibTypeFactory.CorLibScope;
                var typeFromHandle = corLibScope
                    .CreateTypeReference("System", "Type")
                    .CreateMemberReference("GetTypeFromHandle", MethodSignature.CreateStatic(
                        corLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false),
                        [corLibScope.CreateTypeReference("System", "RuntimeTypeHandle").ToTypeSignature(true)]));

                instructions.Add(CilOpCodes.Ldtoken, type.ToTypeSignature().ToTypeDefOrRef());
                instructions.Add(CilOpCodes.Call, typeFromHandle);
                break;
            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic("Unknown operand: " + operand));
                instructions.Add(CilOpCodes.Call, writeLine);
                instructions.Add(CilOpCodes.Ldnull);
                break;
        }
    }

    private static bool TryEmitExactTypeComparison(Instruction instruction, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var left = instruction.Operands[1];
        var right = instruction.Operands[2];

        IOperand typeOperand;
        LocalVariable objLocal;

        if (left is TypeAnalysisContext && IsKlassPointerLoad(right, out var rightLocal))
            (typeOperand, objLocal) = (left, rightLocal);
        else if (right is TypeAnalysisContext && IsKlassPointerLoad(left, out var leftLocal))
            (typeOperand, objLocal) = (right, leftLocal);
        else
            return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        var getType = module.CorLibTypeFactory.CorLibScope
            .CreateTypeReference("System", "Object")
            .CreateMemberReference("GetType", MethodSignature.CreateInstance(
                module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false)));

        LoadLocal(objLocal, method, locals);
        instructions.Add(CilOpCodes.Callvirt, getType);
        LoadOperand(typeOperand, method, locals, writeLine); // emits typeof(T)
        instructions.Add(CilOpCodes.Ceq);

        if (instruction.OpCode == OpCode.CheckNotEqual)
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Ceq);
        }

        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
        return true;
    }

    private static bool TryEmitOverloadedReferenceEquals(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var left = instruction.Operands[1];
        var right = instruction.Operands[2];
        IOperand value;
        TypeAnalysisContext? valueType;

        if (IsZeroConstant(left) && (valueType = ReferenceComparisonType(right)) != null)
            value = right;
        else if (IsZeroConstant(right) && (valueType = ReferenceComparisonType(left)) != null)
            value = left;
        else
            return false;

        if (!HasOverloadedReferenceEquality(valueType))
            return false;

        // Keep the native reference comparison semantics while avoiding a cast that only
        // exists to suppress a class's overloaded == / != operators in generated C#.
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var objectType = factory.CorLibScope.CreateTypeReference("System", "Object");
        var referenceEquals = objectType.CreateMemberReference("ReferenceEquals", MethodSignature.CreateStatic(
            factory.Boolean, [factory.Object, factory.Object]));

        LoadOperand(value, method, locals, writeLine, context.AppContext.SystemTypes.SystemObjectType);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldnull);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Call, referenceEquals);

        if (instruction.OpCode == OpCode.CheckNotEqual)
        {
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_0);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ceq);
        }

        StoreToOperand(instruction.Operands[0], method, locals, writeLine);
        return true;
    }

    private static bool HasOverloadedReferenceEquality(TypeAnalysisContext? type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null && visited.Add(current); current = current.BaseType)
        {
            if (current is { FullName: "UnityEngine.Object", DeclaringAssembly.CleanAssemblyName: "UnityEngine.CoreModule" or "UnityEngine" }
                || current.Methods.Any(method => method is
                    { Name: "op_Equality" or "op_Inequality", IsStatic: true, ReturnType.FullName: "System.Boolean", Parameters.Count: 2 }
                    && method.Parameters.Any(parameter => ReferenceEquals(parameter.ParameterType, current))))
                return true;
        }

        return false;
    }

    private static bool IsKlassPointerLoad(IOperand operand, out LocalVariable local)
    {
        if (operand is MemoryOperand { Index: null, Addend: 0, Scale: 0, Base: LocalVariable { Type.IsValueType: false } baseLocal })
        {
            local = baseLocal;
            return true;
        }

        local = null!;
        return false;
    }

    private static void PushDefaultOf(TypeAnalysisContext type, MethodDefinition method)
    {
        var instructions = method.CilMethodBody!.Instructions;

        // A missing by-ref argument still needs an address on the stack. Use a scratch local of the
        // element type so the placeholder has the right managed-pointer type.
        if (type is ByRefTypeAnalysisContext byRef)
        {
            var scratch = new CilLocalVariable(byRef.ElementType.ToTypeSignature());
            method.CilMethodBody.LocalVariables.Add(scratch);
            instructions.Add(CilOpCodes.Ldloca, scratch);
            return;
        }

        var primitiveTypeName = type.IsEnumType ? type.EnumUnderlyingType?.FullName : type.FullName;
        switch (primitiveTypeName)
        {
            case "System.Boolean":
            case "System.Char":
            case "System.SByte":
            case "System.Byte":
            case "System.Int16":
            case "System.UInt16":
            case "System.Int32":
            case "System.UInt32":
                instructions.Add(CilOpCodes.Ldc_I4_0);
                return;
            case "System.Int64":
            case "System.UInt64":
                instructions.Add(CilOpCodes.Ldc_I8, 0L);
                return;
            case "System.Single":
                instructions.Add(CilOpCodes.Ldc_R4, 0f);
                return;
            case "System.Double":
                instructions.Add(CilOpCodes.Ldc_R8, 0d);
                return;
        }

        // IL2CPP runtime handles and unmanaged pointers are represented as pointer-sized values,
        // even though their analysis contexts are not marked as managed value types.
        if (type is PointerTypeAnalysisContext or RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext
            or StaticFieldStorageTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext
            || primitiveTypeName is "System.IntPtr" or "System.UIntPtr")
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(primitiveTypeName == "System.UIntPtr" ? CilOpCodes.Conv_U : CilOpCodes.Conv_I);
            return;
        }

        if (!type.IsValueType && type is not GenericParameterTypeAnalysisContext)
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        // A 32-bit zero is not a valid stand-in for every value type (for example, a struct or
        // decimal). Initialize a typed scratch local and load its actual default value instead.
        // Generic parameters need the same treatment because T may be either a value or reference type.
        var value = new CilLocalVariable(type.ToTypeSignature());
        method.CilMethodBody.LocalVariables.Add(value);
        instructions.Add(CilOpCodes.Ldloca, value);
        instructions.Add(CilOpCodes.Initobj, type.ToTypeSignature().ToTypeDefOrRef());
        instructions.Add(CilOpCodes.Ldloc, value);
    }

    private static TypeAnalysisContext? InferUnknownMemoryLoadType(LocalVariable local, MethodAnalysisContext context)
    {
        var instructions = context.ControlFlowGraph!.Instructions;
        var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray();
        if (definitions.Length == 0 || definitions.Any(instruction =>
                instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable, MemoryOperand] }))
            return null;

        var widths = definitions.Select(instruction => instruction.OperandWidth).Distinct().ToArray();
        if (widths.Length > 1)
            return null;

        int? width = widths.Length == 1 ? widths[0] : null;
        width ??= local.Register.Name.StartsWith("X", StringComparison.Ordinal)
            ? context.AppContext.Binary.PointerSizeBytes * 8
            : null;
        if (width == null)
            return null;

        var unsignedUse = false;
        var signedUse = false;
        foreach (var instruction in instructions)
        {
            if (!InstructionUsesLocal(instruction, local))
                continue;

            if (instruction.OpCode is OpCode.DivideUnsigned or OpCode.ModuloUnsigned or OpCode.ShiftRightUnsigned
                or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned or OpCode.CheckGreaterOrEqualUnsigned
                or OpCode.CheckLessOrEqualUnsigned)
                unsignedUse = true;
            else if (instruction.OpCode is OpCode.Divide or OpCode.Modulo or OpCode.CheckGreater or OpCode.CheckLess
                or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual)
                signedUse = true;
        }

        var types = context.AppContext.SystemTypes;
        if (width == 32)
            return signedUse && !unsignedUse ? types.SystemInt32Type : types.SystemUInt32Type;

        if (width != 64)
            return null;

        var isNativePointer = context.AppContext.Binary.PointerSizeBytes == 8
            && (definitions.Any(instruction => instruction.Operands[1] is MemoryOperand { IsConstant: true })
                || instructions.Any(instruction => instruction is
                    { OpCode: OpCode.IndirectCall, Operands: [LocalVariable target, ..] }
                    && ReferenceEquals(target, local)
                    || instruction.Operands.Any(operand => operand is MemoryOperand { Base: LocalVariable baseLocal }
                        && ReferenceEquals(baseLocal, local))));

        if (isNativePointer)
            return types.SystemIntPtrType;

        return unsignedUse && !signedUse ? types.SystemUInt64Type : types.SystemInt64Type;
    }

    private static bool InstructionUsesLocal(Instruction instruction, LocalVariable local)
    {
        foreach (var operand in instruction.SourcesAndConstants)
            if (OperandUsesLocal(operand, local))
                return true;

        return false;
    }

    private static bool OperandUsesLocal(IOperand operand, LocalVariable local) => operand switch
    {
        LocalVariable candidate => ReferenceEquals(candidate, local),
        MemoryOperand memory => ReferenceEquals(memory.Base, local) || ReferenceEquals(memory.Index, local),
        FieldReference field => ReferenceEquals(field.Local, local),
        ArrayAccess array => ReferenceEquals(array.Array, local) || OperandUsesLocal(array.Index, local),
        ArrayLength length => ReferenceEquals(length.Array, local),
        AddressOf { Target: LocalVariable target } => ReferenceEquals(target, local),
        AddressOf { Target: ArrayAccess array } => OperandUsesLocal(array, local),
        _ => false,
    };

    private static bool IsUnknownNativeAddress(LocalVariable local, MethodAnalysisContext context)
    {
        var instructions = context.ControlFlowGraph!.Instructions;
        var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray();
        if (definitions.Length == 0 || definitions.Any(instruction =>
                instruction.OpCode is not (OpCode.Add or OpCode.Subtract)
                || instruction.Operands is not [LocalVariable, var left, var right]
                || !IsPointerArithmetic(left, right, local, instructions)
                    && !IsPointerArithmetic(right, left, local, instructions)))
            return false;

        return instructions.Any(instruction => instruction is
                { OpCode: OpCode.IndirectCall, Operands: [LocalVariable target, ..] }
                && ReferenceEquals(target, local)
            || instruction.Operands.Any(operand => operand is MemoryOperand { Base: LocalVariable baseLocal }
                && ReferenceEquals(baseLocal, local)));
    }

    private static bool IsPointerArithmetic(IOperand pointerCandidate, IOperand offsetCandidate,
        LocalVariable result, List<Instruction> instructions)
    {
        var pointerType = OperandType(pointerCandidate);
        if (pointerType != null && IsNativePointerType(pointerType)
            && (offsetCandidate is Immediate || IntegerOperandType(OperandType(offsetCandidate)) != null))
            return true;

        // A managed reference plus an integer only represents lowered native address arithmetic
        // when the result is subsequently dereferenced or used as an indirect call target.
        return pointerType is { IsValueType: false }
            && pointerType is not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext)
            && (offsetCandidate is Immediate || IntegerOperandType(OperandType(offsetCandidate)) != null)
            && instructions.Any(instruction => instruction.Operands.Any(operand => operand is MemoryOperand
                { Base: LocalVariable baseLocal } && ReferenceEquals(baseLocal, result)));
    }

    private static bool IsNativePointerType(TypeAnalysisContext type) =>
        type is PointerTypeAnalysisContext or RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext
            or StaticFieldStorageTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext
        || type.FullName is "System.IntPtr" or "System.UIntPtr";

    private static bool IsBoolean(IOperand operand) =>
        OperandType(operand)?.FullName == "System.Boolean";

    private static bool IsBooleanOrBooleanConstant(IOperand operand) =>
        IsBoolean(operand) || operand is Immediate { Value: 0 or 1 };

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };

    private static TypeAnalysisContext? ReferenceComparisonType(IOperand operand)
    {
        var type = operand switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } elementType } } } => elementType,
            _ => null
        };

        return type is { IsValueType: false }
            and not (ByRefTypeAnalysisContext or RuntimeClassTypeAnalysisContext
                or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext
                or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext
                or StaticFieldStorageTypeAnalysisContext)
            ? type
            : null;
    }

    private static TypeAnalysisContext? OperandType(IOperand operand) =>
        operand switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } elementType } } } => elementType,
            _ => null
        };

    private static TypeAnalysisContext? IntegerOperandType(TypeAnalysisContext? type)
    {
        if (type == null)
            return null;

        var primitiveType = type.IsEnumType ? type.EnumUnderlyingType?.FullName : type.FullName;
        return primitiveType is "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char"
            or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32"
            or "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr"
            ? type
            : null;
    }

    private static bool IsInt32StackValue(IOperand operand, TypeAnalysisContext? expectedType)
    {
        if (operand is Immediate { Value: >= int.MinValue and <= int.MaxValue } && expectedType == null)
            return true;
        var type = operand is Immediate ? expectedType : OperandType(operand);
        return (type?.IsEnumType == true ? type.EnumUnderlyingType?.FullName : type?.FullName) is
            "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char"
            or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32";
    }

    private static void LoadLocal(LocalVariable local, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        if (local.IsThis)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return;
        }

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == local.Name);

        if (parameter != null)
            instructions.Add(CilOpCodes.Ldarg, parameter);
        else
            instructions.Add(CilOpCodes.Ldloc, locals[local]);
    }

    private static void LoadFieldReceiver(FieldReference field, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var path = new Stack<FieldReference>();
        for (var parent = field.Parent; parent != null; parent = parent.Parent)
            path.Push(parent);

        if (path.Count > 0 && path.Peek().Field.IsStatic)
        {
            var root = path.Pop();
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldsflda, root.Field.ToFieldDescriptor());
            while (path.Count > 0)
                method.CilMethodBody.Instructions.Add(CilOpCodes.Ldflda, path.Pop().Field.ToFieldDescriptor());
            return;
        }

        if (field.Parent == null && field.Field.IsStatic)
        {
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor());
            return;
        }

        if (!field.Local.IsThis
            && field.Local.Type is { IsValueType: true } and not ByRefTypeAnalysisContext)
        {
            var parameter = method.Parameters.FirstOrDefault(p => p.Name == field.Local.Name);
            if (parameter != null)
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldarga, parameter);
            else
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloca, locals[field.Local]);
        }
        else
        {
            LoadLocal(field.Local, method, locals);
        }

        var instructions = method.CilMethodBody!.Instructions;
        while (path.Count > 0)
            instructions.Add(CilOpCodes.Ldflda, path.Pop().Field.ToFieldDescriptor());
    }

    private static MethodAnalysisContext? StringLengthGetter(FieldAnalysisContext field)
    {
        if (field.IsStatic || field.DeclaringType.FullName != "System.String" || field.Name != "_stringLength")
            return null;

        var getter = field.DeclaringType.Properties.FirstOrDefault(property => property.Name == "Length")?.Getter;
        return getter is { IsStatic: false, Parameters.Count: 0 }
            && getter.ReturnType.FullName == "System.Int32"
                ? getter
                : null;
    }

    private static void StoreToOperand(IOperand operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, IMethodDescriptor writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;

        switch (operand)
        {
            case LocalVariable local:
                instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                var fieldDescriptor = field.Field.ToFieldDescriptor();

                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Stsfld, fieldDescriptor);
                    break;
                }

                // stfld wants the object underneath the value, but the value is already on the stack, so
                // park it in a temporary while we load the object.
                var scratch = new CilLocalVariable(fieldDescriptor.Signature!.FieldType);
                method.CilMethodBody!.LocalVariables.Add(scratch);

                instructions.Add(CilOpCodes.Stloc, scratch);
                LoadFieldReceiver(field, method, locals);
                instructions.Add(CilOpCodes.Ldloc, scratch);
                instructions.Add(CilOpCodes.Stfld, fieldDescriptor);
                break;

            case ArrayAccess arrayAccess:
                // stelem needs array and index before the value, so the same trick as stfld
                var elementType = ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType;
                var elementScratch = new CilLocalVariable(elementType.ToTypeSignature());
                method.CilMethodBody!.LocalVariables.Add(elementScratch);

                instructions.Add(CilOpCodes.Stloc, elementScratch);
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals, writeLine);
                instructions.Add(CilOpCodes.Ldloc, elementScratch);
                instructions.Add(CilOpCodes.Stelem, elementType.ToTypeSignature().ToTypeDefOrRef());
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    if (local2.Type is ByRefTypeAnalysisContext { ElementType: { } referent })
                    {
                        // The value is already on the stack. Store through the managed pointer,
                        // not into the synthetic local used to track the by-ref argument.
                        var value = new CilLocalVariable(referent.ToTypeSignature());
                        method.CilMethodBody!.LocalVariables.Add(value);
                        instructions.Add(CilOpCodes.Stloc, value);
                        LoadLocal(local2, method, locals);
                        instructions.Add(CilOpCodes.Ldloc, value);
                        instructions.Add(CilOpCodes.Stobj, referent.ToTypeSignature().ToTypeDefOrRef());
                    }
                    else
                    {
                        instructions.Add(CilOpCodes.Stloc, locals[local2]);
                    }
                    break;
                }
                instructions.Add(CilOpCodes.Pop);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, Diagnostic($"Store into unknown operand: {operand}"));
                instructions.Add(CilOpCodes.Call, writeLine);
                instructions.Add(CilOpCodes.Pop);
                break;
        }
    }
}
