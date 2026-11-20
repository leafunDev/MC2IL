using System.Collections.Generic;
using System.Linq;
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

    // After SSA propagation a boolean return can have the form (phi(flag, 1) & 1).
    // The untyped merge would otherwise become an object local when the phi is lowered.
    internal static void TypeMaskedReturnMerges(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;
        if (method.ReturnType != booleanType)
            return;

        var instructions = method.ControlFlowGraph!.Instructions;
        var returned = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Return, Operands: [LocalVariable result] })
                returned.Add(result);

        if (returned.Count == 0)
            return;

        Dictionary<LocalVariable, Instruction>? candidates = null;
        foreach (var instruction in instructions)
        {
            if (instruction is not { OpCode: OpCode.And, Operands: [LocalVariable result, var left, var right] }
                || !returned.Contains(result))
                continue;

            if ((right is Immediate { Value: 1 } ? left : left is Immediate { Value: 1 } ? right : null)
                is LocalVariable { Type: null } merged)
                (candidates ??= [])[merged] = instruction;
        }

        if (candidates == null)
            return;

        var phis = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
            if (instruction is { OpCode: OpCode.Phi, Destination: LocalVariable { Type: null } destination })
                phis[destination] = instruction;

        if (phis.Count == 0)
            return;

        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
            foreach (var input in DeadCodeEliminator.UsedLocals(instruction))
                if (phis.ContainsKey(input))
                {
                    if (!uses.TryGetValue(input, out var readers))
                        uses[input] = readers = [];
                    readers.Add(instruction);
                }

        foreach (var candidate in candidates)
        {
            var proven = new HashSet<LocalVariable>();
            var visiting = new HashSet<LocalVariable>();
            if (!IsBit(candidate.Key, 0))
                continue;

            // Only retype merges whose uses stay inside this Boolean expression. A merge
            // also used as a wider integer, memory address or ref argument keeps its type.
            if (proven.Any(local => uses[local].Any(reader => reader != candidate.Value
                    && !(reader.OpCode == OpCode.Phi && reader.Destination is LocalVariable result && proven.Contains(result)))))
                continue;

            foreach (var local in proven)
                local.Type = booleanType;

            bool IsBit(IOperand input, int depth)
            {
                if (input is Immediate { Value: 0 or 1 } || input is LocalVariable typed && typed.Type == booleanType)
                    return true;

                if (input is not LocalVariable { Type: null } local)
                    return false;
                if (proven.Contains(local))
                    return true;
                if (depth >= 64 || !phis.TryGetValue(local, out var phi) || phi.Operands.Count < 2 || !visiting.Add(local))
                    return false;

                for (var i = 1; i < phi.Operands.Count; i++)
                    if (!IsBit(phi.Operands[i], depth + 1))
                        return false;

                visiting.Remove(local);
                proven.Add(local);
                return true;
            }
        }
    }

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
}
