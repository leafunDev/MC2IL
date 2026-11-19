using System.Collections.Generic;
using LibMC2IL.BinaryStructures;
using LibMC2IL.Metadata;

#pragma warning disable CS0618 // This static facade intentionally uses LibMC2ILMain.DefaultContext for backwards compatibility

namespace LibMC2IL.Reflection;

public static class LibMC2ILReflection
{
    private static LibMC2ILReflectionCache DefaultCache =>
        LibMC2ILMain.DefaultContext.ReflectionCache;

    public static Dictionary<Il2CppTypeEnum, Il2CppTypeDefinition> PrimitiveTypeDefinitions =>
        DefaultCache.PrimitiveTypeDefinitions;

    internal static void ResetCaches() =>
        DefaultCache.Reset();

    internal static void InitCaches() =>
        DefaultCache.Init(LibMC2ILMain.DefaultContext);

    public static Il2CppTypeDefinition? GetType(string name, string? @namespace = null) =>
        DefaultCache.GetType(name, @namespace);

    public static Il2CppTypeDefinition? GetTypeByFullName(string fullName) =>
        DefaultCache.GetTypeByFullName(fullName);

    public static Il2CppTypeDefinition? GetTypeDefinitionByTypeIndex(Il2CppVariableWidthIndex<Il2CppType> index) =>
        DefaultCache.GetTypeDefinitionByTypeIndex(index);

    public static Il2CppVariableWidthIndex<Il2CppTypeDefinition> GetTypeIndexFromType(Il2CppTypeDefinition typeDefinition) =>
        DefaultCache.GetTypeIndexFromType(typeDefinition);

    public static Il2CppVariableWidthIndex<Il2CppMethodDefinition> GetMethodIndexFromMethod(Il2CppMethodDefinition methodDefinition) =>
        DefaultCache.GetMethodIndexFromMethod(methodDefinition);

    public static Il2CppVariableWidthIndex<Il2CppFieldDefinition> GetFieldIndexFromField(Il2CppFieldDefinition fieldDefinition) =>
        DefaultCache.GetFieldIndexFromField(fieldDefinition);

    public static int GetPropertyIndexFromProperty(Il2CppPropertyDefinition propertyDefinition) =>
        DefaultCache.GetPropertyIndexFromProperty(propertyDefinition);

    public static Il2CppTypeDefinition GetDeclaringTypeFromField(Il2CppFieldDefinition fieldDefinition) =>
        DefaultCache.GetDeclaringTypeFromField(fieldDefinition);

    public static Il2CppType? GetTypeFromDefinition(Il2CppTypeDefinition definition) =>
        DefaultCache.GetTypeFromDefinition(definition);
}
