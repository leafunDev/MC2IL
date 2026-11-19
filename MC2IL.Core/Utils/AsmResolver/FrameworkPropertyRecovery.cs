using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils.AsmResolver;

internal static class FrameworkPropertyRecovery
{
    private sealed record CollectionCountLayout(
        string TypeName,
        int GenericArity,
        string CountFieldName,
        string? RemovedCountFieldName,
        string AssemblyName,
        string? DirectReadFieldName);

    private static readonly CollectionCountLayout[] CollectionCountLayouts =
    [
        new("List`1", 1, "_size", null, "mscorlib", "_size"),
        new("Queue`1", 1, "_size", null, "mscorlib", "_size"),
        new("Stack`1", 1, "_size", null, "mscorlib", "_size"),
        // Mono's HashSet count is the number of active values. Its _lastIndex is a
        // high-water mark and must not be used as Count.
        new("HashSet`1", 1, "_count", null, "System.Core", "_count"),
        // Dictionary._count includes deleted slots; its Count is _count - _freeCount.
        // Keep the individual fields intact in callers because neither one maps alone.
        new("Dictionary`2", 2, "_count", "_freeCount", "mscorlib", null)
    ];

    public static IMethodDescriptor? GetGetterForRead(FieldReference field, MethodDefinition caller)
    {
        if (field.Parent != null
            || field.Field is not ConcreteGenericFieldAnalysisContext
            {
                DeclaringType: GenericInstanceTypeAnalysisContext instance
            } concreteField
            || !ReferenceEquals(concreteField.BaseFieldContext.DeclaringType, instance.GenericType))
            return null;

        if (IsNullableHasValueField(instance.GenericType, concreteField.BaseFieldContext)
            && !IsInsideDefinition(caller, instance.GenericType)
            && GetHasValueGetter(instance.GenericType) is { } hasValueGetter)
            return GetClosedGetter(instance, hasValueGetter);

        var layout = GetCollectionCountLayout(instance.GenericType);
        if (layout?.DirectReadFieldName != concreteField.BaseFieldContext.DefaultName
            || GetCountStorage(instance.GenericType, layout.CountFieldName) != concreteField.BaseFieldContext
            || GetCountGetter(instance.GenericType) is not { } countGetter
            || IsInsideDefinition(caller, instance.GenericType))
            return null;

        return GetClosedGetter(instance, countGetter);
    }

    public static bool TryEmitGetter(MethodAnalysisContext context, MethodDefinition method)
    {
        if (context.DeclaringType is not { } type)
            return false;

        if (GetCollectionCountLayout(type) is { } layout
            && context == GetCountGetter(type)
            && GetCountStorage(type, layout.CountFieldName) is { } countField)
        {
            var removedCountField = layout.RemovedCountFieldName is { } removedFieldName
                ? GetCountStorage(type, removedFieldName)
                : null;
            if (layout.RemovedCountFieldName != null && removedCountField == null)
                return false;

            var instructions = method.CilMethodBody!.Instructions;
            instructions.Add(CilOpCodes.Ldarg_0);
            instructions.Add(CilOpCodes.Ldfld, MakeClosedField(type, countField));

            if (removedCountField != null)
            {
                instructions.Add(CilOpCodes.Ldarg_0);
                instructions.Add(CilOpCodes.Ldfld, MakeClosedField(type, removedCountField));
                instructions.Add(CilOpCodes.Sub);
            }

            instructions.Add(CilOpCodes.Ret);
            return true;
        }

        if (IsNullableType(type) && context == GetHasValueGetter(type)
            && GetNullableHasValueField(type) is { } hasValueField)
        {
            var instructions = method.CilMethodBody!.Instructions;
            instructions.Add(CilOpCodes.Ldarg_0);
            instructions.Add(CilOpCodes.Ldfld, MakeClosedField(type, hasValueField));
            instructions.Add(CilOpCodes.Ret);
            return true;
        }

        return false;
    }

    private static IMethodDescriptor GetClosedGetter(
        GenericInstanceTypeAnalysisContext instance,
        MethodAnalysisContext getter) =>
        new ConcreteGenericMethodAnalysisContext(getter, instance.GenericArguments, []).ToMethodDescriptor();

    private static IFieldDescriptor MakeClosedField(TypeAnalysisContext owner, FieldAnalysisContext field) =>
        new ConcreteGenericFieldAnalysisContext(field, owner.GenericParameters).ToFieldDescriptor();

    private static CollectionCountLayout? GetCollectionCountLayout(TypeAnalysisContext type)
    {
        if (type.DefaultNamespace != "System.Collections.Generic"
            || type.DeclaringType != null
            || type.Definition == null)
            return null;

        var layout = CollectionCountLayouts.FirstOrDefault(candidate =>
            candidate.TypeName == type.DefaultName && candidate.GenericArity == type.GenericParameters.Count);
        if (layout == null)
            return null;

        var expectedAssembly = type.AppContext.GetAssemblyByName(layout.AssemblyName);
        return expectedAssembly != null && type.DeclaringAssembly == expectedAssembly
            ? layout
            : null;
    }

    private static FieldAnalysisContext? GetCountStorage(TypeAnalysisContext type, string fieldName) =>
        type.Fields.FirstOrDefault(field => field.DefaultName == fieldName
            && !field.IsStatic
            && field.FieldType == type.AppContext.SystemTypes.SystemInt32Type);

    private static MethodAnalysisContext? GetCountGetter(TypeAnalysisContext type)
    {
        var getter = type.Properties.FirstOrDefault(property => property.DefaultName == "Count")?.Getter;
        return getter is
            {
                DefaultName: "get_Count",
                Visibility: MethodAttributes.Public,
                IsStatic: false,
                IsAbstract: false,
                Parameters.Count: 0,
                GenericParameters.Count: 0
            }
            && (!getter.IsVirtual || getter.IsFinal)
            && getter.ReturnType == type.AppContext.SystemTypes.SystemInt32Type
                ? getter
                : null;
    }

    private static bool IsInsideDefinition(MethodDefinition caller, TypeAnalysisContext type)
    {
        var definition = type.GetExtraData<TypeDefinition>("AsmResolverType");
        for (var owner = caller.DeclaringType; owner != null; owner = owner.DeclaringType)
            if (owner == definition)
                return true;

        return false;
    }

    private static bool IsNullableType(TypeAnalysisContext type) =>
        type.DefaultNamespace == "System"
        && type.DefaultName == "Nullable`1"
        && type.DeclaringType == null
        && type.GenericParameters.Count == 1
        && type.DeclaringAssembly == type.AppContext.SystemTypes.SystemObjectType.DeclaringAssembly;

    private static bool IsNullableHasValueField(TypeAnalysisContext type, FieldAnalysisContext field) =>
        IsNullableType(type)
        && ReferenceEquals(field.DeclaringType, type)
        && field.DefaultName == "hasValue"
        && !field.IsStatic
        && field.FieldType == type.AppContext.SystemTypes.SystemBooleanType;

    private static FieldAnalysisContext? GetNullableHasValueField(TypeAnalysisContext type) =>
        type.Fields.FirstOrDefault(field => IsNullableHasValueField(type, field));

    private static MethodAnalysisContext? GetHasValueGetter(TypeAnalysisContext type)
    {
        if (!IsNullableType(type))
            return null;

        var getter = type.Properties.FirstOrDefault(property => property.DefaultName == "HasValue")?.Getter;
        return getter is
            {
                DefaultName: "get_HasValue",
                Visibility: MethodAttributes.Public,
                IsStatic: false,
                IsAbstract: false,
                Parameters.Count: 0,
                GenericParameters.Count: 0
            }
            && getter.ReturnType == type.AppContext.SystemTypes.SystemBooleanType
                ? getter
                : null;
    }
}