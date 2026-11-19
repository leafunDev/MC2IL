using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class IteratorFactoryRecovery
{
    internal static bool IsIteratorType(TypeAnalysisContext type) =>
        type.DeclaringType != null && type.IsSealed && !type.IsValueType
        && type.BaseType?.FullName == "System.Object"
        && type.Name.StartsWith("<", StringComparison.Ordinal)
        && (type.Name.Contains(">d__", StringComparison.Ordinal) || type.Name.Contains(">c__Iterator", StringComparison.Ordinal))
        && type.InterfaceContexts.Any(iface => iface.FullName == "System.Collections.IEnumerator");

    public static void Run(MethodAnalysisContext method)
    {
        var owner = method.DeclaringType;
        if (owner == null || method.ReturnType.FullName is not ("System.Collections.IEnumerator" or "System.Collections.IEnumerable")
            && (method.ReturnType is not GenericInstanceTypeAnalysisContext generic
                || generic.GenericType.FullName is not ("System.Collections.Generic.IEnumerator`1" or "System.Collections.Generic.IEnumerable`1")))
            return;

        var graph = method.ControlFlowGraph!;
        var allocated = new Dictionary<LocalVariable, TypeAnalysisContext>();
        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Newobj,
                    Operands: [LocalVariable receiver, TypeAnalysisContext type] }
                && type.DeclaringType == owner && IsIteratorType(type)
                && (type.Name.StartsWith($"<{method.Name}>d__", StringComparison.Ordinal)
                    || type.Name.StartsWith($"<{method.Name}>c__Iterator", StringComparison.Ordinal)))
                allocated[receiver] = type;
        }

        if (allocated.Count == 0)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var call = block.Instructions[index];
                if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext
                        { Name: ".ctor", IsStatic: false, DeclaringType.FullName: "System.Object", Parameters.Count: 0 } baseCtor
                    || call.Operands.Count <= (call.OpCode == OpCode.CallVoid ? 1 : 2)
                    || call.Operands[call.OpCode == OpCode.CallVoid ? 1 : 2] is not LocalVariable receiver
                    || !allocated.TryGetValue(receiver, out var type))
                    continue;

                // IL2CPP inlined the iterator constructor into the factory. Recover it only
                // when the immediately following state store matches the real constructor.
                if (FindNext(block, index + 1) is not { OpCode: OpCode.Move,
                        Operands: [FieldReference { Parent: null, Field: { Name: "<>1__state", IsStatic: false,
                            FieldType.FullName: "System.Int32" } state } field, Immediate initialState] } store
                    || !ReferenceEquals(field.Local, receiver) || state.DeclaringType != type)
                    continue;

                var constructors = type.Methods.Where(candidate => candidate is { Name: ".ctor", IsStatic: false,
                    Parameters.Count: 1 } && candidate.Parameters[0].ParameterType.FullName == "System.Int32").ToArray();
                if (constructors is not [var ctor] || !IsInlinedStateConstructor(ctor, baseCtor, state))
                    continue;

                call.OpCode = OpCode.CallVoid;
                call.SetOperands(ctor, receiver, new Immediate(unchecked((int)initialState.Value)));
                store.OpCode = OpCode.Nop;
                store.SetOperands();
            }
        }
    }

    private static Instruction? FindNext(Block block, int index)
    {
        var visited = new HashSet<Block>();
        while (visited.Add(block))
        {
            for (; index < block.Instructions.Count; index++)
                if (block.Instructions[index].OpCode is not (OpCode.Nop or OpCode.Jump))
                    return block.Instructions[index];

            if (block.Successors is not [var next] || next.Predecessors is not [var predecessor] || predecessor != block)
                break;
            block = next;
            index = 0;
        }
        return null;
    }

    private static bool IsInlinedStateConstructor(MethodAnalysisContext ctor, MethodAnalysisContext baseCtor, FieldAnalysisContext state)
    {
        var instructionSet = ctor.AppContext.InstructionSet;
        if (instructionSet is not NewArmV8InstructionSet
            || ctor.UnderlyingPointer == 0 || ctor.RawBytes.Length == 0 || ctor.RawBytes.Length > 128
            || instructionSet.GetParameterOperandsFromMethod(ctor) is not [Register self, Register initialState, ..])
            return false;

        var aliases = new Dictionary<string, Register> { [self.Name!] = self, [initialState.Name!] = initialState };
        var calledBase = false;
        var storedState = false;
        foreach (var instruction in instructionSet.GetIsilFromMethod(ctor))
        {
            switch (instruction)
            {
                case { OpCode: OpCode.Nop or OpCode.ShiftStack }:
                case { OpCode: OpCode.Move, Operands: [StackOffset, Register] }:
                    continue;
                case { OpCode: OpCode.Move, Operands: [Register destination, var source] }:
                    if (source is Register from && aliases.TryGetValue(from.Name!, out var alias))
                        aliases[destination.Name!] = alias;
                    else if (source is StackOffset or Immediate)
                        aliases.Remove(destination.Name!);
                    else
                        return false;
                    break;
                case { OpCode: OpCode.CallVoid, Operands: [Immediate target, Register receiver, ..] }
                    when !calledBase && !storedState && target.UnsignedValue == baseCtor.UnderlyingPointer
                        && aliases.TryGetValue(receiver.Name!, out var objectAlias) && objectAlias == self:
                    calledBase = true;
                    // Only x19–x29 keep the saved this/state aliases across Object..ctor.
                    foreach (var register in aliases.Keys.ToArray())
                        if (!register.StartsWith("X", StringComparison.Ordinal)
                            || !int.TryParse(register.Substring(1), out var number) || number is < 19 or > 29)
                            aliases.Remove(register);
                    break;
                case { OpCode: OpCode.Move, Operands: [MemoryOperand { Index: null, Scale: 0, Base: Register receiver } memory, Register value] }
                    when calledBase && !storedState && memory.Addend == state.Offset
                        && aliases.TryGetValue(receiver.Name!, out var objectAlias) && objectAlias == self
                        && aliases.TryGetValue(value.Name!, out var stateAlias) && stateAlias == initialState:
                    storedState = true;
                    break;
                case { OpCode: OpCode.Return, Operands: [] }:
                    return calledBase && storedState;
                default:
                    return false;
            }
        }
        return false;
    }
}
