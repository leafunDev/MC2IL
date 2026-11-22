using System.Collections.Generic;
using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class MethodInfoCallRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CallerHandleInALaterRegisterDoesNotMaskTheCallee(bool voidCall, bool copyHandle)
    {
        var (caller, target) = Methods();
        var handle = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var currentHandle = new RuntimeMethodInfoAnalysisContext(caller, caller.DeclaringType!.DeclaringAssembly);
        var local = new LocalVariable("targetHandle", new Register(null, "X0"));
        var result = new LocalVariable("result", new Register(null, "X0_result"));
        var callerInfo = new LocalVariable("methodInfo", new Register(null, "X2"), currentHandle);
        var operands = new List<IOperand> { Imm(0x1000) };
        if (!voidCall)
            operands.Add(result);
        operands.Add(copyHandle ? local : handle);
        operands.Add(Imm(0));
        operands.Add(callerInfo);
        var call = new Instruction(1, voidCall ? OpCode.CallVoid : OpCode.Call, operands);
        var body = new List<Instruction>();
        if (copyHandle)
            body.Add(new Instruction(0, OpCode.Move, local, handle));
        body.Add(call);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller, body), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(target));
    }

    [Test]
    public void AHandleOutsideItsHiddenArgumentSlotCannotResolveACall()
    {
        var (caller, target) = Methods();
        var handle = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var result = new LocalVariable("result", new Register(null, "X0_result"));
        var address = Imm(0x1000);
        var call = new Instruction(0, OpCode.Call, address, result, Imm(0), handle);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller, [call]), Is.False);
        Assert.That(call.Operands[0], Is.EqualTo(address));
    }

    [Test]
    public void AMisplacedHandleInALaterRegisterDoesNotMaskTheCallee()
    {
        var (caller, target) = Methods();
        var types = caller.AppContext.SystemTypes;
        var other = new InjectedMethodAnalysisContext(target.DeclaringType!, "Other", types.SystemObjectType,
            MethodAttributes.Public | MethodAttributes.Static, [types.SystemObjectType, types.SystemObjectType]);
        var correct = new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly);
        var misplaced = new RuntimeMethodInfoAnalysisContext(other, other.DeclaringType!.DeclaringAssembly);
        var result = new LocalVariable("result", new Register(null, "X0_result"));
        var call = new Instruction(0, OpCode.Call, Imm(0x1000), result, correct, misplaced);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller, [call]), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(target));
    }

    private static (MethodAnalysisContext Caller, MethodAnalysisContext Target) Methods()
    {
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var caller = new InjectedMethodAnalysisContext(types.SystemObjectType, "Caller", types.SystemObjectType,
            MethodAttributes.Public, [types.SystemObjectType]);
        var target = new InjectedMethodAnalysisContext(types.SystemObjectType, "ReadValue", types.SystemObjectType,
            MethodAttributes.Public | MethodAttributes.Static, []);
        return (caller, target);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AHandleForAnotherNativeBodyCannotMaskTheCallee(bool includeCorrectHandle)
    {
        var (caller, _) = Methods();
        var type = caller.AppContext.SystemTypes.SystemObjectType;
        var target = new NativeMethod(type, 0x1000);
        var other = new NativeMethod(type, 0x2000, type, type);
        var correct = new RuntimeMethodInfoAnalysisContext(target, type.DeclaringAssembly);
        var stale = new RuntimeMethodInfoAnalysisContext(other, type.DeclaringAssembly);
        var result = new LocalVariable("result", new Register(null, "X0_result"));
        var call = new Instruction(0, OpCode.Call, Imm(0x1000), result,
            includeCorrectHandle ? correct : Imm(0), Imm(0), stale);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller, [call]), Is.EqualTo(includeCorrectHandle));
        if (includeCorrectHandle)
            Assert.That(call.Operands[0], Is.SameAs(target));
        else
            Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    private sealed class NativeMethod(TypeAnalysisContext type, ulong address, params TypeAnalysisContext[] parameters)
        : InjectedMethodAnalysisContext(type, "Target", type, MethodAttributes.Public | MethodAttributes.Static, parameters)
    {
        public override ulong UnderlyingPointer => address;
    }
}
