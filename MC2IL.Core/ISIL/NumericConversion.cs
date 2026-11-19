using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.ISIL;

/// <summary>
/// Numeric conversion represented by an ISIL instruction. The source and result types are derived
/// from the conversion kind during type propagation and IL generation.
/// </summary>
public readonly record struct NumericConversion(
    NumericConversionKind Kind,
    NumericConversionRounding Rounding = NumericConversionRounding.TowardZero,
    bool Saturating = false,
    int FixedPointScale = 0) : IOperand;

public enum NumericConversionRounding
{
    TowardZero,
    TowardNegativeInfinity,
    NearestTiesToEven,
    TowardPositiveInfinity,
    NearestTiesAwayFromZero,
}

public static class NumericConversionTypes
{
    public static (TypeAnalysisContext? Source, TypeAnalysisContext? Result) Get(NumericConversionKind kind, SystemTypesContext types) => kind switch
    {
        NumericConversionKind.Float32ToFloat64 => (types.SystemSingleType, types.SystemDoubleType),
        NumericConversionKind.Float64ToFloat32 => (types.SystemDoubleType, types.SystemSingleType),
        NumericConversionKind.SignedInt32ToFloat32 => (types.SystemInt32Type, types.SystemSingleType),
        NumericConversionKind.SignedInt32ToFloat64 => (types.SystemInt32Type, types.SystemDoubleType),
        NumericConversionKind.SignedInt64ToFloat32 => (types.SystemInt64Type, types.SystemSingleType),
        NumericConversionKind.SignedInt64ToFloat64 => (types.SystemInt64Type, types.SystemDoubleType),
        NumericConversionKind.UnsignedInt32ToFloat32 => (types.SystemUInt32Type, types.SystemSingleType),
        NumericConversionKind.UnsignedInt32ToFloat64 => (types.SystemUInt32Type, types.SystemDoubleType),
        NumericConversionKind.UnsignedInt64ToFloat32 => (types.SystemUInt64Type, types.SystemSingleType),
        NumericConversionKind.UnsignedInt64ToFloat64 => (types.SystemUInt64Type, types.SystemDoubleType),
        NumericConversionKind.I32WrapI64 => (types.SystemInt64Type, types.SystemInt32Type),
        NumericConversionKind.I64ExtendI32Signed => (types.SystemInt32Type, types.SystemInt64Type),
        NumericConversionKind.I64ExtendI32Unsigned => (types.SystemUInt32Type, types.SystemUInt64Type),
        NumericConversionKind.Float32ToInt32Signed => (types.SystemSingleType, types.SystemInt32Type),
        NumericConversionKind.Float32ToInt32Unsigned => (types.SystemSingleType, types.SystemUInt32Type),
        NumericConversionKind.Float64ToInt32Signed => (types.SystemDoubleType, types.SystemInt32Type),
        NumericConversionKind.Float64ToInt32Unsigned => (types.SystemDoubleType, types.SystemUInt32Type),
        NumericConversionKind.Float32ToInt64Signed => (types.SystemSingleType, types.SystemInt64Type),
        NumericConversionKind.Float32ToInt64Unsigned => (types.SystemSingleType, types.SystemUInt64Type),
        NumericConversionKind.Float64ToInt64Signed => (types.SystemDoubleType, types.SystemInt64Type),
        NumericConversionKind.Float64ToInt64Unsigned => (types.SystemDoubleType, types.SystemUInt64Type),
        NumericConversionKind.I32Extend8Signed or NumericConversionKind.I32Extend16Signed
            => (types.SystemInt32Type, types.SystemInt32Type),
        NumericConversionKind.I64Extend8Signed or NumericConversionKind.I64Extend16Signed or NumericConversionKind.I64Extend32Signed
            => (types.SystemInt64Type, types.SystemInt64Type),
        NumericConversionKind.Float32ReinterpretI32 => (types.SystemSingleType, types.SystemInt32Type),
        NumericConversionKind.Float64ReinterpretI64 => (types.SystemDoubleType, types.SystemInt64Type),
        NumericConversionKind.I32ReinterpretFloat32 => (types.SystemInt32Type, types.SystemSingleType),
        NumericConversionKind.I64ReinterpretFloat64 => (types.SystemInt64Type, types.SystemDoubleType),
        NumericConversionKind.Float32ToInt32Truncate => (types.SystemSingleType, types.SystemInt32Type),
        NumericConversionKind.Float32ToInt32UnsignedTruncate => (types.SystemSingleType, types.SystemUInt32Type),
        NumericConversionKind.Float32ToInt64Truncate => (types.SystemSingleType, types.SystemInt64Type),
        NumericConversionKind.Float32ToInt64UnsignedTruncate => (types.SystemSingleType, types.SystemUInt64Type),
        NumericConversionKind.Float64ToInt32Truncate => (types.SystemDoubleType, types.SystemInt32Type),
        NumericConversionKind.Float64ToInt32UnsignedTruncate => (types.SystemDoubleType, types.SystemUInt32Type),
        NumericConversionKind.Float64ToInt64Truncate => (types.SystemDoubleType, types.SystemInt64Type),
        NumericConversionKind.Float64ToInt64UnsignedTruncate => (types.SystemDoubleType, types.SystemUInt64Type),
        _ => (null, null),
    };
}

public enum NumericConversionKind
{
    Float32ToFloat64,
    Float64ToFloat32,
    SignedInt32ToFloat32,
    SignedInt32ToFloat64,
    SignedInt64ToFloat32,
    SignedInt64ToFloat64,
    UnsignedInt32ToFloat32,
    UnsignedInt32ToFloat64,
    UnsignedInt64ToFloat32,
    UnsignedInt64ToFloat64,
    I32WrapI64,
    I64ExtendI32Signed,
    I64ExtendI32Unsigned,
    Float32ToInt32Signed,
    Float32ToInt32Unsigned,
    Float64ToInt32Signed,
    Float64ToInt32Unsigned,
    Float32ToInt64Signed,
    Float32ToInt64Unsigned,
    Float64ToInt64Signed,
    Float64ToInt64Unsigned,
    I32Extend8Signed,
    I32Extend16Signed,
    I64Extend8Signed,
    I64Extend16Signed,
    I64Extend32Signed,
    Float32ReinterpretI32,
    Float64ReinterpretI64,
    I32ReinterpretFloat32,
    I64ReinterpretFloat64,
    Float32ToInt32Truncate,
    Float32ToInt32UnsignedTruncate,
    Float32ToInt64Truncate,
    Float32ToInt64UnsignedTruncate,
    Float64ToInt32Truncate,
    Float64ToInt32UnsignedTruncate,
    Float64ToInt64Truncate,
    Float64ToInt64UnsignedTruncate,
}
