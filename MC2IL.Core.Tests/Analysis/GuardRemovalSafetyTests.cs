using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Tests.Analysis;

public class GuardRemovalSafetyTests
{
    private static LocalVariable Local(string name) => new(name, new Register(null, name));

    private static ISILControlFlowGraph Build(List<Instruction> instructions)
    {
        for (var i = 0; i < instructions.Count; i++)
            instructions[i].Index = i;
        return new ISILControlFlowGraph(instructions);
    }

    private static ISILControlFlowGraph ClassGuard(List<Instruction> body, Instruction merge,
        out LocalVariable klass)
    {
        klass = Local("klass");
        var flag = Local("flag");
        var condition = Local("condition");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, klass, new MemoryOperand(null, null, 0x1000)),
            new(0, OpCode.Move, flag, new MemoryOperand(klass, null, 0xE0)),
            new(0, OpCode.CheckEqual, condition, flag, Imm(0)),
            new(0, OpCode.ConditionalJump, merge, condition),
            new(0, OpCode.CallVoid, Str("il2cpp_codegen_runtime_class_init"), klass),
        };
        instructions.AddRange(body);
        instructions.Add(new Instruction(0, OpCode.Jump, merge));
        instructions.Add(merge);
        return Build(instructions);
    }

    [Test]
    public void RemovesRecognizedClassInitializerAndDeadFlagReads()
    {
        var graph = ClassGuard([], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Any(i => i.OpCode is OpCode.CallVoid or OpCode.ConditionalJump), Is.False);
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Move), Is.False);
    }

    [Test]
    public void KeepsValueUsedAfterInitializerRegion()
    {
        var value = Local("value");
        var definition = new Instruction(0, OpCode.Move, value, Imm(42));
        var graph = ClassGuard([definition], new Instruction(0, OpCode.Return, value), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(definition), Is.True);
        Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump), Is.True);
    }

    [Test]
    public void KeepsValueUsedInsideSurvivingArrayOperand()
    {
        var index = Local("index");
        var definition = new Instruction(0, OpCode.Move, index, Imm(42));
        var array = Local("array");
        var graph = ClassGuard([definition],
            new Instruction(0, OpCode.Return, new ArrayAccess(array, index)), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(definition), Is.True);
        Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    public void RemovesOnlyPhiInputFromDiscardedPredecessor()
    {
        var fromRegion = Local("fromRegion");
        var fallback = Local("fallback");
        var selected = Local("selected");
        var definition = new Instruction(0, OpCode.Move, fromRegion, Imm(42));
        var phi = new Instruction(0, OpCode.Phi, selected);
        var graph = ClassGuard([definition], phi, out _);
        var merge = graph.Blocks.Single(block => block.Instructions.Contains(phi));
        phi.AddOperands(merge.Predecessors.Select(predecessor => (IOperand)
            (predecessor.Instructions.Contains(definition) ? fromRegion : fallback)));
        merge.Instructions.Add(new Instruction(100, OpCode.Return, selected));

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(phi.Operands.Count, Is.EqualTo(2));
        Assert.That(phi.Operands[1], Is.SameAs(fallback));
        Assert.That(merge.Predecessors.Count, Is.EqualTo(1));
    }

    [Test]
    public void KeepsUnrelatedGlobalWriteInsideInitializerRegion()
    {
        var store = new Instruction(0, OpCode.Move, new MemoryOperand(null, null, 0x2000), Imm(7));
        var graph = ClassGuard([store], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(store), Is.True);
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump), Is.True);
    }

    [Test]
    public void KeepsPotentiallyThrowingDivisionInsideInitializerRegion()
    {
        var divide = new Instruction(0, OpCode.Divide, Local("quotient"), Imm(1), Local("divisor"));
        var graph = ClassGuard([divide], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(divide), Is.True);
        Assert.That(divide.OpCode, Is.EqualTo(OpCode.Divide));
    }

    [Test]
    public void KeepsPotentialNullExceptionFromUnusedArrayLengthInInitializerRegion()
    {
        var read = new Instruction(0, OpCode.Move, Local("length"), new ArrayLength(Local("array")));
        var graph = ClassGuard([read], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(read), Is.True);
        Assert.That(read.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    public void KeepsUnrelatedDynamicMemoryReadInsideInitializerRegion()
    {
        var read = new Instruction(0, OpCode.Move, Local("unused"), new MemoryOperand(Local("object"), null, 0x10));
        var graph = ClassGuard([read], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(read), Is.True);
        Assert.That(read.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    public void RemovesProvenMetadataSlotIndirectionInsideInitializerRegion()
    {
        var slot = Local("slot");
        var runtimeClass = Local("runtimeClass");
        var graph = ClassGuard([
            new Instruction(0, OpCode.Move, slot, new MemoryOperand(null, null, 0x5000)),
            new Instruction(0, OpCode.Move, runtimeClass, new MemoryOperand(slot, null, 0)),
            new Instruction(0, OpCode.CallVoid, Str("il2cpp_codegen_runtime_class_init"), runtimeClass),
        ], new Instruction(0, OpCode.Return), out _);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Any(i => i.OpCode is OpCode.Move or OpCode.ConditionalJump), Is.False);
    }

    [TestCase(0x3000, 1, true)]
    [TestCase(0x4000, 1, false)]
    [TestCase(0x3000, 7, false)]
    public void RemovesOnlyMatchingMetadataInitializedFlagStore(long storeAddress, long storedValue, bool remove)
    {
        var flag = Local("flag");
        var condition = Local("condition");
        var merge = new Instruction(0, OpCode.Return);
        var metadataInit = new Instruction(0, OpCode.CallVoid,
            Str("il2cpp_codegen_initialize_runtime_metadata"), Imm(0x5000));
        var store = new Instruction(0, OpCode.Move, new MemoryOperand(null, null, storeAddress), Imm(storedValue));
        var graph = Build([
            new(0, OpCode.Move, flag, new MemoryOperand(null, null, 0x3000)),
            new(0, OpCode.And, condition, flag, Imm(1)),
            new(0, OpCode.ConditionalJump, merge, condition),
            metadataInit,
            store,
            new(0, OpCode.Jump, merge),
            merge,
        ]);

        MetadataInitGuardRemover.Run(graph, 0xE0);

        Assert.That(graph.Instructions.Contains(store), Is.EqualTo(!remove));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump), Is.EqualTo(!remove));
    }
}
