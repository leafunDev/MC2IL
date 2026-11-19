using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AsmResolver.DotNet;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Tests;

public class Arm64ConversionHelperTests
{
    [Test]
    public void ReusesOneHelperAcrossPrecisionsAndParallelCallers()
    {
        var module = new ModuleDefinition("Recovery.dll");
        var methods = new MethodDefinition[32];
        Parallel.For(0, methods.Length, i =>
            methods[i] = Arm64ConversionHelpers.GetConversion(module,
                new NumericConversion(i % 2 == 0 ? NumericConversionKind.Float32ToInt32Signed
                    : NumericConversionKind.Float64ToInt32Signed, Saturating: true)));

        var type = module.TopLevelTypes.Single(t => t.Name == "Arm64Conversions");
        Assert.That(type.Methods.Single(), Is.SameAs(methods[0]));
        Assert.That(methods.All(method => ReferenceEquals(method, methods[0])), Is.True);
    }

    [Test]
    public void GeneratedAssemblySaturatesAtSignedAndUnsignedBoundaries()
    {
        var helpers = LoadHelpers(
            (NumericConversionKind.Float64ToInt32Signed, NumericConversionRounding.TowardZero),
            (NumericConversionKind.Float64ToInt32Unsigned, NumericConversionRounding.TowardZero),
            (NumericConversionKind.Float64ToInt64Signed, NumericConversionRounding.TowardZero),
            (NumericConversionKind.Float64ToInt64Unsigned, NumericConversionRounding.TowardZero));

        var i32 = Get<int>(helpers, "TruncateToInt32");
        var u32 = Get<uint>(helpers, "TruncateToUInt32");
        var i64 = Get<long>(helpers, "TruncateToInt64");
        var u64 = Get<ulong>(helpers, "TruncateToUInt64");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(i32(double.NaN), Is.Zero);
            Assert.That(u32(double.NaN), Is.Zero);
            Assert.That(i64(double.NaN), Is.Zero);
            Assert.That(u64(double.NaN), Is.Zero);
            Assert.That(i32(-42.9), Is.EqualTo(-42));
            Assert.That(u32(-2.9), Is.Zero);
            Assert.That(i32(2147483648d), Is.EqualTo(int.MaxValue));
            Assert.That(i32(-2147483649d), Is.EqualTo(int.MinValue));
            Assert.That(u32(4294967294d), Is.EqualTo(uint.MaxValue - 1));
            Assert.That(u32(4294967296d), Is.EqualTo(uint.MaxValue));
            Assert.That(i64(Math.BitDecrement(9223372036854775808d)), Is.EqualTo(long.MaxValue - 1023));
            Assert.That(i64(9223372036854775808d), Is.EqualTo(long.MaxValue));
            Assert.That(i64(-9223372036854775808d), Is.EqualTo(long.MinValue));
            Assert.That(i64(double.NegativeInfinity), Is.EqualTo(long.MinValue));
            Assert.That(u64(-0.0), Is.Zero);
            Assert.That(u64(double.NegativeInfinity), Is.Zero);
            Assert.That(u64(9223372036854775808d), Is.EqualTo(1UL << 63));
            Assert.That(u64(Math.BitDecrement(18446744073709551616d)), Is.EqualTo(ulong.MaxValue - 2047));
            Assert.That(u64(18446744073709551616d), Is.EqualTo(ulong.MaxValue));
            Assert.That(u64(double.PositiveInfinity), Is.EqualTo(ulong.MaxValue));
        }
    }

    [TestCase(NumericConversionRounding.TowardNegativeInfinity, "FloorToInt32", 2, -3)]
    [TestCase(NumericConversionRounding.TowardPositiveInfinity, "CeilingToInt32", 3, -2)]
    [TestCase(NumericConversionRounding.NearestTiesToEven, "RoundToEvenToInt32", 2, -2)]
    [TestCase(NumericConversionRounding.NearestTiesAwayFromZero, "RoundAwayFromZeroToInt32", 3, -3)]
    public void GeneratedAssemblyRoundsBeforeSaturating(NumericConversionRounding rounding, string name,
        int positive, int negative)
    {
        var helpers = LoadHelpers((NumericConversionKind.Float64ToInt32Signed, rounding));
        var convert = Get<int>(helpers, name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(convert(2.5), Is.EqualTo(positive));
            Assert.That(convert(-2.5), Is.EqualTo(negative));
            Assert.That(convert(2147483647.75d), Is.EqualTo(int.MaxValue));
            Assert.That(convert(double.NaN), Is.Zero);
        }
    }

    private static Func<double, T> Get<T>(Type helpers, string name) =>
        helpers.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<double, T>>();

    private static Type LoadHelpers(params (NumericConversionKind Kind, NumericConversionRounding Rounding)[] conversions)
    {
        var module = new ModuleDefinition("Recovery.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var assembly = new AssemblyDefinition("Recovery", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);

        foreach (var (kind, rounding) in conversions)
            Arm64ConversionHelpers.GetConversion(module, new NumericConversion(kind, rounding, Saturating: true));

        using var stream = new MemoryStream();
        assembly.WriteManifest(stream);
        return Assembly.Load(stream.ToArray()).GetType("MC2ILInjected.Arm64Conversions", throwOnError: true)!;
    }
}
