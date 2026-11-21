using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Extensions;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class UnusedReceiverRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet arm64
            || method.ParameterLocals.FirstOrDefault(local => local.IsThis) is not { Type: { IsValueType: false } type } self)
            return;

        var verified = new Dictionary<MethodAnalysisContext, bool>();
        foreach (var call in method.ControlFlowGraph!.Instructions)
        {
            if (!call.IsCall || call.NullCheckReceiver || call.Operands[0] is not MethodAnalysisContext
                { IsStatic: false, IsVirtual: false, DeclaringType: { IsValueType: false } owner } target
                || target.Name is ".ctor" or ".cctor"
                || !type.IsAssignableTo(owner) && !MetadataResolver.IsSameType(type, owner))
                continue;

            var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
            if (call.Operands.Count <= receiverIndex
                || call.Operands[receiverIndex] is not LocalVariable { Type.FullName:
                    "System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                    or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" }
                || !call.Operands.Skip(receiverIndex + 1).Take(target.Parameters.Count).Any(argument => ReferenceEquals(argument, self)))
                continue;

            // A direct call may leave garbage in X0 when the callee ignores its receiver.
            // Only use a real receiver already present among the original managed arguments.
            if (!verified.TryGetValue(target, out var unused))
                verified[target] = unused = IgnoresReceiver(target, arm64);
            if (unused)
                call.SetOperand(receiverIndex, self);
        }
    }

    private static bool IgnoresReceiver(MethodAnalysisContext target, NewArmV8InstructionSet arm64)
    {
        if (target.UnderlyingPointer == 0 || target.RawBytes.Length is 0 or > 4096)
            return false;

        ISILControlFlowGraph graph;
        try
        {
            graph = new ISILControlFlowGraph(arm64.GetIsilFromMethod(target));
        }
        catch (System.Exception)
        {
            // Analyzing this optional optimization must not fail the enclosing caller.
            return false;
        }
        if (graph.EntryBlock.Successors.Count != 1)
            return false;

        var pending = new Queue<Block>();
        var seen = new HashSet<Block>();
        pending.Enqueue(graph.EntryBlock);
        while (pending.Count > 0)
        {
            var block = pending.Dequeue();
            if (!seen.Add(block))
                continue;

            var overwritten = false;
            foreach (var instruction in block.Instructions)
            {
                // The generated null/bounds helpers throw a fixed exception and take no
                // managed receiver, even when the raw call lists all argument registers.
                if (instruction.IsCall && instruction.Operands[0] is Immediate address
                    && !target.AppContext.MethodsByAddress.ContainsKey(address.UnsignedValue)
                    && ThrowHelperRecovery.GetThrownException(target.AppContext, address.UnsignedValue) is
                        { FullName: "System.NullReferenceException" or "System.IndexOutOfRangeException" }
                    && ThrowHelperRecovery.IsExceptionRaiser(target.AppContext, address.UnsignedValue))
                {
                    overwritten = true;
                    break;
                }

                if (instruction.OpCode is OpCode.Invalid or OpCode.NotImplemented or OpCode.Interrupt
                    or OpCode.IndirectJump or OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall)
                    return false;

                var destinationIndex = instruction.DestinationOperandIndex;
                for (var index = 0; index < instruction.Operands.Count; index++)
                {
                    var operand = instruction.Operands[index];
                    if (index == destinationIndex && operand is Register)
                        continue;
                    if (ReadsReceiver(operand))
                        return false;
                }

                if (instruction.Destination is Register { Name: "X0" })
                {
                    overwritten = true;
                    break;
                }
            }
            if (!overwritten)
                foreach (var successor in block.Successors)
                    pending.Enqueue(successor);
        }
        return true;
    }

    private static bool ReadsReceiver(IOperand? operand) => operand switch
    {
        Register { Name: "X0" } => true,
        MemoryOperand memory => ReadsReceiver(memory.Base) || ReadsReceiver(memory.Index),
        AddressOf address => ReadsReceiver(address.Target),
        _ => false
    };
}
