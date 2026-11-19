using MC2IL.Core.Extensions;

namespace MC2IL.Core.Tests;

public class StringLiteralFormattingTests
{
    [Test]
    public void EscapesCSharpStringLiteralContentsWithoutChangingUnicodeText()
    {
        var value = "quote \" slash \\ line\n tab\t nul\0 control\u0001 separator\u2028 emoji 😀 isolated\uD800";

        Assert.That(value.EscapeString(), Is.EqualTo("quote \\\" slash \\\\ line\\n tab\\t nul\\0 control\\u0001 separator\\u2028 emoji 😀 isolated\\uD800"));
    }

    [Test]
    public void EscapesCSharpCharacterLiteralContentsWithValidUnicodeEscapes()
    {
        Assert.That('A'.EscapeChar(), Is.EqualTo("A"));
        Assert.That('\''.EscapeChar(), Is.EqualTo("\\'"));
        Assert.That('\\'.EscapeChar(), Is.EqualTo("\\\\"));
        Assert.That('\u0001'.EscapeChar(), Is.EqualTo("\\u0001"));
        Assert.That('\uD800'.EscapeChar(), Is.EqualTo("\\uD800"));
    }
}
