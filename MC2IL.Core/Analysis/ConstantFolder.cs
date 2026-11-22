using System;
using MC2IL.Core.Graphs;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Analysis;

// Folds operations whose operands have become constant, and applies algebraic identities.
public static class ConstantFolder
{
    public static bool Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!, method.AppContext.Binary.PointerSizeBytes);

    public static bool Run(ISILControlFlowGraph cfg) => Run(cfg, IntPtr.Size);

    private static bool Run(ISILControlFlowGraph cfg, int pointerSize)
    {
        var changed = false;

        foreach (var instruction in cfg.Instructions)
            changed |= TryFold(instruction, pointerSize);

        return changed;
    }

    private static bool TryFold(Instruction instruction, int pointerSize)
    {
        if (instruction is { OpCode: OpCode.Call, Operands: [MethodAnalysisContext
                { IsStatic: true, Name: "IsNullOrEmpty", Parameters.Count: 1 } target,
                LocalVariable result, StringLiteral or Immediate { Value: 0 }, ..] }
            && target.AppContext.SystemTypes is { SystemStringType: { } stringType, SystemBooleanType: { } boolType }
            && ReferenceEquals(target.DeclaringType, stringType)
            && ReferenceEquals(target.Parameters[0].ParameterType, stringType)
            && ReferenceEquals(target.ReturnType, boolType))
        {
            var isNullOrEmpty = instruction.Operands[2] is not StringLiteral { Value.Length: > 0 };
            result.Type ??= boolType;
            instruction.OpCode = OpCode.Move;
            instruction.SetOperands(result, new Immediate(isNullOrEmpty ? 1 : 0));
            return true;
        }

        // FMOV across register banks preserves every bit, including infinities,
        // signed zero and subnormals. The numeric literal heuristics do not apply.
        if (instruction is { OpCode: OpCode.Convert, Operands: [_, var input, NumericConversion { Kind: var kind }] })
        {
            IOperand? reinterpreted = (kind, input) switch
            {
                (NumericConversionKind.I32ReinterpretFloat32, Immediate bits)
                    => new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits.Value)), 0)),
                (NumericConversionKind.I64ReinterpretFloat64, Immediate bits)
                    => new DoubleLiteral(BitConverter.Int64BitsToDouble(bits.Value)),
                (NumericConversionKind.Float32ReinterpretI32, FloatLiteral value)
                    => new Immediate(BitConverter.ToInt32(BitConverter.GetBytes(value.Value), 0)),
                (NumericConversionKind.Float64ReinterpretI64, DoubleLiteral value)
                    => new Immediate(BitConverter.DoubleToInt64Bits(value.Value)),
                _ => null,
            };
            if (reinterpreted != null)
            {
                instruction.OperandWidth = null;
                return ToMove(instruction, reinterpreted);
            }
        }

        // Unary constant folds.
        if (instruction is { OpCode: OpCode.Not, Operands: [LocalVariable { Type.FullName: "System.Boolean" }, Immediate n] })
            return ToConstant(instruction, n.Value == 0 ? 1 : 0);
        if (instruction is { OpCode: OpCode.Not, Operands: [_, Immediate notInput] })
            return ToConstant(instruction, ~notInput.Value);
        if (instruction is { OpCode: OpCode.Negate, Operands: [_, Immediate m] })
            return ToConstant(instruction, -m.Value);

        // A managed string literal always denotes a non-null reference, including "".
        if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
            && instruction.Operands is [_, StringLiteral, Immediate { Value: 0 }]
                or [_, Immediate { Value: 0 }, StringLiteral])
            return ToConstant(instruction, instruction.OpCode == OpCode.CheckEqual ? 0 : 1);

        // metadata handles are by definition never null
        if (instruction is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, TypeAnalysisContext, var against] }
            && Constant(against) == 0)
            return ToConstant(instruction, instruction.OpCode == OpCode.CheckEqual ? 0 : 1);

        // Binary constant folds.
        if (instruction.Operands is [_, var floatLeft, var floatRight, FloatingPointPrecision precision]
            && TryFloatConstant(floatLeft, precision.IsDouble, out var leftFloat)
            && TryFloatConstant(floatRight, precision.IsDouble, out var rightFloat))
        {
            var unordered = double.IsNaN(leftFloat) || double.IsNaN(rightFloat);
            bool? comparison = instruction.OpCode switch
            {
                OpCode.CheckEqual => leftFloat == rightFloat,
                OpCode.CheckNotEqual => leftFloat != rightFloat,
                OpCode.CheckLess => leftFloat < rightFloat,
                OpCode.CheckLessOrEqual => leftFloat <= rightFloat,
                OpCode.CheckGreater => leftFloat > rightFloat,
                OpCode.CheckGreaterOrEqual => leftFloat >= rightFloat,
                OpCode.CheckLessUnsigned => unordered || leftFloat < rightFloat,
                OpCode.CheckLessOrEqualUnsigned => unordered || leftFloat <= rightFloat,
                OpCode.CheckGreaterUnsigned => unordered || leftFloat > rightFloat,
                OpCode.CheckGreaterOrEqualUnsigned => unordered || leftFloat >= rightFloat,
                _ => null
            };
            if (comparison is { } comparisonResult)
                return ToConstant(instruction, comparisonResult ? 1 : 0);
        }

        if (BinaryConstants(instruction, out var a, out var b))
        {
            switch (instruction.OpCode)
            {
                case OpCode.CheckEqual: return ToConstant(instruction, a == b ? 1 : 0);
                case OpCode.CheckNotEqual: return ToConstant(instruction, a != b ? 1 : 0);
                case OpCode.And: return ToConstant(instruction, a & b);
                case OpCode.Or: return ToConstant(instruction, a | b);
                case OpCode.Xor: return ToConstant(instruction, a ^ b);
                case OpCode.ShiftLeft:
                case OpCode.ShiftRight:
                case OpCode.ShiftRightUnsigned:
                    if (ShiftWidth(instruction, pointerSize) is not { } width)
                        return false;

                    return ToConstant(instruction, FoldShift(instruction.OpCode, a, b, width));
            }
        }

        // One-constant algebraic identities.
        switch (instruction.OpCode)
        {
            case OpCode.And:
                if (Constant(instruction.Operands[1]) == 0 || Constant(instruction.Operands[2]) == 0)
                    return ToConstant(instruction, 0);
                // x & 1 == x only when x is a 0/1 boolean
                return BooleanIdentity(instruction, 1) is { } andBool && ToMove(instruction, andBool);
            case OpCode.Or:
            case OpCode.Xor:
            case OpCode.Add:
                return Identity(instruction, 0);
            case OpCode.Multiply:
                return Identity(instruction, 1);
            case OpCode.Subtract:
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.ShiftRightUnsigned:
                // right identity only: x - 0 / x << 0 / x >> 0 == x, but 0 - x etc. are not
                return Constant(instruction.Operands[2]) == 0 && ToMove(instruction, instruction.Operands[1]);
        }

        return false;
    }

    private static bool TryFloatConstant(IOperand operand, bool isDouble, out double value)
    {
        if (operand is FloatLiteral single)
        {
            value = single.Value;
            return true;
        }
        if (operand is DoubleLiteral wide)
        {
            value = isDouble ? wide.Value : (float)wide.Value;
            return true;
        }
        value = 0;
        return false;
    }

    // For a commutative op, if one operand is the identity constant, the result is the other operand.
    private static bool Identity(Instruction instruction, long identity)
    {
        if (Constant(instruction.Operands[1]) == identity)
            return ToMove(instruction, instruction.Operands[2]);
        if (Constant(instruction.Operands[2]) == identity)
            return ToMove(instruction, instruction.Operands[1]);
        return false;
    }

    // The boolean operand of `bool & const`, or null if that isn't the shape.
    private static IOperand? BooleanIdentity(Instruction instruction, long constant)
    {
        if (Constant(instruction.Operands[2]) == constant && IsBoolean(instruction.Operands[1]))
            return instruction.Operands[1];
        if (Constant(instruction.Operands[1]) == constant && IsBoolean(instruction.Operands[2]))
            return instruction.Operands[2];
        return null;
    }

    private static bool IsBoolean(IOperand operand) => operand is LocalVariable { Type.FullName: "System.Boolean" };

    private static bool BinaryConstants(Instruction instruction, out long left, out long right)
    {
        (left, right) = (0, 0);

        if (instruction.Operands is [_, Immediate a, Immediate b])
        {
            (left, right) = (a.Value, b.Value);
            return true;
        }

        return false;
    }

    private static long? Constant(IOperand operand) => operand is Immediate immediate ? immediate.Value : null;

    private static int? ShiftWidth(Instruction instruction, int pointerSize)
    {
        if (instruction.Destination is not LocalVariable { Type: { } type })
            return null;

        if (type.IsEnumType)
            type = type.EnumUnderlyingType;

        return type?.FullName switch
        {
            "System.Int64" or "System.UInt64" => 64,
            "System.IntPtr" or "System.UIntPtr" => pointerSize * 8,
            "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char"
                or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => 32,
            _ => null
        };
    }

    private static long FoldShift(OpCode opCode, long value, long count, int width)
    {
        if (width == 32)
        {
            var shift = (int)(count & 0x1F);
            return opCode switch
            {
                OpCode.ShiftLeft => unchecked((int)value << shift),
                OpCode.ShiftRight => unchecked((int)value) >> shift,
                OpCode.ShiftRightUnsigned => unchecked((uint)value) >> shift,
                _ => value
            };
        }

        var shift64 = (int)(count & 0x3F);
        return opCode switch
        {
            OpCode.ShiftLeft => unchecked(value << shift64),
            OpCode.ShiftRight => value >> shift64,
            OpCode.ShiftRightUnsigned => unchecked((long)(unchecked((ulong)value) >> shift64)),
            _ => value
        };
    }

    private static bool ToConstant(Instruction instruction, long value)
    {
        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(instruction.Operands[0], new Immediate(value));
        return true;
    }

    private static bool ToMove(Instruction instruction, IOperand source)
    {
        instruction.OpCode = OpCode.Move;
        instruction.SetOperands(instruction.Operands[0], source);
        return true;
    }
}
