using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Graphs;
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

    public static void RecoverInlinedFieldConstructors(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var objectType = method.AppContext.SystemTypes.SystemObjectType;
        var allocations = new Dictionary<LocalVariable, TypeAnalysisContext>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction is { OpCode: OpCode.Newobj, Operands: [LocalVariable receiver, TypeAnalysisContext type] }
                && !type.IsValueType && type != objectType && type.BaseType == objectType
                && (IsDisplayClass(type) || type.DeclaringAssembly == method.DeclaringType?.DeclaringAssembly
                    && !type.Methods.Any(candidate => candidate.Name == ".cctor")))
                allocations[receiver] = type;

        if (allocations.Count != 0)
            RecoverInlinedFieldConstructors(method, allocations, objectType);
    }

    private static void RecoverInlinedFieldConstructors(MethodAnalysisContext method,
        Dictionary<LocalVariable, TypeAnalysisContext> allocations, TypeAnalysisContext objectType)
    {
        var graph = method.ControlFlowGraph!;
        var instructions = graph.Instructions;
        var dominators = new DominatorInfo(graph);

        foreach (var allocationEntry in allocations)
        {
            var receiver = allocationEntry.Key;
            var allocatedType = allocationEntry.Value;
            var allocationIndex = instructions.FindIndex(instruction => instruction is
                { OpCode: OpCode.Newobj, Operands: [LocalVariable destination, TypeAnalysisContext type] }
                && ReferenceEquals(destination, receiver) && ReferenceEquals(type, allocatedType));
            if (allocationIndex < 0)
                continue;

            var baseConstructorCall = instructions.Skip(allocationIndex + 1).FirstOrDefault(instruction => instruction is
                { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext
                    { Name: ".ctor", IsStatic: false, DeclaringType: { } declaringType }, LocalVariable constructorReceiver, ..] }
                && ReferenceEquals(declaringType, objectType) && ReferenceEquals(constructorReceiver, receiver));
            if (baseConstructorCall == null)
                continue;

            var constructors = allocatedType.Methods.Where(candidate => candidate is
                { Name: ".ctor", IsStatic: false }).Take(2).ToArray();
            if (constructors is not [var constructor]
                || constructor.Parameters.Count == 0
                || !IsAccessible(constructor, method, allocatedType)
                || constructor.Parameters.Count != allocatedType.Fields.Count(field => !field.IsStatic))
                continue;

            var block = graph.FindBlockByInstruction(baseConstructorCall);
            if (block == null)
                continue;

            var allocation = instructions[allocationIndex];
            var allocationBlock = graph.FindBlockByInstruction(allocation);
            if (allocationBlock == null
                || allocationBlock != block && !dominators.Dominates(allocationBlock, block))
                continue;

            var baseCallIndex = block.Instructions.IndexOf(baseConstructorCall);
            var allocationBlockIndex = allocationBlock.Instructions.IndexOf(allocation);
            if (baseCallIndex < 0 || allocationBlock == block && allocationBlockIndex >= baseCallIndex)
                continue;

            var fieldStores = new Dictionary<FieldAnalysisContext,
                List<(Instruction Instruction, FieldReference Destination, IOperand Value)>>();
            for (var i = baseCallIndex + 1; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];
                if (instruction.OpCode == OpCode.Nop)
                    continue;

                if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference
                        destination, var value] })
                {
                    var root = destination;
                    while (root.Parent != null)
                        root = root.Parent;

                    var isUniqueTypedAlias = root.Local.Type != null
                        && root.Local.Type.FullName == allocatedType.FullName
                        && !root.Local.IsThis
                        && !method.Parameters.Any(parameter => parameter.Name == root.Local.Name)
                        && !instructions.Any(candidate => ReferenceEquals(candidate.Destination, root.Local))
                        && allocations.Count(pair => pair.Value.FullName == allocatedType.FullName) == 1;
                    if (!ReferenceEquals(root.Local, receiver) && !isUniqueTypedAlias
                        || !ReferenceEquals(root.Field.DeclaringType, allocatedType))
                        break;

                    if (!fieldStores.TryGetValue(root.Field, out var writes))
                        fieldStores[root.Field] = writes = [];
                    writes.Add((instruction, destination, value));
                    continue;
                }

                break;
            }

            var instanceFields = allocatedType.Fields.Where(field => !field.IsStatic).ToArray();
            var stores = new List<(FieldAnalysisContext Field, IOperand Value, List<Instruction> Instructions)>();
            foreach (var field in instanceFields)
            {
                if (!fieldStores.TryGetValue(field, out var writes)
                    || !TryGetInitializationValue(field, writes, out var value))
                {
                    stores.Clear();
                    break;
                }
                stores.Add((field, value, writes.Select(write => write.Instruction).ToList()));
            }

            if (stores.Count != instanceFields.Length)
                continue;

            var matches = new List<(MethodAnalysisContext Constructor, IOperand[] Arguments, List<Instruction> SuppressedStores)>();
            foreach (var candidate in constructors)
                if (TryMapArguments(candidate, stores, method, instructions, allocationIndex, dominators,
                    out var arguments, out var mappedSuppressedStores))
                    matches.Add((candidate, arguments, mappedSuppressedStores));

            if (matches is not [(var recoveredConstructor, var constructorArguments, var recoveredStores)])
                continue;

            var operands = new IOperand[constructorArguments.Length + 2];
            operands[0] = recoveredConstructor;
            operands[1] = receiver;
            Array.Copy(constructorArguments, 0, operands, 2, constructorArguments.Length);
            baseConstructorCall.SetOperands(operands.ToList());

            // The allocation was followed by the inlined constructor's equivalent field stores.
            // Once its constructor call is restored, retaining those stores duplicates initialization.
            foreach (var store in recoveredStores)
            {
                store.OpCode = OpCode.Nop;
                store.SetOperands();
            }
        }

        static bool TryMapArguments(MethodAnalysisContext constructor,
            List<(FieldAnalysisContext Field, IOperand Value, List<Instruction> Instructions)> stores,
            MethodAnalysisContext caller, List<Instruction> instructions, int allocationIndex,
            DominatorInfo dominators, out IOperand[] arguments, out List<Instruction> suppressedStores)
        {
            arguments = [];
            suppressedStores = [];
            var mappedArguments = new IOperand[constructor.Parameters.Count];
            var mappedStores = new List<Instruction>();
            var remaining = stores.ToList();

            for (var i = 0; i < constructor.Parameters.Count; i++)
            {
                var parameter = constructor.Parameters[i];
                var parameterName = NormalizeName(parameter.Name);
                var named = remaining.Where(store => SameType(store.Field.FieldType, parameter.ParameterType)
                    && NamesMatch(parameterName, NormalizeName(store.Field.Name))).ToArray();
                if (named.Length == 1)
                {
                    if (!AddArgument(named[0], parameter, i))
                        return false;
                    remaining.Remove(named[0]);
                    continue;
                }

                if (named.Length > 1)
                    return false;

                // Names such as `type` -> `AttackType` or `hitboxId` -> `AffectedHitbox`
                // can still be mapped when the field type makes the choice unique.
                var sameType = remaining.Where(store => SameType(store.Field.FieldType, parameter.ParameterType)).ToArray();
                if (sameType.Length != 1)
                    return false;

                if (!AddArgument(sameType[0], parameter, i))
                    return false;
                remaining.Remove(sameType[0]);
            }

            arguments = mappedArguments;
            suppressedStores = mappedStores;
            return remaining.Count == 0;

            bool AddArgument((FieldAnalysisContext Field, IOperand Value, List<Instruction> Instructions) store,
                ParameterAnalysisContext parameter, int parameterIndex)
            {
                if (parameter.IsRef)
                    return false;

                if (CanReadAtAllocation(store.Value, caller, instructions, allocationIndex, dominators))
                {
                    mappedArguments[parameterIndex] = store.Value;
                    mappedStores.AddRange(store.Instructions);
                    return true;
                }

                // Preserve a value computed after allocation. Initializing that parameter to its
                // default and keeping its original field store retains the method's evaluation order.
                if (!CanSafelyDefer(store.Value, caller, instructions, []))
                    return false;

                mappedArguments[parameterIndex] = new DefaultValue(parameter.ParameterType);
                return true;
            }
        }

        static bool NamesMatch(string parameter, string field) => parameter == field
            || parameter.Length > 2 && parameter.StartsWith("is", StringComparison.Ordinal) && parameter[2..] == field
            || field.Length > 2 && field.StartsWith("is", StringComparison.Ordinal) && field[2..] == parameter;

        static string NormalizeName(string name)
        {
            if (name.StartsWith("<", StringComparison.Ordinal)
                && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                name = name.Substring(1, name.IndexOf('>') - 1);
            return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        static bool SameType(TypeAnalysisContext left, TypeAnalysisContext right) =>
            ReferenceEquals(left, right) || left.FullName == right.FullName;

        static bool CanReadAtAllocation(IOperand operand, MethodAnalysisContext method,
            List<Instruction> instructions, int allocationIndex, DominatorInfo dominators) =>
            CanReadAtAllocationCore(operand, method, instructions, allocationIndex, dominators, []);

        static bool CanReadAtAllocationCore(IOperand operand, MethodAnalysisContext method,
            List<Instruction> instructions, int allocationIndex, DominatorInfo dominators,
            HashSet<LocalVariable> seen)
        {
            if (operand is Immediate or FloatLiteral or DoubleLiteral or StringLiteral or DefaultValue)
                return true;
            if (operand is not LocalVariable local || !seen.Add(local))
                return false;

            var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray();
            if (definitions.Length == 0)
                return local.IsThis || method.Parameters.Any(parameter => parameter.Name == local.Name);
            if (definitions is not [var definition] || !IsPureValueDefinition(definition))
                return false;

            var graph = method.ControlFlowGraph!;
            var definitionBlock = graph.FindBlockByInstruction(definition);
            var allocation = instructions[allocationIndex];
            var allocationBlock = graph.FindBlockByInstruction(allocation);
            if (definitionBlock == null || allocationBlock == null)
                return false;

            if (definitionBlock == allocationBlock)
            {
                if (definitionBlock.Instructions.IndexOf(definition) >= definitionBlock.Instructions.IndexOf(allocation))
                    return false;
            }
            else if (!dominators.Dominates(definitionBlock, allocationBlock))
                return false;

            foreach (var source in definition.Operands.Skip(1))
                if (!CanReadAtAllocationCore(source, method, instructions, allocationIndex, dominators,
                        new HashSet<LocalVariable>(seen)))
                    return false;
            return true;
        }

        static bool CanSafelyDefer(IOperand operand, MethodAnalysisContext method,
            List<Instruction> instructions, HashSet<LocalVariable> seen)
        {
            if (operand is Immediate or FloatLiteral or DoubleLiteral or StringLiteral or DefaultValue)
                return true;
            if (operand is not LocalVariable local || !seen.Add(local))
                return false;

            var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray();
            if (definitions.Length == 0)
                return local.IsThis || method.Parameters.Any(parameter => parameter.Name == local.Name);
            if (definitions is not [var definition] || !IsPureValueDefinition(definition))
                return false;

            foreach (var source in definition.Operands.Skip(1))
                if (!CanSafelyDefer(source, method, instructions, new HashSet<LocalVariable>(seen)))
                    return false;
            return true;
        }

        static bool IsPureValueDefinition(Instruction instruction) => instruction.OpCode is
            OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.ShiftLeft
            or OpCode.ShiftRight or OpCode.ShiftRightUnsigned or OpCode.And or OpCode.Or or OpCode.Xor
            or OpCode.Not or OpCode.Negate or OpCode.Convert or OpCode.CheckEqual or OpCode.CheckNotEqual
            or OpCode.CheckGreater or OpCode.CheckLess or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
            or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned or OpCode.CheckGreaterOrEqualUnsigned
            or OpCode.CheckLessOrEqualUnsigned;

        static bool TryGetInitializationValue(FieldAnalysisContext field,
            List<(Instruction Instruction, FieldReference Destination, IOperand Value)> writes, out IOperand value)
        {
            value = null!;
            var direct = writes.Where(write => write.Destination.Parent == null).ToArray();
            var nested = writes.Where(write => write.Destination.Parent != null).ToArray();

            if (direct.Length == 1 && nested.Length == 0)
            {
                value = IsZero(direct[0].Value) && field.FieldType.IsValueType
                    ? new DefaultValue(field.FieldType)
                    : direct[0].Value;
                return true;
            }

            if (field.FieldType.IsValueType
                && nested.Length > 0
                && nested.All(write => IsZero(write.Value))
                && (direct.Length == 0 || direct.Length == 1 && IsZero(direct[0].Value)))
            {
                value = new DefaultValue(field.FieldType);
                return true;
            }

            return false;
        }

        static bool IsZero(IOperand operand) => operand is Immediate { Value: 0 }
            or FloatLiteral { Value: 0f }
            or DoubleLiteral { Value: 0d }
            or DefaultValue;
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
