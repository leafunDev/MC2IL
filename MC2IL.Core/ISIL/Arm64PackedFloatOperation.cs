namespace MC2IL.Core.ISIL;

internal enum Arm64PackedFloatOperation
{
    None,
    Binary,
    Negate,
    GreaterMask,
    SelectByMask,
    Broadcast,
    Duplicate,
    ReplicateBits,
    InsertLow,
    InsertHigh,
    ExtractLow,
    ExtractHigh,
    Copy
}
