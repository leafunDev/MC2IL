using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class FloatAggregateRecovery
{
    private sealed record AggregateStackSpill(Instruction Call, LocalVariable Result, TypeAnalysisContext Type,
        FieldAnalysisContext[] Fields, long BaseOffset,
        Dictionary<FieldAnalysisContext, (LocalVariable Slot, Instruction Store)> Lanes);

    // The CIL emitter builds this value in scratch storage from typed constants only.
    // There is no external read, allocation or call between the receiver and the setter.
    internal static bool IsLiteralPack(Instruction instruction)
    {
        if (instruction is not { OpCode: OpCode.PackFloatAggregate,
                Operands: [LocalVariable { Type: var resultType }, TypeAnalysisContext type, ..] }
            || resultType != type || Arm64CallingConventionResolver.GetFloatAggregateFields(type) is not { } fields
            || fields.Length + 2 != instruction.Operands.Count)
            return false;

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if ((field.Attributes & (FieldAttributes.FieldAccessMask | FieldAttributes.InitOnly)) != FieldAttributes.Public
                || !(field.FieldType.FullName == "System.Single"
                    ? instruction.Operands[i + 2] is FloatLiteral
                    : instruction.Operands[i + 2] is DoubleLiteral))
                return false;
        }

        return true;
    }

    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not Arm64CallingConventionResolver)
            return;

        var graph = method.ControlFlowGraph!;
        if (!method.ParameterOperands.Any(operand => operand is Register register
                && IsAggregateParameter(register))
            && !graph.Instructions.Any(instruction => instruction.OpCode == OpCode.PackFloatAggregate
                || instruction is { OpCode: OpCode.Call, Destination: LocalVariable { Register.Name: "TEMP_CALL_RESULT" } }))
            return;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        var reads = new Dictionary<LocalVariable, int>();
        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;
            foreach (var used in DeadCodeEliminator.UsedLocals(instruction))
                reads[used] = reads.TryGetValue(used, out var count) ? count + 1 : 1;
        }

        foreach (var block in graph.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instruction = block.Instructions[i];
            if (instruction is { OpCode: OpCode.PackFloatAggregate,
                    Operands: [LocalVariable result, TypeAnalysisContext type, ..] }
                && Arm64CallingConventionResolver.GetFloatAggregateFields(type) is { } fields)
            {
                // Putting the lanes of a returned struct back together requires no field access in CIL.
                var original = GetProjection(instruction.Operands[2], fields[0], definitions);
                if (original != null && original.Type == type && fields.Select((field, lane) =>
                        GetProjection(instruction.Operands[lane + 2], field, definitions) == original).All(match => match))
                {
                    instruction.OpCode = OpCode.Move;
                    instruction.SetOperands(result, original);
                }
                else
                {
                    RecoverStaticLoad(block, i, method.DeclaringType, type, fields, reads);
                }
            }
            else if (instruction is { OpCode: OpCode.Move, Operands: [var destination, var firstValue] }
                     && TryGetStaticAggregateLane(destination, out var target, out var firstLane, out var storedFields)
                     && firstLane == storedFields[0])
            {
                var source = GetProjection(firstValue, storedFields[0], definitions);
                if (source == null || source.Type != target.Field.FieldType)
                    continue;

                var stores = new List<Instruction>();
                for (var j = i + 1; j < block.Instructions.Count && stores.Count < storedFields.Length - 1; j++)
                {
                    var store = block.Instructions[j];
                    if (store.OpCode == OpCode.Nop)
                        continue;
                    var field = storedFields[stores.Count + 1];
                    if (store is not { OpCode: OpCode.Move, Operands: [var storeDestination, var value] }
                        || !IsStaticAggregateLane(storeDestination, target, field)
                        || GetProjection(value, field, definitions) != source)
                        break;
                    stores.Add(store);
                }

                if (stores.Count != storedFields.Length - 1)
                    continue;

                instruction.SetOperand(0, target);
                instruction.SetOperand(1, source);
                instruction.MemoryAccessSize = null;
                foreach (var store in stores)
                    Remove(store);
            }
            else if (instruction is { OpCode: OpCode.Move,
                         Operands: [FieldReference { Parent: null, Field.IsStatic: false, Local.IsThis: true } field, _] })
            {
                RecoverInstanceStore(block, i, field, definitions);
            }
        }
    }

    internal static void RecoverAggregateStackReceivers(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not Arm64CallingConventionResolver)
            return;

        var graph = method.ControlFlowGraph!;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        RecoverAggregateStackReceivers(graph, definitions, method.AppContext);
    }

    private static void RecoverAggregateStackReceivers(ISILControlFlowGraph graph,
        Dictionary<LocalVariable, Instruction> definitions, ApplicationAnalysisContext appContext)
    {
        var order = new Dictionary<Instruction, int>();
        var instructionBlocks = new Dictionary<Instruction, Block>();
        var instructionIndex = 0;
        foreach (var block in graph.Blocks)
        foreach (var instruction in block.Instructions)
        {
            order[instruction] = instructionIndex++;
            instructionBlocks[instruction] = block;
        }

        var spills = new List<AggregateStackSpill>();
        foreach (var call in graph.Instructions.ToArray())
        {
            if (call is not { OpCode: OpCode.Call,
                    Operands: [var targetOperand, LocalVariable result, ..] }
                || !TryResolveDirectCallTarget(targetOperand, appContext, out var target)
                || Arm64CallingConventionResolver.GetFloatAggregateFields(target.ReturnType) is not { } fields)
                continue;

            var callSpills = new Dictionary<long, Dictionary<FieldAnalysisContext, (LocalVariable Slot, Instruction Store)>>();
            foreach (var store in graph.Instructions)
            {
                if (store is not { OpCode: OpCode.Move, MemoryAccessSize: var size,
                        Operands: [var destination, var source] }
                    || size is not (4 or 8)
                    || !TryGetAggregateProjection(source, definitions, out var owner, out var fieldOffset)
                    || owner != result
                    || fields.FirstOrDefault(field => field.Offset == fieldOffset) is not { } field
                    || !TryGetStackSlot(destination, out var slot, out var offset))
                    continue;

                var baseOffset = offset - field.Offset;
                if (!callSpills.TryGetValue(baseOffset, out var lanes))
                    callSpills.Add(baseOffset, lanes = []);
                lanes[field] = (slot, store);
            }

            foreach (var spill in callSpills)
            {
                var baseOffset = spill.Key;
                var lanes = spill.Value;
                if (fields.Any(field => !lanes.ContainsKey(field)))
                    continue;

                var firstSlot = lanes[fields[0]].Slot;
                if (!TryGetStackOffset(firstSlot, out var actualBase) || actualBase != baseOffset)
                    continue;

                spills.Add(new AggregateStackSpill(call, result, target.ReturnType, fields, baseOffset, lanes));
            }
        }

        if (spills.Count == 0)
            return;

        var dominance = new DominatorInfo(graph);
        var consumersBySpill = spills.ToDictionary(spill => spill, _ => new List<(Instruction Call, HashSet<Instruction> Setup)>());
        foreach (var consumer in graph.Instructions)
        {
            if (consumer is not { OpCode: OpCode.Call, Operands: [var consumerTarget, ..] }
                || !TryResolveDirectCallTarget(consumerTarget, appContext, out var target)
                || target.IsStatic || consumer.Operands.Count <= 2
                || !TryGetStackReceiver(consumer.Operands[2], definitions, out var receiver, out var setup)
                || !TryGetStackOffset(receiver, out var receiverOffset))
                continue;

            var matching = spills
                .Where(spill => spill.BaseOffset == receiverOffset
                    && MetadataResolver.IsSameType(target.DeclaringType, spill.Type)
                    && ReachesConsumer(spill, consumer, order, instructionBlocks, dominance))
                .OrderByDescending(spill => order[spill.Call])
                .FirstOrDefault();
            if (matching != null)
                consumersBySpill[matching].Add((consumer, setup));
        }

        foreach (var spillGroup in spills.GroupBy(spill => spill.BaseOffset))
        {
            var groupSpills = spillGroup.ToArray();
            var allStores = new HashSet<Instruction>(
                groupSpills.SelectMany(spill => spill.Lanes.Values.Select(value => value.Store)));
            var allConsumers = groupSpills.SelectMany(spill => consumersBySpill[spill]).ToArray();
            var consumerAndSetup = new HashSet<Instruction>(allConsumers.Select(item => item.Call));
            consumerAndSetup.UnionWith(allConsumers.SelectMany(item => item.Setup));
            var stackOffsets = new HashSet<long>(groupSpills
                .SelectMany(spill => spill.Lanes.Values.Select(value => value.Slot))
                .Select(slot => TryGetStackOffset(slot, out var offset) ? offset : long.MinValue)
            );
            var externalUse = graph.Instructions.Any(instruction => instruction.OpCode != OpCode.Phi
                && !allStores.Contains(instruction) && !consumerAndSetup.Contains(instruction)
                && DeadCodeEliminator.UsedLocals(instruction)
                    .Any(local => TryGetStackOffset(local, out var offset) && stackOffsets.Contains(offset)));
            if (externalUse)
                continue;

            foreach (var spill in groupSpills)
            {
                var consumers = consumersBySpill[spill];
                if (consumers.Count == 0)
                    continue;

                foreach (var (consumer, _) in consumers)
                    consumer.SetOperand(2, new AddressOf(spill.Result));
                foreach (var store in spill.Lanes.Values.Select(value => value.Store))
                    Remove(store);
            }
        }

        static bool ReachesConsumer(AggregateStackSpill spill, Instruction consumer,
            Dictionary<Instruction, int> order, Dictionary<Instruction, Block> instructionBlocks,
            DominatorInfo dominance)
        {
            var producerBlock = instructionBlocks[spill.Call];
            var consumerBlock = instructionBlocks[consumer];
            if (producerBlock == consumerBlock)
            {
                if (order[spill.Call] >= order[consumer])
                    return false;
            }
            else if (!dominance.Dominates(producerBlock, consumerBlock))
            {
                return false;
            }

            foreach (var (_, store) in spill.Lanes.Values)
            {
                var storeBlock = instructionBlocks[store];
                if (storeBlock == producerBlock && order[spill.Call] >= order[store]
                    || storeBlock != producerBlock && !dominance.Dominates(producerBlock, storeBlock))
                    return false;
                if (storeBlock == consumerBlock && order[store] >= order[consumer]
                    || storeBlock != consumerBlock && !dominance.Dominates(storeBlock, consumerBlock))
                    return false;
            }

            return true;
        }
    }

    private static bool TryResolveDirectCallTarget(IOperand operand, ApplicationAnalysisContext appContext,
        out MethodAnalysisContext target)
    {
        if (operand is MethodAnalysisContext resolved)
        {
            target = resolved;
            return true;
        }

        if (operand is Immediate { UnsignedValue: var address }
            && appContext.MethodsByAddress.TryGetValue(address, out var candidates)
            && candidates.Distinct().Take(2).ToArray() is [var unique])
        {
            target = unique;
            return true;
        }

        target = null!;
        return false;
    }

    private static bool TryGetAggregateProjection(IOperand operand,
        Dictionary<LocalVariable, Instruction> definitions, out LocalVariable owner, out int fieldOffset)
    {
        for (var depth = 0; depth < 16 && operand is LocalVariable local; depth++)
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, var source] })
                break;
            operand = source;
        }

        if (operand is FieldReference { Parent: null } projection)
        {
            owner = projection.Local;
            fieldOffset = projection.Field.Offset;
            return true;
        }

        if (operand is MemoryOperand { Base: LocalVariable memoryOwner, Index: null, Scale: 0 } memory)
        {
            owner = memoryOwner;
            fieldOffset = (int)memory.Addend;
            return true;
        }

        owner = null!;
        fieldOffset = 0;
        return false;
    }

    private static bool TryGetStackReceiver(IOperand operand,
        Dictionary<LocalVariable, Instruction> definitions, out LocalVariable receiver,
        out HashSet<Instruction> setup)
    {
        setup = [];
        for (var depth = 0; depth < 16; depth++)
        {
            if (operand is AddressOf { Target: LocalVariable addressed })
            {
                receiver = addressed;
                return true;
            }

            if (operand is LocalVariable stackLocal && TryGetStackOffset(stackLocal, out _))
            {
                receiver = stackLocal;
                return true;
            }

            if (operand is not LocalVariable local
                || !definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, var source] })
                break;

            setup.Add(definition);
            operand = source;
        }

        receiver = null!;
        return false;
    }

    private static bool TryGetStackOffset(LocalVariable local, out long offset)
    {
        var name = local.Register.Name;
        var negative = name.StartsWith("stack_-", StringComparison.Ordinal);
        var digits = name.StartsWith("stack_", StringComparison.Ordinal)
            ? name.Substring(negative ? 7 : 6)
            : null;
        if (digits != null && long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            offset = negative ? -value : value;
            return true;
        }

        offset = 0;
        return false;
    }

    private static bool TryGetStackSlot(IOperand operand, out LocalVariable slot, out long offset)
    {
        var fieldOffset = 0;
        switch (operand)
        {
            case LocalVariable local:
                slot = local;
                break;
            case FieldReference field:
                fieldOffset = field.Offset;
                while (field.Parent != null)
                    field = field.Parent;
                slot = field.Local;
                break;
            default:
                slot = null!;
                offset = 0;
                return false;
        }

        if (!TryGetStackOffset(slot, out var stackOffset))
        {
            offset = 0;
            return false;
        }

        offset = stackOffset + fieldOffset;
        return true;
    }

    private static bool IsAggregateParameter(Register register) =>
        register.Name.StartsWith(Arm64CallingConventionResolver.AggregateParameterPrefix, StringComparison.Ordinal);

    private static void RecoverInstanceStore(Block block, int index, FieldReference target,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        var first = block.Instructions[index];
        if (first.MemoryAccessSize is not (4 or 8)
            || Arm64CallingConventionResolver.GetFloatAggregateFields(target.Field.FieldType) is not { } fields)
            return;

        var size = fields[0].FieldType.FullName == "System.Double" ? 8 : 4;
        var argument = GetProjection(first.Operands[1], fields[0], definitions);
        if (first.MemoryAccessSize != size || argument == null || !IsAggregateParameter(argument.Register)
            || argument.Type != target.Field.FieldType)
            return;

        var lane = 1;
        var end = index + 1;
        for (; end < block.Instructions.Count && lane < fields.Length; end++)
        {
            var store = block.Instructions[end];
            if (store.OpCode == OpCode.Nop)
                continue;

            var field = fields[lane];
            if (store is not { OpCode: OpCode.Move, MemoryAccessSize: var width,
                    Operands: [FieldReference { Parent: { Parent: null } parent } destination, var value] }
                || width != size || parent.Field != target.Field || !ReferenceEquals(destination.Local, target.Local)
                || destination.Field != field || destination.Offset != target.Offset + field.Offset
                || GetProjection(value, field, definitions) != argument)
                break;
            lane++;
        }

        if (lane == fields.Length)
        {
            first.SetOperand(1, argument);
            first.MemoryAccessSize = null;
            for (var j = index + 1; j < end; j++)
                Remove(block.Instructions[j]);
        }
        else
        {
            // An exact offset match resolved the first scalar store to its enclosing struct.
            // If the other lanes do not form a copy, keep the write to the first lane only.
            first.SetOperand(0, new FieldReference(fields[0], target.Local, target.Offset, target));
        }
    }

    private static LocalVariable? GetProjection(IOperand operand, FieldAnalysisContext field,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        // Copies of the lanes often pass through callee-saved SIMD registers.
        for (var depth = 0; depth < 16 && operand is LocalVariable local; depth++)
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, var source] })
                break;
            operand = source;
        }

        return operand is FieldReference { Parent: null, Local.IsThis: false } projection
               && projection.Field == field ? projection.Local : null;
    }

    private static void RecoverStaticLoad(Block block, int index, TypeAnalysisContext? caller, TypeAnalysisContext type,
        FieldAnalysisContext[] fields, Dictionary<LocalVariable, int> reads)
    {
        var pack = block.Instructions[index];
        var loads = new HashSet<Instruction>();
        var sources = new IOperand[fields.Length];
        for (var lane = 0; lane < fields.Length; lane++)
        {
            var operand = pack.Operands[lane + 2];
            if (operand is not LocalVariable local)
            {
                sources[lane] = operand;
                continue;
            }

            if (!reads.TryGetValue(local, out var count) || count != 1)
                return;

            // Keep the combined field read between the same calls and memory writes.
            Instruction? load = null;
            for (var j = index - 1; j >= 0; j--)
            {
                var candidate = block.Instructions[j];
                if (candidate.OpCode is OpCode.Nop or OpCode.PackFloatAggregate)
                    continue;
                if (candidate is not { OpCode: OpCode.Move, Operands: [LocalVariable destination, var source] })
                    break;
                if (destination != local)
                    continue;
                sources[lane] = source;
                load = candidate;
                break;
            }
            if (load == null)
                return;
            loads.Add(load);
        }

        if (sources[0] is not FieldReference { Parent: null, Field.IsStatic: true } first
            || first.Field.FieldType != type
            || (first.Field.DeclaringType != caller
                && (first.Field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public))
            return;
        for (var lane = 1; lane < fields.Length; lane++)
            if (!IsStaticLane(sources[lane], first, fields[lane]))
                return;

        if (loads.Count > 0)
        {
            // Do not defer any read past another operation that can observe or change this field.
            var remaining = loads.Count;
            for (var j = index - 1; j >= 0 && remaining > 0; j--)
            {
                var candidate = block.Instructions[j];
                if (loads.Contains(candidate))
                    remaining--;
                else if (candidate.OpCode is not (OpCode.Nop or OpCode.PackFloatAggregate)
                         && candidate is not { OpCode: OpCode.Move,
                             Operands: [LocalVariable, LocalVariable or Immediate] })
                    return;
            }
        }

        var result = pack.Operands[0];
        pack.OpCode = OpCode.Move;
        pack.SetOperands(result, first);
        foreach (var load in loads)
            Remove(load);
    }

    private static bool IsStaticLane(IOperand operand, FieldReference first, FieldAnalysisContext lane) =>
        IsStaticAggregateLane(operand, first, lane)
        || operand is MemoryOperand { Index: null, Scale: 0 } memory && ReferenceEquals(memory.Base, first.Local)
        && memory.Addend == (long)first.Offset + lane.Offset;

    private static bool TryGetStaticAggregateLane(IOperand operand, out FieldReference root,
        out FieldAnalysisContext lane, out FieldAnalysisContext[] fields)
    {
        if (operand is FieldReference reference)
        {
            var current = reference;
            while (current.Parent != null)
                current = current.Parent;

            if (current.Field.IsStatic
                && Arm64CallingConventionResolver.GetFloatAggregateFields(current.Field.FieldType) is { } aggregateFields)
            {
                root = current;
                fields = aggregateFields;
                lane = ReferenceEquals(current, reference) ? aggregateFields[0] : reference.Field;
                return aggregateFields.Contains(lane);
            }
        }

        root = null!;
        lane = null!;
        fields = null!;
        return false;
    }

    private static bool IsStaticAggregateLane(IOperand operand, FieldReference first, FieldAnalysisContext lane)
    {
        if (operand is not FieldReference reference)
            return false;

        var current = reference;
        while (current.Parent != null)
            current = current.Parent;

        return current.Field.IsStatic
            && current.Field == first.Field
            && ReferenceEquals(current.Local, first.Local)
            && reference.Field == lane;
    }

    private static void Remove(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }
}
