using System.Linq;
using System.Reflection;
using MC2IL.Core.Analysis;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Tests.Analysis;

public class ArrayLoopRecoveryTests
{
    [SetUp]
    public void SetUp()
    {
        MC2ILApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(OpCode.CheckGreaterOrEqual, 0, 1, false, true)]
    [TestCase(OpCode.CheckGreaterOrEqual, 0, 1, true, true)]
    [TestCase(OpCode.CheckGreater, 0, 1, false, false)]
    [TestCase(OpCode.CheckGreaterOrEqual, -1, 1, false, false)]
    [TestCase(OpCode.CheckGreaterOrEqual, 0, 2, false, false)]
    public void RecoversSplitElementLoadForBoundedInductionVariable(OpCode comparison, long seed,
        long step, bool addressInferredAsArray, bool recover)
    {
        var app = MC2ILApi.CurrentAppContext!;
        var types = app.SystemTypes;
        var arrayType = new SzArrayTypeAnalysisContext(types.SystemStringType);
        var method = new InjectedMethodAnalysisContext(types.SystemObjectType, "Loop", types.SystemVoidType,
            MethodAttributes.Static, []);
        var array = Local("array", arrayType);
        var index = Local("index");
        var nextIndex = Local("nextIndex");
        var length = Local("length");
        var condition = Local("condition", types.SystemBooleanType);
        var offset = Local("offset", types.SystemInt64Type);
        var address = Local("address", addressInferredAsArray ? arrayType : null);
        var element = Local("element", types.SystemObjectType);
        var pointerSize = app.Binary.PointerSizeBytes;
        var exit = new Instruction(10, OpCode.Return);
        var headerLoad = new Instruction(1, OpCode.Move, length, new MemoryOperand(array, addend: 3 * pointerSize))
            { MemoryAccessSize = 4 };
        var load = new Instruction(6, OpCode.Move, element, new MemoryOperand(address, addend: 4 * pointerSize))
            { MemoryAccessSize = pointerSize };
        var increment = new Instruction(8, OpCode.Add, nextIndex, index, Imm(step)) { OperandWidth = 64 };
        var cfg = new ISILControlFlowGraph([
            new(0, OpCode.Jump, headerLoad),
            headerLoad,
            new(2, comparison, condition, index, length),
            new(3, OpCode.ConditionalJump, exit, condition),
            new(4, OpCode.ShiftLeft, offset, index, Imm(pointerSize == 8 ? 3 : 2)) { OperandWidth = 64 },
            new(5, OpCode.Add, address, array, offset),
            load,
            new(7, OpCode.CallVoid, Str("Consume"), element),
            increment,
            new(9, OpCode.Jump, headerLoad),
            exit,
        ]);
        var header = cfg.FindBlockByInstruction(headerLoad)!;
        var phi = new Instruction(-1, OpCode.Phi, index);
        phi.AddOperands(header.Predecessors.Select(p => p.Instructions.Contains(increment) ? (IOperand)nextIndex : Imm(seed)));
        header.Instructions.Insert(0, phi);
        method.ControlFlowGraph = cfg;

        ArrayRecovery.RecoverSplitElementAccesses(method);

        if (recover)
        {
            Assert.That(index.Type, Is.SameAs(types.SystemInt32Type));
            Assert.That(nextIndex.Type, Is.SameAs(types.SystemInt32Type));
            Assert.That(element.Type, Is.SameAs(types.SystemStringType));
            Assert.That(load.Operands[1], Is.TypeOf<ArrayAccess>());
            var access = (ArrayAccess)load.Operands[1];
            Assert.That(access.Array, Is.SameAs(array));
            Assert.That(access.Index, Is.SameAs(index));
        }
        else
        {
            Assert.That(index.Type, Is.Null);
            Assert.That(load.Operands[1], Is.TypeOf<MemoryOperand>());
        }
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type = null) =>
        new(name, new Register(null, name), type);
}
