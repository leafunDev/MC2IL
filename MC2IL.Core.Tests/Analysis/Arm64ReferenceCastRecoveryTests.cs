using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class Arm64ReferenceCastRecoveryTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RecoversCastOnlyWhenBothArmsWriteTheSameField(bool duplicateStore, bool differentField)
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
        var app = MC2ILApi.CurrentAppContext!;
        var objectType = app.SystemTypes.SystemObjectType;
        var targetType = app.SystemTypes.SystemStringType;
        var badCast = app.AllTypes.Single(type => type.FullName == "System.InvalidCastException");
        var self = new LocalVariable("this", new Register(null, "X19"), objectType);
        var value = new LocalVariable("value", new Register(null, "X0"), objectType);
        var klass = new LocalVariable("klass", new Register(null, "X8"), new RuntimeClassTypeAnalysisContext(objectType, objectType.DeclaringAssembly));
        var condition = new LocalVariable("condition", new Register(null, "condition"), app.SystemTypes.SystemBooleanType);
        var field = new InjectedFieldAnalysisContext("Result", targetType, FieldAttributes.Public, objectType);
        var other = new InjectedFieldAnalysisContext("OtherResult", targetType, FieldAttributes.Public, objectType);
        var destination = new FieldReference(field, self, 16);
        var store = new Instruction(0, OpCode.Move, destination, value);
        var fail = new Instruction(0, OpCode.Throw, badCast);
        var exit = new Instruction(0, OpCode.Return);
        var body = new List<Instruction>
        {
            new(0, OpCode.CheckEqual, condition, value, new Immediate(0)),
            new(0, OpCode.ConditionalJump, store, condition),
            LoadClass(),
            new(0, OpCode.CheckNotEqual, condition, klass, targetType),
            new(0, OpCode.ConditionalJump, fail, condition),
        };

        if (duplicateStore)
        {
            body.Add(new Instruction(0, OpCode.Move, new FieldReference(differentField ? other : field, self, 16), value));
            body.Add(LoadClass());
            body.Add(new Instruction(0, OpCode.CheckEqual, condition, klass, targetType));
            body.Add(new Instruction(0, OpCode.ConditionalJump, exit, condition));
            body.Add(fail);
            body.Add(store);
            body.Add(exit);
        }
        else
        {
            body.Add(store);
            body.Add(exit);
            body.Add(fail);
        }

        for (var i = 0; i < body.Count; i++)
            body[i].Index = i;
        var graph = new ISILControlFlowGraph(body);

        Arm64ReferenceCastRecovery.Run(graph);

        if (differentField)
        {
            Assert.That(store.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(graph.Instructions, Does.Contain(fail));
        }
        else
        {
            Assert.That(store.OpCode, Is.EqualTo(OpCode.CastClass));
            Assert.That(store.Operands.ToArray(), Is.EqualTo(new IOperand[] { destination, targetType, value }));
            Assert.That(graph.Instructions, Does.Not.Contain(fail));
            Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump), Is.False);
        }

        Instruction LoadClass() => new(0, OpCode.Move, klass, new MemoryOperand(value, null, 0)) { MemoryAccessSize = 8 };
    }
}
