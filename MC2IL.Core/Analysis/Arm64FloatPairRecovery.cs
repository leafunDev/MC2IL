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
        foreach (var usage in uses)
            if (usage.Value.Any(use => use.OpCode != OpCode.Phi))
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
            Require(candidate);
            var valid = true;
            while (remaining.Count != 0 && valid)
            {
                var local = remaining.Dequeue();
                valid = definitions.TryGetValue(local, out var definition)
                    && CanSplitDefinition(definition)
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

            bool CanSplitUse(Instruction use, LocalVariable value)
            {
                if (IsExtraction(use.Arm64PackedFloat))
                    return ReferenceEquals(use.Operands[2], value);
                if (use.OpCode == OpCode.Phi)
                    return !livePhis.Contains(use) || Require(use.Destination!);
                if (IsPairProducer(use.Arm64PackedFloat))
                    return Require(use.Destination!);
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
        foreach (var block in graph.Blocks)
        for (var i = 0; i < block.Instructions.Count; i++)
        {
            var instruction = block.Instructions[i];
            var mode = instruction.Arm64PackedFloat;
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
            else if (instruction is { OpCode: OpCode.Move, MemoryAccessSize: 8,
                         Operands: [MemoryOperand store, LocalVariable source] } && high.TryGetValue(source, out var secondValue))
            {
                instruction.MemoryAccessSize = 4;
                store.Addend += 4;
                Insert(new Instruction(instruction.Index, OpCode.Move, store, secondValue) { MemoryAccessSize = 4 });
            }

            void Insert(Instruction added) => block.Instructions.Insert(++i, added);
        }

        bool IsScalarSingle(LocalVariable value) =>
            value.Type?.FullName == "System.Single"
            || definitions.TryGetValue(value, out var producer)
                && (producer is { OpCode: OpCode.Move, MemoryAccessSize: 4 }
                    || producer.Operands.Count >= 4 && producer.Operands[3] is FloatingPointPrecision { IsDouble: false }
                    || producer is { OpCode: OpCode.Move, Operands: [_, FloatLiteral] }
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
            or Arm64PackedFloatOperation.InsertLow or Arm64PackedFloatOperation.InsertHigh;
}
