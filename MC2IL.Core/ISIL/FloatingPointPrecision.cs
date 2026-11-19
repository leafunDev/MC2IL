namespace MC2IL.Core.ISIL;

/// <summary>
/// Scalar floating-point precision attached to arithmetic and comparisons from an instruction whose
/// register names no longer carry their S/D width (for example, ARM64 S0 and D0 both become V0).
/// </summary>
public readonly record struct FloatingPointPrecision(bool IsDouble) : IOperand;
