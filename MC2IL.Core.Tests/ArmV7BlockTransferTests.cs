using System;
using System.Linq;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests;

public class ArmV7BlockTransferTests
{
    private sealed class SyntheticMethod(bool thumb) : MethodAnalysisContext(null!)
    {
        public override ulong UnderlyingPointer => thumb ? 0x1001UL : 0x1000UL;
    }

    [TestCase("12008DE8", 0)] // STMIA sp, {r1, r4}
    [TestCase("12008DE9", 4)] // STMIB
    [TestCase("12000DE8", -4)] // STMDA
    [TestCase("12000DE9", -8)] // STMDB
    public void ArmStoresUseAscendingStackSlots(string bytes, int firstOffset)
    {
        var code = Lift(bytes);
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.Move }));
        Assert.That(((Register)code[0].Operands[1]).Name, Is.EqualTo("r1"));
        Assert.That(((Register)code[1].Operands[1]).Name, Is.EqualTo("r4"));
        Assert.That(code.Select(i => ((StackOffset)i.Operands[0]).Offset),
            Is.EqualTo(new[] { firstOffset, firstOffset + 4 }));
        Assert.That(code.Select(i => i.MemoryAccessSize), Is.All.EqualTo(4));
    }

    [TestCase("12009DE8", 0)] // LDMIA sp, {r1, r4}
    [TestCase("12009DE9", 4)] // LDMIB
    [TestCase("12001DE8", -4)] // LDMDA
    [TestCase("12001DE9", -8)] // LDMDB
    public void ArmLoadsReadAscendingStackSlots(string bytes, int firstOffset)
    {
        var code = Lift(bytes);
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.Move }));
        Assert.That(code.Select(i => ((Register)i.Operands[0]).Name), Is.EqualTo(new[] { "r1", "r4" }));
        Assert.That(code.Select(i => ((StackOffset)i.Operands[1]).Offset),
            Is.EqualTo(new[] { firstOffset, firstOffset + 4 }));
        Assert.That(code.Select(i => i.MemoryAccessSize), Is.All.EqualTo(4));
    }

    [Test]
    public void StackWritebackOccursAfterAllTransfers()
    {
        // STMIA sp!, {r1, r4}
        var code = Lift("1200ADE8");
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.Move, OpCode.ShiftStack }));
        Assert.That(code.Take(2).Select(i => ((StackOffset)i.Operands[0]).Offset), Is.EqualTo(new[] { 0, 4 }));
        Assert.That(((Immediate)code[2].Operands[0]).Value, Is.EqualTo(8));
    }

    [Test]
    public void ThumbLoadReadsSeparateStackSlots()
    {
        // LDM.W sp, {r1, r4}, without writeback.
        var code = Lift("9DE81200", thumb: true);
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.Move }));
        Assert.That(code.Select(i => ((Register)i.Operands[0]).Name), Is.EqualTo(new[] { "r1", "r4" }));
        Assert.That(code.Select(i => ((StackOffset)i.Operands[1]).Offset), Is.EqualTo(new[] { 0, 4 }));
    }

    [TestCase("1CCA", true)] // LDM r2, {r2, r3, r4}, without writeback.
    [TestCase("120086E8", false)] // STM r6, {r1, r4}
    [TestCase("1200CDE8", false)] // STM sp, {r1, r4}^ (user-mode bank)
    [TestCase("02809DE8", false)] // LDM sp, {r1, pc}, without writeback
    public void UnsupportedTransferSemanticsStayExplicit(string bytes, bool thumb)
    {
        var code = Lift(bytes, thumb);
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.NotImplemented }));
    }

    [Test]
    public void ArmStackStoreCanBePredicatedWithoutChangingTheStackPointer()
    {
        // STMIBNE sp, {r1, r4}; NOP
        var code = Lift("12008D1900F020E3");
        var branch = Array.FindIndex(code, i => i.OpCode == OpCode.ConditionalJump);
        Assert.That(branch, Is.GreaterThanOrEqualTo(0));
        Assert.That(code.Skip(branch).Select(i => i.OpCode),
            Is.EqualTo(new[] { OpCode.ConditionalJump, OpCode.Move, OpCode.Move, OpCode.Nop }));
        Assert.That(code[branch].Operands[0], Is.SameAs(code[^1]));
        Assert.That(((StackOffset)code[branch + 1].Operands[0]).Offset, Is.EqualTo(4));
        Assert.That(((StackOffset)code[branch + 2].Operands[0]).Offset, Is.EqualTo(8));
    }

    [Test]
    public void SingleRegisterDecrementAfterStillMovesTheStackPointerDown()
    {
        // STMDA sp!, {r1} stores at old SP, then subtracts one word.
        var code = Lift("02002DE8");
        Assert.That(code.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.ShiftStack }));
        Assert.That(((StackOffset)code[0].Operands[0]).Offset, Is.Zero);
        Assert.That(((Immediate)code[1].Operands[0]).Value, Is.EqualTo(-4));
    }

    private static Instruction[] Lift(string bytes, bool thumb = false)
    {
        var method = new SyntheticMethod(thumb);
        using var disassembler = CapstoneDisassembler.CreateArmDisassembler(thumb ? ArmDisassembleMode.Thumb : ArmDisassembleMode.Arm);
        disassembler.EnableInstructionDetails = true;
        var instructions = disassembler.Iterate(Convert.FromHexString(bytes), 0x1000).ToList();
        return new ArmV7InstructionSet.Lifter(method, instructions).Lift().ToArray();
    }
}
