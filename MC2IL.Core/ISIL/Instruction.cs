using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MC2IL.Core.Graphs;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.ISIL;

public class Instruction : IOperand
{
    public int Index;

    /// <summary>
    /// Native integer operand width when it differs from the width inferred from CIL types.
    /// Used by lifters such as ARM64 where Wn and Xn alias the same architectural register.
    /// </summary>
    public int? OperandWidth;

    // Number of bytes read or written by a native load/store, when known.
    public int? MemoryAccessSize;

    // Provenance needed to separate the two low S lanes before managed type propagation.
    internal Arm64PackedFloatOperation Arm64PackedFloat;

    // This immediate address came from a PC-relative native instruction, not an integer literal.
    public bool IsPcRelativeAddress;

    // A removed native receiver guard must still be enforced by the emitted CIL call.
    public bool NullCheckReceiver;

    // A scalar native absolute value emitted as a managed math call.
    public bool IsLiftedFloatAbs;

    // Native instruction address, retained through early analysis for architecture-specific recovery.
    internal ulong? NativeAddress;

    // Scalar FMIN has native NaN semantics and can be part of an inlined clamp.
    public bool IsLiftedFloatMin;

    public OpCode OpCode
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            ResetSources();
        }
    }

    private List<IOperand> _operands;

    public OperandList Operands => new(_operands);

    // Exists to clear the return register after a CallVoid, basically.
    public Register? ImplicitDefinition;

    public bool IsFallThrough =>
        OpCode switch
        {
            OpCode.Return or OpCode.Jump or OpCode.ConditionalJump or OpCode.IndirectJump or OpCode.Throw => false,
            _ => true
        };

    public bool IsCall => OpCode is OpCode.Call or OpCode.CallVoid;

    public bool IsAssignment => Destination != null;

    public OperandList Sources => new(_sources ??= GetSources());
    private List<IOperand>? _sources;

    public OperandList SourcesAndConstants => new(_sourcesAndConstants ??= GetSources(false));
    private List<IOperand>? _sourcesAndConstants;

    public Instruction(int index, OpCode opcode, params List<IOperand> operands)
    {
        Index = index;
        _operands = operands;
        OpCode = opcode;
    }

    [MemberNotNull(nameof(_operands))]
    public void SetOperands(params List<IOperand> operands)
    {
        _operands = operands;
        ResetSources();
    }

    public void SetOperand(int index, IOperand value)
    {
        _operands[index] = value;
        ResetSources();
    }

    public void AddOperands(IEnumerable<IOperand> operands)
    {
        _operands.AddRange(operands);
        ResetSources();
    }

    public void RemoveOperandAt(int index)
    {
        _operands.RemoveAt(index);
        ResetSources();
    }

    public IOperand? Destination
    {
        get => GetOrSetDestination();
        set => GetOrSetDestination(value);
    }

    internal int DestinationOperandIndex => OpCode switch
    {
        OpCode.Move or OpCode.Convert or OpCode.PackFloatAggregate or OpCode.Phi
            or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.DivideUnsigned
            or OpCode.Modulo or OpCode.ModuloUnsigned or OpCode.ShiftLeft or OpCode.ShiftRight
            or OpCode.ShiftRightUnsigned or OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate
            or OpCode.CheckEqual or OpCode.CheckGreater or OpCode.CheckLess or OpCode.CheckNotEqual
            or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual or OpCode.CheckGreaterUnsigned
            or OpCode.CheckLessUnsigned or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned
            or OpCode.Newobj or OpCode.Box or OpCode.IsInstance or OpCode.Select => 0,
        OpCode.Call or OpCode.IndirectCall when _operands.Count > 1 => 1,
        _ => -1
    };

    private IOperand? GetOrSetDestination(IOperand? newDestination = null)
    {
        var index = DestinationOperandIndex;
        if (index < 0)
            return null;

        if (newDestination != null)
            SetOperand(index, newDestination);
        return IsConstantValue(_operands[index]) ? null : _operands[index];
    }

    private void ResetSources()
    {
        _sources = null;
        _sourcesAndConstants = null;
    }

    private List<IOperand> GetSources(bool constantsOnly = true)
    {
        var sources = OpCode switch
        {
            OpCode.Move when _operands.Count == 3 && Arm64PackedFloat is Arm64PackedFloatOperation.ExtractLow
                or Arm64PackedFloatOperation.ExtractHigh or Arm64PackedFloatOperation.InsertLow or Arm64PackedFloatOperation.InsertHigh
                => [_operands[1], _operands[2]],

            OpCode.Move or OpCode.ConditionalJump
                or OpCode.ShiftStack or OpCode.Not or OpCode.Negate
                or OpCode.Newobj
                => [_operands[1]],

            OpCode.Convert or OpCode.IsInstance => [_operands[1]],
            OpCode.Select => [_operands[1], _operands[2], _operands[3]],

            OpCode.PackFloatAggregate => _operands.Skip(2).ToList(),

            OpCode.Box => [_operands[2]],

            OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.Divide or OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                or OpCode.And or OpCode.Or or OpCode.Xor
                => [_operands[2], _operands[1]],

            OpCode.Call => _operands.Skip(2).ToList(),

            // Unlike a direct call, operand 0 is the address being called and so is itself a source.
            OpCode.IndirectCall => _operands.Count > 2
                ? _operands.Skip(2).Prepend(_operands[0]).ToList()
                : _operands.Take(1).ToList(),

            OpCode.CallVoid or OpCode.Phi => _operands.Skip(1).ToList(),
            OpCode.CheckEqual or OpCode.CheckGreater or OpCode.CheckLess
                or OpCode.CheckNotEqual or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
                or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
                or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned
                => [_operands[1], _operands[2]],

            _ => []
        };

        if (OpCode == OpCode.Return && _operands.Count == 1)
            sources.Add(_operands[0]);

        if (constantsOnly)
            sources.RemoveAll(static operand => IsConstantValue(operand));

        return sources;
    }

    public override string ToString()
    {
        if (OpCode == OpCode.Jump && _operands[0] is Immediate jumpTarget)
            return $"{Index} {OpCode} {jumpTarget.Value:X4}";
        if (OpCode == OpCode.ConditionalJump && _operands[0] is Immediate jumpTarget2)
            return $"{Index} {OpCode} {jumpTarget2.Value:X4}, {FormatOperand(_operands[1])}";

        if ((OpCode is OpCode.CallVoid or OpCode.Call) && _operands[0] is Immediate callTarget)
        {
            var remainingOperands = string.Join(", ", _operands.Skip(1).Select(FormatOperand));
            return string.IsNullOrEmpty(remainingOperands)
                ? $"{Index} {OpCode} {callTarget.Value:X4}"
                : $"{Index} {OpCode} {callTarget.Value:X4}, {remainingOperands}";
        }

        var formattedOperands = string.Join(", ", _operands.Select(FormatOperand));
        return string.IsNullOrEmpty(formattedOperands)
            ? $"{Index} {OpCode}"
            : $"{Index} {OpCode} {formattedOperands}";
    }

    private static string FormatOperand(IOperand operand)
    {
        return operand switch
        {
            MethodAnalysisContext method => $"{method.DeclaringType!.Name}.{method.Name}",
            RuntimeMethodInfoAnalysisContext methodInfo => $"methodof({methodInfo.RepresentedMethod.FullName})",
            RuntimeFieldInfoAnalysisContext fieldInfo => $"fieldof({fieldInfo.RepresentedField.DeclaringType.FullName}.{fieldInfo.RepresentedField.Name})",
            TypeAnalysisContext type => $"typeof({type.FullName})",
            Instruction instruction => $"@{instruction.Index}",
            Block block => $"@b{block.ID}",
            _ => operand.ToString()!
        };
    }

    public static bool IsConstantValue(IOperand operand) =>
        operand switch
        {
            Register or StackOffset or LocalVariable => false,
            AddressOf or ArrayAccess or ArrayLength => false,
            MemoryOperand memory => memory.IsConstant,
            _ => true
        };

    // Deliberately not Equals/GetHashCode. Instructions are identity objects: the graph, stack analyzer and
    // IL generator all key sets and dictionaries on the specific instruction instance, and generated
    // instructions (phi copies, for one) are routinely structurally identical to each other.
    public bool IsStructurallyEqualTo(Instruction? other)
    {
        if (ReferenceEquals(this, other))
            return true;

        if (other is null)
            return false;

        if (OpCode != other.OpCode)
            return false;

        if (Index != other.Index)
            return false;

        if (_operands.Count != other._operands.Count)
            return false;

        for (var i = 0; i < _operands.Count; i++)
        {
            var thisOperand = _operands[i];
            var otherOperand = other._operands[i];

            // Branch targets are compared by index, so a back edge doesn't send us round in circles.
            if (thisOperand is Instruction thisTarget)
            {
                if (otherOperand is not Instruction otherTarget || thisTarget.Index != otherTarget.Index)
                    return false;

                continue;
            }

            if (!thisOperand.Equals(otherOperand))
                return false;
        }

        return true;
    }
}
