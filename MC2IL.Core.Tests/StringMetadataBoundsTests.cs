using System.Collections.Generic;
using System.Reflection;
using LibMC2IL;

namespace MC2IL.Core.Tests;

public class StringMetadataBoundsTests
{
    private LibMC2ILContext _post27Context = null!;
    private LibMC2ILContext _legacyContext = null!;

    [OneTimeSetUp]
    public void LoadFixtures()
    {
        _post27Context = TestGameLoader.LoadSimple2022Game().LibMC2ILContext;
        _legacyContext = TestGameLoader.LoadSimple2019Game().LibMC2ILContext;
    }

    [Test]
    public void RejectsOutOfRangeStringUsageInPost27Metadata()
    {
        var index = (uint)_post27Context.Metadata.stringLiterals.Length;
        var encoded = ((ulong)MetadataUsageType.StringLiteral << 29) | ((ulong)index << 1);

        Assert.That(MetadataUsage.DecodeMetadataUsage(encoded, 0, _post27Context), Is.Null);
    }

    [Test]
    public void RejectsOutOfRangeStringUsageFromLegacyAddressMap()
    {
        const ulong address = 0xFFFF_FFFF_FFFF_FF00;
        var invalidIndex = (uint)_legacyContext.Metadata.stringLiterals.Length;
        var literalMap = (Dictionary<ulong, MetadataUsage>)typeof(LibMC2ILContext)
            .GetField("LiteralsByAddress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_legacyContext)!;
        literalMap[address] = new MetadataUsage(
            MetadataUsageType.StringLiteral, address, invalidIndex, _legacyContext);

        Assert.That(_legacyContext.GetLiteralByAddress(address), Is.Null);
    }
}
