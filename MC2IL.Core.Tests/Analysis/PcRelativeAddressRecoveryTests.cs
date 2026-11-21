using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;

namespace MC2IL.Core.Tests.Analysis;

public class PcRelativeAddressRecoveryTests
{
    [Test]
    public void KeepsObjectBaseWhenAnotherVersionOfTheRegisterHoldsAPage()
    {
        var register = new Register(null, "X21");
        var receiver = new LocalVariable("receiver", register.Copy(1));
        var page = new LocalVariable("page", register.Copy(2));
        var field = new LocalVariable("field", new Register(null, "X0"));
        var load = new Instruction(2, OpCode.Move, field, new MemoryOperand(receiver, addend: 40));
        var graph = new ISILControlFlowGraph(
        [
            new Instruction(0, OpCode.Move, receiver, new Register(null, "argument")),
            new Instruction(1, OpCode.Move, page, Imm(0x10000)) { IsPcRelativeAddress = true },
            load,
            new Instruction(3, OpCode.Return, field),
        ]);

        PcRelativeMemoryRecovery.Run(graph);

        var memory = (MemoryOperand)load.Operands[1];
        Assert.That(memory.Base, Is.SameAs(receiver));
        Assert.That(memory.Addend, Is.EqualTo(40));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void FoldsOnlyProvenPcRelativeAddressesWithoutAnIndex(bool pcRelative, bool indexed)
    {
        var page = new LocalVariable("page", new Register(null, "X8"));
        var index = indexed ? new LocalVariable("index", new Register(null, "X9")) : null;
        var value = new LocalVariable("value", new Register(null, "X0"));
        var load = new Instruction(1, OpCode.Move, value,
            new MemoryOperand(page, index, -8, indexed ? 8 : 0));
        var store = new Instruction(2, OpCode.Move, new MemoryOperand(page, addend: 24), value);
        var graph = new ISILControlFlowGraph(
        [
            new Instruction(0, OpCode.Move, page, Imm(0x10000)) { IsPcRelativeAddress = pcRelative },
            load,
            store,
            new Instruction(3, OpCode.Return),
        ]);

        PcRelativeMemoryRecovery.Run(graph);

        var memory = (MemoryOperand)load.Operands[1];
        Assert.That(memory.IsConstant, Is.EqualTo(pcRelative && !indexed));
        Assert.That(memory.Addend, Is.EqualTo(pcRelative && !indexed ? 0xFFF8 : -8));
        if (!memory.IsConstant)
            Assert.That(memory.Base, Is.SameAs(page));
        var destination = (MemoryOperand)store.Operands[0];
        Assert.That(destination.IsConstant, Is.EqualTo(pcRelative));
        Assert.That(destination.Addend, Is.EqualTo(pcRelative ? 0x10018 : 24));
    }
}
