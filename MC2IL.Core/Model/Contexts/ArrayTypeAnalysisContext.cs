using System;
using MC2IL.Core.Utils;
using LibMC2IL.BinaryStructures;

namespace MC2IL.Core.Model.Contexts;

public class ArrayTypeAnalysisContext(TypeAnalysisContext elementType, int rank)
    : WrappedTypeAnalysisContext(elementType)
{
    public ArrayTypeAnalysisContext(Il2CppType rawType, ApplicationAnalysisContext context)
        : this(context.ResolveIl2CppType(rawType.GetArrayElementType()), rawType.GetArrayRank())
    {
    }

    public sealed override Il2CppTypeEnum Type => Il2CppTypeEnum.IL2CPP_TYPE_ARRAY;

    public sealed override string DefaultName => $"{ElementType.DefaultName}[{Rank}]";

    public sealed override string? OverrideName
    {
        get => $"{ElementType.Name}[{Rank}]";
        set => throw new NotSupportedException();
    }

    public sealed override bool IsValueType => false;

    public int Rank { get; } = rank;
}
