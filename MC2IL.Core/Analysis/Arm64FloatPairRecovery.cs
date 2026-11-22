using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64FloatPairRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var instructions = graph.Instructions;
        if (!instructions.Any(instruction => IsPairProducer(instruction.Arm64PackedFloat)
                || IsExtraction(instruction.Arm64PackedFloat)))
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        var stackSlots = new Dictionary<long, (int Width, bool Addressed)>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is LocalVariable result)
                definitions.Add(result, instruction);
            foreach (var local in DeadCodeEliminator.UsedLocals(instruction))
            {
                if (!uses.TryGetValue(local, out var readers))
                    uses.Add(local, readers = []);
                readers.Add(instruction);
            }
            foreach (var operand in instruction.Operands)
            {
                var slot = operand is AddressOf { Target: LocalVariable addressed } ? addressed : operand as LocalVariable;
                if (slot == null || !FloatAggregateRecovery.TryGetStackOffset(slot, out var offset))
                    continue;
                stackSlots.TryGetValue(offset, out var previous);
                stackSlots[offset] = (Math.Max(previous.Width, instruction.MemoryAccessSize ?? 16),
                    previous.Addressed || operand is AddressOf);
            }
        }

        // Phi placement can leave unused merges, including entry values merged
        // into the synthetic exit. They place no demands on the packed value.
        var livePhis = new HashSet<Instruction>();
        var pendingPhis = new Queue<Instruction>();
        var helpersWithoutVectorArguments = new Dictionary<ulong, bool>();
        foreach (var usage in uses)
            if (definitions.TryGetValue(usage.Key, out var producer) && producer.OpCode == OpCode.Phi
                && usage.Value.Any(use => use.OpCode != OpCode.Phi && !IsUnusedHelperOperand(use, usage.Key)))
                MarkPhi(usage.Key);
        while (pendingPhis.Count != 0)
            foreach (var input in pendingPhis.Dequeue().Operands.Skip(1).OfType<LocalVariable>())
                MarkPhi(input);

        void MarkPhi(LocalVariable value)
        {
            if (definitions.TryGetValue(value, out var producer) && producer.OpCode == OpCode.Phi && livePhis.Add(producer))
                pendingPhis.Enqueue(producer);
        }

        // Follow only connected SSA values whose packed representation has no
        // observable use as a whole word, double, wider vector or address.
        var maskComparisons = new Dictionary<Instruction, (Instruction Comparison, Instruction Extraction, Instruction Duplicate)>();
        foreach (var instruction in instructions)
        {
            if (instruction is not { Arm64PackedFloat: Arm64PackedFloatOperation.SelectByMask,
                    Operands: [_, LocalVariable mask, _, _] }
                || !definitions.TryGetValue(mask, out var duplicate)
                || duplicate is not { Arm64PackedFloat: Arm64PackedFloatOperation.Duplicate, Operands: [_, LocalVariable lane] }
                || !definitions.TryGetValue(lane, out var extraction)
                || extraction is not { Arm64PackedFloat: Arm64PackedFloatOperation.ExtractLow,
                    Operands: [_, _, LocalVariable comparisonResult] }
                || !definitions.TryGetValue(comparisonResult, out var comparison)
                || comparison.Arm64PackedFloat != Arm64PackedFloatOperation.GreaterMask)
                continue;
            maskComparisons.Add(instruction, (comparison, extraction, duplicate));
        }

        var pairs = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            var mode = instruction.Arm64PackedFloat;
            var candidate = IsPairProducer(mode) ? instruction.Destination as LocalVariable
                : mode == Arm64PackedFloatOperation.ExtractHigh ? instruction.Operands[2] as LocalVariable : null;
            if (candidate == null || pairs.Contains(candidate))
                continue;

            var group = new HashSet<LocalVariable>();
            var remaining = new Queue<LocalVariable>();
            var valid = Require(candidate);
            while (remaining.Count != 0 && valid)
            {
                var local = remaining.Dequeue();
                valid = definitions.TryGetValue(local, out var definition) && CanSplitDefinition(definition)
                    && (!uses.TryGetValue(local, out var readers) || readers.All(use => CanSplitUse(use, local)));
            }
            if (valid)
                pairs.UnionWith(group);

            bool Require(IOperand value)
            {
                if (value is not LocalVariable local)
                    return false;
                if (FloatAggregateRecovery.TryGetStackOffset(local, out var offset)
                    && (offset >= 0 || stackSlots[offset].Addressed || stackSlots.Any(other => other.Key != offset
                        && other.Key < offset + stackSlots[offset].Width && other.Key + other.Value.Width > offset)))
                    return false;
                if (group.Add(local))
                    remaining.Enqueue(local);
                return true;
            }

            bool CanSplitDefinition(Instruction definition) => definition.Arm64PackedFloat switch
            {
                Arm64PackedFloatOperation.Binary => Require(definition.Operands[1]) && Require(definition.Operands[2]),
                Arm64PackedFloatOperation.Broadcast => Require(definition.Operands[1]),
                Arm64PackedFloatOperation.Negate => Require(definition.Operands[1]),
                Arm64PackedFloatOperation.SelectByMask => maskComparisons.TryGetValue(definition, out var mask)
                    && RequireFloatLane(mask.Comparison.Operands[1]) && RequireFloatLane(mask.Comparison.Operands[2])
                    && Require(definition.Operands[2]) && Require(definition.Operands[3]),
                Arm64PackedFloatOperation.InsertLow => Require(definition.Operands[2]),
                Arm64PackedFloatOperation.InsertHigh => definition.Operands[2] is LocalVariable previous
                    && (IsScalarSingle(previous) || Require(previous)),
                Arm64PackedFloatOperation.ReplicateBits or Arm64PackedFloatOperation.Duplicate => true,
                _ => definition.OpCode switch
                {
                    OpCode.Phi => definition.Operands.Skip(1).All(Require),
                    OpCode.Move when IsPairCopy(definition) => definition.Operands[1] switch
                    {
                        MemoryOperand { Index: null, Scale: 0 } => true,
                        LocalVariable source => Require(source),
                        _ => false
                    },
                    _ => false
                }
            };

            bool RequireFloatLane(IOperand operand) =>
                operand is LocalVariable source && (IsScalarSingle(source) || Require(source));

            bool CanSplitUse(Instruction use, LocalVariable value)
            {
                if (IsExtraction(use.Arm64PackedFloat))
                    return ReferenceEquals(use.Operands[2], value);
                if (use.OpCode == OpCode.Phi)
                    return !livePhis.Contains(use) || Require(use.Destination!);
                if (use.Arm64PackedFloat == Arm64PackedFloatOperation.SelectByMask
                    && ReferenceEquals(use.Operands[1], value))
                    return false;
                if (use.Arm64PackedFloat == Arm64PackedFloatOperation.GreaterMask)
                    return true; // The recovered boolean comparison only uses lane zero.
                if (IsPairProducer(use.Arm64PackedFloat))
                    return Require(use.Destination!);
                if (use is { OpCode: OpCode.PackFloatAggregate, Operands: [_, TypeAnalysisContext type, ..] }
                    && Utils.Arm64CallingConventionResolver.GetFloatAggregateFields(type) is { } fields
                    && fields.All(field => field.FieldType.FullName == "System.Single"))
                    return use.Operands.Skip(2).Any(operand => ReferenceEquals(operand, value));
                if (IsUnusedHelperOperand(use, value))
                    return true;
                if (use.Operands.Count >= 4 && use.Operands[3] is FloatingPointPrecision { IsDouble: false })
                    return ReferenceEquals(use.Operands[1], value) || ReferenceEquals(use.Operands[2], value);
                // A direct 4-byte store reads only the low lane. A packed memory
                // store must account for both lanes and exactly eight bytes.
                if (use is { OpCode: OpCode.Move, Operands: [MemoryOperand { Index: null, Scale: 0 }, var source] }
                    && ReferenceEquals(source, value))
                    return use.MemoryAccessSize is 4 or 8;
                return IsPairCopy(use) && ReferenceEquals(use.Operands[1], value) && Require(use.Destination!);
            }
        }

        bool IsUnusedHelperOperand(Instruction use, LocalVariable value) =>
            use.IsCall && use.Operands[0] is Immediate address
            && value.Register.Name.StartsWith("V", StringComparison.Ordinal)
            && HasNoVectorArguments(address.UnsignedValue);

        bool HasNoVectorArguments(ulong address)
        {
            if (address == 0 || method.AppContext.MethodsByAddress.ContainsKey(address))
                return false;
            if (helpersWithoutVectorArguments.TryGetValue(address, out var cached))
                return cached;

            var app = method.AppContext;
            var keys = app.GetOrCreateKeyFunctionAddresses();
            // Raw calls to helpers carry placeholder V0-V7 arguments until the
            // runtime call is resolved. These helpers never consume those slots.
            var confirmed = address == keys.il2cpp_codegen_initialize_method
                || address == keys.il2cpp_codegen_initialize_runtime_metadata
                || address == keys.il2cpp_codegen_initialize_runtime_metadata_inline
                || address == keys.il2cpp_codegen_runtime_class_init
                || ThrowHelperRecovery.GetThrownException(app, address)?.FullName is
                    ("System.NullReferenceException" or "System.IndexOutOfRangeException")
                    && ThrowHelperRecovery.IsExceptionRaiser(app, address);
            helpersWithoutVectorArguments.Add(address, confirmed);
            return confirmed;
        }

        var high = new Dictionary<LocalVariable, LocalVariable>();
        var single = method.AppContext.SystemTypes.SystemSingleType;
        foreach (var low in pairs)
        {
            low.Type = single;
            var second = new LocalVariable(low.Name + "_high", new Register(null, low.Register.Name + ".High", low.Register.Version), single);
            high.Add(low, second);
            method.Locals.Add(second);
        }

        var precision = new FloatingPointPrecision(IsDouble: false);
        var conditions = new Dictionary<Instruction, LocalVariable>();
        var loweredSelections = maskComparisons.Where(selection =>
            selection.Key.Destination is LocalVariable value && pairs.Contains(value)).ToArray();
        foreach (var selection in loweredSelections)
        {
            var comparison = selection.Value.Comparison;
            if (conditions.ContainsKey(comparison))
                continue;
            var value = (LocalVariable)selection.Key.Destination!;
            var condition = new LocalVariable(value.Name + "_mask",
                new Register(null, "TEMP_SIMD_MASK", comparison.Index), method.AppContext.SystemTypes.SystemBooleanType);
            conditions.Add(comparison, condition);
            method.Locals.Add(condition);
        }

        var deadMasks = new HashSet<Instruction>();
        var convertedSelections = new HashSet<Instruction>(loweredSelections.Select(selection => selection.Key));
        // Process the chain from its consumers back to the original comparison.
        foreach (var mask in loweredSelections.Select(selection => selection.Value.Duplicate))
            if (OnlyUsedBy(mask, convertedSelections))
                deadMasks.Add(mask);
        foreach (var mask in loweredSelections.Select(selection => selection.Value.Extraction))
            if (OnlyUsedBy(mask, deadMasks))
                deadMasks.Add(mask);
        foreach (var mask in conditions.Keys)
            if (OnlyUsedBy(mask, deadMasks))
                deadMasks.Add(mask);

        bool OnlyUsedBy(Instruction instruction, HashSet<Instruction> lowered)
        {
            var result = (LocalVariable)instruction.Destination!;
            return !uses.TryGetValue(result, out var readers) || readers.All(use =>
                lowered.Contains(use) || use.OpCode == OpCode.Phi && !livePhis.Contains(use)
                || IsUnusedHelperOperand(use, result));
        }

        foreach (var block in graph.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instruction = block.Instructions[i];
            var mode = instruction.Arm64PackedFloat;
            if (mode == Arm64PackedFloatOperation.GreaterMask)
            {
                if (conditions.TryGetValue(instruction, out var condition))
                    Insert(new Instruction(instruction.Index, OpCode.CheckGreater, condition,
                        instruction.Operands[1], instruction.Operands[2], precision));
            }
            if (deadMasks.Contains(instruction))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
                continue;
            }
            if (mode == Arm64PackedFloatOperation.GreaterMask)
            {
                // Other consumers still need the raw bits of the mask.
                instruction.OpCode = OpCode.NotImplemented;
                instruction.SetOperands(new StringLiteral("ARM64 FCMGT mask is used as raw bits."));
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
                continue;
            }
            if (instruction.OpCode == OpCode.Phi && !livePhis.Contains(instruction))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
                continue;
            }
            if (IsExtraction(mode))
            {
                var result = (LocalVariable)instruction.Operands[0];
                var value = (LocalVariable)instruction.Operands[2];
                if (high.TryGetValue(value, out var lane))
                    instruction.SetOperand(1, mode == Arm64PackedFloatOperation.ExtractHigh ? lane : value);
                else if (mode == Arm64PackedFloatOperation.ExtractLow && IsScalarSingle(value))
                    instruction.SetOperand(1, value);
                instruction.RemoveOperandAt(2);
                result.Type = single;
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
            }
            else if (instruction.Destination is LocalVariable low && high.TryGetValue(low, out var second))
            {
                var index = instruction.Index;
                if (mode is Arm64PackedFloatOperation.Binary or Arm64PackedFloatOperation.Broadcast)
                {
                    var left = (LocalVariable)instruction.Operands[1];
                    var right = (LocalVariable)instruction.Operands[2];
                    instruction.SetOperands(low, left, right, precision);
                    Insert(new Instruction(index, instruction.OpCode, second, high[left],
                        mode == Arm64PackedFloatOperation.Binary ? high[right] : right, precision));
                }
                else if (mode == Arm64PackedFloatOperation.Negate)
                {
                    Insert(new Instruction(index, OpCode.Negate, second, high[(LocalVariable)instruction.Operands[1]]));
                }
                else if (mode == Arm64PackedFloatOperation.SelectByMask)
                {
                    var whenTrue = (LocalVariable)instruction.Operands[2];
                    var whenFalse = (LocalVariable)instruction.Operands[3];
                    var condition = conditions[maskComparisons[instruction].Comparison];
                    instruction.SetOperands(low, condition, whenTrue, whenFalse);
                    Insert(new Instruction(index, OpCode.Select, second, condition, high[whenTrue], high[whenFalse]));
                }
                else if (mode is Arm64PackedFloatOperation.Duplicate or Arm64PackedFloatOperation.ReplicateBits)
                {
                    var source = instruction.Operands[1];
                    if (source is Immediate bits)
                        source = new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits.Value)), 0));
                    instruction.SetOperand(1, source);
                    Insert(new Instruction(index, OpCode.Move, second, source));
                }
                else if (mode is Arm64PackedFloatOperation.InsertLow or Arm64PackedFloatOperation.InsertHigh)
                {
                    var inserted = instruction.Operands[1];
                    var previous = (LocalVariable)instruction.Operands[2];
                    var insertHigh = mode == Arm64PackedFloatOperation.InsertHigh;
                    instruction.SetOperands(low, insertHigh ? previous : inserted);
                    Insert(new Instruction(index, OpCode.Move, second, insertHigh ? inserted : high[previous]));
                }
                else if (instruction.OpCode == OpCode.Phi)
                {
                    Insert(new Instruction(index, OpCode.Phi,
                        [second, .. instruction.Operands.Skip(1).Cast<LocalVariable>().Select(local => high[local])]));
                }
                else if (instruction.Operands[1] is MemoryOperand memory)
                {
                    instruction.MemoryAccessSize = 4;
                    memory.Addend += 4;
                    Insert(new Instruction(index, OpCode.Move, second, memory) { MemoryAccessSize = 4 });
                }
                else
                {
                    instruction.MemoryAccessSize = null;
                    Insert(new Instruction(index, OpCode.Move, second, high[(LocalVariable)instruction.Operands[1]]));
                }
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
            }
            else if (mode is Arm64PackedFloatOperation.InsertLow or Arm64PackedFloatOperation.InsertHigh)
            {
                // Scalar consumers can still use the low lane when another
                // consumer needs a packed representation we cannot split.
                if (mode == Arm64PackedFloatOperation.InsertHigh)
                    instruction.SetOperand(1, instruction.Operands[2]);
                instruction.RemoveOperandAt(2);
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
            }
            else if (mode == Arm64PackedFloatOperation.SelectByMask)
            {
                instruction.OpCode = OpCode.NotImplemented;
                instruction.SetOperands(new StringLiteral("ARM64 BSL requires a supported SIMD mask and operands."));
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
            }
            else if (mode == Arm64PackedFloatOperation.Negate)
            {
                instruction.OpCode = OpCode.NotImplemented;
                instruction.SetOperands(new StringLiteral("ARM64 FNEG uses an unsupported packed float value."));
                instruction.Arm64PackedFloat = Arm64PackedFloatOperation.None;
            }
            else if (instruction is { OpCode: OpCode.Move, MemoryAccessSize: 8,
                         Operands: [MemoryOperand store, LocalVariable source] } && high.TryGetValue(source, out var secondValue))
            {
                instruction.MemoryAccessSize = 4;
                store.Addend += 4;
                Insert(new Instruction(instruction.Index, OpCode.Move, store, secondValue) { MemoryAccessSize = 4 });
            }

            void Insert(Instruction added) => block.Instructions.Insert(++i, added);
        }

        bool IsScalarSingle(LocalVariable value) => IsScalarSingleThroughCopies(value, []);

        bool IsScalarSingleThroughCopies(LocalVariable value, HashSet<LocalVariable> visited) =>
            value.Type?.FullName == "System.Single"
            || visited.Count < 16 && visited.Add(value) && definitions.TryGetValue(value, out var producer)
                && (producer is { OpCode: OpCode.Move, MemoryAccessSize: 4 }
                    || producer.Operands.Count >= 4 && producer.Operands[3] is FloatingPointPrecision { IsDouble: false }
                    || producer is { OpCode: OpCode.Move, Operands: [_, FloatLiteral] }
                    || producer is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] }
                        && (source.Register.Name.StartsWith("V", StringComparison.Ordinal)
                            || FloatAggregateRecovery.TryGetStackOffset(source, out _))
                        && IsScalarSingleThroughCopies(source, visited)
                    || producer.OpCode == OpCode.Phi && producer.Operands.Skip(1).All(input =>
                        input is LocalVariable incoming && IsScalarSingleThroughCopies(incoming, [.. visited]))
                    || producer is { OpCode: OpCode.Convert, Operands: [_, _,
                        NumericConversion { Kind: NumericConversionKind.I32ReinterpretFloat32
                            or NumericConversionKind.SignedInt32ToFloat32 or NumericConversionKind.UnsignedInt32ToFloat32 }] }
                    || producer is { OpCode: OpCode.Call, Operands: [Immediate target, ..] }
                        && method.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var methods)
                        && methods.Count != 0 && methods.All(callee => callee.ReturnType.FullName == "System.Single"));
    }

    private static bool IsPairCopy(Instruction instruction) =>
        instruction.OpCode == OpCode.Move
        && (instruction.Arm64PackedFloat == Arm64PackedFloatOperation.Copy || instruction.MemoryAccessSize is 8 or 16);

    private static bool IsExtraction(Arm64PackedFloatOperation mode) =>
        mode is Arm64PackedFloatOperation.ExtractLow or Arm64PackedFloatOperation.ExtractHigh;

    private static bool IsPairProducer(Arm64PackedFloatOperation mode) =>
        mode is Arm64PackedFloatOperation.Binary or Arm64PackedFloatOperation.Broadcast
            or Arm64PackedFloatOperation.Duplicate or Arm64PackedFloatOperation.ReplicateBits
            or Arm64PackedFloatOperation.InsertLow or Arm64PackedFloatOperation.InsertHigh
            or Arm64PackedFloatOperation.Negate or Arm64PackedFloatOperation.SelectByMask;
}
