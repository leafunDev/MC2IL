using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

// Bind native stack storage before SSA assigns versions to the values written by calls.
internal static class Arm64StackValueRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not Arm64CallingConventionResolver convention)
            return;

        var returnedValues = new Dictionary<Register, (TypeAnalysisContext Type, Block Block, Instruction Call)>();
        var consumers = new List<(Block Block, Instruction Call, MethodAnalysisContext Target, Dictionary<Register, Register> Addresses)>();
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            var addresses = new Dictionary<Register, Register>();
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                if (instruction is { OpCode: OpCode.Move, Operands: [Register destination, var source] })
                {
                    addresses.Remove(destination);
                    if (source is AddressOf { Target: Register slot } && IsStackSlot(slot))
                        addresses[destination] = slot;
                    else if (source is Register copy && addresses.TryGetValue(copy, out var copiedSlot))
                        addresses[destination] = copiedSlot;
                }
                else if (instruction.Destination is Register overwritten)
                {
                    addresses.Remove(overwritten);
                }

                if (!instruction.IsCall)
                    continue;
                if (instruction.Operands[0] is Immediate address
                    && method.AppContext.MethodsByAddress.TryGetValue(address.UnsignedValue, out var candidates)
                    && candidates is [var target])
                {
                    if (instruction.OpCode == OpCode.Call && convention.ReturnsViaHiddenBuffer(target)
                        && convention.HiddenReturnBufferRegister(target) is { } buffer
                        && addresses.TryGetValue(buffer, out var result))
                    {
                        instruction.Destination = result;
                        returnedValues[result] = (target.ReturnType, block, instruction);
                    }
                    else if (target is { Name: ".ctor", IsStatic: false, DeclaringType.IsValueType: true }
                        && instruction.Operands[1] is Register receiver
                        && addresses.TryGetValue(receiver, out var storage))
                    {
                        // Put the write at the call itself, after any intervening zero fill.
                        instruction.SetOperand(1, new AddressOf(storage));
                    }
                    else if (addresses.Count != 0)
                    {
                        consumers.Add((block, instruction, target, new Dictionary<Register, Register>(addresses)));
                    }
                }
                addresses.Clear();
            }
        }
        if (returnedValues.Count == 0)
            return;
        foreach (var consumer in consumers)
            RecoverIndirectArguments(consumer.Block, consumer.Block.Instructions.IndexOf(consumer.Call), consumer.Call,
                consumer.Target, consumer.Addresses, returnedValues);
    }

    private static void RecoverIndirectArguments(Block block, int callIndex, Instruction call,
        MethodAnalysisContext target, Dictionary<Register, Register> addresses,
        Dictionary<Register, (TypeAnalysisContext Type, Block Block, Instruction Call)> returnedValues)
    {
        var firstParameter = (call.OpCode == OpCode.Call ? 2 : 1) + (target.IsStatic ? 0 : 1);
        for (var parameter = 0; parameter < target.Parameters.Count && firstParameter + parameter < call.Operands.Count; parameter++)
        {
            var type = target.Parameters[parameter].ParameterType;
            if (!type.IsValueType || call.Operands[firstParameter + parameter] is not Register pointer
                || !addresses.TryGetValue(pointer, out var destination))
                continue;

            var size = TypeSizes.UnboxedSize(type, 8);
            if (size <= 16)
                continue;

            var loads = new Dictionary<Register, (Register Slot, int Size)>();
            var stores = new List<Instruction>();
            Register? source = null;
            var covered = new HashSet<long>();
            for (var index = callIndex - 1; index >= 0; index--)
            {
                var instruction = block.Instructions[index];
                if (instruction.OpCode == OpCode.Nop)
                    continue;
                if (instruction is not { OpCode: OpCode.Move, Operands: [Register written, var value] })
                    break;

                if (IsStackSlot(written))
                {
                    if (value is not Register lane || instruction.MemoryAccessSize is not (4 or 8 or 16))
                        break;
                    stores.Add(instruction);
                    if (loads.ContainsKey(lane))
                        break;
                    loads[lane] = (written, instruction.MemoryAccessSize.Value);
                    continue;
                }
                if (value is Register read && IsStackSlot(read) && loads.TryGetValue(written, out var store)
                    && instruction.MemoryAccessSize == store.Size
                    && TryOffset(store.Slot, out var to) && TryOffset(destination, out var destinationOffset)
                    && TryOffset(read, out var from))
                {
                    var sourceOffset = from - (to - destinationOffset);
                    var candidate = returnedValues.Keys.FirstOrDefault(slot => TryOffset(slot, out var offset) && offset == sourceOffset);
                    if (candidate.Name == null || !MetadataResolver.IsSameType(returnedValues[candidate].Type, type)
                        || source is { } previous && previous != candidate
                        || to < destinationOffset || to + store.Size > destinationOffset + size
                        || block.Predecessors is not [var producer] || producer != returnedValues[candidate].Block
                        || producer.Successors is not [var successor] || successor != block
                        || producer.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) != returnedValues[candidate].Call)
                        break;

                    source = candidate;
                    stores.Add(instruction);
                    for (var offset = to - destinationOffset; offset < to - destinationOffset + store.Size; offset++)
                        covered.Add(offset);
                    loads.Remove(written);
                    if (covered.Count == size && loads.Count == 0)
                    {
                        var removed = new HashSet<Instruction>(stores);
                        var lanes = new HashSet<Register>(stores
                            .Where(item => item.Operands[0] is Register register && !IsStackSlot(register))
                            .Select(item => (Register)item.Operands[0]));
                        if (block.Instructions.Skip(index).Take(callIndex - index).Any(item => !removed.Contains(item)
                            && item.Operands.Where((_, operandIndex) => operandIndex != item.DestinationOperandIndex)
                                .Any(operand => operand is Register register && lanes.Contains(register)
                                    || operand is MemoryOperand memory && (memory.Base is Register baseRegister && lanes.Contains(baseRegister)
                                        || memory.Index is Register indexRegister && lanes.Contains(indexRegister)))))
                            break;

                        // The copy is complete and no call or memory effect separated its lanes.
                        var copyInstruction = stores.First();
                        copyInstruction.SetOperands(destination, candidate);
                        copyInstruction.MemoryAccessSize = null;
                        copyInstruction.OperandWidth = null;
                        call.SetOperand(firstParameter + parameter, destination);
                        foreach (var obsolete in stores.Skip(1))
                        {
                            obsolete.OpCode = OpCode.Nop;
                            obsolete.SetOperands();
                        }
                        break;
                    }
                }
                else if (value is not (Register or Immediate or AddressOf))
                {
                    break;
                }
            }
        }
    }

    private static bool IsStackSlot(Register register) => register.Name.StartsWith("stack_", System.StringComparison.Ordinal);

    private static bool TryOffset(Register register, out int offset)
    {
        var name = register.Name;
        var negative = name.StartsWith("stack_-", System.StringComparison.Ordinal);
        if (!IsStackSlot(register) || !int.TryParse(name.Substring(negative ? 7 : 6),
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out offset))
        {
            offset = 0;
            return false;
        }
        if (negative)
            offset = -offset;
        return true;
    }
}
