using System.Collections.Generic;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

/// <summary>
/// Recovers high-level relational conditions from the explicit EFLAGS computations the lifter emits.
///
/// The x86 lifter models a <c>cmp</c>/<c>test</c> as a cluster of flag pseudo-registers
/// (ZF = (a-b)==0, SF = (a-b)&lt;0, OF = signed overflow, ...) and lowers each <c>jcc</c> into a
/// boolean expression over those flags. This pass recognises those canonical shapes for each
/// <see cref="OpCode.ConditionalJump"/> condition and rewrites the condition's defining instruction
/// into a signed or unsigned relational comparison on the original compare operands. The now-orphaned
/// flag arithmetic is removed by the dead-code pass that runs next.
///
/// Runs in SSA form, where each flag/temporary has a single, version-stable definition, so the
/// operands referenced at the branch are provably the ones captured at the compare.
/// </summary>
public static class FlagConditionRecovery
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg)
    {
        var defOf = BuildDefMap(cfg);
        FloatConditionRecovery.Run(defOf);

        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode != OpCode.ConditionalJump)
                    continue;

                if (instruction.Operands[1] is not LocalVariable condition)
                    continue;

                if (Def(condition, defOf) is { Operands: [_, _, _, FloatingPointPrecision] })
                    continue;

                if (!TryClassify(condition, defOf, out var relop, out var op0, out var op1))
                    continue;

                // Rewrite the condition's defining instruction in place into a single comparison.
                // Its destination (the condition local the branch reads) is preserved.
                var definition = defOf[condition];
                var width = FindComparisonWidth(condition, op0!, op1!, defOf, 0);
                definition.OpCode = relop;
                definition.SetOperands(definition.Operands[0], op0!, op1!);
                definition.OperandWidth = width is 32 or 64 ? width : definition.OperandWidth;
            }
        }

        // ARM64 CMP's zero flag may be read as a value by CSET or CSEL, without a
        // conditional branch. Fold it after classifying branches that need the flag cluster.
        foreach (var entry in defOf)
        {
            if (entry.Key.Register.Name != "Z"
                || entry.Value is not { OpCode: OpCode.CheckEqual,
                    Operands: [var destination, LocalVariable { Register.Name: "TEMP1" } difference, Immediate { Value: 0 }] }
                || Def(difference, defOf) is not { OpCode: OpCode.Subtract, Operands: [_, var left, var right] } subtract
                || subtract.OperandWidth != entry.Value.OperandWidth)
                continue;

            entry.Value.SetOperands(destination, left, right);
        }
    }

    private static int? FindComparisonWidth(IOperand condition, IOperand left, IOperand right,
        Dictionary<LocalVariable, Instruction> defOf, int depth)
    {
        if (depth > 5 || ReferenceEquals(condition, left) || ReferenceEquals(condition, right)
            || Def(AsLocal(condition), defOf) is not { } definition)
            return null;

        if (definition is { OpCode: OpCode.Subtract or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned),
                OperandWidth: 32 or 64, Operands: [_, var a, var b] }
            && Equals(a, left) && Equals(b, right))
            return definition.OperandWidth;

        if (definition.OpCode is not (OpCode.And or OpCode.Or or OpCode.Not or >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned))
            return null;

        int? width = null;
        for (var i = 1; i < definition.Operands.Count; i++)
        {
            var operandWidth = FindComparisonWidth(definition.Operands[i], left, right, defOf, depth + 1);
            if (operandWidth == 0 || width.HasValue && operandWidth.HasValue && width != operandWidth)
                return 0;
            width ??= operandWidth;
        }
        return width;
    }

    private static Dictionary<LocalVariable, Instruction> BuildDefMap(ISILControlFlowGraph cfg)
    {
        var defs = new Dictionary<LocalVariable, Instruction>();

        foreach (var block in cfg.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Destination is LocalVariable destination)
                    defs[destination] = instruction;

        return defs;
    }

    private static bool TryClassify(LocalVariable condition, Dictionary<LocalVariable, Instruction> defOf,
        out OpCode relop, out IOperand? op0, out IOperand? op1)
    {
        relop = default;

        // Carry/borrow conditions from x86 and ARM => unsigned relational comparisons.
        if (IsUnsignedLess(condition, defOf, out op0, out op1)) { relop = OpCode.CheckLessUnsigned; return true; }
        if (IsUnsignedGreaterOrEqual(condition, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterOrEqualUnsigned; return true; }

        // ZF on its own  =>  a == b   (je)
        if (IsZeroFlag(condition, defOf, out op0, out op1)) { relop = OpCode.CheckEqual; return true; }
        // SF alone only preserves signed ordering when the subtraction was against zero;
        // for a - b, overflow means SF alone is not equivalent to a < b.
        if (IsSignOfValue(condition, defOf, out op0, out op1)) { relop = OpCode.CheckLess; return true; }

        var definition = Def(condition, defOf);
        if (definition == null)
            return false;

        switch (definition.OpCode)
        {
            case OpCode.Not:
                var inner = AsLocal(definition.Operands[1]);
                if (IsUnsignedLess(inner, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterOrEqualUnsigned; return true; }
                if (IsUnsignedGreaterOrEqual(inner, defOf, out op0, out op1)) { relop = OpCode.CheckLessUnsigned; return true; }
                if (IsZeroFlag(inner, defOf, out op0, out op1)) { relop = OpCode.CheckNotEqual; return true; }          // !ZF        => !=  (jne)
                if (IsSignOfValue(inner, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterOrEqual; return true; }  // !SF => >= 0 (jns)
                if (IsSignEqualsOverflow(inner, defOf, out op0, out op1)) { relop = OpCode.CheckLess; return true; }    // !(SF==OF)  => <   (jl/jb)
                // ARM64 conditional selects jump to the alternative assignment on the
                // inverse of the flag expression. Recover the relation before pruning flags.
                if (Def(inner, defOf) is { OpCode: OpCode.And } conjunction)
                {
                    if (IsSignGreater(conjunction, defOf, out op0, out op1)) { relop = OpCode.CheckLessOrEqual; return true; }
                    if (IsUnsignedGreater(conjunction, defOf, out op0, out op1)) { relop = OpCode.CheckLessOrEqualUnsigned; return true; }
                }
                if (Def(inner, defOf) is { OpCode: OpCode.Or } disjunction)
                {
                    if (IsSignLessOrEqual(disjunction, defOf, out op0, out op1)) { relop = OpCode.CheckGreater; return true; }
                    if (IsUnsignedLessOrEqual(disjunction, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterUnsigned; return true; }
                }
                return false;

            case OpCode.CheckEqual:
                // SF == OF  =>  a >= b   (jge/jae)
                if (IsSignEqualsOverflow(condition, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterOrEqual; return true; }
                return false;

            case OpCode.And:
                // !CF && !ZF => a > b using unsigned ordering (ja)
                if (IsUnsignedGreater(definition, defOf, out op0, out op1)) { relop = OpCode.CheckGreaterUnsigned; return true; }
                // (SF==OF) && !ZF  =>  a > b   (jg)
                if (IsSignGreater(definition, defOf, out op0, out op1)) { relop = OpCode.CheckGreater; return true; }
                return false;

            case OpCode.Or:
                // CF || ZF => a <= b using unsigned ordering (jbe)
                if (IsUnsignedLessOrEqual(definition, defOf, out op0, out op1)) { relop = OpCode.CheckLessOrEqualUnsigned; return true; }
                // !(SF==OF) || ZF  =>  a <= b   (jle)
                if (IsSignLessOrEqual(definition, defOf, out op0, out op1)) { relop = OpCode.CheckLessOrEqual; return true; }
                return false;

            default:
                return false;
        }
    }

    // ZF: local := CheckEqual(t, 0) where t := Subtract(a, b)
    private static bool IsZeroFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.CheckEqual } || !IsZeroConstant(def.Operands[2]))
            return false;
        return IsSubtraction(AsLocal(def.Operands[1]), defOf, out op0, out op1);
    }

    // SF: local := CheckLess(t, 0) where t := Subtract(a, b)
    private static bool IsSignFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.CheckLess } || !IsZeroConstant(def.Operands[2]))
            return false;
        return IsSubtraction(AsLocal(def.Operands[1]), defOf, out op0, out op1);
    }

    // SF alone means a < 0 only when it came from a - 0. For a general subtraction,
    // signed comparison must also account for overflow via SF != OF.
    private static bool IsSignOfValue(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        if (IsSignFlag(local, defOf, out op0, out op1))
            return IsZeroConstant(op1!);

        op0 = op1 = null;
        return false;
    }

    // local := Subtract(a, b)
    private static bool IsSubtraction(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.Subtract })
            return false;
        op0 = def.Operands[1];
        op1 = def.Operands[2];
        return true;
    }

    // local := CheckEqual(SF, OF) / CheckEqual(N, V) - signed "not less".
    // Validate both flag identities: matching only the sign side also accepted SF == ZF.
    private static bool IsSignEqualsOverflow(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.CheckEqual })
            return false;

        var left = AsLocal(def.Operands[1]);
        var right = AsLocal(def.Operands[2]);
        if (IsNamedSignFlag(left) && IsSignFlag(left, defOf, out op0, out op1)
            && IsOverflowFlag(right, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        if (IsNamedSignFlag(right) && IsSignFlag(right, defOf, out op0, out op1)
            && IsOverflowFlag(left, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        return false;
    }

    private static bool IsNamedSignFlag(LocalVariable? local)
        => local?.Register.Name is "SF" or "N";

    private static bool IsOverflowFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf)
    {
        if (local?.Register.Name is not ("OF" or "V"))
            return false;

        var def = Def(local, defOf);
        return def is { OpCode: OpCode.CheckLess } && IsZeroConstant(def.Operands[2]);
    }

    private static bool IsOverflowFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand compareLeft, IOperand compareRight)
    {
        if (!IsOverflowFlag(local, defOf))
            return false;

        var overflowDef = Def(local, defOf)!;
        var and = Def(AsLocal(overflowDef.Operands[1]), defOf);
        if (and is not { OpCode: OpCode.And })
            return false;

        var leftTerm = AsLocal(and.Operands[1]);
        var rightTerm = AsLocal(and.Operands[2]);
        return (IsXorOfCompareOperands(leftTerm, defOf, compareLeft, compareRight)
                && IsXorOfCompareAndDifference(rightTerm, defOf, compareLeft, compareRight))
            || (IsXorOfCompareOperands(rightTerm, defOf, compareLeft, compareRight)
                && IsXorOfCompareAndDifference(leftTerm, defOf, compareLeft, compareRight));
    }

    private static bool IsXorOfCompareOperands(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand compareLeft, IOperand compareRight)
    {
        var def = Def(local, defOf);
        return def is { OpCode: OpCode.Xor }
            && ((Equals(def.Operands[1], compareLeft) && Equals(def.Operands[2], compareRight))
                || (Equals(def.Operands[1], compareRight) && Equals(def.Operands[2], compareLeft)));
    }

    private static bool IsXorOfCompareAndDifference(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand compareLeft, IOperand compareRight)
    {
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.Xor })
            return false;

        return (Equals(def.Operands[1], compareLeft)
                && IsExpectedDifference(AsLocal(def.Operands[2]), defOf, compareLeft, compareRight))
            || (Equals(def.Operands[2], compareLeft)
                && IsExpectedDifference(AsLocal(def.Operands[1]), defOf, compareLeft, compareRight));
    }

    private static bool IsExpectedDifference(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand compareLeft, IOperand compareRight)
    {
        var def = Def(local, defOf);
        return def is { OpCode: OpCode.Subtract }
            && Equals(def.Operands[1], compareLeft)
            && Equals(def.Operands[2], compareRight);
    }

    // local := Not(CheckEqual(SF, OF))
    private static bool IsNotSignEqualsOverflow(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.Not })
            return false;
        return IsSignEqualsOverflow(AsLocal(def.Operands[1]), defOf, out op0, out op1);
    }

    // x86 CF: local := CheckLessUnsigned(a, b)
    private static bool IsCarryFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        if (def is not { OpCode: OpCode.CheckLessUnsigned, Operands.Count: 3 })
            return false;

        op0 = def.Operands[1];
        op1 = def.Operands[2];
        return true;
    }

    // Unsigned less-than is either x86 CF, or the negation of ARM's no-borrow C flag (a >= b).
    private static bool IsUnsignedLess(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        if (IsCarryFlag(local, defOf, out op0, out op1))
            return true;

        op0 = op1 = null;
        var definition = Def(local, defOf);
        return definition is { OpCode: OpCode.Not }
            && IsUnsignedGreaterOrEqual(AsLocal(definition.Operands[1]), defOf, out op0, out op1);
    }

    // Unsigned greater-or-equal is either ARM's C flag or !x86-CF.
    private static bool IsUnsignedGreaterOrEqual(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var definition = Def(local, defOf);
        if (definition is { OpCode: OpCode.CheckGreaterOrEqualUnsigned, Operands.Count: 3 })
        {
            op0 = definition.Operands[1];
            op1 = definition.Operands[2];
            return true;
        }

        return IsNotCarryFlag(local, defOf, out op0, out op1);
    }

    // !CF && !ZF, in either operand order => a > b unsigned (ja)
    private static bool IsUnsignedGreater(Instruction and, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        var left = AsLocal(and.Operands[1]);
        var right = AsLocal(and.Operands[2]);

        if (IsUnsignedGreaterOrEqual(left, defOf, out op0, out op1) && IsNotZeroFlagForOperands(right, defOf, op0!, op1!))
            return true;
        if (IsUnsignedGreaterOrEqual(right, defOf, out op0, out op1) && IsNotZeroFlagForOperands(left, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        return false;
    }

    private static bool IsNotCarryFlag(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        op0 = op1 = null;
        var def = Def(local, defOf);
        return def is { OpCode: OpCode.Not }
            && IsCarryFlag(AsLocal(def.Operands[1]), defOf, out op0, out op1);
    }

    // CF || ZF, in either operand order => a <= b unsigned (jbe)
    private static bool IsUnsignedLessOrEqual(Instruction or, Dictionary<LocalVariable, Instruction> defOf,
        out IOperand? op0, out IOperand? op1)
    {
        var left = AsLocal(or.Operands[1]);
        var right = AsLocal(or.Operands[2]);

        if (IsUnsignedLess(left, defOf, out op0, out op1) && IsZeroFlagForOperands(right, defOf, op0!, op1!))
            return true;
        if (IsUnsignedLess(right, defOf, out op0, out op1) && IsZeroFlagForOperands(left, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        return false;
    }

    private static bool IsZeroFlagForOperands(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand op0, IOperand op1) => IsZeroFlag(local, defOf, out var actual0, out var actual1)
        && Equals(op0, actual0) && Equals(op1, actual1);

    private static bool IsNotZeroFlagForOperands(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf,
        IOperand op0, IOperand op1)
    {
        var definition = Def(local, defOf);
        return definition is { OpCode: OpCode.Not }
            && IsZeroFlagForOperands(AsLocal(definition.Operands[1]), defOf, op0, op1);
    }

    // And((SF==OF), !ZF), in either operand order
    private static bool IsSignGreater(Instruction and, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        var left = AsLocal(and.Operands[1]);
        var right = AsLocal(and.Operands[2]);

        if (IsSignEqualsOverflow(left, defOf, out op0, out op1) && IsNotZeroFlagForOperands(right, defOf, op0!, op1!))
            return true;
        if (IsSignEqualsOverflow(right, defOf, out op0, out op1) && IsNotZeroFlagForOperands(left, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        return false;
    }

    // Or(!(SF==OF), ZF), in either operand order
    private static bool IsSignLessOrEqual(Instruction or, Dictionary<LocalVariable, Instruction> defOf, out IOperand? op0, out IOperand? op1)
    {
        var left = AsLocal(or.Operands[1]);
        var right = AsLocal(or.Operands[2]);

        if (IsNotSignEqualsOverflow(left, defOf, out op0, out op1) && IsZeroFlagForOperands(right, defOf, op0!, op1!))
            return true;
        if (IsNotSignEqualsOverflow(right, defOf, out op0, out op1) && IsZeroFlagForOperands(left, defOf, op0!, op1!))
            return true;

        op0 = op1 = null;
        return false;
    }

    private static Instruction? Def(LocalVariable? local, Dictionary<LocalVariable, Instruction> defOf)
        => local != null && defOf.TryGetValue(local, out var def) ? def : null;

    private static LocalVariable? AsLocal(IOperand operand) => operand as LocalVariable;

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
}
