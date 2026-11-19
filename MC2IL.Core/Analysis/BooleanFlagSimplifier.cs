using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Fixes the compare-a-flag-against-zero pairs the we get for every conditional: <c>bool != 0</c>
/// is the bool itself, and <c>bool == 0</c> is its negation.
/// </summary>
public static class BooleanFlagSimplifier
{
    public static void Run(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
                continue;

            var value = IsZeroConstant(instruction.Operands[2])
                ? instruction.Operands[1]
                : IsZeroConstant(instruction.Operands[1])
                    ? instruction.Operands[2]
                    : null;

            if (value is not LocalVariable { Type: { } type } || type != booleanType)
                continue;

            instruction.OpCode = instruction.OpCode == OpCode.CheckNotEqual ? OpCode.Move : OpCode.Not;
            instruction.SetOperands(instruction.Operands[0], value);
        }
    }

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
}
