using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class TrivialConstructorRecovery
{
    internal static bool IsDisplayClass(TypeAnalysisContext type) =>
        type.DeclaringType != null && type.IsSealed && !type.IsValueType
        && type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)
        && type.BaseType == type.AppContext.SystemTypes.SystemObjectType;

    // IL2CPP can inline a trivial constructor down to its call to Object..ctor.
    // Restore the constructor of the allocated type before lowering the allocation to CIL.
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet instructionSet)
            return;

        var objectType = method.AppContext.SystemTypes.SystemObjectType;
        var allocations = new Dictionary<LocalVariable, TypeAnalysisContext>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is { OpCode: OpCode.Newobj, Operands: [LocalVariable receiver, TypeAnalysisContext type] }
                && !type.IsValueType && type != objectType && type.BaseType == objectType
                && (IsDisplayClass(type) || type.DeclaringAssembly == method.DeclaringType?.DeclaringAssembly
                    && !type.Methods.Any(candidate => candidate.Name == ".cctor")))
                allocations[receiver] = type;
        }

        if (allocations.Count == 0)
            return;

        var resolved = new Dictionary<TypeAnalysisContext, MethodAnalysisContext?>();
        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.CallVoid, Operands:
                    [MethodAnalysisContext { Name: ".ctor", IsStatic: false, Parameters.Count: 0 } baseCtor,
                    LocalVariable receiver, ..] }
                || baseCtor.DeclaringType != objectType
                || !allocations.TryGetValue(receiver, out var type))
                continue;

            if (!resolved.TryGetValue(type, out var constructor))
            {
                var candidates = type.Methods.Where(candidate =>
                    candidate is { Name: ".ctor", IsStatic: false, Parameters.Count: 0 }).Take(2).ToArray();
                constructor = candidates is [var candidate]
                    && IsAccessible(candidate, method, type)
                    && IsTrivialConstructor(instructionSet, candidate, baseCtor) ? candidate : null;
                resolved.Add(type, constructor);
            }

            if (constructor != null)
                instruction.SetOperands(constructor, receiver);
        }
    }

    private static bool IsAccessible(MethodAnalysisContext constructor, MethodAnalysisContext caller, TypeAnalysisContext type)
    {
        var access = constructor.Attributes & MethodAttributes.MemberAccessMask;
        if (IsDisplayClass(type) || access == MethodAttributes.Public || caller.DeclaringType == type)
            return true;
        return access is MethodAttributes.Assembly or MethodAttributes.FamORAssem
            && caller.DeclaringType?.DeclaringAssembly == type.DeclaringAssembly;
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
