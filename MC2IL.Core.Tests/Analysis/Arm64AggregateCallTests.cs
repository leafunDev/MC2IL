using System;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Api;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Tests.Analysis;

public class Arm64AggregateCallTests
{
    private ApplicationAnalysisContext _app = null!;
    private MC2ILInstructionSet _originalInstructionSet = null!;

    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
        _originalInstructionSet = _app.InstructionSet;
        _app.InstructionSet = new NewArmV8InstructionSet();
    }

    [TearDown]
    public void TearDown() => _app.InstructionSet = _originalInstructionSet;

    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(4, false)]
    [TestCase(2, true)]
    public void ResolvedCallPacksFloatingPointLanesInsteadOfMethodInfo(int count, bool doublePrecision)
    {
        var aggregate = Aggregate(count, doublePrecision);
        var callee = Method("Accept", aggregate);
        var caller = Method("Caller");
        var (call, registers) = RawCall(callee);
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);
        var convention = new Arm64CallingConventionResolver();

        convention.RemapRawArguments(call, callee, caller);

        var pack = caller.ControlFlowGraph.Instructions.Single(i => i.OpCode == OpCode.PackFloatAggregate);
        Assert.That(pack.Operands[1], Is.SameAs(aggregate));
        Assert.That(pack.Operands.Skip(2), Is.EqualTo(registers.Skip(8).Take(count)));
        Assert.That(call.Operands[2], Is.SameAs(pack.Operands[0]));
        Assert.That(call.Operands[3], Is.SameAs(registers[1]), "The hidden MethodInfo remains in X1.");
        Assert.That(caller.ControlFlowGraph.Instructions.IndexOf(pack),
            Is.LessThan(caller.ControlFlowGraph.Instructions.IndexOf(call)));

        convention.RemapRawArguments(call, callee, caller);
        Assert.That(caller.ControlFlowGraph.Instructions.Count(i => i.OpCode == OpCode.PackFloatAggregate), Is.EqualTo(1));
    }

    [Test]
    public void ScalarArgumentsKeepTheirPositionsAroundAnAggregate()
    {
        var types = _app.SystemTypes;
        var aggregate = Aggregate(3);
        var callee = Method("Accept", types.SystemSingleType, aggregate, types.SystemInt32Type, types.SystemSingleType);
        var caller = Method("Caller");
        var (call, registers) = RawCall(callee);
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        new Arm64CallingConventionResolver().RemapRawArguments(call, callee, caller);

        var pack = caller.ControlFlowGraph.Instructions.Single(i => i.OpCode == OpCode.PackFloatAggregate);
        Assert.That(call.Operands[2], Is.SameAs(registers[8]));
        Assert.That(pack.Operands.Skip(2), Is.EqualTo(registers.Skip(9).Take(3)));
        Assert.That(call.Operands[4], Is.SameAs(registers[1]));
        Assert.That(call.Operands[5], Is.SameAs(registers[12]));
        Assert.That(call.Operands[6], Is.SameAs(registers[2]));
    }

    [Test]
    public void AggregateOverflowDoesNotConsumeIntegerArgumentRegisters()
    {
        var types = _app.SystemTypes;
        var aggregate = Aggregate(4);
        var parameters = Enumerable.Repeat(types.SystemSingleType, 6)
            .Concat([aggregate, types.SystemInt32Type, types.SystemSingleType]).ToArray();
        var callee = Method("Accept", parameters);
        var caller = Method("Caller");
        var (call, registers) = RawCall(callee);
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        new Arm64CallingConventionResolver().RemapRawArguments(call, callee, caller);

        Assert.That(call.Operands[8], Is.TypeOf<UnrecoveredCallArgument>());
        Assert.That(((UnrecoveredCallArgument)call.Operands[8]).ExpectedType, Is.SameAs(aggregate));
        Assert.That(call.Operands[9], Is.SameAs(registers[1]));
        Assert.That(call.Operands[10], Is.TypeOf<UnrecoveredCallArgument>());
        Assert.That(call.Operands[11], Is.SameAs(registers[2]));
        Assert.That(caller.ControlFlowGraph.Instructions.Any(i => i.OpCode == OpCode.PackFloatAggregate), Is.False);
    }

    [Test]
    public void AReturnedAggregateStaysLiveWhenPassedToAResolvedCall()
    {
        var aggregate = Aggregate(4);
        var produce = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType, "Produce", aggregate,
            MethodAttributes.Public | MethodAttributes.Static, []);
        var result = new LocalVariable("result", new Register(null, "TEMP_CALL_RESULT"), aggregate);
        var producer = new Instruction(0, OpCode.Call, produce, result);
        var callee = Method("Accept", aggregate);
        var caller = Method("Caller");
        var (call, registers) = RawCall(callee);
        var loads = aggregate.Fields.Select((field, lane) =>
            new Instruction(lane + 1, OpCode.Move, registers[8 + lane], new FieldReference(field, result, field.Offset))).ToList();
        call.Index = 5;
        caller.ControlFlowGraph = new ISILControlFlowGraph([producer, .. loads, call, new(6, OpCode.Return)]);
        caller.Locals.Add(result);
        caller.Locals.AddRange(registers);

        new Arm64CallingConventionResolver().RemapRawArguments(call, callee, caller);
        FloatAggregateRecovery.Run(caller);
        SsaSimplifier.Run(caller);
        DeadCodeEliminator.Run(caller);

        Assert.That(caller.ControlFlowGraph.Instructions, Does.Contain(producer));
        Assert.That(producer.OpCode, Is.EqualTo(OpCode.Call));
        Assert.That(call.Operands[2], Is.SameAs(result));
    }

    [Test]
    public void MixedPrecisionFieldsAreNotAHomogeneousAggregate()
    {
        var aggregate = Aggregate(4);
        aggregate.Fields[1].FieldType = _app.SystemTypes.SystemInt32Type;
        Assert.That(Arm64CallingConventionResolver.GetFloatAggregateFields(aggregate), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PackedByteCallArgumentsAreMaterializedAsFields(bool literal)
    {
        var aggregate = Aggregate(1);
        aggregate.Fields.Clear();
        for (var lane = 0; lane < 4; lane++)
            aggregate.Fields.Add(new InjectedFieldAnalysisContext($"Byte{lane}", _app.SystemTypes.SystemByteType,
                FieldAttributes.Public, aggregate, lane));
        var bits = new LocalVariable("bits", new Register(null, "X0"), aggregate);
        var producer = new Instruction(0, OpCode.Or, bits, Imm(0x12340000), Imm(0x5678));
        var callee = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType, "Accept", _app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static, [aggregate]);
        var call = new Instruction(1, OpCode.CallVoid, callee, literal ? Imm(0x12345678) : bits);
        var caller = Method("Caller");
        caller.ControlFlowGraph = new ISILControlFlowGraph([producer, call, new(2, OpCode.Return)]);

        PackedByteAggregateRecovery.RecoverCallArguments(caller);

        Assert.That(call.Operands[1], Is.TypeOf<LocalVariable>());
        var argument = (LocalVariable)call.Operands[1];
        Assert.That(argument.Type, Is.SameAs(aggregate));
        Assert.That(argument, Is.Not.SameAs(bits));
        var stores = caller.ControlFlowGraph.Instructions.Where(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [FieldReference, _] }).ToList();
        Assert.That(stores.Count, Is.EqualTo(4));
        Assert.That(stores.Select(store => ((FieldReference)store.Operands[0]).Field), Is.EqualTo(aggregate.Fields));
        if (!literal)
            Assert.That(bits.Type, Is.SameAs(_app.SystemTypes.SystemInt32Type));
    }

    [TestCase(0x70, 1d)]
    [TestCase(0xF0, -1d)]
    [TestCase(0x00, 2d)]
    [TestCase(0x60, 0.5d)]
    [TestCase(0x7F, 1.9375d)]
    public void VectorFloatImmediatesRetainTheirEncodedValue(int immediate, double expected)
    {
        var encoding = 0x4F00F400u | ((uint)(immediate >> 5) << 16) | ((uint)(immediate & 31) << 5);
        Assert.That(NewArm64Utils.DecodeVectorFloatImmediate(encoding, false), Is.EqualTo(expected));
        Assert.That(NewArm64Utils.DecodeVectorFloatImmediate(encoding, true), Is.EqualTo(expected));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void VectorSplatStoresRequireCompleteAggregateCoverage(bool doublePrecision, bool partialStore)
    {
        var count = doublePrecision ? 2 : 4;
        var aggregate = Aggregate(count, doublePrecision);
        var source = new LocalVariable("splat", new Register(null, "V0"));
        IOperand literal = doublePrecision ? new DoubleLiteral(1d) : new FloatLiteral(1f);
        var splat = new Instruction(0, OpCode.Move, source, literal) { VectorElementCount = count };
        var self = new LocalVariable("this", new Register(null, "X0"), _app.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext("Value", aggregate, FieldAttributes.Public, self.Type!, 16);
        var store = new Instruction(1, OpCode.Move, new FieldReference(field, self, 16), source)
        {
            MemoryAccessSize = partialStore ? 4 : 16,
        };
        var caller = Method("Initialize");
        caller.ControlFlowGraph = new ISILControlFlowGraph([splat, store, new(2, OpCode.Return)]);

        Arm64AggregateLiteralRecovery.Run(caller);

        var packs = caller.ControlFlowGraph.Instructions.Where(i => i.OpCode == OpCode.PackFloatAggregate).ToList();
        Assert.That(packs.Count, Is.EqualTo(partialStore ? 0 : 1));
        if (partialStore)
            Assert.That(store.Operands[1], Is.SameAs(source));
        else
        {
            Assert.That(packs[0].Operands.Skip(2), Is.EqualTo(Enumerable.Repeat(literal, count)));
            Assert.That(store.Operands[1], Is.SameAs(packs[0].Operands[0]));
        }
    }

    [Test]
    public void DuplicatedIntegerBitsPopulateEveryFloatingPointField()
    {
        var aggregate = Aggregate(2);
        var duplicate = new LocalVariable("duplicate", new Register(null, "V0"), _app.SystemTypes.SystemSingleType);
        var convert = new Instruction(0, OpCode.Convert, duplicate, Imm(0x42800000),
            new NumericConversion(NumericConversionKind.I32ReinterpretFloat32)) { VectorElementCount = 2 };
        var self = new LocalVariable("this", new Register(null, "X0"), _app.SystemTypes.SystemObjectType);
        var field = new InjectedFieldAnalysisContext("Value", aggregate, FieldAttributes.Public, self.Type!, 16);
        var store = new Instruction(1, OpCode.Move, new FieldReference(field, self, 16), duplicate) { MemoryAccessSize = 8 };
        var caller = Method("Initialize");
        caller.ControlFlowGraph = new ISILControlFlowGraph([convert, store, new(2, OpCode.Return)]);

        Arm64AggregateLiteralRecovery.Run(caller);
        ConstantFolder.Run(caller);
        FloatLiteralRecovery.Run(caller);

        var pack = caller.ControlFlowGraph.Instructions.Single(i => i.OpCode == OpCode.PackFloatAggregate);
        Assert.That(pack.Operands.Skip(2), Is.EqualTo(new[] { duplicate, duplicate }));
        Assert.That(convert.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(((FloatLiteral)convert.Operands[1]).Value, Is.EqualTo(64f));
        Assert.That(store.Operands[1], Is.SameAs(pack.Operands[0]));
    }

    private TypeAnalysisContext Aggregate(int count, bool doublePrecision = false)
    {
        var size = count * (doublePrecision ? sizeof(double) : sizeof(float));
        var prototype = _app.AllTypes.First(type => type.IsValueType && !type.IsEnumType
            && TypeSizes.UnboxedSize(type, 8) == size);
        var type = new TypeAnalysisContext(prototype.Definition, prototype.DeclaringAssembly)
        {
            Name = $"SampleAggregate{count}", Namespace = "Regression", Attributes = TypeAttributes.Public | TypeAttributes.SequentialLayout,
        };
        type.Fields.Clear();
        for (var lane = 0; lane < count; lane++)
            type.Fields.Add(new InjectedFieldAnalysisContext($"Lane{lane}",
                doublePrecision ? _app.SystemTypes.SystemDoubleType : _app.SystemTypes.SystemSingleType,
                FieldAttributes.Public, type, lane * (size / count)));
        return type;
    }

    private InjectedMethodAnalysisContext Method(string name, params TypeAnalysisContext[] parameters) =>
        new(_app.SystemTypes.SystemObjectType, name, _app.SystemTypes.SystemVoidType, MethodAttributes.Public, parameters);

    private static (Instruction Call, LocalVariable[] Registers) RawCall(MethodAnalysisContext callee)
    {
        var registers = Enumerable.Range(0, 8).Select(index => $"X{index}")
            .Concat(Enumerable.Range(0, 8).Select(index => $"V{index}"))
            .Select(name => new LocalVariable(name, new Register(null, name))).ToArray();
        return (new Instruction(0, OpCode.CallVoid, [callee, .. registers]), registers);
    }
}
