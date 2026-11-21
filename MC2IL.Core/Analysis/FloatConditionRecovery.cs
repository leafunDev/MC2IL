using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// Recognize boolean expressions over the flags of a floating-point comparison in SSA.
internal static class FloatConditionRecovery
{
    [Flags]
    private enum Relation
    {
        Less = 1,
        Equal = 2,
        Greater = 4,
        LeftNaN = 8,
        RightNaN = 16,
        BothNaN = 32,
        Unordered = LeftNaN | RightNaN | BothNaN,
        All = Less | Equal | Greater | Unordered,
    }

    private readonly record struct Comparison(IOperand Left, IOperand Right, FloatingPointPrecision Precision);

    internal static void SimplifyOrderedInequalityBranches(ISILControlFlowGraph graph, SystemTypesContext types)
    {
        Dictionary<LocalVariable, int>? uses = null;
        foreach (var block in graph.Blocks)
        {
            if (!TryGetStrictBranch(block, out var first, out var firstJump, out var ordered, out var secondBlock)
                || secondBlock.Predecessors is not [var predecessor] || predecessor != block
                || !TryGetStrictBranch(secondBlock, out var second, out var secondJump, out var sameTarget, out var equalOrUnordered)
                || sameTarget != ordered || first.OpCode == second.OpCode
                || secondBlock.Instructions.Any(instruction => instruction != second && instruction != secondJump && instruction.OpCode != OpCode.Nop)
                || first.Operands is not [LocalVariable firstResult, var left, var right, FloatingPointPrecision precision]
                || second.Operands is not [LocalVariable secondResult, var secondLeft, var secondRight, FloatingPointPrecision secondPrecision]
                || precision != secondPrecision || !Equals(left, secondLeft) || !Equals(right, secondRight)
                || equalOrUnordered == graph.ExitBlock)
                continue;

            // For a known non-NaN operand, !(a < b || a > b) is a == b || IsNaN(variable).
            // Use the existing short-circuit blocks so the C# decompiler can express that directly.
            var variable = IsOrderedLiteral(right, precision) ? left : IsOrderedLiteral(left, precision) ? right : null;
            // Pass IsNaN the same precision used by the native comparison.
            var type = precision.IsDouble ? types.SystemDoubleType : types.SystemSingleType;
            if (variable is not LocalVariable local || local.Type != type)
                continue;

            var isNaN = type.Methods.FirstOrDefault(candidate => candidate is
                { Name: "IsNaN", IsStatic: true, ReturnType.FullName: "System.Boolean", Parameters: [var parameter] }
                && parameter.ParameterType == type);
            if (isNaN == null)
                continue;

            uses ??= graph.Blocks.SelectMany(candidate => candidate.Instructions)
                .SelectMany(DeadCodeEliminator.UsedLocals).GroupBy(local => local)
                .ToDictionary(group => group.Key, group => group.Count());
            if (!uses.TryGetValue(firstResult, out var firstUses) || firstUses != 1
                || !uses.TryGetValue(secondResult, out var secondUses) || secondUses != 1)
                continue;

            first.OpCode = OpCode.CheckEqual;
            second.OpCode = OpCode.Call;
            second.SetOperands(isNaN, secondResult, variable);
            firstJump.SetOperand(0, equalOrUnordered);
            secondJump.SetOperand(0, equalOrUnordered);
            graph.RemoveEdge(block, ordered);
            block.Successors.Add(equalOrUnordered);
            equalOrUnordered.Predecessors.Add(block);
        }
    }

    private static bool TryGetStrictBranch(Block block, out Instruction comparison, out Instruction jump,
        out Block taken, out Block fallthrough)
    {
        comparison = jump = null!;
        taken = fallthrough = null!;
        if (block.Instructions.Count < 2
            || block.Instructions[^1] is not { OpCode: OpCode.ConditionalJump, Operands: [Block target, LocalVariable condition] } branch
            || block.Successors is not [_, _] || !block.Successors.Contains(target))
            return false;

        var beforeJump = block.Instructions.Count - 2;
        while (beforeJump >= 0 && block.Instructions[beforeJump].OpCode == OpCode.Nop)
            beforeJump--;
        if (beforeJump < 0 || block.Instructions[beforeJump] is not { OpCode: OpCode.CheckLess or OpCode.CheckGreater,
                Operands: [var result, _, _, FloatingPointPrecision] } relation || result != condition)
            return false;

        comparison = relation;
        jump = branch;
        taken = target;
        fallthrough = block.Successors.First(successor => successor != target);
        return true;
    }

    private static bool IsOrderedLiteral(IOperand operand, FloatingPointPrecision precision) =>
        (operand, precision.IsDouble) switch
        {
            (FloatLiteral literal, false) => !float.IsNaN(literal.Value),
            (DoubleLiteral literal, true) => !double.IsNaN(literal.Value),
            _ => false,
        };

    public static void Run(Dictionary<LocalVariable, Instruction> definitions)
    {
        if (!definitions.Values.Any(static instruction =>
                instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned
                && instruction.Operands is [_, _, _, FloatingPointPrecision]))
            return;

        foreach (var definition in definitions.Values)
        {
            if (definition.Operands.Count is not (2 or 3)
                || definition.OpCode is not (OpCode.Not or OpCode.And or OpCode.Or or OpCode.Xor
                    or OpCode.CheckEqual or OpCode.CheckNotEqual)
                || FindComparison(definition, definitions, 8) is not { } comparison
                || Evaluate(definition, comparison, definitions, 8) is not { } relation
                || ToOpCode(relation) is not { } opCode)
                continue;

            definition.OpCode = opCode;
            definition.SetOperands(definition.Operands[0], comparison.Left, comparison.Right, comparison.Precision);
        }
    }

    private static Comparison? FindComparison(Instruction? instruction, Dictionary<LocalVariable, Instruction> definitions, int depth)
    {
        if (depth <= 0)
            return null;

        return instruction switch
        {
            { Operands: [_, var left, var right, FloatingPointPrecision precision] }
                when !Equals(left, right) && ComparisonRelation(instruction.OpCode) != null
                => new Comparison(left, right, precision),
            { OpCode: OpCode.Not, Operands: [_, var source] }
                => FindComparison(Definition(source, definitions), definitions, depth - 1),
            { OpCode: OpCode.And or OpCode.Or or OpCode.Xor or OpCode.CheckEqual or OpCode.CheckNotEqual,
                Operands: [_, var left, var right] }
                => FindComparison(Definition(left, definitions), definitions, depth - 1)
                   ?? FindComparison(Definition(right, definitions), definitions, depth - 1),
            _ => null,
        };
    }

    private static Relation? Evaluate(Instruction? instruction, Comparison comparison,
        Dictionary<LocalVariable, Instruction> definitions, int depth)
    {
        if (depth <= 0)
            return null;

        if (instruction is { Operands: [_, var left, var right, FloatingPointPrecision precision] }
            && precision == comparison.Precision)
        {
            if (Equals(left, comparison.Left) && Equals(right, comparison.Right))
                return ComparisonRelation(instruction.OpCode);

            // A self-inequality detects NaN on only one side. Track each unordered case
            // separately so that expressions using just one self-check cannot be misfolded.
            if (instruction.OpCode == OpCode.CheckNotEqual && Equals(left, right))
            {
                if (Equals(left, comparison.Left))
                    return Relation.LeftNaN | Relation.BothNaN;
                if (Equals(left, comparison.Right))
                    return Relation.RightNaN | Relation.BothNaN;
            }

            return null;
        }

        if (instruction is { OpCode: OpCode.Not, Operands: [_, var source] })
            return Evaluate(Definition(source, definitions), comparison, definitions, depth - 1) ^ Relation.All;

        if (instruction is not { OpCode: OpCode.And or OpCode.Or or OpCode.Xor or OpCode.CheckEqual or OpCode.CheckNotEqual,
            Operands: [_, var lhs, var rhs] })
            return null;

        var a = Evaluate(Definition(lhs, definitions), comparison, definitions, depth - 1);
        var b = Evaluate(Definition(rhs, definitions), comparison, definitions, depth - 1);
        return instruction.OpCode switch
        {
            OpCode.And => a & b,
            OpCode.Or => a | b,
            OpCode.CheckEqual => a ^ b ^ Relation.All,
            _ => a ^ b,
        };
    }

    private static Instruction? Definition(IOperand operand, Dictionary<LocalVariable, Instruction> definitions)
        => operand is LocalVariable local && definitions.TryGetValue(local, out var instruction) ? instruction : null;

    private static Relation? ComparisonRelation(OpCode opCode) => opCode switch
    {
        OpCode.CheckLess => Relation.Less,
        OpCode.CheckEqual => Relation.Equal,
        OpCode.CheckGreater => Relation.Greater,
        OpCode.CheckNotEqual => Relation.Less | Relation.Greater | Relation.Unordered,
        OpCode.CheckLessOrEqual => Relation.Less | Relation.Equal,
        OpCode.CheckGreaterOrEqual => Relation.Greater | Relation.Equal,
        OpCode.CheckLessUnsigned => Relation.Less | Relation.Unordered,
        OpCode.CheckGreaterUnsigned => Relation.Greater | Relation.Unordered,
        OpCode.CheckLessOrEqualUnsigned => Relation.Less | Relation.Equal | Relation.Unordered,
        OpCode.CheckGreaterOrEqualUnsigned => Relation.Greater | Relation.Equal | Relation.Unordered,
        _ => null,
    };

    private static OpCode? ToOpCode(Relation relation)
    {
        for (var opCode = OpCode.CheckEqual; opCode <= OpCode.CheckLessOrEqualUnsigned; opCode++)
            if (ComparisonRelation(opCode) == relation)
                return opCode;

        return null;
    }
}
