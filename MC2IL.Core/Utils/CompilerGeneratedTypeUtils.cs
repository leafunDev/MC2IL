using System;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils;

public static class CompilerGeneratedTypeUtils
{
    public static bool IsCompilerGeneratedType(this TypeAnalysisContext type)
    {
        if (type.IsCompilerGeneratedBasedOnCustomAttributes)
            return true;

        var name = type.Name;
        return name is "<>c" or "<PrivateImplementationDetails>"
            || name.StartsWith("<>c`", StringComparison.Ordinal)
            || name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)
            || name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
            || name.StartsWith("<>O", StringComparison.Ordinal)
            || name.StartsWith("<>9", StringComparison.Ordinal)
            || name.StartsWith("<>y__InlineArray", StringComparison.Ordinal)
            || name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)
            || name.StartsWith("<", StringComparison.Ordinal)
                && (name.Contains(">d__", StringComparison.Ordinal)
                    || name.Contains(">c__Iterator", StringComparison.Ordinal)
                    || name.Contains(">c__AnonStorey", StringComparison.Ordinal)
                    || name.Contains(">c__DisplayClass", StringComparison.Ordinal));
    }
}
