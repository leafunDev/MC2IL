using System.Linq;
using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class InjectedNullCheckTests
{
    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void FoldsSharedNullChecksOnlyAcrossSafeArgumentEvaluation(bool extraCall, bool explicitThrow)
    {
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var source = Local("source", types.SystemObjectType);
        var receiver = Local("receiver", types.SystemObjectType);
        var sourceNull = Local("sourceNull", types.SystemBooleanType);
        var receiverNull = Local("receiverNull", types.SystemBooleanType);
        var integer = Local("integer", types.SystemInt32Type);
        var argument = Local("argument", types.SystemSingleType);
        var field = new InjectedFieldAnalysisContext("Count", types.SystemInt32Type, FieldAttributes.Public,
            types.SystemObjectType);
        var setter = new InjectedMethodAnalysisContext(types.SystemObjectType, "SetValue",
            types.SystemVoidType, MethodAttributes.Public, [types.SystemSingleType]);
        var exception = types.SystemObjectType.DeclaringAssembly.GetTypeByFullName("System.NullReferenceException")!;
        var failure = new Instruction(9, OpCode.Throw, explicitThrow ? Local("exception", exception) : exception);
        var set = new Instruction(7, OpCode.CallVoid, setter, receiver, argument);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.CheckEqual, sourceNull, source, Imm(0)),
            new(1, OpCode.ConditionalJump, failure, sourceNull),
            new(2, OpCode.CheckEqual, receiverNull, receiver, Imm(0)),
            new(3, OpCode.ConditionalJump, failure, receiverNull),
            new(4, OpCode.Move, integer, new FieldReference(field, source, 0)),
            extraCall ? new(5, OpCode.CallVoid, Str("SideEffect")) : new(5, OpCode.Nop),
            new(6, OpCode.Convert, argument, integer, new NumericConversion(NumericConversionKind.SignedInt32ToFloat32)),
            set,
            new(8, OpCode.Return),
            failure,
        ]);

        InjectedCheckRemover.Run(graph);

        Assert.That(set.NullCheckReceiver, Is.EqualTo(!extraCall && !explicitThrow));
        Assert.That(graph.Instructions.Contains(failure), Is.EqualTo(extraCall || explicitThrow));
        Assert.That(graph.Instructions.Count(i => i.OpCode == OpCode.ConditionalJump),
            Is.EqualTo(explicitThrow ? 2 : extraCall ? 1 : 0));
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type) =>
        new(name, new Register(null, name), type);
}
