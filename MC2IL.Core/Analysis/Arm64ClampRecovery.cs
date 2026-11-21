using System;
using System.Linq;
using Disarm;
using Disarm.InternalDisassembly;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.Analysis;

internal static class Arm64ClampRecovery
{
    internal static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        MethodAnalysisContext? clamp = null;
        foreach (var block in graph.Blocks)
        {
            if (block.Successors is not [_, _] || block.Instructions.Count < 3
                || block.Instructions[^1] is not
                    { OpCode: OpCode.ConditionalJump, Operands: [Block belowZero, LocalVariable condition] } branch
                || !block.Successors.Contains(belowZero)
                || block.Instructions[^2] is not { OpCode: OpCode.CheckLess,
                    Operands: [LocalVariable comparison, LocalVariable value, FloatLiteral { Value: 0 },
                        FloatingPointPrecision { IsDouble: false }] } || !ReferenceEquals(comparison, condition)
                || block.Instructions[^3] is not { OpCode: OpCode.Call, IsLiftedFloatMin: true,
                    Operands: [MethodAnalysisContext { Name: "Min", DeclaringType: { FullName: "UnityEngine.Mathf" } mathf },
                        LocalVariable minimum, LocalVariable minInput, FloatLiteral { Value: 1 }] } min
                || !ReferenceEquals(minInput, value) || value.Type?.FullName != "System.Single")
                continue;

            var nonnegative = block.Successors.First(successor => successor != belowZero);
            if (belowZero.Predecessors is not [var negativeSource] || negativeSource != block
                || nonnegative.Predecessors is not [var nonnegativeSource] || nonnegativeSource != block
                || belowZero.Successors is not [var join] || nonnegative.Successors is not [var otherJoin] || otherJoin != join
                || join == block || join == belowZero || join == nonnegative
                || GetArmCopy(belowZero, join) is not
                    { Operands: [LocalVariable { Type.FullName: "System.Single" } output, FloatLiteral { Value: 0 } zero] }
                || BitConverter.ToInt32(BitConverter.GetBytes(zero.Value), 0) != 0
                || GetArmCopy(nonnegative, join) is not { Operands: [LocalVariable otherOutput, LocalVariable source] } copy
                || !ReferenceEquals(output, otherOutput) || !ReferenceEquals(source, minimum)
                || graph.Instructions.SelectMany(DeadCodeEliminator.UsedLocals).Count(local => ReferenceEquals(local, minimum)) != 1)
                continue;

            clamp ??= FindEquivalentClamp(mathf);
            if (clamp == null)
                return;
            if (clamp.DeclaringType != mathf)
                continue;

            min.SetOperands(clamp, output, value);
            min.IsLiftedFloatMin = false;
            copy.OpCode = OpCode.Nop;
            copy.SetOperands();
            branch.OpCode = OpCode.Jump;
            branch.SetOperands(nonnegative);
            graph.RemoveEdge(block, belowZero);
            block.CalculateBlockType();
        }

        if (clamp != null)
            graph.RemoveUnreachableBlocks();
    }

    // A lowered FCSEL has two short arms with just a copy and an optional jump.
    private static Instruction? GetArmCopy(Block block, Block join)
    {
        var operations = block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        return operations switch
        {
            [{ OpCode: OpCode.Move, OperandWidth: null } move] => move,
            [{ OpCode: OpCode.Move, OperandWidth: null } move, { OpCode: OpCode.Jump, Operands: [Block target] }]
                when target == join => move,
            _ => null,
        };
    }

    private static MethodAnalysisContext? FindEquivalentClamp(TypeAnalysisContext mathf)
    {
        if (mathf.DeclaringAssembly.CleanAssemblyName != "UnityEngine.CoreModule"
            || mathf.Methods.FirstOrDefault(method =>
                method is { Name: "Clamp01", IsStatic: true, Parameters.Count: 1, ReturnType.FullName: "System.Single" }
                && method.Parameters[0].ParameterType.FullName == "System.Single") is not { } clamp
            || clamp.UnderlyingPointer == 0 || clamp.RawBytes.Length != 28)
            return null;

        // Verify the actual implementation. Another Unity build could handle NaN or
        // signed zero differently, even with the same managed signature.
        var native = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(clamp.AppContext, clamp.UnderlyingPointer);
        return native is
        [
            { Mnemonic: Arm64Mnemonic.FCMP, Op0Reg: Arm64Register.S0, Op1Kind: Arm64OperandKind.Immediate, Op1Imm: 0 },
            { Mnemonic: Arm64Mnemonic.B, MnemonicConditionCode: Arm64ConditionCode.MI } branch,
            { Mnemonic: Arm64Mnemonic.FMOV, Op0Reg: Arm64Register.S1, Op1Kind: Arm64OperandKind.FloatingPointImmediate,
                Op1FpImm: 1 },
            { Mnemonic: Arm64Mnemonic.FMIN, Op0Reg: Arm64Register.S0, Op1Reg: Arm64Register.S0, Op2Reg: Arm64Register.S1 },
            { Mnemonic: Arm64Mnemonic.RET },
            { Mnemonic: Arm64Mnemonic.FMOV, Op0Reg: Arm64Register.S0, Op1Kind: Arm64OperandKind.Register,
                Op1Reg: Arm64Register.W31 } zero,
            { Mnemonic: Arm64Mnemonic.RET }
        ] && branch.BranchTarget == zero.Address ? clamp : null;
    }
}
