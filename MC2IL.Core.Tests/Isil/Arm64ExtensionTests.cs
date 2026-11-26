using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Disarm;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using CilMethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using CilTypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace MC2IL.Core.Tests.Isil;

public class Arm64ExtensionTests
{
    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(0x13001C20u, 8, true, false)] // sxtb w0, w1
    [TestCase(0x13003C20u, 16, true, false)] // sxth w0, w1
    [TestCase(0x93401C20u, 8, true, true)] // sxtb x0, w1
    [TestCase(0x93403C20u, 16, true, true)] // sxth x0, w1
    [TestCase(0x93407C20u, 32, true, true)] // sxtw x0, w1
    [TestCase(0x53001C20u, 8, false, false)] // uxtb w0, w1
    [TestCase(0x53003C20u, 16, false, false)] // uxth w0, w1
    public void GeneratedIlPreservesLowBitsAndSign(uint encoding, int bits, bool signed, bool wideResult)
    {
        var instructions = Convert(encoding);
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var resultType = wideResult ? types.SystemInt64Type : types.SystemInt32Type;
        var source = new LocalVariable("input", new Register(null, "X1"), types.SystemInt64Type);
        var result = new LocalVariable("result", new Register(null, "X0"), resultType);
        foreach (var instruction in instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
                if (instruction.Operands[i] is Register register)
                    instruction.SetOperand(i, register.Name == "X1" ? source : result);
        }
        instructions.Add(new Instruction(instructions.Count, OpCode.Return, result));
        var context = new InjectedMethodAnalysisContext(types.SystemObjectType, "Extend", resultType,
            MethodAttributes.Public | MethodAttributes.Static, [types.SystemInt64Type])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions), Locals = [source, result],
            ParameterLocals = [source], AnalysisWarnings = [],
        };
        var module = new ModuleDefinition("Extensions.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var corelib = ModuleDefinition.FromFile(typeof(object).Assembly.Location);
        types.SystemInt32Type.PutExtraData("AsmResolverType", corelib.GetAllTypes().Single(type => type.FullName == "System.Int32"));
        types.SystemInt64Type.PutExtraData("AsmResolverType", corelib.GetAllTypes().Single(type => type.FullName == "System.Int64"));
        var assembly = new AssemblyDefinition("Extensions", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Tests", "Extensions", CilTypeAttributes.Public,
            module.CorLibTypeFactory.Object.ToTypeDefOrRef());
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Extend", CilMethodAttributes.Public | CilMethodAttributes.Static,
            MethodSignature.CreateStatic(wideResult ? module.CorLibTypeFactory.Int64 : module.CorLibTypeFactory.Int32,
                [module.CorLibTypeFactory.Int64]));
        type.Methods.Add(method);
        method.ParameterDefinitions.Add(new ParameterDefinition(1, source.Name, 0));
        IlGenerator.GenerateIl(context, method);
        using var stream = new MemoryStream();
        assembly.WriteManifest(stream);
        var generated = Assembly.Load(stream.ToArray()).GetType("Tests.Extensions")!.GetMethod("Extend")!;
        var mask = (1L << bits) - 1;
        long[] inputs = [0, 1, (1L << (bits - 1)) - 1, 1L << (bits - 1), mask, mask + 1,
            -1, long.MinValue, long.MaxValue, unchecked((long)0x123456788765FEDCUL)];
        using (Assert.EnterMultipleScope())
            foreach (var input in inputs)
            {
                var expected = input & mask;
                if (signed && (expected & (1L << (bits - 1))) != 0)
                    expected |= ~mask;
                Assert.That(System.Convert.ToInt64(generated.Invoke(null, [input])), Is.EqualTo(expected),
                    $"encoding 0x{encoding:X8}, input 0x{input:X16}");
            }
    }

    [TestCase(0x13001C20u)]
    [TestCase(0x13003C20u)]
    [TestCase(0x93401C20u)]
    [TestCase(0x93403C20u)]
    [TestCase(0x93407C20u)]
    [TestCase(0x53001C20u)]
    [TestCase(0x53003C20u)]
    public void ZeroRegisterDestinationIsDiscarded(uint encoding)
    {
        var instruction = Convert(encoding | 31).Single();
        Assert.That(instruction.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [TestCase(0x13001C20u)]
    [TestCase(0x93407C20u)]
    [TestCase(0x53003C20u)]
    public void ZeroRegisterSourceIsAConstant(uint encoding)
    {
        var instruction = Convert((encoding & ~(31u << 5)) | (31u << 5)).Single();
        Assert.That(instruction.Operands[1], Is.EqualTo(new Immediate(0)));
    }

    private static List<Instruction> Convert(uint encoding)
    {
        using var decoded = Disassembler.Disassemble(BitConverter.GetBytes(encoding), 0x1000,
            new Disassembler.Options(true, true, false)).GetEnumerator();
        Assert.That(decoded.MoveNext(), Is.True);
        var instructions = new List<Instruction>();
        var converter = typeof(NewArmV8InstructionSet).GetMethod("ConvertInstructionStatement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var context = new InjectedMethodAnalysisContext(types.SystemObjectType, "Test", types.SystemVoidType,
            MethodAttributes.Static, []);
        // GetIsilFromMethod normally initializes the per-thread ADRP state before lowering.
        typeof(NewArmV8InstructionSet).GetField("adrpOffsets", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new Dictionary<string, ulong>());
        converter.Invoke(new NewArmV8InstructionSet(), [decoded.Current, instructions, new List<ulong>(), context]);
        return instructions;
    }
}
