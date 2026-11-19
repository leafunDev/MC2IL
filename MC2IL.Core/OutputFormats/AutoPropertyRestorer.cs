using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils.AsmResolver;

namespace MC2IL.Core.OutputFormats;

/// <summary>
/// Reconstructs high-level auto-property accesses in recovered managed assemblies.
/// The backing field remains as storage; only confidently identified call sites are rewritten.
/// </summary>
internal static class AutoPropertyRestorer
{
    private const string CompilerGeneratedAttributeName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private sealed record AutoProperty(
        MethodDefinition? Getter,
        MethodDefinition? Setter,
        bool GetterIsTrivial,
        bool SetterIsTrivial);

    public static void Run(ApplicationAnalysisContext appContext, IReadOnlyCollection<AssemblyDefinition> assemblies)
    {
        var runtimeContext = appContext.GetExtraData<RuntimeContext>("AsmResolverRuntimeContext");
        if (runtimeContext == null)
            return;

        var propertiesByField = new Dictionary<FieldDefinition, AutoProperty>();
        var accessorMethods = new HashSet<MethodDefinition>();
        var fieldSetters = new Dictionary<FieldDefinition, MethodDefinition?>();
        var compilerGeneratedConstructor = GetCompilerGeneratedConstructor(appContext);
        var markedFields = 0;

        foreach (var type in EnumerateTypes(assemblies))
        {
            foreach (var property in type.Properties)
            {
                if (property.Signature is not { ParameterTypes.Count: 0 } propertySignature)
                    continue;

                var backingFieldName = $"<{property.Name}>k__BackingField";
                var backingField = type.Fields.FirstOrDefault(field => field.IsPrivate && field.Name == backingFieldName);
                if (backingField?.Signature is not { } fieldSignature
                    || !runtimeContext.SignatureComparer.Equals(fieldSignature.FieldType, propertySignature.ReturnType))
                    continue;

                var getter = property.Semantics
                    .FirstOrDefault(semantics => semantics.Attributes == MethodSemanticsAttributes.Getter)?.Method;
                var setter = property.Semantics
                    .FirstOrDefault(semantics => semantics.Attributes == MethodSemanticsAttributes.Setter)?.Method;

                if (getter == null && setter == null)
                    continue;

                var getterIsTrivial = getter != null && IsTrivialGetter(getter, backingField, runtimeContext);
                var setterIsTrivial = setter != null && IsTrivialSetter(setter, backingField, runtimeContext);
                if (!getterIsTrivial && !setterIsTrivial)
                    continue;

                if (backingField.IsStatic)
                {
                    if (getterIsTrivial)
                        NormalizeStaticAccessor(getter!);
                    if (setterIsTrivial)
                        NormalizeStaticAccessor(setter!);
                }

                propertiesByField[backingField] = new AutoProperty(getter, setter, getterIsTrivial, setterIsTrivial);

                if (getter != null)
                    accessorMethods.Add(getter);
                if (setter != null)
                    accessorMethods.Add(setter);

                // Some C# decompilers also require the marker on the accessors before
                // they can turn their bodies into get; and set;.
                if (compilerGeneratedConstructor != null
                    && (getter == null || getterIsTrivial)
                    && (setter == null || setterIsTrivial))
                {
                    if (MarkCompilerGenerated(backingField, compilerGeneratedConstructor))
                        markedFields++;
                    if (getter != null)
                        MarkCompilerGenerated(getter, compilerGeneratedConstructor);
                    if (setter != null)
                        MarkCompilerGenerated(setter, compilerGeneratedConstructor);
                }
            }
        }

        RegisterUnityButtonGetter(appContext, propertiesByField, accessorMethods);

        var restoredReads = 0;
        var restoredWrites = 0;
        var fieldAccessesScanned = 0;
        var fieldReferencesResolved = 0;
        var candidateFieldAccesses = 0;

        foreach (var type in EnumerateTypes(assemblies))
        {
            foreach (var method in type.Methods)
            {
                if (method.CilMethodBody == null
                    || method.Name?.ToString() is ".ctor" or ".cctor"
                    || accessorMethods.Contains(method))
                    continue;

                var instructions = method.CilMethodBody.Instructions;
                for (var i = 0; i < instructions.Count; i++)
                {
                    var instruction = instructions[i];
                    var code = instruction.OpCode.Code;
                    var isRead = code is CilCode.Ldfld or CilCode.Ldsfld;
                    var isWrite = code is CilCode.Stfld or CilCode.Stsfld;
                    if (!isRead && !isWrite)
                        continue;

                    fieldAccessesScanned++;
                    if (instruction.Operand is not IFieldDescriptor fieldDescriptor
                        || ResolveField(fieldDescriptor, runtimeContext) is not { } field)
                        continue;

                    fieldReferencesResolved++;
                    var isStaticOperation = code is CilCode.Ldsfld or CilCode.Stsfld;
                    if (isStaticOperation != field.IsStatic
                        || method.DeclaringType is not { } callerType
                        || (i > 0 && instructions[i - 1].OpCode.OpCodeType == CilOpCodeType.Prefix))
                        continue;

                    MethodDefinition? accessor;
                    if (propertiesByField.TryGetValue(field, out var autoProperty))
                    {
                        accessor = isRead
                            ? autoProperty.GetterIsTrivial ? autoProperty.Getter : null
                            : autoProperty.SetterIsTrivial ? autoProperty.Setter : null;
                    }
                    else if (isWrite && !field.IsStatic && field.IsPrivate && field.DeclaringType != callerType)
                    {
                        if (!fieldSetters.TryGetValue(field, out accessor))
                            fieldSetters[field] = accessor = FindTrivialSetter(field, runtimeContext);
                    }
                    else
                        continue;

                    candidateFieldAccesses++;
                    if (accessor == null || (accessor.IsVirtual && !accessor.IsFinal)
                        || method.DeclaringModule is not { } callerModule
                        || !accessor.IsAccessibleFromType(callerType, runtimeContext))
                        continue;

                    var accessorDescriptor = ToAccessorDescriptor(accessor, fieldDescriptor);
                    if (accessorDescriptor == null)
                        continue;

                    instruction.OpCode = accessor.IsStatic
                        ? CilOpCodes.Call
                        : field.DeclaringType?.IsValueType == true
                            ? CilOpCodes.Call
                            : CilOpCodes.Callvirt;
                    instruction.Operand = callerModule.DefaultImporter.ImportMethod(accessorDescriptor);

                    if (isRead)
                        restoredReads++;
                    else
                        restoredWrites++;
                }
            }
        }

        Logger.InfoNewline(
            $"Recognized {propertiesByField.Count} simple auto-property backing fields; "
            + $"rewrote {restoredReads} reads and {restoredWrites} writes from {candidateFieldAccesses} matching accesses "
            + $"({fieldReferencesResolved}/{fieldAccessesScanned} field references resolved); "
            + $"marked {markedFields} fields as compiler-generated. "
            + "Backing storage is retained.",
            "AutoPropertyRestorer");
    }

    private static void RegisterUnityButtonGetter(ApplicationAnalysisContext appContext,
        Dictionary<FieldDefinition, AutoProperty> propertiesByField, HashSet<MethodDefinition> accessorMethods)
    {
        if (appContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var button = appContext.GetAssemblyByName("UnityEngine.UI")?.GetTypeByFullName("UnityEngine.UI.Button");
        var field = button?.Fields.FirstOrDefault(candidate => candidate.Name == "m_OnClick");
        var getter = button?.Properties.FirstOrDefault(property => property.Name == "onClick")?.Getter;
        if (field?.BackingData?.FieldOffset is not >= 0
            || getter is not { IsStatic: false, Parameters.Count: 0, UnderlyingPointer: > 0 }
            || getter.ReturnType != field.FieldType
            || field.ToFieldDescriptor() is not FieldDefinition managedField
            || getter.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } managedGetter)
            return;

        // Unity method bodies are stubbed in DLL recovery. Check the original getter instead.
        List<Instruction> native;
        try
        {
            native = appContext.InstructionSet.GetIsilFromMethod(getter);
        }
        catch
        {
            return;
        }

        if (native is [
            { OpCode: OpCode.Move, Operands: [Register result, MemoryOperand { Base: Register receiver, Index: null, Scale: 0 } load] },
            { OpCode: OpCode.Return, Operands: [Register returned] }]
            && receiver.Name == "X0" && returned == result && load.Addend == field.BackingData.FieldOffset)
        {
            propertiesByField[managedField] = new AutoProperty(managedGetter, null, true, false);
            accessorMethods.Add(managedGetter);
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<AssemblyDefinition> assemblies)
    {
        foreach (var assembly in assemblies)
        foreach (var module in assembly.Modules)
        foreach (var type in module.TopLevelTypes)
        foreach (var nestedType in EnumerateTypeAndNested(type))
            yield return nestedType;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypeAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var descendant in EnumerateTypeAndNested(nested))
            yield return descendant;
    }

    private static FieldDefinition? ResolveField(IFieldDescriptor descriptor, RuntimeContext runtimeContext)
    {
        if (descriptor is FieldDefinition field)
            return field;

        if (descriptor is MemberReference memberReference
            && memberReference.TryResolve(runtimeContext, out var definition)
            && definition is FieldDefinition resolvedField)
            return resolvedField;

        return null;
    }

    private static IMethodDescriptor? ToAccessorDescriptor(MethodDefinition accessor, IFieldDescriptor field)
    {
        if (field is FieldDefinition)
            return accessor;

        if (field.DeclaringType is not IMemberRefParent declaringType)
            return null;

        // A generic field reference is scoped to the closed declaring type. Give its property
        // accessor the same type scope so !0 substitutions remain correct in the emitted CIL.
        return new MemberReference(declaringType, accessor.Name, accessor.Signature!);
    }

    private static bool IsTrivialGetter(MethodDefinition getter, FieldDefinition field, RuntimeContext runtimeContext)
    {
        if (getter.IsStatic != field.IsStatic || getter.Parameters.Count != 0 || !HasSimpleBody(getter))
            return false;

        var instructions = SignificantInstructions(getter);
        if (getter.IsStatic)
            return instructions.Length == 2
                   && IsFieldInstruction(instructions[0], CilCode.Ldsfld, field, runtimeContext)
                   && instructions[1].OpCode.Code == CilCode.Ret;

        return instructions.Length == 3
               && instructions[0].OpCode.Code == CilCode.Ldarg_0
               && IsFieldInstruction(instructions[1], CilCode.Ldfld, field, runtimeContext)
               && instructions[2].OpCode.Code == CilCode.Ret;
    }

    private static bool IsTrivialSetter(MethodDefinition setter, FieldDefinition field, RuntimeContext runtimeContext)
    {
        if (setter.IsStatic != field.IsStatic || setter.Parameters.Count != 1 || !HasSimpleBody(setter))
            return false;

        var instructions = SignificantInstructions(setter);
        if (setter.IsStatic)
            return instructions.Length == 3
                   && IsValueParameterLoad(instructions[0], setter, isInstance: false)
                   && IsFieldInstruction(instructions[1], CilCode.Stsfld, field, runtimeContext)
                   && instructions[2].OpCode.Code == CilCode.Ret;

        if (instructions.Length == 6
            && IsValueParameterLoad(instructions[0], setter, isInstance: true)
            && instructions[1].OpCode.Code == CilCode.Stloc
            && instructions[2].OpCode.Code == CilCode.Ldarg_0
            && instructions[3].OpCode.Code == CilCode.Ldloc
            && ReferenceEquals(instructions[1].Operand, instructions[3].Operand))
            return IsFieldInstruction(instructions[4], CilCode.Stfld, field, runtimeContext)
                   && instructions[5].OpCode.Code == CilCode.Ret;

        return instructions.Length == 4
               && instructions[0].OpCode.Code == CilCode.Ldarg_0
               && IsValueParameterLoad(instructions[1], setter, isInstance: true)
               && IsFieldInstruction(instructions[2], CilCode.Stfld, field, runtimeContext)
               && instructions[3].OpCode.Code == CilCode.Ret;
    }

    private static MethodDefinition? FindTrivialSetter(FieldDefinition field, RuntimeContext runtimeContext)
    {
        if (field.DeclaringType is not { } owner || field.Name?.ToString() is not { } name
            || !name.StartsWith("m_", StringComparison.Ordinal) || field.Signature is not { } fieldSignature)
            return null;

        var setters = owner.Methods.Where(candidate => candidate.Name?.ToString() == "Set" + name[2..]
            && !candidate.IsVirtual
            && candidate.Signature is { ParameterTypes.Count: 1 } signature
            && signature.ReturnType.FullName == "System.Void"
            && runtimeContext.SignatureComparer.Equals(signature.ParameterTypes[0], fieldSignature.FieldType)
            && IsTrivialSetter(candidate, field, runtimeContext)).Take(2).ToArray();
        return setters is [var setter] ? setter : null;
    }

    // IL generation may leave unused locals in the signature. The exact opcode
    // sequences checked above already reject all reads or writes to locals.
    private static bool HasSimpleBody(MethodDefinition method) => method.CilMethodBody is
        { ExceptionHandlers.Count: 0 };

    private static CilInstruction[] SignificantInstructions(MethodDefinition method)
    {
        var body = method.CilMethodBody!.Instructions;
        var instructions = new List<CilInstruction>();
        for (var i = 0; i < body.Count; i++)
        {
            var instruction = body[i];
            if (instruction.OpCode.Code == CilCode.Nop)
                continue;

            if (method.IsStatic && instruction.OpCode.Code is CilCode.Br or CilCode.Br_S
                && instruction.Operand is CilInstructionLabel { Instruction: { } target })
            {
                // Removing a native initialization guard can leave a jump over nops.
                // Ignore it only when the target is on the very same fallthrough path.
                var next = i + 1;
                while (next < body.Count && body[next].OpCode.Code == CilCode.Nop && body[next] != target)
                    next++;
                if (next < body.Count && body[next] == target)
                    continue;
            }

            instructions.Add(instruction);
        }

        return instructions.ToArray();
    }

    private static void NormalizeStaticAccessor(MethodDefinition method)
    {
        var body = method.CilMethodBody!;
        var instructions = SignificantInstructions(method);
        // Both verified static accessor shapes consist solely of argument/field loads,
        // a possible field store and ret. They reference neither labels nor local slots.
        body.Instructions.Clear();
        foreach (var instruction in instructions)
            body.Instructions.Add(instruction);
        body.LocalVariables.Clear();
    }

    private static bool IsFieldInstruction(
        CilInstruction instruction, CilCode expectedCode, FieldDefinition expectedField, RuntimeContext runtimeContext) =>
        instruction.OpCode.Code == expectedCode
        && instruction.Operand is IFieldDescriptor descriptor
        && ReferenceEquals(ResolveField(descriptor, runtimeContext), expectedField);

    private static bool IsValueParameterLoad(CilInstruction instruction, MethodDefinition method, bool isInstance)
    {
        var parameter = method.Parameters[0];
        if (instruction.OpCode.Code == (isInstance ? CilCode.Ldarg_1 : CilCode.Ldarg_0))
            return true;

        return instruction.OpCode.Code is CilCode.Ldarg or CilCode.Ldarg_S
               && ReferenceEquals(instruction.Operand, parameter);
    }

    private static MethodDefinition? GetCompilerGeneratedConstructor(ApplicationAnalysisContext appContext) =>
        appContext.GetAssemblyByName("mscorlib")
            ?.GetTypeByFullName(CompilerGeneratedAttributeName)
            ?.Methods.FirstOrDefault(method => method.Name == ".ctor" && method.Parameters.Count == 0)
            ?.GetExtraData<MethodDefinition>("AsmResolverMethod");

    private static bool MarkCompilerGenerated(IHasCustomAttribute member, MethodDefinition constructor)
    {
        if (member.CustomAttributes.Any(attribute =>
                attribute.Constructor?.DeclaringType?.FullName == CompilerGeneratedAttributeName))
            return false;

        member.CustomAttributes.Add(new CustomAttribute(constructor, new()));
        return true;
    }
}
