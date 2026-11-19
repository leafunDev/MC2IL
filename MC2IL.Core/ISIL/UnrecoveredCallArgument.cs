using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.ISIL;

/// <summary>
/// Marks a call argument that was passed on the native stack but was not captured in the raw
/// register argument list. IL recovery emits the typed default and records a warning for it.
/// </summary>
public sealed record UnrecoveredCallArgument(TypeAnalysisContext ExpectedType) : IOperand
{
    public override string ToString() => $"unrecovered stack argument ({ExpectedType.FullName})";
}
