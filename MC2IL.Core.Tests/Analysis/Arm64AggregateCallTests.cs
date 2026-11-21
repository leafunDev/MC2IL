using System;
using System.Linq;
using System.Reflection;
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

    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
        _app.InstructionSet = new NewArmV8InstructionSet();
    }

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
