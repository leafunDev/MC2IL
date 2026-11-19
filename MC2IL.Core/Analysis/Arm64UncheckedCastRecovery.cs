using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

internal static class Arm64UncheckedCastRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
            return;

        var graph = method.ControlFlowGraph!;
        if (!graph.Instructions.Any(i => i is { OpCode: OpCode.Convert, Operands: [_, _,
                NumericConversion { Saturating: true, Rounding: NumericConversionRounding.TowardZero,
                    Kind: NumericConversionKind.Float32ToInt32Signed or NumericConversionKind.Float64ToInt32Signed }] }))
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;

        var changed = false;
        foreach (var guard in graph.Blocks)
        {
            if (guard.Instructions.LastOrDefault() is not { OpCode: OpCode.ConditionalJump,
                    Operands: [Block taken, LocalVariable test] } branch
                || guard.Successors.Count != 2
                || guard.Successors.FirstOrDefault(block => block != taken) is not { } fallthrough
                || !definitions.TryGetValue(test, out var comparison)
                || !TryMatchInfinityComparison(comparison, out var value))
                continue;

            var exceptional = comparison.OpCode == OpCode.CheckEqual ? taken : fallthrough;
            var normal = exceptional == taken ? fallthrough : taken;

            // Both arms only forward the converted value or its infinity override.
            if (!FollowEmptyArm(guard, exceptional, out var infinityPredecessor, out var merge)
                || !FollowEmptyArm(guard, normal, out var normalPredecessor, out var normalMerge)
                || merge != normalMerge || infinityPredecessor == normalPredecessor
                || merge.Predecessors.Count != 2)
                continue;

            var infinityIndex = 1 + merge.Predecessors.IndexOf(infinityPredecessor);
            var normalIndex = 1 + merge.Predecessors.IndexOf(normalPredecessor);
            var phis = merge.Instructions.Where(i => i.OpCode == OpCode.Phi).ToList();
            if (infinityIndex <= 0 || normalIndex <= 0
                || phis.Any(phi => phi.Operands.Count != 3))
                continue;

            var matchingCasts = phis.Where(phi => phi.Operands[infinityIndex] is Immediate { Value: int.MinValue or 0x80000000L }
                && phi.Operands[normalIndex] is LocalVariable local
                && definitions.TryGetValue(local, out var definition)
                && IsMatchingConversion(definition, value)).Take(2).ToArray();
            if (matchingCasts is not [var cast]
                || phis.Any(phi => phi != cast && !Equals(phi.Operands[normalIndex], phi.Operands[infinityIndex])))
                continue;

            // This is the IL2CPP lowering of an unchecked signed cast. Within range it
            // truncates; the unchecked C# result is unspecified outside the Int32 range.
            var original = definitions[(LocalVariable)cast.Operands[normalIndex]];
            var conversion = (NumericConversion)original.Operands[2];
            var castKind = conversion.Kind == NumericConversionKind.Float32ToInt32Signed
                ? NumericConversionKind.Float32ToInt32Truncate : NumericConversionKind.Float64ToInt32Truncate;
            if (cast.Destination is LocalVariable result)
                result.Type = method.AppContext.SystemTypes.SystemInt32Type;
            cast.OpCode = OpCode.Convert;
            cast.SetOperands(cast.Operands[0], value, new NumericConversion(castKind));
            foreach (var phi in phis)
            {
                if (phi == cast)
                    continue;
                phi.OpCode = OpCode.Move;
                phi.SetOperands(phi.Operands[0], phi.Operands[normalIndex]);
            }

            // The original native conversion is pure. Retain it if another use still reads it.
            if (!graph.Instructions.SelectMany(DeadCodeEliminator.UsedLocals)
                    .Any(local => ReferenceEquals(local, original.Destination)))
            {
                original.OpCode = OpCode.Nop;
                original.SetOperands();
            }

            branch.OpCode = OpCode.Jump;
            branch.SetOperands(normal);
            guard.Successors.Remove(exceptional);
            exceptional.Predecessors.Remove(guard);
            guard.CalculateBlockType();
            changed = true;
        }

        if (changed)
        {
            graph.RemoveUnreachableBlocks();
            DeadCodeEliminator.Run(graph);
        }
    }

    private static bool FollowEmptyArm(Block guard, Block first, out Block predecessor, out Block merge)
    {
        predecessor = guard;
        merge = first;
        while (merge.Predecessors.Count == 1 && merge.Successors is [var next])
        {
            if (merge == guard || merge.Instructions.Any(i => i.OpCode is not (OpCode.Nop or OpCode.Jump)))
                return false;
            predecessor = merge;
            merge = next;
        }
        return true;
    }

    private static bool TryMatchInfinityComparison(Instruction comparison, out LocalVariable value)
    {
        value = null!;
        if (comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                Operands: [_, LocalVariable input, var constant, FloatingPointPrecision precision] })
            return false;

        if (precision.IsDouble ? constant is not DoubleLiteral { Value: double.PositiveInfinity }
            : constant is not FloatLiteral { Value: float.PositiveInfinity })
            return false;

        value = input;
        return true;
    }

    private static bool IsMatchingConversion(Instruction instruction, LocalVariable value) =>
        instruction is { OpCode: OpCode.Convert, Operands: [_, var input, NumericConversion
            { Saturating: true, Rounding: NumericConversionRounding.TowardZero, FixedPointScale: 0,
                Kind: NumericConversionKind.Float32ToInt32Signed or NumericConversionKind.Float64ToInt32Signed }] }
        && ReferenceEquals(input, value);
}
