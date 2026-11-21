using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class Arm64BranchRecoveryTests
{
    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    public void PageAddressOnOnePathDoesNotReplaceObjectBaseOnTheOther()
    {
        var baseRegister = new Register(null, "X21");
        var fieldLoad = new Instruction(5, OpCode.Move, new Register(null, "X0"), new MemoryOperand(baseRegister, addend: 0x28));
        var globalLoad = new Instruction(3, OpCode.Move, new Register(null, "X0"), new MemoryOperand(baseRegister, addend: 0x28));
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, baseRegister, new Register(null, "X1")),
            new(1, OpCode.ConditionalJump, fieldLoad, new Register(null, "condition")),
            new(2, OpCode.Move, baseRegister, new Immediate(0x1000)) { IsPcRelativeAddress = true },
            globalLoad,
            new(4, OpCode.Return),
            fieldLoad,
            new(6, OpCode.Return),
        ]);
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var method = new InjectedMethodAnalysisContext(types.SystemObjectType, "Read", types.SystemVoidType,
            MethodAttributes.Static, []) { ControlFlowGraph = graph };

        SsaForm.Build(graph, new DominatorInfo(graph));
        LocalVariables.CreateAll(method);
        PcRelativeMemoryRecovery.Run(graph);

        var field = (MemoryOperand)fieldLoad.Operands[1];
        var global = (MemoryOperand)globalLoad.Operands[1];
        Assert.That(field.Base, Is.TypeOf<LocalVariable>());
        Assert.That(field.Addend, Is.EqualTo(0x28));
        Assert.That(global.IsConstant, Is.True);
        Assert.That(global.Addend, Is.EqualTo(0x1028));
    }

    [TestCase(0f, true, true)]
    [TestCase(float.NaN, true, false)]
    [TestCase(0f, false, false)]
    public void OppositeStrictBranchesKeepTheUnorderedPath(float constant, bool samePrecision, bool simplifies)
    {
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var valueType = samePrecision ? types.SystemSingleType : types.SystemDoubleType;
        var value = new LocalVariable("value", new Register(null, "V0"), valueType);
        var less = new LocalVariable("less", new Register(null, "N"), types.SystemBooleanType);
        var greater = new LocalVariable("greater", new Register(null, "G"), types.SystemBooleanType);
        var ordered = new Instruction(5, OpCode.Return, new Immediate(1));
        var equalOrUnordered = new Instruction(4, OpCode.Return, new Immediate(0));
        var precision = new FloatingPointPrecision(false);
        var first = new Instruction(0, OpCode.CheckLess, less, value, new FloatLiteral(constant), precision);
        var second = new Instruction(2, OpCode.CheckGreater, greater, value, new FloatLiteral(constant), precision);
        var firstJump = new Instruction(1, OpCode.ConditionalJump, ordered, less);
        var secondJump = new Instruction(3, OpCode.ConditionalJump, ordered, greater);
        var graph = new ISILControlFlowGraph([first, firstJump, second, secondJump, equalOrUnordered, ordered]);
        var firstBlock = graph.FindBlockByInstruction(first);
        var secondBlock = graph.FindBlockByInstruction(second);
        var orderedBlock = graph.FindBlockByInstruction(ordered);
        var equalOrUnorderedBlock = graph.FindBlockByInstruction(equalOrUnordered);
        var expectedTarget = simplifies ? equalOrUnorderedBlock : orderedBlock;

        FloatConditionRecovery.SimplifyOrderedInequalityBranches(graph, types);

        Assert.That(first.OpCode, Is.EqualTo(simplifies ? OpCode.CheckEqual : OpCode.CheckLess));
        Assert.That(second.OpCode, Is.EqualTo(simplifies ? OpCode.Call : OpCode.CheckGreater));
        Assert.That(firstJump.Operands[0], Is.SameAs(expectedTarget));
        Assert.That(secondJump.Operands[0], Is.SameAs(expectedTarget));
        Assert.That(firstBlock.Successors, Is.EquivalentTo(new[] { expectedTarget, secondBlock }));
        Assert.That(expectedTarget.Predecessors, Is.EquivalentTo(new[] { firstBlock, secondBlock }));
        if (simplifies)
        {
            Assert.That(((MethodAnalysisContext)second.Operands[0]).Name, Is.EqualTo("IsNaN"));
            Assert.That(second.Operands[2], Is.SameAs(value));
        }
    }
}
