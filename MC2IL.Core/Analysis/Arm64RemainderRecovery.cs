using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64RemainderRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        Dictionary<LocalVariable, int>? reads = null;

        foreach (var block in graph.Blocks)
        {
            var instructions = block.Instructions;
            for (var index = 2; index < instructions.Count; index++)
            {
                var divide = instructions[index - 2];
                var multiply = instructions[index - 1];
                var subtract = instructions[index];

                // ARM64 lowers managed remainder to DIV followed by MSUB. The lifter splits
                // MSUB into these two instructions with consecutive indices.
                if (divide is not { OpCode: OpCode.Divide or OpCode.DivideUnsigned,
                        Operands: [LocalVariable quotient, LocalVariable dividend, LocalVariable divisor] }
                    || multiply is not { OpCode: OpCode.Multiply,
                        Operands: [LocalVariable product, var factor1, var factor2] }
                    || subtract is not { OpCode: OpCode.Subtract,
                        Operands: [LocalVariable result, var minuend, var subtrahend] }
                    || divide.OperandWidth != multiply.OperandWidth || divide.OperandWidth != subtract.OperandWidth
                    || multiply.Index != divide.Index + 1 || subtract.Index != multiply.Index + 1
                    || !ReferenceEquals(minuend, dividend) || !ReferenceEquals(subtrahend, product)
                    || !(ReferenceEquals(factor1, quotient) && ReferenceEquals(factor2, divisor)
                         || ReferenceEquals(factor2, quotient) && ReferenceEquals(factor1, divisor)))
                    continue;

                reads ??= graph.Instructions.SelectMany(DeadCodeEliminator.UsedLocals)
                    .GroupBy(local => local).ToDictionary(group => group.Key, group => group.Count());
                if (!reads.TryGetValue(quotient, out var quotientReads) || quotientReads != 1
                    || !reads.TryGetValue(product, out var productReads) || productReads != 1)
                    continue;

                subtract.OpCode = divide.OpCode == OpCode.Divide ? OpCode.Modulo : OpCode.ModuloUnsigned;
                subtract.SetOperands(result, dividend, divisor);
                Nop(divide);
                Nop(multiply);
            }
        }
    }

    private static void Nop(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }
}
