using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class FloatBranchRecoveryTests
{
    private MethodAnalysisContext _singleIsNaN = null!;
    private MethodAnalysisContext _doubleIsNaN = null!;
    private SystemTypesContext _types = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        _types = app.SystemTypes;
        _singleIsNaN = _types.SystemSingleType.Methods.Single(method => method.Name == "IsNaN");
        _doubleIsNaN = _types.SystemDoubleType.Methods.Single(method => method.Name == "IsNaN");
    }

    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public void PreservesOrderedInequalityIncludingNaNAndInfinities(bool isDouble, bool reversed, bool literalOnLeft)
    {
        var (graph, input, first, second) = MakeGraph(isDouble, reversed, literalOnLeft);
        double[] values = [-1, -0.0, 0, 1, double.NegativeInfinity, double.PositiveInfinity, double.NaN];
        var before = values.Select(value => Evaluate(graph, input, value)).ToArray();

        FloatConditionRecovery.SimplifyOrderedInequalityBranches(graph, _types);

        Assert.That(first.OpCode, Is.EqualTo(OpCode.CheckEqual));
        Assert.That(second.OpCode, Is.EqualTo(OpCode.Call));
        Assert.That(second.Operands[0], Is.SameAs(isDouble ? _doubleIsNaN : _singleIsNaN));
        for (var i = 0; i < values.Length; i++)
        {
            Assert.That(before[i], Is.EqualTo(values[i] == 0 || double.IsNaN(values[i])));
            Assert.That(Evaluate(graph, input, values[i]), Is.EqualTo(before[i]), $"input = {values[i]}");
        }
        foreach (var block in graph.Blocks)
            foreach (var successor in block.Successors)
                Assert.That(successor.Predecessors, Does.Contain(block));
    }

    [TestCase("sideEffect")]
    [TestCase("differentInput")]
    [TestCase("differentPrecision")]
    [TestCase("extraPredecessor")]
    [TestCase("reusedCondition")]
    [TestCase("wrongType")]
    [TestCase("nanLiteral")]
    public void LeavesUnsafeOrUnrepresentableBranchesUnchanged(string reason)
    {
        var (graph, input, first, second) = MakeGraph(false, false, false);
        var secondBlock = graph.Blocks.Single(block => block.Instructions.Contains(second));
        switch (reason)
        {
            case "sideEffect":
                secondBlock.Instructions.Insert(0, new Instruction(-1, OpCode.CallVoid, Str("sideEffect")));
                break;
            case "differentInput":
                second.SetOperand(1, new LocalVariable("other", new Register(null, "other")));
                break;
            case "differentPrecision":
                second.SetOperand(3, new FloatingPointPrecision(true));
                break;
            case "extraPredecessor":
                secondBlock.Predecessors.Add(graph.EntryBlock);
                break;
            case "reusedCondition":
                graph.Blocks.First(block => block.Instructions.Any(instruction => instruction.OpCode == OpCode.Return))
                    .Instructions.Insert(0, new Instruction(-1, OpCode.CallVoid, Str("observe"), first.Operands[0]));
                break;
            case "wrongType":
                input.Type = _types.SystemDoubleType;
                break;
            case "nanLiteral":
                first.SetOperand(2, new FloatLiteral(float.NaN));
                second.SetOperand(2, new FloatLiteral(float.NaN));
                break;
        }

        FloatConditionRecovery.SimplifyOrderedInequalityBranches(graph, _types);

        Assert.That(first.OpCode, Is.EqualTo(OpCode.CheckLess));
        Assert.That(second.OpCode, Is.EqualTo(OpCode.CheckGreater));
    }

    private (ISILControlFlowGraph Graph, LocalVariable Input, Instruction First, Instruction Second)
        MakeGraph(bool isDouble, bool reversed, bool literalOnLeft)
    {
        var input = new LocalVariable("input", new Register(null, "input"),
            isDouble ? _types.SystemDoubleType : _types.SystemSingleType);
        var a = new LocalVariable("first", new Register(null, "first"), _types.SystemBooleanType);
        var b = new LocalVariable("second", new Register(null, "second"), _types.SystemBooleanType);
        IOperand zero = isDouble ? new DoubleLiteral(0) : new FloatLiteral(0);
        IOperand left = literalOnLeft ? zero : input;
        IOperand right = literalOnLeft ? input : zero;
        var precision = new FloatingPointPrecision(isDouble);
        var first = new Instruction(0, reversed ? OpCode.CheckGreater : OpCode.CheckLess, a, left, right, precision);
        var second = new Instruction(2, reversed ? OpCode.CheckLess : OpCode.CheckGreater, b, left, right, precision);
        var ordered = new Instruction(5, OpCode.Return, Imm(0));
        var graph = new ISILControlFlowGraph(
        [
            first,
            new Instruction(1, OpCode.ConditionalJump, ordered, a),
            second,
            new Instruction(3, OpCode.ConditionalJump, ordered, b),
            new Instruction(4, OpCode.Return, Imm(1)),
            ordered,
        ]);
        return (graph, input, first, second);
    }

    // Execute the actual CFG before and after rewriting, including both branch targets.
    private static bool Evaluate(ISILControlFlowGraph graph, LocalVariable input, double value)
    {
        var locals = new Dictionary<LocalVariable, double> { [input] = value };
        var block = graph.EntryBlock;
        for (var steps = 0; steps < 32; steps++)
        {
            Block? next = null;
            foreach (var instruction in block.Instructions)
            {
                switch (instruction.OpCode)
                {
                    case OpCode.CheckLess:
                    case OpCode.CheckGreater:
                    case OpCode.CheckEqual:
                        var left = Read(instruction.Operands[1]);
                        var right = Read(instruction.Operands[2]);
                        locals[(LocalVariable)instruction.Operands[0]] = (instruction.OpCode switch
                        {
                            OpCode.CheckLess => left < right,
                            OpCode.CheckGreater => left > right,
                            _ => left == right,
                        }) ? 1 : 0;
                        break;
                    case OpCode.Call:
                        locals[(LocalVariable)instruction.Operands[1]] = double.IsNaN(Read(instruction.Operands[2])) ? 1 : 0;
                        break;
                    case OpCode.ConditionalJump:
                        next = Read(instruction.Operands[1]) != 0
                            ? (Block)instruction.Operands[0]
                            : block.Successors.Single(successor => successor != instruction.Operands[0]);
                        break;
                    case OpCode.Return:
                        return Read(instruction.Operands[0]) != 0;
                }
            }
            block = next ?? block.Successors.Single();
        }
        throw new InvalidOperationException("The comparison graph did not return.");

        double Read(IOperand operand) => operand switch
        {
            LocalVariable local => locals[local],
            FloatLiteral literal => literal.Value,
            DoubleLiteral literal => literal.Value,
            Immediate literal => literal.Value,
            _ => throw new InvalidOperationException($"Unexpected operand: {operand}"),
        };
    }
}
