using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.ISIL;

internal readonly record struct DefaultValue(TypeAnalysisContext Type) : IOperand
{
    public override string ToString() => $"default({Type.FullName})";
}
