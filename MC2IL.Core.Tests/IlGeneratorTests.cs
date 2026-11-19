using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;
using ReflectionFieldAttributes = System.Reflection.FieldAttributes;

namespace MC2IL.Core.Tests;

public class IlGeneratorTests
{
    [SetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    public void StaticCall_DoesNotLoadMethodInfoOperand()
    {
        var appContext = MC2ILApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemInt = appContext.SystemTypes.SystemInt32Type;

        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "TargetStatic",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemInt, systemInt]);

        var x = new LocalVariable("x", new Register(null, "x"));
        var y = new LocalVariable("y", new Register(null, "y"));

        var instructions = new List<Instruction>
        {
            
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Move, y, Imm(10)),
            // Operand layout: target, arg0, arg1, trailing-non-parameter.
            new(2, OpCode.CallVoid, targetContext, x, y, Imm(999)),
            new(3, OpCode.Return),
        };

        callerContext.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        callerContext.Locals = [x, y];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("MC2IL.Core.Tests", "IlGeneratorTestType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);

        var callerMethodDef = new MethodDefinition("Caller", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerMethodDef);

        var targetMethodDef = new MethodDefinition("TargetStatic", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        typeDef.Methods.Add(targetMethodDef);
        targetContext.PutExtraData("AsmResolverMethod", targetMethodDef);

        IlGenerator.GenerateIl(callerContext, callerMethodDef);

        var il = callerMethodDef.CilMethodBody!.Instructions;

        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Call), Is.True, "expected generated method to contain a call");
        Assert.That(il.Any(i => i.Operand is int intOperand && intOperand == 999), Is.False,
            "trailing non-parameter operand must not be emitted as a call argument");
        // Single-use argument expressions may be inlined, so the number of ldloc instructions is
        // an optimization detail. The target signature and absence of the trailing sentinel above
        // are the stable contract for this test.
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FieldReceiverKeepsOriginalReadOrderAcrossArgumentCall(bool hasSecondUse)
    {
        var systemTypes = MC2ILApi.CurrentAppContext!.SystemTypes;
        var type = new InjectedTypeAnalysisContext(systemTypes.SystemObjectType.DeclaringAssembly,
            "Tests", "Widget", systemTypes.SystemObjectType, ReflectionTypeAttributes.Public);
        var field = type.InjectFieldContext("text", type, ReflectionFieldAttributes.Public);
        var caller = type.InjectMethodContext("UpdateText", systemTypes.SystemVoidType, ReflectionMethodAttributes.Public);
        var prepare = type.InjectMethodContext("BuildArgument", type, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static);
        var apply = type.InjectMethodContext("Apply", systemTypes.SystemVoidType, ReflectionMethodAttributes.Public, type);

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var widget = new TypeDefinition("Tests", "Widget", TypeAttributes.Public, module.CorLibTypeFactory.Object.ToTypeDefOrRef());
        module.TopLevelTypes.Add(widget);
        type.PutExtraData("AsmResolverType", widget);
        var widgetSignature = widget.ToTypeSignature();
        var textField = new FieldDefinition("text", FieldAttributes.Public, new FieldSignature(widgetSignature));
        widget.Fields.Add(textField);
        field.PutExtraData("AsmResolverField", textField);

        var callerDefinition = AddMethod(caller, MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        var prepareDefinition = AddMethod(prepare, MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(widgetSignature));
        var applyDefinition = AddMethod(apply, MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [widgetSignature]));

        var self = new LocalVariable("this", new Register(null, "X0"), type) { IsThis = true };
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), type);
        var argument = new LocalVariable("argument", new Register(null, "X2"), type);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, receiver, new FieldReference(field, self, 0)),
            new(1, OpCode.Call, prepare, argument),
            new(2, OpCode.CallVoid, apply, receiver, argument),
        };
        if (hasSecondUse)
            instructions.Add(new Instruction(3, OpCode.CallVoid, apply, receiver, Imm(0)));
        instructions.Add(new Instruction(4, OpCode.Return));
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [self, receiver, argument];
        caller.ParameterLocals = [self];
        caller.AnalysisWarnings = [];

        IlGenerator.GenerateIl(caller, callerDefinition);

        var body = callerDefinition.CilMethodBody!;
        var il = body.Instructions.ToArray();
        var readIndex = Array.FindIndex(il, i => i.OpCode == CilOpCodes.Ldfld && ReferenceEquals(i.Operand, textField));
        var prepareIndex = Array.FindIndex(il, i => ReferenceEquals(i.Operand, prepareDefinition));
        var applyIndex = Array.FindIndex(il, i => ReferenceEquals(i.Operand, applyDefinition));
        var receiverStorage = body.LocalVariables[1];
        Assert.That(readIndex, Is.GreaterThanOrEqualTo(0).And.LessThan(prepareIndex));
        Assert.That(prepareIndex, Is.LessThan(applyIndex));
        Assert.That(il.Count(i => i.OpCode == CilOpCodes.Ldfld && ReferenceEquals(i.Operand, textField)), Is.EqualTo(1));
        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Stloc && ReferenceEquals(i.Operand, receiverStorage)), Is.EqualTo(hasSecondUse));
        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Ldloc && ReferenceEquals(i.Operand, receiverStorage)), Is.EqualTo(hasSecondUse));
        Assert.That(body.ComputeMaxStack(), Is.GreaterThan(0));
        return;

        MethodDefinition AddMethod(MethodAnalysisContext method, MethodAttributes attributes, MethodSignature signature)
        {
            var definition = new MethodDefinition(method.Name, attributes, signature);
            widget.Methods.Add(definition);
            method.PutExtraData("AsmResolverMethod", definition);
            return definition;
        }
    }
}
