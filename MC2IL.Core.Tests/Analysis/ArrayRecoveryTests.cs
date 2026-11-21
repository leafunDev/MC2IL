using System.Linq;
using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class ArrayRecoveryTests
{
    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RemovesNullGuardOnlyWhenSharedLengthReadUsesCheckedArray(bool copyCheckedArray)
    {
        var types = MC2ILApi.CurrentAppContext!.SystemTypes;
        var exception = types.SystemObjectType.DeclaringAssembly.GetTypeByFullName("System.NullReferenceException")!;
        var arrayType = new SzArrayTypeAnalysisContext(types.SystemObjectType);
        var array = Local("array", arrayType);
        var other = Local("other", arrayType);
        var selected = Local("selected", arrayType);
        var isNull = Local("isNull", types.SystemBooleanType);
        var isNonNull = Local("isNonNull", types.SystemBooleanType);
        var otherPath = new Instruction(6, OpCode.Move, selected, other);
        var length = new Instruction(8, OpCode.Return, new ArrayLength(selected));
        var failure = new Instruction(5, OpCode.Throw, exception);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ConditionalJump, otherPath, Local("takeOtherPath", types.SystemBooleanType)),
            new Instruction(1, OpCode.CheckEqual, isNull, array, Imm(0)),
            new Instruction(2, OpCode.Not, isNonNull, isNull),
            new Instruction(3, OpCode.Move, selected, copyCheckedArray ? array : other),
            new Instruction(4, OpCode.ConditionalJump, length, isNonNull),
            failure,
            otherPath,
            new Instruction(7, OpCode.Jump, length),
            length,
        ]);

        InjectedCheckRemover.Run(graph);

        Assert.That(graph.Instructions.Contains(failure), Is.EqualTo(!copyCheckedArray));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void RecoversElementLoadWhenLoopBoundProvesIndexFitsInt32(bool strictBound, bool splitAddress)
    {
        var app = MC2ILApi.CurrentAppContext!;
        var types = app.SystemTypes;
        var arrayType = new SzArrayTypeAnalysisContext(types.SystemObjectType);
        var method = new InjectedMethodAnalysisContext(types.SystemObjectType, "Loop", types.SystemVoidType,
            MethodAttributes.Static, []);
        var array = Local("array", arrayType);
        var index = Local("index");
        var nextIndex = Local("nextIndex");
        var length = Local("length", types.SystemInt32Type);
        var condition = Local("condition", types.SystemBooleanType);
        var offset = Local("offset");
        var address = Local("address", arrayType);
        var element = Local("element");
        var exit = new Instruction(10, OpCode.Return);
        var header = new Instruction(1, OpCode.Move, length, new ArrayLength(array));
        var stride = app.Binary.PointerSizeBytes;
        var memory = splitAddress ? new MemoryOperand(address, addend: 4 * stride) : new MemoryOperand(array, index, 4 * stride, stride);
        var elementLoad = new Instruction(6, OpCode.Move, element, memory)
        {
            MemoryAccessSize = stride,
        };
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Jump, header),
            header,
            new Instruction(2, strictBound ? OpCode.CheckGreaterOrEqual : OpCode.CheckGreater, condition, index, length),
            new Instruction(3, OpCode.ConditionalJump, exit, condition),
            new Instruction(4, OpCode.ShiftLeft, offset, index, Imm(3)) { OperandWidth = 64 },
            new Instruction(5, OpCode.Add, address, array, offset) { OperandWidth = 64 },
            elementLoad,
            new Instruction(7, OpCode.CallVoid, Str("Consume"), element),
            new Instruction(8, OpCode.Add, nextIndex, index, Imm(1)) { OperandWidth = 64 },
            new Instruction(9, OpCode.Jump, header),
            exit,
        ]);
        var headerBlock = graph.Blocks.Single(block => block.Instructions.Contains(header));
        var phi = new Instruction(-1, OpCode.Phi, index);
        phi.AddOperands(headerBlock.Predecessors.Select(block => block.Instructions.Any(i => i.Destination == nextIndex)
            ? (IOperand)nextIndex : Imm(0)));
        headerBlock.Instructions.Insert(0, phi);
        method.ControlFlowGraph = graph;

        ArrayRecovery.RecoverSplitElementAccesses(method);

        if (strictBound)
        {
            Assert.That(index.Type, Is.SameAs(types.SystemInt32Type));
            Assert.That(nextIndex.Type, Is.SameAs(types.SystemInt32Type));
            Assert.That(elementLoad.Operands[1], Is.TypeOf<ArrayAccess>());
            var access = (ArrayAccess)elementLoad.Operands[1];
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
            Assert.That(element.Type, Is.SameAs(types.SystemObjectType));
        }
        else
        {
            Assert.That(index.Type, Is.Null);
            Assert.That(elementLoad.Operands[1], Is.TypeOf<MemoryOperand>());
        }
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type = null) => new(name, new Register(null, name), type);
}
