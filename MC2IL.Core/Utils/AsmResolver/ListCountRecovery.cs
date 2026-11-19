using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils.AsmResolver;

internal static class ListCountRecovery
{
    public static IMethodDescriptor? GetGetterForRead(FieldReference field, MethodDefinition caller)
    {
        if (field.Parent != null
            || field.Field is not ConcreteGenericFieldAnalysisContext
            {
                DeclaringType: GenericInstanceTypeAnalysisContext { GenericArguments.Count: 1 } instance,
                BaseFieldContext: { DefaultName: "_size" } size
            })
            return null;

        var list = instance.GenericType;
        if (!IsCoreList(list) || !IsSizeField(list, size) || GetCountGetter(list) is not { } getter)
            return null;

        // Keep direct access legal and avoid a recursive call inside the List implementation.
        var listDefinition = list.GetExtraData<TypeDefinition>("AsmResolverType");
        for (var owner = caller.DeclaringType; owner != null; owner = owner.DeclaringType)
            if (owner == listDefinition)
                return null;

        return new ConcreteGenericMethodAnalysisContext(getter, instance.GenericArguments, []).ToMethodDescriptor();
    }

    public static bool TryEmitGetter(MethodAnalysisContext context, MethodDefinition method)
    {
        if (context.DeclaringType is not { } list || !IsCoreList(list) || context != GetCountGetter(list)
            || list.Fields.FirstOrDefault(f => IsSizeField(list, f)) is not { } size)
            return false;

        // Core library methods are normally stubbed. Calls restored to Count must read the size.
        var field = new ConcreteGenericFieldAnalysisContext(size, list.GenericParameters).ToFieldDescriptor();
        var instructions = method.CilMethodBody!.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, field);
        instructions.Add(CilOpCodes.Ret);
        return true;
    }

    private static bool IsCoreList(TypeAnalysisContext type) => type is
        { DefaultName: "List`1", DefaultNamespace: "System.Collections.Generic", DeclaringType: null, GenericParameters.Count: 1 }
        && type.DeclaringAssembly == type.AppContext.SystemTypes.SystemObjectType.DeclaringAssembly;

    private static bool IsSizeField(TypeAnalysisContext list, FieldAnalysisContext field) => field.DeclaringType == list
        && field.DefaultName == "_size" && !field.IsStatic && field.FieldType == list.AppContext.SystemTypes.SystemInt32Type;

    private static MethodAnalysisContext? GetCountGetter(TypeAnalysisContext list)
    {
        var getter = list.Properties.FirstOrDefault(p => p.DefaultName == "Count")?.Getter;
        return getter is { Visibility: MethodAttributes.Public, IsStatic: false, IsAbstract: false, Parameters.Count: 0, GenericParameters.Count: 0 }
            && (!getter.IsVirtual || getter.IsFinal)
            && getter.ReturnType == list.AppContext.SystemTypes.SystemInt32Type
                ? getter
                : null;
    }
}
