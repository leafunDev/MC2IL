using MC2IL.Core.Utils;
using LibMC2IL.BinaryStructures;

namespace MC2IL.Core.Model.Contexts;

public class PinnedTypeAnalysisContext(TypeAnalysisContext elementType)
    : WrappedTypeAnalysisContext(elementType)
{
    public PinnedTypeAnalysisContext(Il2CppType rawType, ApplicationAnalysisContext context)
        : this(context.ResolveIl2CppType(rawType.GetEncapsulatedType()))
    {
    }

    public sealed override Il2CppTypeEnum Type => Il2CppTypeEnum.IL2CPP_TYPE_PINNED;

    public sealed override string DefaultName => ElementType.DefaultName;

    public sealed override string? OverrideName
    {
        get => ElementType.OverrideName;
        set => ElementType.OverrideName = value;
    }

    public sealed override bool IsValueType => false;
}
