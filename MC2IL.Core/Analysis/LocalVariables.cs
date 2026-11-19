using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Extensions;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

public static class LocalVariables
{
    public static int MaxTypePropagationLoopCount = 5000;

    private const long StaticFieldsOffset64 = 0xB8;
    private const long StaticFieldsOffset32 = 0x5C;

    public static void CreateAll(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Instructions;

        // Get all registers
        var registers = new List<Register>();
        var seenRegisters = new HashSet<Register>();
        foreach (var instruction in instructions)
            AddRegisters(instruction, registers, seenRegisters);

        // Map those to locals
        var locals = new Dictionary<Register, LocalVariable>(registers.Count);
        for (var i = 0; i < registers.Count; i++)
        {
            var register = registers[i];
            locals.Add(register, new LocalVariable($"v{i}", register));
        }

        // Replace registers with locals
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is Register register)
                    instruction.SetOperand(i, locals[register]);

                if (operand is AddressOf { Target: Register addressed })
                    instruction.SetOperand(i, new AddressOf(locals[addressed]));

                if (operand is MemoryOperand memory)
                {
                    if (memory.Base != null)
                    {
                        var baseRegister = (Register)memory.Base;
                        memory.Base = locals[baseRegister];
                    }

                    if (memory.Index != null)
                    {
                        var index = (Register)memory.Index;
                        memory.Index = locals[index];
                    }

                    instruction.SetOperand(i, memory);
                }
            }
        }

        method.Locals = locals.Select(kv => kv.Value).ToList();

        // Return local names
        var retValIndex = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Return || instruction.Operands.Count != 1) continue;

            var returnLocal = (LocalVariable)instruction.Sources[0];

            returnLocal.Name = $"returnVal{retValIndex + 1}";
            returnLocal.IsReturn = true;
            retValIndex++;
        }

        // Add parameter names
        var paramLocals = new List<LocalVariable>();

        var operandOffset = method.IsStatic ? 0 : 1; // 'this'

        // 'this' param
        if (!method.IsStatic && method.Locals.Count > 0 && method.ParameterOperands.Count > 0)
        {
            var thisOperand = (Register)method.ParameterOperands[0];
            var thisLocal = method.Locals.FirstOrDefault(l => l.Register.Number == thisOperand.Number && l.Register.Version == -1);

            if (thisLocal != null)
            {
                thisLocal.Name = "this";
                thisLocal.IsThis = true;
                paramLocals.Add(thisLocal);
            }
            else
            {
                method.AddWarning($"'this' local not found (operand: {thisOperand})");
            }
        }

        // Check if method has MethodInfo*
        var hasMethodInfo = (method.ParameterOperands.Count - operandOffset) > method.Parameters.Count;
        var methodInfoIndex = method.ParameterOperands.Count - 1;

        // Add normal parameter names
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var operandIndex = i + operandOffset;
            if (hasMethodInfo && operandIndex == methodInfoIndex)
                break; // Skip MethodInfo*

            if (operandIndex >= method.ParameterOperands.Count)
                break;

            if (method.ParameterOperands[operandIndex] is not Register reg)
                continue;

            var local = method.Locals.FirstOrDefault(l => l.Register.Number == reg.Number && l.Register.Version == -1);
            if (local == null)
                continue;

            local.Name = method.Parameters[i].ParameterName;
            paramLocals.Add(local);
        }

        // Add MethodInfo*
        if (hasMethodInfo)
        {
            var methodInfoOperand = (Register)method.ParameterOperands[methodInfoIndex];
            var methodInfoLocal = method.Locals.FirstOrDefault(l => l.Register.Number == methodInfoOperand.Number && l.Register.Version == -1);

            if (methodInfoLocal != null)
            {
                methodInfoLocal.Name = "methodInfo";
                methodInfoLocal.IsMethodInfo = true;
                paramLocals.Add(methodInfoLocal);
            }
        }

        method.ParameterLocals = paramLocals;

        // the hidden return buffer takes the first argument register. we type it as the return
        // type so stores into it resolve to fields
        if (method.AppContext.Binary.PointerSizeBytes == 8
            && method.AppContext.InstructionSet.CallingConventionResolver?.HiddenReturnBufferRegister(method) is { } bufferRegister
            && method.Locals.FirstOrDefault(l => l.Register.Number == bufferRegister.Number && l.Register.Version == -1) is { } bufferLocal)
        {
            bufferLocal.Name = "returnBuffer";
            bufferLocal.Type = method.ReturnType;

            // The machine function returns a pointer to the caller-provided buffer (or leaves the
            // scalar return register unspecified). In reconstructed CIL the method returns the
            // value accumulated in that buffer, so make each native return read the synthetic local.
            foreach (var instruction in instructions)
            {
                if (instruction.OpCode == OpCode.Return
                    && instruction.Operands.FirstOrDefault() is LocalVariable result
                    && result.Register.Number == bufferRegister.Number)
                    instruction.SetOperand(0, bufferLocal);
            }
        }
    }

    public static void RemoveUnused(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        cfg.BuildUseDefLists();

        var usedLocals = new HashSet<LocalVariable>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var usedVar in block.Use.OfType<LocalVariable>())
                usedLocals.Add(usedVar);

            foreach (var definedVar in block.Def.OfType<LocalVariable>())
                usedLocals.Add(definedVar);

            foreach (var operand in block.Use)
                if (operand is AddressOf { Target: FieldReference { Local: var receiver } })
                    usedLocals.Add(receiver);
        }

        method.Locals.RemoveAll(x => !usedLocals.Contains(x));
    }

    private static void AddRegisters(Instruction instruction, List<Register> registers, HashSet<Register> seenRegisters)
    {
        foreach (var operand in instruction.Operands)
        {
            if (operand is AddressOf { Target: Register addressed } && seenRegisters.Add(addressed))
                registers.Add(addressed);

            if (operand is Register register && seenRegisters.Add(register))
                registers.Add(register);

            if (operand is MemoryOperand memory)
            {
                if (memory.Base != null)
                {
                    var baseRegister = (Register)memory.Base;
                    if (seenRegisters.Add(baseRegister))
                        registers.Add(baseRegister);
                }

                if (memory.Index != null)
                {
                    var index = (Register)memory.Index;
                    if (seenRegisters.Add(index))
                        registers.Add(index);
                }
            }
        }
    }

    /// <summary>
    /// Resolves field accesses and propagates types together, to a fixpoint, while the method is
    /// still in SSA form (every local has a single, version-stable definition).
    ///
    /// The two are mutually enabling and so cannot be ordered as separate passes: a typed base lets
    /// <see cref="MetadataResolver.ResolveFieldOffsets"/> turn <c>[base + offset]</c> into a
    /// <see cref="FieldReference"/>, a resolved field load types its result with the field's type,
    /// and that result is in turn the base of the next access (directly, or after flowing through
    /// moves/phis). Both steps are monotonic - each only ever resolves an operand or fills a
    /// previously-unknown type - so the loop converges.
    /// </summary>
    public static void ResolveTypesAndFields(MethodAnalysisContext method)
    {
        // This fixpoint changes only operands and local types, not the CFG's block or instruction
        // membership. Reuse one traversal across every seed and propagation sweep.
        var instructions = method.ControlFlowGraph!.Instructions;

        // Seed types from fixed ground truth - the method's own signature, and type-metadata global
        // loads. Applied once up front and, being applied first, they win over anything inferred later.
        PropagateFromReturn(method, instructions);
        PropagateFromParameters(method);
        SeedRuntimeClassTypes(method, instructions);
        SeedNewobjResults(method, instructions);
        SeedFloatAggregateTypes(instructions);
        SeedMethodInfoTypes(method, instructions);
        SeedComparisonResults(method, instructions);
        SeedLiteralTypes(method, instructions);
        SeedFloatingPointArithmeticResults(method, instructions);

        // Everywhere there's a CallVoid after a Newobj, we can resolve the constructor call.
        MetadataResolver.ResolveConstructorCalls(method, instructions);

        // Everything else is mutually enabling and so runs to a fixpoint: a typed receiver lets an
        // ambiguous call resolve, a resolved call types its return value and arguments, a typed base
        // lets a field offset resolve, a field load types its result, and any of those can be the
        // receiver/base of the next step. Every pass is monotonic - it only resolves an operand or
        // fills a previously-unknown type - so the loop converges.
        var changed = true;
        var loopCount = 0;

        while (changed)
        {
            if (MaxTypePropagationLoopCount != -1 && ++loopCount > MaxTypePropagationLoopCount)
                throw new DecompilerException($"Type and field resolution not settling! (looped {MaxTypePropagationLoopCount} times)");

            changed = false;
            changed |= MetadataResolver.ResolveCallsViaMethodInfo(method, instructions);
            changed |= MetadataResolver.ResolveAmbiguousCalls(method, instructions);
            changed |= MetadataResolver.ResolveVirtualCalls(method, instructions);
            changed |= PropagateFromCallParameters(method, instructions);
            changed |= MetadataResolver.ResolveFieldOffsets(method, instructions);
            changed |= RefineAbsoluteMemoryPlaceholderTypes(instructions);
            changed |= RgctxResolver.Run(method, instructions);
            changed |= PropagateStaticFieldStorage(method, instructions);
            changed |= TypeAddressedLocals(method, instructions);
            changed |= PropagateTypesOnce(method, instructions);
            changed |= SpecializeSingleUseDelegateAllocations(method, instructions);
            changed |= MetadataResolver.ResolveConstructorCalls(method, instructions);
        }
    }

    private static bool SpecializeSingleUseDelegateAllocations(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var firstArgument = call.OpCode == OpCode.CallVoid
                ? (calledMethod.IsStatic ? 1 : 2)
                : (calledMethod.IsStatic ? 2 : 3);

            for (var operandIndex = firstArgument; operandIndex < call.Operands.Count; operandIndex++)
            {
                var parameterIndex = operandIndex - firstArgument;
                if (parameterIndex >= calledMethod.Parameters.Count
                    || calledMethod.Parameters[parameterIndex].ParameterType is not GenericInstanceTypeAnalysisContext expectedType
                    || !IsDelegateType(expectedType.GenericType)
                    || call.Operands[operandIndex] is not LocalVariable delegateLocal
                    || CountDirectLocalUses(instructions, delegateLocal) != 1)
                    continue;

                var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, delegateLocal)).ToList();
                if (definitions.Count != 1
                    || definitions[0] is not { OpCode: OpCode.Newobj, Operands: [_, var allocatedOperand] } allocation
                    || InstantiatedType(allocatedOperand) is not GenericInstanceTypeAnalysisContext allocatedType
                    || !SameGenericDefinition(allocatedType.GenericType, expectedType.GenericType)
                    || allocatedType.GenericArguments.Count != expectedType.GenericArguments.Count)
                    continue;

                if (allocatedType.GenericArguments.Select((type, index) => SameType(type, expectedType.GenericArguments[index])).All(equal => equal))
                    continue;

                // The consuming method's closed delegate parameter is stronger type evidence than
                // an erased generic allocation such as Action<object>. Refine only when the value
                // is constructed once and passed to one known delegate parameter.
                delegateLocal.Type = expectedType;
                allocation.SetOperand(1, expectedType);
                changed = true;

                foreach (var constructorCall in instructions)
                {
                    if (constructorCall is not { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" } oldConstructor, LocalVariable receiver, ..] }
                        || !ReferenceEquals(receiver, delegateLocal)
                        || oldConstructor.DeclaringType is not GenericInstanceTypeAnalysisContext oldType
                        || !SameGenericDefinition(oldType.GenericType, expectedType.GenericType))
                        continue;

                    var constructors = expectedType.GenericType.Methods
                        .Where(candidate => candidate is { Name: ".ctor", IsStatic: false }
                            && candidate.Parameters.Count == oldConstructor.Parameters.Count)
                        .ToList();
                    if (constructors is not [{ } constructor])
                        continue;

                    var specializedConstructor = new ConcreteGenericMethodAnalysisContext(constructor, expectedType.GenericArguments, []);
                    constructorCall.SetOperand(0, specializedConstructor);
                    specializedConstructor.AppContext.InstructionSet.CallingConventionResolver?.RemapRawArguments(constructorCall, specializedConstructor);
                }
            }
        }

        return changed;
    }

    private static int CountDirectLocalUses(List<Instruction> instructions, LocalVariable local)
    {
        var count = 0;
        foreach (var instruction in instructions)
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var constructorReceiver = instruction.IsCall
                    && instruction.Operands[0] is MethodAnalysisContext { Name: ".ctor" }
                    && i == (instruction.OpCode == OpCode.CallVoid ? 1 : 2);
                if (!constructorReceiver
                    && !ReferenceEquals(instruction.Operands[i], instruction.Destination)
                    && ReferenceEquals(instruction.Operands[i], local))
                    count++;
            }
        return count;
    }

    private static bool IsDelegateType(TypeAnalysisContext type) =>
        type.BaseType?.FullName == "System.MulticastDelegate";

    private static bool SameGenericDefinition(TypeAnalysisContext left, TypeAnalysisContext right) =>
        ReferenceEquals(left, right) || left.FullName == right.FullName;

    private static bool SameType(TypeAnalysisContext left, TypeAnalysisContext right) =>
        ReferenceEquals(left, right) || left.FullName == right.FullName;

    // A type-metadata global load (Move local, typeof(T)) puts the runtime class pointer for T into
    // the local - an Il2CppClass*, not an instance of T. That is known exactly from the instruction,
    // so it is seeded as ground truth (overriding any prior guess) before the inference fixpoint,
    // rather than letting a monotonic pass first mistype the local as T itself.
    private static void SeedRuntimeClassTypes(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is TypeAnalysisContext type and not (RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext))
                destination.Type = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);
        }
    }

    private static void SeedNewobjResults(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Newobj || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination && InstantiatedType(instruction.Operands[1]) is { } type)
                destination.Type = type;
        }
    }

    private static void SeedFloatAggregateTypes(List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.PackFloatAggregate, Operands: [LocalVariable destination, TypeAnalysisContext type, ..] }
                || Arm64CallingConventionResolver.GetFloatAggregateFields(type) is not { } fields)
                continue;

            destination.Type = type;
            for (var i = 0; i < fields.Length; i++)
                if (instruction.Operands[i + 2] is LocalVariable lane)
                    lane.Type ??= fields[i].FieldType;
        }
    }

    // The ARM64 caller can inline Vector3.zero as three scalar reads from the same
    // readonly static field used by Unity's getter. Restore that getter at the call site.
    internal static void RecoverFloatAggregateSources(MethodAnalysisContext method)
    {
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var pack = block.Instructions[i];
                if (pack is not { OpCode: OpCode.PackFloatAggregate,
                        Operands: [LocalVariable result, TypeAnalysisContext { FullName: "UnityEngine.Vector3" } type,
                            LocalVariable x, LocalVariable y, LocalVariable z] }
                    || type.DeclaringAssembly.CleanAssemblyName != "UnityEngine.CoreModule")
                    continue;

                var getter = type.Properties.FirstOrDefault(property => property.Name == "zero")?.Getter;
                if (getter is not { IsStatic: true, Parameters.Count: 0 }
                    || getter.ReturnType != type)
                    continue;

                var loads = new Dictionary<LocalVariable, Instruction>();
                for (var j = i - 1; j >= 0 && loads.Count < 3; j--)
                {
                    var candidate = block.Instructions[j];
                    if (candidate.OpCode == OpCode.Nop)
                        continue;
                    if (candidate is not { OpCode: OpCode.Move, Operands: [LocalVariable lane, var source] })
                        break;
                    if (ReferenceEquals(lane, x) || ReferenceEquals(lane, y) || ReferenceEquals(lane, z))
                        loads[lane] = candidate;
                    else if (source is not (Immediate or LocalVariable))
                        break;
                }

                if (!loads.TryGetValue(x, out var xLoad) || !loads.TryGetValue(y, out var yLoad)
                    || !loads.TryGetValue(z, out var zLoad)
                    || xLoad.Operands[1] is not FieldReference { Parent: null, Field: { IsStatic: true } field } first
                    || field.Name != "zeroVector" || field.DeclaringType != type || field.FieldType != type
                    || (field.Attributes & System.Reflection.FieldAttributes.InitOnly) == 0
                    || !IsLaneAt(yLoad, first, sizeof(float))
                    || !IsLaneAt(zLoad, first, 2 * sizeof(float)))
                    continue;

                var instructions = method.ControlFlowGraph.Instructions;
                if (loads.Keys.Any(lane => instructions.Any(instruction => !ReferenceEquals(instruction, pack)
                    && instruction.Sources.Any(source => ReferenceEquals(source, lane)))))
                    continue;

                foreach (var load in loads.Values)
                {
                    load.OpCode = OpCode.Nop;
                    load.SetOperands();
                }
                pack.OpCode = OpCode.Call;
                pack.SetOperands(getter, result);
            }
        }

        static bool IsLaneAt(Instruction load, FieldReference first, int byteOffset) =>
            load.Operands[1] is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable storage } memory
            && ReferenceEquals(first.Local, storage)
            && memory.Addend == first.Offset + byteOffset;
    }

    private static TypeAnalysisContext? InstantiatedType(IOperand classOperand) =>
        classOperand switch
        {
            LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var t } } => t,
            RuntimeClassTypeAnalysisContext { RepresentedType: var t } => t,
            TypeAnalysisContext type => type, //not sure this is actually valid but for completeness
            _ => null,
        };

    // A method/field-metadata global load (Move local, methodof(M) / fieldof(F)) puts a MethodInfo*
    // or FieldInfo* into the local. MetadataResolver already resolved the address to a context naming
    // the member; that same context is the local's type (a runtime handle, recoverable via its
    // RepresentedMethod/RepresentedField).
    private static void SeedMethodInfoTypes(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext)
                destination.Type = (TypeAnalysisContext)instruction.Operands[1];
        }
    }

    // A comparison (CheckEqual, CheckLess, ...) writes a 0/1 result into its destination, so that local
    // is a System.Boolean regardless of what the compared operands are.
    private static void SeedComparisonResults(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is < OpCode.CheckEqual or > OpCode.CheckLessOrEqualUnsigned)
                continue;

            if (instruction.Destination is LocalVariable destination)
                destination.Type = booleanType;
        }
    }
    
    //Handles typing of locals for ref/out params. Returns whether anything new was typed
    public static bool TypeAddressedLocals(MethodAnalysisContext method)
        => TypeAddressedLocals(method, method.ControlFlowGraph!.Instructions);

    private static bool TypeAddressedLocals(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var firstArg = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // the receiver of a value type's instance method is a pointer to the value
            if (!calledMethod.IsStatic && firstArg < instruction.Operands.Count
                && instruction.Operands[firstArg] is AddressOf { Target: LocalVariable receiver }
                && calledMethod.DeclaringType is { IsValueType: true } declaringType)
                changed |= SetTypeIfUnknown(receiver, declaringType);

            var paramOffset = firstArg + (calledMethod.IsStatic ? 0 : 1);

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                if (instruction.Operands[i] is AddressOf { Target: LocalVariable referenced }
                    && calledMethod.Parameters[parameterIndex].ParameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType })
                    changed |= SetTypeIfUnknown(referenced, referencedType);
            }
        }

        return changed;
    }

    // Fills in a local's type only when it is currently unknown, keeping propagation monotonic (a
    // type, once set, is never changed) so the fixpoint terminates. Returns whether it set anything.
    private static bool SetTypeIfUnknown(LocalVariable local, TypeAnalysisContext? type)
    {
        if (type == null || local.Type != null)
            return false;

        local.Type = type;
        return true;
    }

    private static bool PropagateStaticFieldStorage(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var staticFieldsOffset = method.AppContext.Binary.is32Bit ? StaticFieldsOffset32 : StaticFieldsOffset64;
        var changed = false;

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination || destination.Type is StaticFieldStorageTypeAnalysisContext)
                continue;

            if (instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0 } memory || memory.Addend != staticFieldsOffset)
                continue;

            if (memory.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var owner } })
                continue;

            var storage = new StaticFieldStorageTypeAnalysisContext(owner, owner.DeclaringAssembly);
            destination.Type = storage;
            // Resolved static field accesses emit ldsfld/stsfld and do not read this
            // pointer. Keep a symbolic handle for any other uses so the native load
            // and its class-info input can disappear when there are no uses left.
            instruction.SetOperand(1, storage);
            changed = true;
        }

        return changed;
    }

    // A single propagation sweep over every move and phi. Returns whether it filled in any type.
    private static bool PropagateTypesOnce(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        foreach (var instruction in instructions)
        {
            switch (instruction.OpCode)
            {
                case OpCode.Move:
                    changed |= PropagateMove(instruction, method.AppContext.Binary.PointerSizeBytes);
                    break;
                case OpCode.Convert:
                    changed |= PropagateNumericConversion(instruction, method.AppContext.SystemTypes);
                    break;
                case OpCode.Phi:
                    changed |= PropagatePhi(instruction);
                    break;
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply:
                    changed |= PropagateArithmetic(instruction, method);
                    break;
                case OpCode.Divide or OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned:
                    changed |= PropagateArithmetic(instruction, method) || PropagateIntegerResult(instruction, method);
                    break;
                case OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate
                    or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned:
                    changed |= PropagateBooleanResult(instruction, method.AppContext.SystemTypes)
                        || PropagateIntegerResult(instruction, method);
                    break;
            }
        }

        return changed;
    }

    private static void SeedLiteralTypes(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands[0] is not LocalVariable destination)
                continue;

            destination.Type = instruction.Operands[1] switch
            {
                StringLiteral => method.AppContext.SystemTypes.SystemStringType,
                FloatLiteral => method.AppContext.SystemTypes.SystemSingleType,
                DoubleLiteral => method.AppContext.SystemTypes.SystemDoubleType,
                _ => destination.Type,
            };
        }
    }

    // The ARM64 lifter records S/D precision explicitly for scalar FP arithmetic because both
    // register widths are normalized to the same ISIL V register name. This instruction metadata
    // is ground truth and must win even before operand types have propagated.
    private static void SeedFloatingPointArithmeticResults(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned
                || instruction.Destination is not LocalVariable destination
                || instruction.Operands.LastOrDefault() is not FloatingPointPrecision precision)
                continue;

            destination.Type = precision.IsDouble
                ? method.AppContext.SystemTypes.SystemDoubleType
                : method.AppContext.SystemTypes.SystemSingleType;
        }
    }

    // Arithmetic on a float operand is float arithmetic, so the result is that float type.
    private static bool PropagateArithmetic(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.Operands is not [LocalVariable { Type: null } destination, var left, var right, ..])
            return false;

        if (instruction.Operands.LastOrDefault() is FloatingPointPrecision precision)
            return SetTypeIfUnknown(destination, precision.IsDouble
                ? method.AppContext.SystemTypes.SystemDoubleType
                : method.AppContext.SystemTypes.SystemSingleType);

        if ((FloatOperandType(left, method) ?? FloatOperandType(right, method)) is not { } floatType)
            return false;

        return SetTypeIfUnknown(destination, floatType);
    }

    // An integer operand makes the result an integer. Excludes bool operands so flag logic stays boolean.
    private static bool PropagateIntegerResult(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.Operands[0] is not LocalVariable { Type: null } destination)
            return false;

        if (instruction.OperandWidth == 64)
        {
            if (instruction.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned)
                return SetTypeIfUnknown(destination, method.AppContext.SystemTypes.SystemInt64Type);
            if (instruction is { OpCode: OpCode.Or, Operands: [_, _, Immediate] })
                return SetTypeIfUnknown(destination, method.AppContext.SystemTypes.SystemUInt64Type);
        }

        for (var i = 1; i < instruction.Operands.Count; i++)
            if (IntegerResultType(instruction.Operands[i], method) is { } integerType)
                return SetTypeIfUnknown(destination, integerType);

        return false;
    }

    // Logical operations on boolean flags stay boolean. Without this edge, negated/composed
    // branch conditions lose their type, become object locals, and decompile as numeric 0/1.
    private static bool PropagateBooleanResult(Instruction instruction, SystemTypesContext types)
    {
        if (instruction.Operands[0] is not LocalVariable { Type: null } destination)
            return false;

        if (instruction.OpCode == OpCode.Not)
            return instruction.Operands[1] is LocalVariable { Type: { } sourceType }
                && sourceType == types.SystemBooleanType
                && SetTypeIfUnknown(destination, types.SystemBooleanType);

        if (instruction.OpCode is not (OpCode.And or OpCode.Or or OpCode.Xor)
            || instruction.Operands.Count < 3)
            return false;

        var leftIsBoolean = instruction.Operands[1] is LocalVariable { Type: { } leftType }
            && leftType == types.SystemBooleanType;
        var rightIsBoolean = instruction.Operands[2] is LocalVariable { Type: { } rightType }
            && rightType == types.SystemBooleanType;
        if (!leftIsBoolean && !rightIsBoolean)
            return false;

        static bool IsBooleanOrBit(IOperand operand, bool isBoolean) =>
            isBoolean || operand is Immediate { Value: 0 or 1 };

        return IsBooleanOrBit(instruction.Operands[1], leftIsBoolean)
            && IsBooleanOrBit(instruction.Operands[2], rightIsBoolean)
            && SetTypeIfUnknown(destination, types.SystemBooleanType);
    }

    private static bool PropagateNumericConversion(Instruction instruction, SystemTypesContext types)
    {
        if (instruction.Operands is not [var destinationOperand, var sourceOperand, NumericConversion conversion])
            return false;

        var (sourceType, destinationType) = NumericConversionTypes.Get(conversion.Kind, types);

        var changed = false;
        if (sourceOperand is LocalVariable source)
            changed |= SetTypeIfUnknown(source, sourceType);
        if (destinationOperand is LocalVariable destination)
            changed |= SetTypeIfUnknown(destination, destinationType);
        return changed;
    }

    private static TypeAnalysisContext? IntegerResultType(IOperand operand, MethodAnalysisContext method)
    {
        var type = operand switch
        {
            LocalVariable { Type: { } localType } => localType,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

        return type?.FullName switch
        {
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.Char" => method.AppContext.SystemTypes.SystemInt32Type,
            "System.UInt32" => method.AppContext.SystemTypes.SystemUInt32Type,
            "System.Int64" => method.AppContext.SystemTypes.SystemInt64Type,
            "System.UInt64" => method.AppContext.SystemTypes.SystemUInt64Type,
            _ => null,
        };
    }

    private static TypeAnalysisContext? FloatOperandType(IOperand operand, MethodAnalysisContext method) =>
        operand switch
        {
            FloatLiteral => method.AppContext.SystemTypes.SystemSingleType,
            DoubleLiteral => method.AppContext.SystemTypes.SystemDoubleType,
            LocalVariable { Type: { FullName: "System.Single" } single } => single,
            LocalVariable { Type: { FullName: "System.Double" } @double } => @double,
            _ => null,
        };

    private static bool PropagateMove(Instruction move, int pointerSize)
    {
        var destination = move.Operands[0];
        var source = move.Operands[1];

        // Move local, local: copy a known type in whichever direction is missing it.
        if (destination is LocalVariable destLocal && source is LocalVariable sourceLocal)
            return SetTypeIfUnknown(destLocal, sourceLocal.Type) || SetTypeIfUnknown(sourceLocal, destLocal.Type);

        // Move local, field: a field load types its result with the field's type. This is the edge
        // that lets the loaded value go on to be the base of a further field access.
        if (destination is LocalVariable loadDest && source is FieldReference loadField)
            return SetTypeIfUnknown(loadDest, loadField.Field.FieldType);

        // Move field, local: a field store types the stored value with the field's type.
        if (destination is FieldReference storeField && source is LocalVariable storeSource)
            return SetTypeIfUnknown(storeSource, storeField.Field.FieldType);

        // An element of T[] is a T, whether we loaded it (reference arrays) or only computed its address
        if (destination is LocalVariable { Type: null } elementDest
            && source is MemoryOperand { Base: LocalVariable { Type: SzArrayTypeAnalysisContext { ElementType: { } elementType } } } elementAccess
            && (elementAccess.Index != null || elementAccess.Addend >= 4L * pointerSize))
            return SetTypeIfUnknown(elementDest, elementType);

        // Move local, [byref]: dereferencing a managed pointer to a reference type yields that referent
        // (a struct byref accesses fields directly with no deref, so this only fires for class referents).
        if (destination is LocalVariable { Type: null } derefDest
            && source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { IsValueType: false } referent } } })
            return SetTypeIfUnknown(derefDest, referent);

        // Move local, [obj]: offset 0 of a reference-typed value is its klass pointer.
        if (destination is LocalVariable { Type: null } klassDest
            && source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable { Type: { } baseType } }
            && baseType is not (RuntimeClassTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext or ByRefTypeAnalysisContext)
            && !baseType.IsValueType)
            return SetTypeIfUnknown(klassDest, new RuntimeClassTypeAnalysisContext(baseType, baseType.DeclaringAssembly));

        return false;
    }

    // Absolute native reads which could not be resolved are emitted as typed defaults. A local
    // initially typed as object can later flow into a more specific reference field or call
    // parameter; preserve that known type so the generated placeholder is null (and the C# output
    // remains typed) instead of an IntPtr zero assigned through an object local. SSA gives each
    // candidate local a single definition, and conflicting use types are deliberately left alone.
    private static bool RefineAbsoluteMemoryPlaceholderTypes(List<Instruction> instructions)
    {
        var roots = new Dictionary<LocalVariable, LocalVariable>();
        var copies = new List<(LocalVariable Destination, LocalVariable Source)>();
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable destination, var source] })
                continue;

            if (source is MemoryOperand { IsConstant: true })
                roots[destination] = destination;
            else if (source is LocalVariable sourceLocal)
                copies.Add((destination, sourceLocal));
        }

        // Copies retain the unresolved load's provenance, even when the address is forwarded through
        // one or more SSA locals before reaching a typed field or call parameter.
        var rootsChanged = true;
        while (rootsChanged)
        {
            rootsChanged = false;
            foreach (var copy in copies)
            {
                if (!roots.ContainsKey(copy.Destination) && roots.TryGetValue(copy.Source, out var root))
                {
                    roots.Add(copy.Destination, root);
                    rootsChanged = true;
                }
            }
        }

        var candidateTypes = new Dictionary<LocalVariable, TypeAnalysisContext>();
        var conflictingTypes = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, LocalVariable source] })
                AddCandidate(source, field.Field.FieldType);

            if (!instruction.IsCall
                || instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            var thisParamIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            if (!calledMethod.IsStatic
                && calledMethod.DeclaringType is { } receiverType
                && thisParamIndex < instruction.Operands.Count
                && instruction.Operands[thisParamIndex] is LocalVariable receiver)
                AddCandidate(receiver, receiverType);

            var parameterOffset = calledMethod.IsStatic ? 1 : 2;
            if (instruction.OpCode == OpCode.Call)
                parameterOffset++;
            for (var i = parameterOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - parameterOffset;
                if (parameterIndex >= calledMethod.Parameters.Count
                    || calledMethod.Parameters[parameterIndex].ParameterType is not { } parameterType)
                    continue;

                if (parameterType is not ByRefTypeAnalysisContext
                    && instruction.Operands[i] is LocalVariable argument)
                    AddCandidate(argument, parameterType);
            }
        }

        var changed = false;
        foreach (var mapping in roots)
        {
            var local = mapping.Key;
            var root = mapping.Value;
            if (conflictingTypes.Contains(root)
                || !candidateTypes.TryGetValue(root, out var type)
                || (local.Type != null && local.Type.FullName != "System.Object"))
                continue;

            if (local.Type == null || local.Type.FullName == "System.Object")
            {
                local.Type = type;
                changed = true;
            }
        }

        return changed;

        void AddCandidate(LocalVariable local, TypeAnalysisContext type)
        {
            if (!roots.TryGetValue(local, out var root)
                || !CanJoinReferenceTypes(type)
                || type.FullName == "System.Object")
                return;

            if (candidateTypes.TryGetValue(root, out var existingType))
            {
                if (!ReferenceEquals(existingType, type))
                    conflictingTypes.Add(root);
            }
            else
                candidateTypes.Add(root, type);
        }
    }

    // A phi merges values from multiple predecessors. Keep value types exact, but use the nearest
    // common reference base when every input is known. Choosing the first input's concrete type
    // makes inference depend on predecessor order and can resolve fields or calls incorrectly.
    private static bool PropagatePhi(Instruction phi)
    {
        if (phi.Operands.Count < 2 || phi.Operands[0] is not LocalVariable destination)
            return false;

        var inputs = phi.Operands.Skip(1).OfType<LocalVariable>().ToArray();
        if (inputs.Length != phi.Operands.Count - 1 || inputs.Length == 0)
            return false;

        var knownInputs = inputs.Where(input => input.Type != null).ToArray();

        // Forward: require every input to be known. For references, their nearest common class
        // base is a safe join and preserves inherited field/method resolution after a branch.
        if (destination.Type == null)
        {
            if (knownInputs.Length != inputs.Length || knownInputs.Length == 0)
                return false;

            var inputType = knownInputs[0].Type!;
            if (knownInputs.All(input => ReferenceEquals(input.Type, inputType)))
                return SetTypeIfUnknown(destination, inputType);

            if (knownInputs.Any(input => !CanJoinReferenceTypes(input.Type!)))
                return false;

            for (var candidate = inputType; candidate != null; candidate = candidate.BaseType)
            {
                if (!CanJoinReferenceTypes(candidate))
                    continue;

                if (knownInputs.All(input => input.Type == candidate || input.Type!.IsAssignableTo(candidate)))
                    return SetTypeIfUnknown(destination, candidate);
            }

            return false;
        }

        // Backward: a base/interface type on the result can safely type an unknown reference
        // input when every known input is assignable to it. Value and synthetic pointer types
        // still require an exact match to avoid conflating handles with managed objects.
        if (knownInputs.Length == 0)
            return false;

        var destinationType = destination.Type;
        var canPropagateToUnknowns = knownInputs.All(input =>
            ReferenceEquals(input.Type, destinationType)
            || CanJoinReferenceTypes(input.Type!)
                && CanJoinReferenceTypes(destinationType)
                && input.Type!.IsAssignableTo(destinationType));
        if (!canPropagateToUnknowns)
            return false;

        var changed = false;
        foreach (var input in inputs)
            changed |= SetTypeIfUnknown(input, destinationType);
        return changed;
    }

    private static bool CanJoinReferenceTypes(TypeAnalysisContext type) =>
        !type.IsValueType
        && type is not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext or PinnedTypeAnalysisContext
            or RuntimeClassTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext or RuntimeFieldInfoAnalysisContext);

    private static bool PropagateFromCallParameters(MethodAnalysisContext method, List<Instruction> instructions)
    {
        var changed = false;

        // A lea and the call it's passed to are still separate here. The address only gets folded into the
        // call later, so an argument's address-of has to be found through the local carrying it.
        var addressesOf = new Dictionary<LocalVariable, LocalVariable>();
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable pointer
                && instruction.Operands[1] is AddressOf { Target: LocalVariable pointee })
                addressesOf[pointer] = pointee;
        }

        LocalVariable? Addressed(IOperand operand) => operand switch
        {
            AddressOf { Target: LocalVariable direct } => direct,
            LocalVariable local when addressesOf.TryGetValue(local, out var indirect) => indirect,
            _ => null
        };

        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall)
                continue;

            if (instruction.Operands[0] is not MethodAnalysisContext calledMethod)
                continue;

            // Return value: a constructor yields its declaring type, otherwise the declared return type.
            if (instruction.Destination is LocalVariable returnValue)
            {
                var producedType = calledMethod.Name is ".ctor" or ".cctor" ? calledMethod.DeclaringType : calledMethod.ReturnType;

                if (producedType != method.AppContext.SystemTypes.SystemVoidType)
                    changed |= SetTypeIfUnknown(returnValue, producedType);
            }


            // Call operands
            // 0. Target
            // 1. ReturnValue
            // 2. thisParam
            // ... parameters

            // CallVoid operands
            // 0. Target
            // 1. thisParam
            // ... parameters
            var thisParamIndex = instruction.OpCode == OpCode.CallVoid ? 1 : 2;

            // 'this' param
            if (!calledMethod.IsStatic
                && instruction.Operands[thisParamIndex] is LocalVariable thisParam)
            {
                changed |= SetTypeIfUnknown(thisParam, calledMethod.DeclaringType);
            }

            // Value type instance method, first arg is address of value, but we need to type the value
            if (!calledMethod.IsStatic
                && Addressed(instruction.Operands[thisParamIndex]) is { } addressedReceiver
                && calledMethod.DeclaringType is { IsValueType: true } valueType)
            {
                changed |= SetTypeIfUnknown(addressedReceiver, valueType);
            }

            // Remaining arguments map positionally onto the callee's declared parameters.
            var paramOffset = calledMethod.IsStatic ? 1 : 2;
            if (instruction.OpCode == OpCode.Call) // Skip the return value operand
                paramOffset += 1;

            for (var i = paramOffset; i < instruction.Operands.Count; i++)
            {
                var parameterIndex = i - paramOffset;
                if (parameterIndex > calledMethod.Parameters.Count - 1) // Probably MethodInfo*
                    continue;

                var parameterType = calledMethod.Parameters[parameterIndex].ParameterType;

                if (parameterType is ByRefTypeAnalysisContext { ElementType: { } referencedType }
                    && Addressed(instruction.Operands[i]) is { } referenced)
                {
                    changed |= SetTypeIfUnknown(referenced, referencedType);
                    continue;
                }

                if (instruction.Operands[i] is LocalVariable local)
                    changed |= SetTypeIfUnknown(local, parameterType);
            }
        }

        return changed;
    }

    private static void PropagateFromParameters(MethodAnalysisContext method)
    {
        // 'this'
        if (!method.IsStatic)
        {
            var thisLocal = method.ParameterLocals.FirstOrDefault(p => p.IsThis);
            if (thisLocal != null)
                thisLocal.Type = method.DeclaringType is { GenericParameters.Count: > 0 } generic
                    ? new GenericInstanceTypeAnalysisContext(generic, generic.GenericParameters)
                    : method.DeclaringType;
        }

        if (method.ParameterLocals.FirstOrDefault(p => p.IsMethodInfo) is { } methodInfoLocal && method.DeclaringType is { } owner)
            methodInfoLocal.Type = new RuntimeMethodInfoAnalysisContext(method, owner.DeclaringAssembly);

        if (method.Parameters.Count == 0)
            return;

        // Normal params
        var paramIndex = 0;
        foreach (var local in method.ParameterLocals)
        {
            if (local.IsThis || local.IsMethodInfo)
                continue;

            if (paramIndex >= method.Parameters.Count)
                break;

            local.Type = method.Parameters[paramIndex].ParameterType;
            paramIndex++;
        }
    }

    private static void PropagateFromReturn(MethodAnalysisContext method, List<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCode.Return)
                continue;

            if (instruction.Operands.Count == 1 && instruction.Operands[0] is LocalVariable local)
                local.Type = method.ReturnType;
        }
    }
}
