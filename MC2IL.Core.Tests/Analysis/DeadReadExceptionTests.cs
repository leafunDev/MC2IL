using System.Reflection;
using System.Runtime.CompilerServices;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class DeadReadExceptionTests
{
    [Test]
    public void KeepsUnusedArrayLengthReadThatCanThrowForNull()
    {
        var array = new LocalVariable("array", new Register(null, "array"));
        var length = new LocalVariable("length", new Register(null, "length"));
        var read = new Instruction(0, OpCode.Move, length, new ArrayLength(array));
        var graph = new ISILControlFlowGraph([read, new Instruction(1, OpCode.Return)]);

        DeadCodeEliminator.Run(graph);

        Assert.That(read.OpCode, Is.EqualTo(OpCode.Move));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void KeepsUnusedFieldReadWithNullOrClassInitializationEffects(bool isStatic)
    {
        var field = (FieldAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(FieldAnalysisContext));
        field.OverrideAttributes = isStatic ? FieldAttributes.Static : FieldAttributes.Public;
        var receiver = new LocalVariable("receiver", new Register(null, "receiver"));
        var value = new LocalVariable("value", new Register(null, "value"));
        var read = new Instruction(0, OpCode.Move, value, new FieldReference(field, receiver, 0));
        var graph = new ISILControlFlowGraph([read, new Instruction(1, OpCode.Return)]);

        DeadCodeEliminator.Run(graph);

        Assert.That(read.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    public void KeepsOriginalValuePassedToCallOverwritingSameLocal()
    {
        var value = new LocalVariable("value", new Register(null, "value"));
        var definition = new Instruction(0, OpCode.Move, value, Imm(42));
        var graph = new ISILControlFlowGraph([
            definition,
            new Instruction(1, OpCode.Call, Str("UserMethod"), value, value),
            new Instruction(2, OpCode.Return),
        ]);

        DeadCodeEliminator.Run(graph);

        Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
    }
}
