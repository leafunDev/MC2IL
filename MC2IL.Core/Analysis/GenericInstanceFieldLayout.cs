using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

//Resolves field offsets on generic types, which are all 0 in the metadata.
public static class GenericInstanceFieldLayout
{
    public static FieldAnalysisContext? FindFieldAtOffset(TypeAnalysisContext definition, long targetOffset)
        => FindField(definition, targetOffset, null, false, out _);

    public static FieldAnalysisContext? FindFieldAtOffset(GenericInstanceTypeAnalysisContext genericInstance, long targetOffset)
        => FindField(genericInstance.GenericType, targetOffset, genericInstance, false, out _);

    internal static FieldAnalysisContext? FindContainingField(GenericInstanceTypeAnalysisContext genericInstance,
        long targetOffset, out long offsetWithinField)
        => FindField(genericInstance.GenericType, targetOffset, genericInstance, true, out offsetWithinField);

    private static FieldAnalysisContext? FindField(TypeAnalysisContext definition, long targetOffset,
        GenericInstanceTypeAnalysisContext? genericInstance, bool allowInterior, out long offsetWithinField)
    {
        offsetWithinField = 0;
        var pointerSize = definition.AppContext.Binary.PointerSizeBytes;

        // Generic definitions report zero offsets, so sequential layouts are rebuilt from the
        // closed field types. Explicit layouts need per-field offsets and cannot be reconstructed
        // from a generic definition whose offsets were erased.
        if (definition.Definition is { Attributes: var attributes }
            && (attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
            return null;

        var offset = 2L * pointerSize;

        // The derived type's fields begin after its closed base type. Generic base classes also
        // lose their offsets in metadata, so compute their size recursively and resolve inherited
        // fields against the closed base instance first.
        if (!definition.IsValueType && GetBaseType(definition, genericInstance) is { } baseType)
        {
            if (FindFieldInTypeHierarchy(baseType, targetOffset) is { } inheritedField)
                return inheritedField;

            if (GetInstanceDataSize(baseType, pointerSize, []) is not { } baseSize)
                return null;

            offset += baseSize;
        }

        var pack = PackingLimit(definition);

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            // Generic definitions report zero offsets, so size fields using the closed type's
            // substituted argument (for example T=int is 4 bytes, not pointer-sized).
            var fieldType = genericInstance == null
                ? field.FieldType
                : new ConcreteGenericFieldAnalysisContext(field, genericInstance).FieldType;

            if (GetSizeAndAlignment(fieldType, pointerSize, []) is not var (size, alignment))
                return null;

            alignment = Math.Min(alignment, pack);
            offset = (offset + alignment - 1) & ~(alignment - 1);

            if (offset == targetOffset || allowInterior && targetOffset > offset && targetOffset - offset < size)
            {
                offsetWithinField = targetOffset - offset;
                return genericInstance == null ? field : new ConcreteGenericFieldAnalysisContext(field, genericInstance);
            }

            offset += size;
        }

        return null;
    }

    private static FieldAnalysisContext? FindFieldInTypeHierarchy(TypeAnalysisContext type, long targetOffset)
    {
        if (type is GenericInstanceTypeAnalysisContext genericInstance)
            return FindFieldAtOffset(genericInstance, targetOffset);

        for (var candidate = type; candidate != null; candidate = GetBaseType(candidate, null))
        {
            if (candidate is GenericInstanceTypeAnalysisContext genericBase
                && FindFieldAtOffset(genericBase, targetOffset) is { } genericField)
                return genericField;

            var field = candidate.Fields.FirstOrDefault(f => !f.IsStatic
                && (f.Attributes & FieldAttributes.Literal) == 0
                && f.BackingData?.FieldOffset == targetOffset);
            if (field != null)
                return field;
        }

        return null;
    }

    private static long? GetInstanceDataSize(TypeAnalysisContext type, int pointerSize,
        HashSet<TypeAnalysisContext> visiting)
    {
        var knownSize = TypeSizes.UnboxedSize(type, pointerSize);
        if (knownSize > 0)
            return knownSize;

        if (!visiting.Add(type))
            return null;

        try
        {
            var genericInstance = type as GenericInstanceTypeAnalysisContext;
            var definition = genericInstance?.GenericType ?? type;
            if (definition.Definition is { Attributes: var attributes }
                && (attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return null;

            var offset = 2L * pointerSize;
            if (!definition.IsValueType && GetBaseType(definition, genericInstance) is { } baseType)
            {
                if (GetInstanceDataSize(baseType, pointerSize, new HashSet<TypeAnalysisContext>(visiting)) is not { } baseSize)
                    return null;

                offset += baseSize;
            }

            var pack = PackingLimit(definition);
            long maxAlignment = 1;

            foreach (var field in definition.Fields)
            {
                if (field.IsStatic)
                    continue;

                var fieldType = genericInstance == null
                    ? field.FieldType
                    : new ConcreteGenericFieldAnalysisContext(field, genericInstance).FieldType;

                if (GetSizeAndAlignment(fieldType, pointerSize, new HashSet<TypeAnalysisContext>(visiting))
                    is not var (fieldSize, fieldAlignment))
                    return null;

                fieldAlignment = Math.Min(fieldAlignment, pack);
                offset = AlignUp(offset, fieldAlignment) + fieldSize;
                maxAlignment = Math.Max(maxAlignment, fieldAlignment);
            }

            var objectHeaderSize = 2L * pointerSize;
            return AlignUp(offset, Math.Min(maxAlignment, pack)) - objectHeaderSize;
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static TypeAnalysisContext? GetBaseType(TypeAnalysisContext definition,
        GenericInstanceTypeAnalysisContext? genericInstance)
    {
        var baseType = genericInstance?.GenericType.BaseType ?? definition.BaseType;
        return baseType != null && genericInstance != null
            ? GenericInstantiation.Instantiate(baseType, genericInstance.GenericArguments, [])
            : baseType;
    }

    private static (long Size, long Alignment)? GetSizeAndAlignment(TypeAnalysisContext fieldType, int pointerSize,
        HashSet<TypeAnalysisContext> visiting)
    {
        if (fieldType is GenericParameterTypeAnalysisContext genericParameter)
        {
            // An unconstrained or value-type generic parameter can occupy a concrete inline
            // layout of any size. Only a reference-type constraint proves pointer-sized storage.
            return (genericParameter.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0
                ? (pointerSize, pointerSize)
                : null;
        }

        if (fieldType is PointerTypeAnalysisContext || !fieldType.IsValueType)
            return (pointerSize, pointerSize);

        if (fieldType.IsEnumType && fieldType.Fields.FirstOrDefault(f => !f.IsStatic) is { } underlying)
            return GetSizeAndAlignment(underlying.FieldType, pointerSize, visiting);

        var primitive = fieldType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => (1L, 1L),
            "System.Int16" or "System.UInt16" or "System.Char" => (2L, 2L),
            "System.Int32" or "System.UInt32" or "System.Single" => (4L, 4L),
            "System.Int64" or "System.UInt64" or "System.Double" => (8L, 8L),
            "System.IntPtr" or "System.UIntPtr" => ((long)pointerSize, pointerSize),
            _ => (0L, 0L)
        };

        if (primitive.Item1 != 0)
            return primitive;

        if (!visiting.Add(fieldType))
            return null;

        try
        {
            var genericInstance = fieldType as GenericInstanceTypeAnalysisContext;
            var definition = genericInstance?.GenericType ?? fieldType;
            var fields = definition.Fields.Where(f => !f.IsStatic).ToList();
            var declaredSize = TypeSizes.UnboxedSize(fieldType, pointerSize);

            if (fields.Count == 0)
                return declaredSize > 0 ? (declaredSize, 1) : null;

            var pack = PackingLimit(definition);
            long size = 0;
            long maxAlignment = 1;

            foreach (var field in fields)
            {
                var concreteFieldType = genericInstance == null
                    ? field.FieldType
                    : new ConcreteGenericFieldAnalysisContext(field, genericInstance).FieldType;

                if (GetSizeAndAlignment(concreteFieldType, pointerSize, new HashSet<TypeAnalysisContext>(visiting))
                    is not var (fieldSize, fieldAlignment))
                    return null;

                fieldAlignment = Math.Min(fieldAlignment, pack);
                size = AlignUp(size, fieldAlignment) + fieldSize;
                maxAlignment = Math.Max(maxAlignment, fieldAlignment);
            }

            maxAlignment = Math.Min(maxAlignment, pack);
            size = declaredSize > 0 ? declaredSize : AlignUp(size, maxAlignment);
            return size > 0 ? (size, maxAlignment) : null;
        }
        finally
        {
            visiting.Remove(fieldType);
        }
    }

    private static long PackingLimit(TypeAnalysisContext type)
    {
        var layoutType = type is GenericInstanceTypeAnalysisContext genericInstance
            ? genericInstance.GenericType
            : type;

        // CLI's default packing limit is 8. A zero packing value has the same default meaning.
        var pack = layoutType.Definition is { PackingSizeIsDefault: false, PackingSize: > 0 } definition
            ? definition.PackingSize
            : 8u;

        return pack;
    }

    private static long AlignUp(long value, long alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
