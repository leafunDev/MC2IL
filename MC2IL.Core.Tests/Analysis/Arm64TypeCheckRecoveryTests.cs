using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MethodAttributes = System.Reflection.MethodAttributes;
using TypeAttributes = System.Reflection.TypeAttributes;

namespace MC2IL.Core.Tests.Analysis;

public class Arm64TypeCheckRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, true)]
    public void RecoversHierarchyTestWithEitherBranchDirection(bool invertDepth, bool invertType, bool compareClassLocal)
    {
        var pattern = CreatePattern();
        InvertBranch(pattern.Graph, pattern.TargetDepthRead, invertDepth, OpCode.CheckGreaterOrEqualUnsigned);
        InvertBranch(pattern.Graph, pattern.TypeCheck, invertType, OpCode.CheckEqual);
        if (compareClassLocal)
            pattern.TypeCheck.SetOperand(2, pattern.TargetLoad.Operands[0]);
        var value = ((MemoryOperand)pattern.HeaderRead.Operands[1]).Base;
        var target = pattern.TargetLoad.Operands[1];

        Arm64TypeCheckRecovery.Run(pattern.Graph);

        Assert.That(pattern.HeaderRead.OpCode, Is.EqualTo(OpCode.IsInst));
        Assert.That(pattern.HeaderRead.Operands.ToArray(), Is.EqualTo(new[] { pattern.HeaderRead.Destination, target, value }));
        Assert.That(((LocalVariable)pattern.HeaderRead.Destination!).Type, Is.SameAs(target));
        Assert.That(pattern.HeaderRead.Sources.ToArray(), Is.EqualTo(new[] { value }));
        Assert.That(pattern.Graph.Instructions.SelectMany(i => i.Operands).OfType<MemoryOperand>(), Is.Empty);
        Assert.That(pattern.Graph.Instructions, Does.Contain(pattern.Success).And.Contain(pattern.Failure));
        foreach (var block in pattern.Graph.Blocks)
        {
            Assert.That(block.Successors.All(successor => successor.Predecessors.Contains(block)), Is.True);
            Assert.That(block.Predecessors.All(predecessor => predecessor.Successors.Contains(block)), Is.True);
        }
    }

    [TestCase("depth-offset")]
    [TestCase("depth-width")]
    [TestCase("table-offset")]
    [TestCase("slot-offset")]
    [TestCase("scale")]
    [TestCase("address-width")]
    [TestCase("target")]
    [TestCase("array-target")]
    [TestCase("external-use")]
    [TestCase("external-write")]
    [TestCase("side-effect")]
    [TestCase("shared-lookup")]
    [TestCase("missing-null-proof")]
    public void KeepsSimilarCodeWhenTheFullTypeTestIsNotProven(string difference)
    {
        var pattern = CreatePattern();
        var graph = pattern.Graph;
        var guard = graph.Blocks.Single(block => block.Instructions.Contains(pattern.HeaderRead));
        var lookup = graph.Blocks.Single(block => block.Instructions.Contains(pattern.TableRead));
        switch (difference)
        {
            case "depth-offset": ChangeOffset(pattern.TargetDepthRead, 0x12D); break;
            case "depth-width": pattern.TargetDepthRead.MemoryAccessSize = 4; break;
            case "table-offset": ChangeOffset(pattern.TableRead, 0xC0); break;
            case "slot-offset": ChangeOffset(pattern.AncestorRead, 0); break;
            case "scale": pattern.Shift.SetOperand(2, Imm(2)); break;
            case "address-width": lookup.Instructions.Single(i => i.OpCode == OpCode.Add).OperandWidth = 32; break;
            case "target": pattern.TypeCheck.SetOperand(2, MC2ILApi.CurrentAppContext!.SystemTypes.SystemObjectType); break;
            case "array-target":
                var arrayType = new SzArrayTypeAnalysisContext(MC2ILApi.CurrentAppContext!.SystemTypes.SystemObjectType);
                pattern.TargetLoad.SetOperand(1, arrayType);
                ((LocalVariable)pattern.TargetLoad.Operands[0]).Type = new RuntimeClassTypeAnalysisContext(arrayType, arrayType.DeclaringAssembly);
                pattern.TypeCheck.SetOperand(2, arrayType);
                break;
            case "external-use": pattern.Success.SetOperands(pattern.TargetDepthRead.Destination!); break;
            case "external-write":
                graph.Blocks.Single(block => block.Instructions.Contains(pattern.Success)).Instructions.Insert(0,
                    new Instruction(0, OpCode.Move, pattern.HeaderRead.Destination!, Imm(0)));
                break;
            case "side-effect": lookup.Instructions.Insert(0, new Instruction(0, OpCode.Throw)); break;
            case "shared-lookup":
                graph.EntryBlock.Successors.Add(lookup);
                lookup.Predecessors.Add(graph.EntryBlock);
                break;
            case "missing-null-proof":
                var proof = guard.Predecessors.Single();
                var branch = proof.Instructions.Last();
                branch.OpCode = OpCode.Jump;
                branch.SetOperands(guard);
                graph.RemoveEdge(proof, proof.Successors.Single(successor => successor != guard));
                proof.CalculateBlockType();
                break;
        }

        Arm64TypeCheckRecovery.Run(graph);

        Assert.That(pattern.HeaderRead.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(graph.Blocks, Does.Contain(lookup));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.IsInst), Is.False);
    }

    [Test]
    public void AcceptsAnEarlierManagedDereferenceAsNonNullEvidence()
    {
        var pattern = CreatePattern();
        var guard = pattern.Graph.Blocks.Single(block => block.Instructions.Contains(pattern.HeaderRead));
        var proof = guard.Predecessors.Single();
        var value = (LocalVariable)((MemoryOperand)pattern.HeaderRead.Operands[1]).Base!;
        var method = new InjectedMethodAnalysisContext(value.Type!, "Read", MC2ILApi.CurrentAppContext!.SystemTypes.SystemVoidType,
            MethodAttributes.Public, []);
        proof.Instructions.Insert(0, new Instruction(0, OpCode.CallVoid, method, value) { NullCheckReceiver = true });
        proof.Instructions.RemoveAt(1);
        proof.Instructions.Last().OpCode = OpCode.Jump;
        proof.Instructions.Last().SetOperands(guard);
        pattern.Graph.RemoveEdge(proof, proof.Successors.Single(successor => successor != guard));
        proof.CalculateBlockType();

        Arm64TypeCheckRecovery.Run(pattern.Graph);

        Assert.That(pattern.HeaderRead.OpCode, Is.EqualTo(OpCode.IsInst));
    }

    [Test]
    public void GeneratedIlRunsForNullUnrelatedExactAndDerivedInstances()
    {
        var pattern = CreatePattern();
        var sourceType = (InjectedTypeAnalysisContext)((LocalVariable)((MemoryOperand)pattern.HeaderRead.Operands[1]).Base!).Type!;
        var targetType = (TypeAnalysisContext)pattern.TargetLoad.Operands[1];
        var value = (LocalVariable)((MemoryOperand)pattern.HeaderRead.Operands[1]).Base!;
        Arm64TypeCheckRecovery.Run(pattern.Graph);

        var assembly = new AssemblyDefinition("TypeCheckTest" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        var module = new ModuleDefinition(assembly.Name + ".dll",
            new AssemblyReference(typeof(object).Assembly.GetName().Name!, typeof(object).Assembly.GetName().Version!));
        assembly.Modules.Add(module);
        var source = new TypeDefinition("Tests", "Source", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public,
            module.CorLibTypeFactory.Object.ToTypeDefOrRef());
        var target = new TypeDefinition("Tests", "Target", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public, source);
        var derived = new TypeDefinition("Tests", "Derived", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public, target);
        foreach (var type in new[] { source, target, derived })
            module.TopLevelTypes.Add(type);
        sourceType.PutExtraData("AsmResolverType", source);
        targetType.PutExtraData("AsmResolverType", target);
        var app = MC2ILApi.CurrentAppContext!;
        var corelib = ModuleDefinition.FromFile(typeof(object).Assembly.Location);
        app.SystemTypes.SystemBooleanType.PutExtraData("AsmResolverType", corelib.GetAllTypes().Single(type => type.FullName == "System.Boolean"));
        var context = sourceType.InjectMethodContext("Check", app.SystemTypes.SystemBooleanType, MethodAttributes.Public | MethodAttributes.Static, sourceType);
        context.ControlFlowGraph = pattern.Graph;
        context.ParameterLocals = [value];
        var definition = new MethodDefinition("Check",
            AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Public | AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean, [source.ToTypeSignature()]));
        source.Methods.Add(definition);
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, value.Name, 0));

        IlGenerator.GenerateIl(context, definition);

        Assert.That(definition.CilMethodBody!.Instructions.Count(i => i.OpCode == CilOpCodes.Isinst), Is.EqualTo(1));
        Assert.That(definition.CilMethodBody.Instructions.Any(i => i.OpCode == CilOpCodes.Ldtoken), Is.False);
        using var stream = new MemoryStream();
        assembly.WriteManifest(stream, new ManagedPEImageBuilder(ThrowErrorListener.Instance));
        var loaded = System.Reflection.Assembly.Load(stream.ToArray());
        var check = loaded.GetType("Tests.Source")!.GetMethod("Check")!;
        Assert.That(check.Invoke(null, [null]), Is.EqualTo(false));
        foreach (var (type, expected) in new[] { ("Tests.Source", false), ("Tests.Target", true), ("Tests.Derived", true) })
            Assert.That(check.Invoke(null, [RuntimeHelpers.GetUninitializedObject(loaded.GetType(type)!)]), Is.EqualTo(expected));
    }

    private static Pattern CreatePattern()
    {
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var sourceType = new InjectedTypeAnalysisContext(types.SystemObjectType.DeclaringAssembly, "Tests", "Source",
            types.SystemObjectType, TypeAttributes.Public);
        var targetType = new InjectedTypeAnalysisContext(sourceType.DeclaringAssembly, "Tests", "Target", sourceType, TypeAttributes.Public);
        var value = Local("value", sourceType);
        var klass = Local("klass", new RuntimeClassTypeAnalysisContext(sourceType, sourceType.DeclaringAssembly));
        var targetClass = Local("targetClass", new RuntimeClassTypeAnalysisContext(targetType, targetType.DeclaringAssembly));
        var sourceDepth = Local("sourceDepth");
        var targetDepth = Local("targetDepth");
        var condition = Local("condition", types.SystemBooleanType);
        var typeCondition = Local("typeCondition", types.SystemBooleanType);
        var table = Local("table");
        var offset = Local("offset");
        var slot = Local("slot");
        var ancestor = Local("ancestor");
        var headerRead = Load(klass, value, 0, 8);
        var targetLoad = new Instruction(0, OpCode.Move, targetClass, targetType);
        var targetDepthRead = Load(targetDepth, targetClass, 0x12C, 1);
        var tableRead = Load(table, klass, 0xC8, 8);
        var shift = new Instruction(0, OpCode.ShiftLeft, offset, targetDepth, Imm(3)) { OperandWidth = 64 };
        var ancestorRead = Load(ancestor, slot, -8, 8);
        var typeCheck = new Instruction(0, OpCode.CheckNotEqual, typeCondition, ancestor, targetType);
        var success = new Instruction(0, OpCode.Return, Imm(1));
        var failure = new Instruction(0, OpCode.Return, Imm(0));
        var body = new List<Instruction>
        {
            new(0, OpCode.CheckEqual, Local("isNull", types.SystemBooleanType), value, Imm(0)),
        };
        body.Add(new Instruction(0, OpCode.ConditionalJump, failure, body[0].Destination!));
        body.AddRange([
            headerRead, targetLoad, Load(sourceDepth, klass, 0x12C, 1), targetDepthRead,
            new Instruction(0, OpCode.CheckLessUnsigned, condition, sourceDepth, targetDepth) { OperandWidth = 32 },
            new Instruction(0, OpCode.ConditionalJump, failure, condition),
            tableRead, shift, new Instruction(0, OpCode.Add, slot, table, offset), ancestorRead, typeCheck,
            new Instruction(0, OpCode.ConditionalJump, failure, typeCondition), success, failure,
        ]);
        for (var i = 0; i < body.Count; i++)
            body[i].Index = i;
        return new Pattern(new ISILControlFlowGraph(body), headerRead, targetLoad, targetDepthRead,
            tableRead, shift, ancestorRead, typeCheck, success, failure);
    }

    private static void InvertBranch(ISILControlFlowGraph graph, Instruction instruction, bool invert, OpCode opposite)
    {
        if (!invert)
            return;
        var block = graph.Blocks.Single(block => block.Instructions.Contains(instruction));
        var branch = block.Instructions.Last();
        branch.SetOperand(0, block.Successors.Single(successor => successor != branch.Operands[0]));
        block.Instructions[^2].OpCode = opposite;
    }

    private static void ChangeOffset(Instruction instruction, long offset)
    {
        var memory = (MemoryOperand)instruction.Operands[1];
        memory.Addend = offset;
        instruction.SetOperand(1, memory);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type = null) => new(name, new Register(null, name), type);
    private static Instruction Load(LocalVariable result, LocalVariable address, long offset, int size) =>
        new(0, OpCode.Move, result, new MemoryOperand(address, null, offset)) { MemoryAccessSize = size };

    private sealed record Pattern(ISILControlFlowGraph Graph, Instruction HeaderRead, Instruction TargetLoad,
        Instruction TargetDepthRead, Instruction TableRead, Instruction Shift, Instruction AncestorRead,
        Instruction TypeCheck, Instruction Success, Instruction Failure);
}
