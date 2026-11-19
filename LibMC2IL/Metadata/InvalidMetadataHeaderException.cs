using System;

namespace LibMC2IL.Metadata;

/// <summary>The input does not start with a complete IL2CPP metadata preamble.</summary>
public sealed class InvalidMetadataHeaderException : FormatException
{
    internal InvalidMetadataHeaderException(string message) : base(message) { }
}
