using System;
using System.Collections.Generic;
using System.Linq;

namespace MC2IL.Core.Api;

public static class OutputFormatRegistry
{
    private static readonly Dictionary<string, MC2ILOutputFormat> _formatsById = new();

    public static IReadOnlyList<MC2ILOutputFormat> AllOutputFormats => _formatsById.Values.ToList();

    public static void Register<T>() where T : MC2ILOutputFormat, new()
    {
        var format = new T();
        _formatsById.Add(format.OutputFormatId, format);
    }

    public static MC2ILOutputFormat GetFormat(string formatId)
        => _formatsById.TryGetValue(formatId, out var format) ? format : throw new ArgumentException($"No output format registered with id '{formatId}'");
}
