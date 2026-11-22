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

public class Arm64ResolvedAggregateTests
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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void ContiguousLanesBecomeAWholeReadOnlyWithCompleteUninterruptedCoverage(bool differentOffset, bool interveningCall)
    {
        var aggregate = Aggregate(4);
        var address = new LocalVariable("element", new Register(null, "X8"), _app.SystemTypes.SystemIntPtrType);
        var result = new LocalVariable("color", new Register(null, "TEMP_CALL_RESULT"), aggregate);
        var lanes = Enumerable.Range(0, 4)
            .Select(index => new LocalVariable($"lane{index}", new Register(null, $"V{index}"), _app.SystemTypes.SystemSingleType)).ToArray();
        var loads = lanes.Select((lane, index) => new Instruction(index, OpCode.Move, lane,
            new MemoryOperand(address, addend: 32 + index * 4 + (differentOffset && index == 2 ? 4 : 0)))
            { MemoryAccessSize = 4 }).ToList();
        var pack = new Instruction(5, OpCode.PackFloatAggregate, [result, aggregate, .. lanes]);
        var caller = Method("Caller");
        caller.ControlFlowGraph = new ISILControlFlowGraph(interveningCall
            ? [loads[0], loads[1], new(2, OpCode.CallVoid, Method("MayWrite")), loads[2], loads[3], pack, new(6, OpCode.Return, result)]
            : [.. loads, pack, new(6, OpCode.Return, result)]);

        FloatAggregateRecovery.Run(caller);

        if (differentOffset || interveningCall)
        {
            Assert.That(pack.OpCode, Is.EqualTo(OpCode.PackFloatAggregate));
            Assert.That(loads.All(load => load.MemoryAccessSize == 4), Is.True);
        }
        else
        {
            Assert.That(pack.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(loads[0].Destination, Is.SameAs(result));
            Assert.That(loads[0].MemoryAccessSize, Is.EqualTo(16));
            Assert.That(((MemoryOperand)loads[0].Operands[1]).Addend, Is.EqualTo(32));
            Assert.That(loads.Skip(1).All(load => load.OpCode == OpCode.Nop), Is.True);
        }
    }

    [TestCase(16, true)]
    [TestCase(8, false)]
    public void StructArrayLoadsRequireTheWholeElement(int width, bool recovered)
    {
        var aggregate = Aggregate(4);
        var array = new LocalVariable("array", new Register(null, "X0"), new SzArrayTypeAnalysisContext(aggregate));
        var index = new LocalVariable("index", new Register(null, "X1"), _app.SystemTypes.SystemInt32Type);
        var scaled = new LocalVariable("scaled", new Register(null, "X2"), _app.SystemTypes.SystemInt64Type);
        var address = new LocalVariable("address", new Register(null, "X3"));
        var result = new LocalVariable("element", new Register(null, "V0"), aggregate);
        var load = new Instruction(2, OpCode.Move, result, new MemoryOperand(address, addend: 32)) { MemoryAccessSize = width };
        var caller = Method("Caller");
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            new(0, OpCode.ShiftLeft, scaled, index, Imm(4)) { OperandWidth = 64 },
            new(1, OpCode.Add, address, array, scaled),
            load,
            new(3, OpCode.CallVoid, Method("Consume", aggregate), array, result),
            new(4, OpCode.Return)
        ]);

        ArrayRecovery.RecoverSplitElementAccesses(caller);

        Assert.That(load.Operands[1] is ArrayAccess, Is.EqualTo(recovered));
        if (recovered)
        {
            var access = (ArrayAccess)load.Operands[1];
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
        }
    }

    [TestCase(16, 0, true)]
    [TestCase(8, 0, false)]
    [TestCase(16, 1, false)]
    public void StaticStructZeroStoresRequireTheWholeValue(int width, int bits, bool recovered)
    {
        var aggregate = Aggregate(4);
        var field = new InjectedFieldAnalysisContext("DefaultColor", aggregate, FieldAttributes.Public | FieldAttributes.Static,
            _app.SystemTypes.SystemObjectType, 0);
        var storage = new LocalVariable("storage", new Register(null, "X0"));
        var store = new Instruction(0, OpCode.Move, new FieldReference(field, storage, 0), Imm(bits))
            { MemoryAccessSize = width };
        var caller = Method("Caller");
        caller.ControlFlowGraph = new ISILControlFlowGraph([store, new(1, OpCode.Return)]);

        Arm64ZeroStoreRecovery.Run(caller);

        Assert.That(store.Operands[1] is DefaultValue, Is.EqualTo(recovered));
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
