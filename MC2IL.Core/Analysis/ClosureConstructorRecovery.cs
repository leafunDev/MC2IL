using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class ClosureConstructorRecovery
{
    internal static bool IsDisplayClass(TypeAnalysisContext type) =>
        type.DeclaringType != null && type.IsSealed && !type.IsValueType
        && type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)
        && type.BaseType == type.AppContext.SystemTypes.SystemObjectType;

    // IL2CPP can inline a closure constructor down to its call to Object..ctor.
    // Restore the constructor of the allocated type before lowering the allocation to CIL.
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet instructionSet)
            return;

        var allocations = new Dictionary<LocalVariable, TypeAnalysisContext>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is { OpCode: OpCode.Newobj, Operands: [LocalVariable receiver, TypeAnalysisContext type] }
                && IsDisplayClass(type))
                allocations[receiver] = type;
        }

        if (allocations.Count == 0)
            return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.CallVoid, Operands:
                    [MethodAnalysisContext { Name: ".ctor", IsStatic: false, Parameters.Count: 0 } baseCtor,
                    LocalVariable receiver, ..] }
                || baseCtor.DeclaringType != method.AppContext.SystemTypes.SystemObjectType
                || !allocations.TryGetValue(receiver, out var type))
                continue;

            var constructors = type.Methods.Where(candidate =>
                candidate is { Name: ".ctor", IsStatic: false, Parameters.Count: 0 }).Take(2).ToArray();
            if (constructors is [var constructor] && IsTrivialConstructor(instructionSet, constructor, baseCtor))
                instruction.SetOperands(constructor, receiver);
        }
    }

    private static bool IsTrivialConstructor(NewArmV8InstructionSet instructionSet, MethodAnalysisContext constructor,
        MethodAnalysisContext baseCtor)
    {
        if (constructor.UnderlyingPointer == 0 || baseCtor.UnderlyingPointer == 0
            || constructor.RawBytes.Length is 0 or > 32)
            return false;

        var body = instructionSet.GetIsilFromMethod(constructor)
            .Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        if (body is [{ OpCode: OpCode.Move, Operands: [Register { Name: "X1" }, Immediate { Value: 0 }] }, ..])
            body = body.Skip(1).ToArray(); // Hidden MethodInfo argument.

        return body is
        [
            { OpCode: OpCode.CallVoid, Operands: [Immediate target, Register { Name: "X0" }, ..] },
            { OpCode: OpCode.Return, Operands: [] }
        ] && target.UnsignedValue == baseCtor.UnderlyingPointer;
    }
}
