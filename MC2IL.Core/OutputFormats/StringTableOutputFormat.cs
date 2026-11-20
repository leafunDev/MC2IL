using System;
using System.Globalization;
using System.IO;
using System.Text;
using LibMC2IL.Metadata;
using MC2IL.Core.Api;
using MC2IL.Core.Extensions;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.OutputFormats;

public sealed class StringTableOutputFormat : MC2ILOutputFormat
{
    public const string FormatId = "strings";
    public const int DefaultMinimumNativeLength = 4;

    private readonly int _minimumNativeLength;

    public StringTableOutputFormat() : this(DefaultMinimumNativeLength) { }

    public StringTableOutputFormat(int minimumNativeLength)
    {
        if (minimumNativeLength < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumNativeLength));
        _minimumNativeLength = minimumNativeLength;
    }

    public override string OutputFormatId => FormatId;
    public override string OutputFormatName => "Strings in a UTF-8 text file";

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot) =>
        Export(context.Binary.GetRawBinaryContent(), context.Metadata, outputRoot);

    public void Export(ReadOnlySpan<byte> binary, Il2CppMetadata? metadata, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var outputPath = Path.Combine(outputRoot, "strings.txt");
        using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(false), bufferSize: 64 * 1024);

        var literalCount = metadata?.stringLiterals.Length ?? 0;
        if (metadata != null)
        {
            writer.WriteLine("[IL2CPP string literals: table index]");
            for (uint index = 0; index < literalCount; index++)
                WriteEntry(writer, index.ToString(CultureInfo.InvariantCulture), metadata.GetStringLiteralFromIndex(index));
            writer.WriteLine();
        }

        writer.WriteLine("[Native ASCII / UTF-8: binary offset]");
        var narrowCount = WriteUtf8Runs(binary, writer);
        writer.WriteLine();
        writer.WriteLine("[Native UTF-16LE: binary offset]");
        var littleEndianCount = WriteUtf16Runs(binary, writer, bigEndian: false);
        writer.WriteLine();
        writer.WriteLine("[Native UTF-16BE: binary offset]");
        var bigEndianCount = WriteUtf16Runs(binary, writer, bigEndian: true);
        writer.Flush();

        Logger.InfoNewline($"Wrote {literalCount} IL2CPP literals and {narrowCount + littleEndianCount + bigEndianCount} native string candidates to {outputPath}.",
            "StringsOutput");
    }

    private int WriteUtf8Runs(ReadOnlySpan<byte> bytes, TextWriter writer)
    {
        var entries = 0;
        var start = 0;
        var length = 0;
        for (var offset = 0; offset < bytes.Length;)
        {
            var width = ReadUtf8Scalar(bytes.Slice(offset), out var scalar);
            if (width > 0 && IsPrintable(scalar))
            {
                length++;
                offset += width;
                continue;
            }

            if (length >= _minimumNativeLength)
            {
                WriteNativeEntry(writer, start, bytes.Slice(start, offset - start), Encoding.UTF8);
                entries++;
            }

            offset += Math.Max(width, 1);
            start = offset;
            length = 0;
        }

        if (length >= _minimumNativeLength)
        {
            WriteNativeEntry(writer, start, bytes.Slice(start), Encoding.UTF8);
            entries++;
        }

        return entries;
    }

    private int WriteUtf16Runs(ReadOnlySpan<byte> bytes, TextWriter writer, bool bigEndian)
    {
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var entries = 0;
        // Untagged wide strings are commonly ASCII in an aligned UTF-16 buffer.
        // Require the zero padding in that case; arbitrary machine code would otherwise
        // decode as long runs of apparently valid Unicode. A BOM allows full Unicode.
        for (var offset = 0; offset < bytes.Length - 1;)
        {
            var tagged = ReadUtf16Unit(bytes, offset, bigEndian) == 0xFEFF;
            if (tagged)
                offset += 2;

            var start = offset;
            var length = 0;
            while (offset < bytes.Length - 1)
            {
                var unit = ReadUtf16Unit(bytes, offset, bigEndian);
                if (!tagged && !IsPrintableAscii(unit))
                    break;

                var width = 2;
                var scalar = unit;
                if (unit is >= 0xD800 and <= 0xDBFF && offset < bytes.Length - 3)
                {
                    var low = ReadUtf16Unit(bytes, offset + 2, bigEndian);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                    {
                        scalar = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                        width = 4;
                    }
                }

                if (tagged && !IsPrintable(scalar))
                    break;

                offset += width;
                length++;
            }

            if (length >= _minimumNativeLength)
            {
                WriteNativeEntry(writer, start, bytes.Slice(start, offset - start), encoding);
                entries++;
            }

            // Leave the next boundary for the next iteration in case it is itself a BOM.
            if (offset == start && !tagged)
                offset += 2;
        }

        return entries;
    }

    private static int ReadUtf16Unit(ReadOnlySpan<byte> bytes, int offset, bool bigEndian) =>
        bigEndian ? (bytes[offset] << 8) | bytes[offset + 1] : (bytes[offset + 1] << 8) | bytes[offset];

    private static int ReadUtf8Scalar(ReadOnlySpan<byte> bytes, out int scalar)
    {
        scalar = bytes[0];
        if (scalar < 0x80)
            return 1;

        var width = scalar switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 0,
        };
        if (width == 0 || bytes.Length < width)
            return 0;

        scalar &= 0x7F >> width;
        for (var i = 1; i < width; i++)
        {
            var part = bytes[i];
            if ((part & 0xC0) != 0x80)
                return 0;
            scalar = (scalar << 6) | (part & 0x3F);
        }

        var minimum = width == 2 ? 0x80 : width == 3 ? 0x800 : 0x10000;
        return scalar >= minimum && scalar is not (>= 0xD800 and <= 0xDFFF) and <= 0x10FFFF ? width : 0;
    }

    private static bool IsPrintableAscii(int scalar) => scalar is 9 or (>= 0x20 and <= 0x7E);

    private static bool IsPrintable(int scalar)
    {
        if (scalar < 0x80)
            return IsPrintableAscii(scalar);
        var category = scalar <= 0xFFFF
            ? char.GetUnicodeCategory((char)scalar)
            : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(scalar), 0);
        return category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }

    private static void WriteNativeEntry(TextWriter writer, int offset, ReadOnlySpan<byte> bytes, Encoding encoding)
    {
#if NETSTANDARD2_0
        var value = encoding.GetString(bytes.ToArray());
#else
        var value = encoding.GetString(bytes);
#endif
        WriteEntry(writer, "0x" + offset.ToString("X8", CultureInfo.InvariantCulture), value);
    }

    private static void WriteEntry(TextWriter writer, string position, string value)
    {
        writer.Write(position);
        writer.Write('\t');
        writer.Write('"');
        writer.Write(value.EscapeString());
        writer.WriteLine('"');
    }
}
