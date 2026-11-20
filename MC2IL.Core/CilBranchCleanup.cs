using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace MC2IL.Core;

internal static class CilBranchCleanup
{
    public static void Run(CilMethodBody body)
    {
        // Branch targets cannot move across protected regions. Recovered bodies use direct
        // instruction labels; skip other forms until they can be retargeted as well.
        if (body.ExceptionHandlers.Count != 0)
            return;

        var instructions = body.Instructions;
        var members = new HashSet<CilInstruction>(instructions, InstructionIdentity.Instance);
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode.OperandType == CilOperandType.ShortInlineBrTarget
                || instruction.Operand is IEnumerable<ICilLabel>
                || instruction.Operand is ICilLabel
                    && (TargetOf(instruction) is not { } target || !members.Contains(target)))
                return;
        }

        var forwarding = new Dictionary<CilInstruction, CilInstruction>(InstructionIdentity.Instance);
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (IsUnconditionalBranch(instruction) && TargetOf(instruction) is { } target)
                forwarding.Add(instruction, target);
            else if (instruction.OpCode == CilOpCodes.Nop && i + 1 < instructions.Count)
                forwarding.Add(instruction, instructions[i + 1]);
        }

        // Cache the end of each chain. A null entry identifies a cycle: leave those
        // targets alone so an empty but intentional loop still loops.
        var resolved = new Dictionary<CilInstruction, CilInstruction?>(InstructionIdentity.Instance);
        var chain = new List<CilInstruction>();
        foreach (var start in forwarding.Keys)
        {
            var current = start;
            while (forwarding.TryGetValue(current, out var next) && !resolved.ContainsKey(current))
            {
                resolved.Add(current, null);
                chain.Add(current);
                current = next;
            }

            var destination = resolved.TryGetValue(current, out var cached) ? cached : current;
            foreach (var instruction in chain)
                resolved[instruction] = destination;
            chain.Clear();
        }

        var referenced = new HashSet<CilInstruction>(InstructionIdentity.Instance);
        foreach (var instruction in instructions)
        {
            if (TargetOf(instruction) is not { } target)
                continue;

            if (resolved.TryGetValue(target, out var destination) && destination != null && !ReferenceEquals(destination, target))
            {
                instruction.Operand = new CilInstructionLabel(destination);
                target = destination;
            }
            referenced.Add(target);
        }

        // Work backwards so newly redundant branches also count as fallthrough.
        var collapsedConditions = new List<int>();
        CilInstruction? nextInstruction = null;
        CilInstruction? afterNext = null;
        for (var i = instructions.Count - 1; i >= 0; i--)
        {
            var instruction = instructions[i];
            var target = TargetOf(instruction);
            if (!referenced.Contains(instruction)
                && (instruction.OpCode == CilOpCodes.Nop
                    || IsUnconditionalBranch(instruction) && ReferenceEquals(target, nextInstruction)))
            {
                MakeNop(instruction);
                continue;
            }

            // If either outcome reaches the same instruction, only consume the condition.
            if (target != null && TryInvert(instruction, out var inverse))
            {
                if (!referenced.Contains(instruction))
                    FoldZeroTests(body, i, referenced, ref inverse);

                if (ReferenceEquals(target, nextInstruction) || nextInstruction is { } samePath
                    && IsUnconditionalBranch(samePath) && ReferenceEquals(TargetOf(samePath), target))
                {
                    collapsedConditions.Add(i);
                    instruction.OpCode = CilOpCodes.Pop;
                    instruction.Operand = null;
                }
                // brtrue A; br B; A: ... can fall through to A. Keep the same stack
                // consumption by using the opposite conditional branch to B.
                else if (ReferenceEquals(target, afterNext)
                    && nextInstruction is { } bridge && IsUnconditionalBranch(bridge)
                    && !referenced.Contains(bridge) && TargetOf(bridge) is { } other)
                {
                    instruction.OpCode = inverse;
                    instruction.Operand = new CilInstructionLabel(other);
                    MakeNop(bridge);
                    nextInstruction = afterNext;
                }
            }

            afterNext = nextInstruction;
            nextInstruction = instruction;
        }

        foreach (var pop in collapsedConditions)
            RemoveUnusedCondition(body, pop, referenced);

        var kept = new List<CilInstruction>(instructions.Count);
        foreach (var instruction in instructions)
            if (instruction.OpCode != CilOpCodes.Nop || referenced.Contains(instruction))
                kept.Add(instruction);

        if (kept.Count != instructions.Count)
        {
            instructions.Clear();
            instructions.AddRange(kept);
        }
    }

    private static void FoldZeroTests(CilMethodBody body, int index, HashSet<CilInstruction> referenced,
        ref CilOpCode inverse)
    {
        var instructions = body.Instructions;
        var branch = instructions[index];
        // Fold ceq against zero into the branch. Native flags can leave several such tests.
        while (true)
        {
            var compare = FindPrevious(index);
            if (compare < 0 || instructions[compare].OpCode != CilOpCodes.Ceq)
                break;

            var zero = FindPrevious(compare);
            if (zero < 0 || !IsZero(instructions[zero]))
                break;

            MakeNop(instructions[compare]);
            MakeNop(instructions[zero]);
            (branch.OpCode, inverse) = (inverse, branch.OpCode);
            index = zero;
        }

        int FindPrevious(int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (referenced.Contains(instructions[i]))
                    break;
                if (instructions[i].OpCode != CilOpCodes.Nop)
                    return i;
            }
            return -1;
        }

        static bool IsZero(CilInstruction instruction) =>
            instruction.OpCode == CilOpCodes.Ldc_I4_0
            || instruction.OpCode == CilOpCodes.Ldc_I4 && instruction.Operand is 0;
    }

    private static void RemoveUnusedCondition(CilMethodBody body, int pop, HashSet<CilInstruction> referenced)
    {
        var instructions = body.Instructions;
        if (referenced.Contains(instructions[pop]))
            return;

        // The pop also became dead when its entire input is a straight-line
        // expression made of non-throwing loads and comparisons.
        var needed = 1;
        for (var i = pop - 1; i >= 0; i--)
        {
            var instruction = instructions[i];
            if (referenced.Contains(instruction))
                return;
            if (instruction.OpCode == CilOpCodes.Nop)
                continue;

            var code = instruction.OpCode.Code;
            var inputs = IsSimpleLoad(code) ? 0 : code switch
            {
                CilCode.Ceq or CilCode.Clt or CilCode.Clt_Un or CilCode.Cgt or CilCode.Cgt_Un => 2,
                _ => -1
            };
            if (inputs < 0)
                return;
            needed += inputs - 1;
            if (needed == 0)
            {
                for (var j = i; j <= pop; j++)
                    MakeNop(instructions[j]);
                return;
            }
        }
    }

    private static bool IsSimpleLoad(CilCode code) => code is
        CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1 or CilCode.Ldarg_2 or CilCode.Ldarg_3
        or CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3
        or CilCode.Ldnull or CilCode.Ldc_I4 or CilCode.Ldc_I4_S or CilCode.Ldc_I4_M1
        or CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1 or CilCode.Ldc_I4_2 or CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4
        or CilCode.Ldc_I4_5 or CilCode.Ldc_I4_6 or CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8
        or CilCode.Ldc_I8 or CilCode.Ldc_R4 or CilCode.Ldc_R8;

    private static CilInstruction? TargetOf(CilInstruction instruction) =>
        (instruction.Operand as CilInstructionLabel)?.Instruction;

    private static bool IsUnconditionalBranch(CilInstruction instruction) =>
        instruction.OpCode == CilOpCodes.Br;

    private static bool TryInvert(CilInstruction instruction, out CilOpCode inverse)
    {
        if (instruction.OpCode == CilOpCodes.Brtrue)
        {
            inverse = CilOpCodes.Brfalse;
            return true;
        }

        inverse = CilOpCodes.Brtrue;
        return instruction.OpCode == CilOpCodes.Brfalse;
    }

    private static void MakeNop(CilInstruction instruction)
    {
        instruction.OpCode = CilOpCodes.Nop;
        instruction.Operand = null;
    }

    // Before offsets are assigned, distinct instructions can compare equal in AsmResolver.
    internal sealed class InstructionIdentity : IEqualityComparer<CilInstruction>
    {
        public static readonly InstructionIdentity Instance = new();

        public bool Equals(CilInstruction? first, CilInstruction? second) => ReferenceEquals(first, second);
        public int GetHashCode(CilInstruction instruction) => RuntimeHelpers.GetHashCode(instruction);
    }
}
