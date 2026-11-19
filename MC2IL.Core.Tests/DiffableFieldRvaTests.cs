using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.OutputFormats;

namespace MC2IL.Core.Tests;

public class DiffableFieldRvaTests
{
    private ApplicationAnalysisContext _ctx = null!;
    private string _outDir = null!;
    private byte[] _byteData = null!;

    [SetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        _ctx = TestGameLoader.LoadSimple2022Game();
        _outDir = Directory.CreateTempSubdirectory("diffable_rva_test_").FullName;

        var assembly = _ctx.Assemblies.First();
        var fixtureType = new RvaFixtureType(assembly);
        _byteData = [0x01, 0x0A, 0x0A, 0x09, 0x10, 0x20, 0x30, 0x40];
        var offsets = new int[] { 0, 4, 8, 12, 16, 20, 24, 28 };
        var intData = new byte[offsets.Length * sizeof(int)];
        Buffer.BlockCopy(offsets, 0, intData, 0, intData.Length);

        fixtureType.Fields.Add(new RvaFixtureField(
            "ByteBlob", fixtureType, _ctx.SystemTypes.SystemByteType.MakeArrayType(1), _byteData));
        fixtureType.Fields.Add(new RvaFixtureField(
            "OffsetTable", fixtureType, _ctx.SystemTypes.SystemInt32Type.MakeArrayType(1), intData));
        assembly.Types.Add(fixtureType);

        new DiffableCsOutputFormat().DoOutput(_ctx, _outDir);
    }

    [TearDown]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_outDir, true);
        }
        catch
        {
            //best effort
        }
    }

    private string AllOutput()
        => string.Concat(Directory.EnumerateFiles(_outDir, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

    [Test]
    public void RendersFieldRvaBytesAsTypedArrayLiteral()
    {
        var output = AllOutput();
        var lines = output.Split('\n');
        var declLine = lines.FirstOrDefault(l => l.Contains("ByteBlob = new byte[]"));

        Assert.That(declLine, Is.Not.Null,
            "field-RVA data on a regular type should render as a byte[] initializer");
        Assert.That(declLine!.TrimStart(), Does.Not.StartWith("//"),
            "the field-RVA initializer must be code, not a comment");
        Assert.That(declLine, Does.Contain("Has Field RVA (address hidden for diffability)"),
            "the field-RVA line should keep the address-hidden marker");
    }

    [Test]
    public void ExercisesBothByteArrayAndIntArrayPaths()
    {
        var output = AllOutput();
        Assert.That(output, Does.Contain("ByteBlob = new byte[]"));
        Assert.That(output, Does.Contain("OffsetTable = new int[]"));
    }

    [Test]
    public void RenderedHexBytesMatchTheDefaultData()
    {
        var firstRow = string.Join(", ", _byteData.Take(4).Select(b => "0x" + b.ToString("X2")));
        Assert.That(AllOutput(), Does.Contain(firstRow));
    }

    private sealed class RvaFixtureType : TypeAnalysisContext
    {
        public RvaFixtureType(AssemblyAnalysisContext assembly) : base(null, assembly)
        {
            CustomAttributes = [];
        }

        protected override bool IsInjected => true;
        public override string DefaultName => "DiffableRvaFixture";
        public override string DefaultNamespace => "MC2IL.Core.Tests";
        public override TypeAttributes DefaultAttributes => TypeAttributes.Public | TypeAttributes.Class;
    }

    private sealed class RvaFixtureField : FieldAnalysisContext
    {
        private readonly string _name;
        private readonly TypeAnalysisContext _fieldType;
        private readonly byte[] _data;

        public RvaFixtureField(string name, TypeAnalysisContext parent, TypeAnalysisContext fieldType, byte[] data)
            : base(null, parent)
        {
            _name = name;
            _fieldType = fieldType;
            _data = data;
            CustomAttributes = [];
        }

        protected override bool IsInjected => true;
        public override string DefaultName => _name;
        public override FieldAttributes DefaultAttributes
            => FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA;
        public override TypeAnalysisContext DefaultFieldType => _fieldType;
        public override int DefaultOffset => 0;
        public override byte[] DefaultStaticArrayInitialValue => _data;
    }
}
