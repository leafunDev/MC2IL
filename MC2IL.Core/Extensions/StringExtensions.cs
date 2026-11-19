using System.Globalization;
using System.Text;

namespace MC2IL.Core.Extensions;

public static class StringExtensions
{
    public static string EscapeString(this string str)
    {
        var escaped = new StringBuilder(str.Length);

        for (var i = 0; i < str.Length; i++)
        {
            var character = str[i];
            switch (character)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\0': escaped.Append("\\0"); break;
                case '\a': escaped.Append("\\a"); break;
                case '\b': escaped.Append("\\b"); break;
                case '\f': escaped.Append("\\f"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                case '\v': escaped.Append("\\v"); break;
                default:
                    if (char.IsHighSurrogate(character) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                    {
                        escaped.Append(character);
                        escaped.Append(str[++i]);
                    }
                    else if (NeedsUnicodeEscape(character))
                        AppendUnicodeEscape(escaped, character);
                    else
                        escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    public static string EscapeChar(this char character)
    {
        return character switch
        {
            '\\' => "\\\\",
            '\'' => "\\'",
            '\0' => "\\0",
            '\a' => "\\a",
            '\b' => "\\b",
            '\f' => "\\f",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\v' => "\\v",
            _ when NeedsUnicodeEscape(character) => $"\\u{((int)character).ToString("X4", CultureInfo.InvariantCulture)}",
            _ => character.ToString(),
        };
    }

    private static bool NeedsUnicodeEscape(char character)
        => char.IsControl(character) || char.IsSurrogate(character) || character is '\u2028' or '\u2029';

    private static void AppendUnicodeEscape(StringBuilder builder, char character)
    {
        builder.Append("\\u");
        builder.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
    }
}
