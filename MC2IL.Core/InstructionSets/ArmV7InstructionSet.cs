using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Api;
using MC2IL.Core.Il2CppApiFunctions;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.InstructionSets;

public class ArmV7InstructionSet : MC2ILInstructionSet
{
    private static readonly ArmV7CallingConventionResolver Convention = new();
    public override BaseCallingConventionResolver CallingConventionResolver => Convention;

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var slice = ArmV7Utils.TryGetMethodBodyBytesFast(context.AppContext, context.UnderlyingPointer, isAttributeGenerator);
        if (slice.Length > 0)
            return slice;
        var instructions = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer, managed: false);
        return new BinarySlice(instructions.SelectMany(i => i.Bytes).ToArray());
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
        => new Lifter(context, ArmV7Utils.Decode(context.AppContext, context.UnderlyingPointer, context.RawBytes)).Lift();

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
        => Convention.ResolveForManaged(context).ToList();

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new ArmV7KeyFunctionAddresses();

    public override (IReadOnlyList<ulong> DataReferences, IReadOnlyList<ulong> CallTargets)
        InspectPotentialThrowHelper(ApplicationAnalysisContext context, ulong address)
        => InspectThrowHelper(context, address, straightLine: false);

    internal static IReadOnlyList<ulong> GetStraightLineCallTargets(ApplicationAnalysisContext context, ulong address)
        => InspectThrowHelper(context, address, straightLine: true).CallTargets;

    private static (IReadOnlyList<ulong> DataReferences, IReadOnlyList<ulong> CallTargets)
        InspectThrowHelper(ApplicationAnalysisContext context, ulong address, bool straightLine)
    {
        var data = new List<ulong>();
        var calls = new List<ulong>();
        var constants = new Dictionary<string, uint>(StringComparer.Ordinal);
        var thumb = (address & 1) != 0;
        foreach (var instruction in ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(context, address, managed: false, count: 24))
        {
            if (instruction.Id == ArmInstructionId.Invalid)
                break;
            var operands = instruction.Details.Operands;
            // A helper that can bypass a throw cannot be replaced with a terminator.
            if (straightLine && (instruction.Details.ConditionCode is not (ArmConditionCode.ARM_CC_AL or ArmConditionCode.Invalid)
                || instruction.Id is ArmInstructionId.ARM_INS_CBZ or ArmInstructionId.ARM_INS_CBNZ
                    or ArmInstructionId.ARM_INS_BX or ArmInstructionId.ARM_INS_TBB or ArmInstructionId.ARM_INS_TBH
                || operands.FirstOrDefault() is { Type: ArmOperandType.Register, Register.Name: "pc" }
                || instruction.Mnemonic.StartsWith("ldm", StringComparison.Ordinal)
                    && operands.Any(o => o is { Type: ArmOperandType.Register, Register.Name: "pc" })))
                break;

            if (instruction.Id is ArmInstructionId.ARM_INS_BL or ArmInstructionId.ARM_INS_BLX or ArmInstructionId.ARM_INS_B
                && operands.FirstOrDefault() is { Type: ArmOperandType.Immediate } target)
            {
                var targetThumb = instruction.Id == ArmInstructionId.ARM_INS_BLX ? !thumb : thumb;
                calls.Add((unchecked((uint)target.Immediate) & ~1U) | (targetThumb ? 1UL : 0));
            }
            if (straightLine)
                continue;

            var pc = unchecked((uint)((instruction.Address + (thumb ? 4 : 8)) & (thumb ? ~3L : ~0L)));
            if (operands.Length < 2 || operands[0].Type != ArmOperandType.Register)
                continue;
            var register = operands[0].Register.Name;
            var previous = constants.TryGetValue(register, out var old);
            constants.Remove(register);
            if (instruction.Id == ArmInstructionId.ARM_INS_LDR
                && operands[1] is { Type: ArmOperandType.Memory, Memory: { Base.Name: "pc" } memory })
            {
                var index = memory.Index?.Name;
                if (index is null or "" or "invalid")
                {
                    var literal = unchecked(pc + (uint)memory.Displacement);
                    if (context.Binary.TryMapVirtualAddressToRaw(literal, out var raw) && raw >= 0 && raw <= context.Binary.RawLength - 4)
                        constants[register] = unchecked((uint)context.Binary.ReadPointerAtVirtualAddress(literal));
                }
                else if (index == register && previous && memory.Scale >= 0)
                    data.Add(unchecked(pc + old + (uint)memory.Displacement));
            }
            else if (instruction.Id == ArmInstructionId.ARM_INS_ADD && operands.Length == 3
                && operands[1] is { Type: ArmOperandType.Register, Register.Name: "pc" }
                && operands[2] is { Type: ArmOperandType.Register } offset
                && (offset.Register.Name == register && previous || constants.TryGetValue(offset.Register.Name, out old)))
                data.Add(unchecked(pc + old));
        }
        return (data, calls);
    }

    public override string PrintAssembly(MethodAnalysisContext context) =>
        string.Join("\n", ArmV7Utils.Decode(context.AppContext, context.UnderlyingPointer, context.RawBytes)
            .Select(i => $"0x{i.Address:X} {i.Mnemonic} {i.Operand}"));

    private sealed class Lifter(MethodAnalysisContext method, List<ArmInstruction> native)
    {
        private readonly List<Instruction> _result = [];
        private readonly Dictionary<long, Instruction> _labels = new();
        private readonly List<(Instruction Branch, long Address)> _branches = [];
        private readonly Dictionary<string, uint> _constants = new(StringComparer.Ordinal);
        private readonly bool _thumb = (method.UnderlyingPointer & 1) != 0;
        private static Register Reg(string name) => new(null, name);
        private static readonly Register Zero = Reg("Z"), Negative = Reg("N"), Overflow = Reg("V"), Carry = Reg("C");

        public List<Instruction> Lift()
        {
            var resetAt = new HashSet<long>();
            foreach (var instruction in native)
                if (instruction.Id is ArmInstructionId.ARM_INS_B or ArmInstructionId.ARM_INS_CBZ or ArmInstructionId.ARM_INS_CBNZ
                    && instruction.Details.Operands.LastOrDefault() is { Type: ArmOperandType.Immediate } target)
                    resetAt.Add(unchecked((uint)target.Immediate) & ~1U);

            foreach (var instruction in native)
            {
                if (resetAt.Contains(instruction.Address)) _constants.Clear();
                var start = _result.Count;
                if (instruction.Id == ArmInstructionId.Invalid)
                    Unsupported(instruction);
                else
                {
                    var condition = instruction.Details.ConditionCode;
                    var predicated = condition is not (ArmConditionCode.ARM_CC_AL or ArmConditionCode.Invalid)
                        && instruction.Id != ArmInstructionId.ARM_INS_B;
                    if (predicated)
                        Branch(instruction.Address + instruction.Bytes.Length, Not(Condition(condition)));
                    LiftInstruction(instruction);
                    if (predicated) _constants.Clear();
                }
                if (_result.Count == start) Add(OpCode.Nop);
                if (!_labels.ContainsKey(instruction.Address)) _labels.Add(instruction.Address, _result[start]);
            }

            foreach (var (branch, address) in _branches)
            {
                if (_labels.TryGetValue(address, out var target))
                    branch.SetOperand(0, target);
                else
                {
                    branch.OpCode = OpCode.NotImplemented;
                    branch.SetOperands(new StringLiteral($"Unresolved ARMv7 branch at 0x{address:X}"));
                }
            }
            return _result;
        }

        private void LiftInstruction(ArmInstruction instruction)
        {
            var ops = instruction.Details.Operands;
            var pc = unchecked((uint)((instruction.Address + (_thumb ? 4 : 8)) & (_thumb ? ~3L : ~0L)));
            var id = instruction.Id;
            IOperand? Read(int i) => i < ops.Length ? ReadOperand(ops[i], pc) : null;
            Register? Dest(int i = 0) => i < ops.Length && ops[i].Type == ArmOperandType.Register ? Reg(ops[i].Register.Name) : null;

            switch (id)
            {
                case ArmInstructionId.ARM_INS_NOP:
                case ArmInstructionId.ARM_INS_IT: // Capstone attaches the condition to each covered instruction.
                    return;
                case ArmInstructionId.ARM_INS_PUSH:
                case ArmInstructionId.ARM_INS_POP:
                {
                    var pop = id == ArmInstructionId.ARM_INS_POP;
                    if (!pop) Add(OpCode.ShiftStack, new Immediate(-4 * ops.Length));
                    for (var i = 0; i < ops.Length; i++)
                        if (Dest(i) is { } register && register.Name != "pc")
                        {
                            if (pop) Move(register, new StackOffset(i * 4));
                            else Add(OpCode.Move, new StackOffset(i * 4), register);
                        }
                    if (pop)
                    {
                        Add(OpCode.ShiftStack, new Immediate(4 * ops.Length));
                        if (ops.Any(op => op.Type == ArmOperandType.Register && op.Register.Name == "pc")) Return();
                    }
                    return;
                }
                case ArmInstructionId.ARM_INS_VPUSH:
                case ArmInstructionId.ARM_INS_VPOP:
                {
                    var pop = id == ArmInstructionId.ARM_INS_VPOP;
                    var registers = new List<Register>(ops.Length);
                    foreach (var operand in ops)
                    {
                        if (operand.Type != ArmOperandType.Register)
                        {
                            registers.Clear();
                            break;
                        }
                        registers.Add(Reg(operand.Register.Name));
                    }
                    if (registers.Count == 0 || registers.Count != ops.Length)
                        break;

                    var widths = registers.Select(register => register.Name.StartsWith("d", StringComparison.Ordinal) ? 8 : 4).ToArray();
                    var totalBytes = widths.Sum();
                    if (!pop) Add(OpCode.ShiftStack, new Immediate(-totalBytes));

                    var offset = 0;
                    for (var i = 0; i < registers.Count; i++)
                    {
                        var slot = new StackOffset(offset);
                        var transfer = pop
                            ? Move(registers[i], slot)
                            : Add(OpCode.Move, slot, registers[i]);
                        transfer.MemoryAccessSize = widths[i];
                        offset += widths[i];
                    }

                    if (pop) Add(OpCode.ShiftStack, new Immediate(totalBytes));
                    return;
                }
                case ArmInstructionId.ARM_INS_MOV:
                case ArmInstructionId.ARM_INS_MOVW:
                case ArmInstructionId.ARM_INS_MOVT:
                case ArmInstructionId.ARM_INS_MVN:
                case ArmInstructionId.ARM_INS_VMOV:
                case ArmInstructionId.ARM_INS_ADR:
                    if (ops.Length == 2 && Dest() is { } dst && Read(1) is { } src)
                    {
                        if (dst.Name == "pc")
                        {
                            if (src is Register { Name: "lr" }) Return();
                            else IndirectTailBranch(src);
                            return;
                        }
                        if (id == ArmInstructionId.ARM_INS_MOVT && src is Immediate upper)
                        {
                            var low = Reg("armMovtLow");
                            Integer(OpCode.And, low, dst, new Immediate(0xFFFF));
                            Integer(OpCode.Or, dst, low, new Immediate(unchecked((uint)upper.Value << 16)));
                        }
                        else if (id == ArmInstructionId.ARM_INS_MVN)
                            Integer(OpCode.Xor, dst, src, new Immediate(-1));
                        else if (src is Register { Name: "sp" })
                            Move(dst, new AddressOf(new StackOffset(0)));
                        else
                            Move(dst, src);
                        if (instruction.Details.UpdateFlags) LogicalFlags(dst);
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_LDR:
                case ArmInstructionId.ARM_INS_LDRB:
                case ArmInstructionId.ARM_INS_LDRH:
                case ArmInstructionId.ARM_INS_LDRSB:
                case ArmInstructionId.ARM_INS_LDRSH:
                case ArmInstructionId.ARM_INS_STR:
                case ArmInstructionId.ARM_INS_STRB:
                case ArmInstructionId.ARM_INS_STRH:
                case ArmInstructionId.ARM_INS_VLDR:
                case ArmInstructionId.ARM_INS_VSTR:
                    if (Dest() is { } data && ops.Length >= 2 && ops[1].Type == ArmOperandType.Memory
                        && LiftMemory(instruction, data, Read(1)))
                        return;
                    break;
                case ArmInstructionId.ARM_INS_CMP:
                case ArmInstructionId.ARM_INS_CMN:
                case ArmInstructionId.ARM_INS_TST:
                case ArmInstructionId.ARM_INS_TEQ:
                    if (Read(0) is { } a && Read(1) is { } b)
                    {
                        if (id is ArmInstructionId.ARM_INS_CMP or ArmInstructionId.ARM_INS_CMN)
                            ArithmeticFlags(a, b, id == ArmInstructionId.ARM_INS_CMP);
                        else
                        {
                            var bits = Reg("armTest");
                            Integer(id == ArmInstructionId.ARM_INS_TST ? OpCode.And : OpCode.Xor, bits, a, b);
                            LogicalFlags(bits);
                        }
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_CBZ:
                case ArmInstructionId.ARM_INS_CBNZ:
                    if (Read(0) is { } tested && Read(1) is Immediate cbz)
                    {
                        Branch((long)(cbz.UnsignedValue & ~1UL),
                            Combine(id == ArmInstructionId.ARM_INS_CBZ ? OpCode.CheckEqual : OpCode.CheckNotEqual, tested, new Immediate(0)));
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_B:
                    if (Read(0) is Immediate target)
                    {
                        var destination = (long)(unchecked((uint)target.Value) & ~1U);
                        var conditional = instruction.Details.ConditionCode is not (ArmConditionCode.ARM_CC_AL or ArmConditionCode.Invalid);
                        var start = (long)(method.UnderlyingPointer & ~1UL);
                        if (destination >= start && destination - start < method.RawBytes.Length)
                            Branch(destination, conditional ? Condition(instruction.Details.ConditionCode) : null);
                        else
                        {
                            if (conditional) Branch(instruction.Address + instruction.Bytes.Length, Not(Condition(instruction.Details.ConditionCode)));
                            Call((ulong)destination | (_thumb ? 1UL : 0));
                            Return();
                        }
                        // Only a taken branch changes the instruction stream. Preserve values
                        // on the straight-line fallthrough and reset them at branch targets.
                        if (!conditional) _constants.Clear();
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_BL:
                case ArmInstructionId.ARM_INS_BLX:
                    if (Read(0) is Immediate direct)
                    {
                        var thumbTarget = id == ArmInstructionId.ARM_INS_BL ? _thumb : !_thumb;
                        Call((unchecked((uint)direct.Value) & ~1U) | (thumbTarget ? 1UL : 0));
                        return;
                    }
                    if (Read(0) is Register indirect)
                    {
                        Add(OpCode.IndirectCall, indirect, Reg("r0")).AddOperands(Convention.ResolveForUnmanaged(method.AppContext, 0));
                        InvalidateCall();
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_BX:
                    if (Read(0) is Register branch)
                    {
                        if (branch.Name == "lr") Return();
                        else IndirectTailBranch(branch);
                        _constants.Clear();
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_SXTB:
                case ArmInstructionId.ARM_INS_SXTH:
                case ArmInstructionId.ARM_INS_UXTB:
                case ArmInstructionId.ARM_INS_UXTH:
                    if (ops.Length == 2 && Dest() is { } narrow && Read(1) is { } value)
                    {
                        if (id is ArmInstructionId.ARM_INS_UXTB or ArmInstructionId.ARM_INS_UXTH)
                            Integer(OpCode.And, narrow, value, new Immediate(id == ArmInstructionId.ARM_INS_UXTB ? 255 : 65535));
                        else
                            Add(OpCode.Convert, narrow, value, new NumericConversion(id == ArmInstructionId.ARM_INS_SXTB
                                ? NumericConversionKind.I32Extend8Signed : NumericConversionKind.I32Extend16Signed));
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_UBFX:
                    if (Dest() is { } extract && Read(1) is { } word && Read(2) is Immediate { Value: >= 0 and < 32 } shift
                        && Read(3) is Immediate { Value: > 0 and <= 32 } count && shift.Value + count.Value <= 32)
                    {
                        var shifted = Reg("armExtract");
                        Integer(OpCode.ShiftRightUnsigned, shifted, word, shift);
                        Integer(OpCode.And, extract, shifted, new Immediate((1L << (int)count.Value) - 1));
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_VMRS:
                    if (instruction.Operand.StartsWith("apsr_nzcv", StringComparison.Ordinal)) return;
                    break;
                case ArmInstructionId.ARM_INS_VCMP:
                case ArmInstructionId.ARM_INS_VCMPE:
                    if (Read(0) is { } fl && Read(1) is { } fr)
                    {
                        FloatFlags(fl, fr, instruction.Mnemonic.EndsWith(".f64", StringComparison.Ordinal));
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_VCVT:
                    if (Dest() is { } converted && Read(1) is { } input && Conversion(instruction.Mnemonic) is { } kind)
                    {
                        Add(OpCode.Convert, converted, input, new NumericConversion(kind));
                        return;
                    }
                    break;
                case ArmInstructionId.ARM_INS_VNEG:
                    if (Dest() is { } negated && Read(1) is { } fp)
                    {
                        var wide = negated.Name.StartsWith("d", StringComparison.Ordinal);
                        Add(OpCode.Negate, negated, fp, new FloatingPointPrecision(wide));
                        return;
                    }
                    break;
            }

            var operation = id switch
            {
                ArmInstructionId.ARM_INS_ADD or ArmInstructionId.ARM_INS_VADD => OpCode.Add,
                ArmInstructionId.ARM_INS_SUB or ArmInstructionId.ARM_INS_RSB or ArmInstructionId.ARM_INS_VSUB => OpCode.Subtract,
                ArmInstructionId.ARM_INS_MUL or ArmInstructionId.ARM_INS_VMUL => OpCode.Multiply,
                ArmInstructionId.ARM_INS_SDIV or ArmInstructionId.ARM_INS_VDIV => OpCode.Divide,
                ArmInstructionId.ARM_INS_UDIV => OpCode.DivideUnsigned,
                ArmInstructionId.ARM_INS_AND or ArmInstructionId.ARM_INS_BIC => OpCode.And,
                ArmInstructionId.ARM_INS_ORR => OpCode.Or,
                ArmInstructionId.ARM_INS_EOR => OpCode.Xor,
                ArmInstructionId.ARM_INS_LSL => OpCode.ShiftLeft,
                ArmInstructionId.ARM_INS_LSR => OpCode.ShiftRightUnsigned,
                ArmInstructionId.ARM_INS_ASR => OpCode.ShiftRight,
                _ => (OpCode?)null,
            };

            if (operation is { } op && Dest() is { } result)
            {
                // Capstone may expose LSR/LSL/ASR as a MOV alias with the shift on its source.
                if (op is OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
                    && ops.Length == 2 && ops[1].ShiftOperation != ArmShiftOperation.Invalid && Read(1) is { } shifted)
                {
                    Move(result, shifted);
                    if (instruction.Details.UpdateFlags) LogicalFlags(result);
                    return;
                }
                var left = ops.Length == 2 ? result : Read(1);
                var right = Read(ops.Length == 2 ? 1 : 2);
                if (left != null && right != null)
                {
                    if (id is ArmInstructionId.ARM_INS_VADD or ArmInstructionId.ARM_INS_VSUB or ArmInstructionId.ARM_INS_VMUL or ArmInstructionId.ARM_INS_VDIV)
                        Add(op, result, left, right, new FloatingPointPrecision(result.Name.StartsWith("d", StringComparison.Ordinal)));
                    else if (left is Register { Name: "sp" } && right is Immediate immediate && op is OpCode.Add or OpCode.Subtract)
                    {
                        var delta = unchecked((int)(op == OpCode.Add ? immediate.Value : -immediate.Value));
                        if (result.Name == "sp") Add(OpCode.ShiftStack, new Immediate(delta));
                        else Move(result, new AddressOf(new StackOffset(delta)));
                    }
                    else
                    {
                        if (id == ArmInstructionId.ARM_INS_RSB) (left, right) = (right, left);
                        if (id == ArmInstructionId.ARM_INS_BIC)
                        {
                            var mask = Reg("armBicMask");
                            Integer(OpCode.Xor, mask, right, new Immediate(-1));
                            right = mask;
                        }
                        if (instruction.Details.UpdateFlags && op is OpCode.Add or OpCode.Subtract)
                            ArithmeticFlags(left, right, op == OpCode.Subtract);
                        Integer(op, result, left, right);
                        if (instruction.Details.UpdateFlags && op is not (OpCode.Add or OpCode.Subtract)) LogicalFlags(result);
                    }
                    return;
                }
            }

            if (id is ArmInstructionId.ARM_INS_VMLA or ArmInstructionId.ARM_INS_VMLS
                && Dest() is { } accum && Read(1) is { } factor && Read(2) is { } other)
            {
                var product = Reg("armVfpProduct");
                var precision = new FloatingPointPrecision(accum.Name.StartsWith("d", StringComparison.Ordinal));
                Add(OpCode.Multiply, product, factor, other, precision);
                Add(id == ArmInstructionId.ARM_INS_VMLA ? OpCode.Add : OpCode.Subtract, accum, accum, product, precision);
                return;
            }
            Unsupported(instruction);
        }

        private bool LiftMemory(ArmInstruction instruction, Register reg, IOperand? address)
        {
            if (address == null)
                return false;
            var id = instruction.Id;
            var ops = instruction.Details.Operands;
            var memory = ops[1].Memory;
            var baseName = memory.Base?.Name;
            var indexName = memory.Index?.Name;
            var writeback = instruction.Details.WriteBack;
            if (writeback && (baseName == null || indexName is not (null or "" or "invalid")
                || ops.Length > 2 && ops[2].Type != ArmOperandType.Immediate))
                return false;
            var post = writeback && ops.Length > 2;
            var delta = post ? ops[2].Immediate * (ops[2].IsSubtracted ? -1 : 1) : memory.Displacement;
            if (writeback && baseName == "sp" && !post)
            {
                Add(OpCode.ShiftStack, new Immediate(delta));
                address = new StackOffset(0);
            }

            var load = id is ArmInstructionId.ARM_INS_LDR or ArmInstructionId.ARM_INS_LDRB or ArmInstructionId.ARM_INS_LDRH
                or ArmInstructionId.ARM_INS_LDRSB or ArmInstructionId.ARM_INS_LDRSH or ArmInstructionId.ARM_INS_VLDR;
            var width = id is ArmInstructionId.ARM_INS_LDRB or ArmInstructionId.ARM_INS_LDRSB or ArmInstructionId.ARM_INS_STRB ? 1
                : id is ArmInstructionId.ARM_INS_LDRH or ArmInstructionId.ARM_INS_LDRSH or ArmInstructionId.ARM_INS_STRH ? 2
                : reg.Name.StartsWith("d", StringComparison.Ordinal) ? 8 : 4;
            if (load && reg.Name == "pc")
            {
                if (baseName == "sp" && post && delta == 4)
                {
                    Add(OpCode.ShiftStack, new Immediate(delta));
                    Return();
                }
                else Add(OpCode.IndirectJump, address);
                return true;
            }

            if (!load)
                Add(OpCode.Move, address, reg).MemoryAccessSize = width;
            else if (baseName == "pc" && address is MemoryOperand { IsConstant: true } literal
                && ReadConstant(literal.Addend, width, id == ArmInstructionId.ARM_INS_VLDR) is { } value)
                Move(reg, value);
            else if (id is ArmInstructionId.ARM_INS_LDRSB or ArmInstructionId.ARM_INS_LDRSH)
            {
                var narrow = Reg("armNarrowLoad");
                Move(narrow, address).MemoryAccessSize = width;
                Add(OpCode.Convert, reg, narrow, new NumericConversion(width == 1
                    ? NumericConversionKind.I32Extend8Signed : NumericConversionKind.I32Extend16Signed));
            }
            else Move(reg, address).MemoryAccessSize = width;

            if (writeback && baseName != null && (baseName != "sp" || post))
            {
                if (baseName == "sp") Add(OpCode.ShiftStack, new Immediate(delta));
                else Integer(OpCode.Add, Reg(baseName), Reg(baseName), new Immediate(delta));
            }
            return true;
        }

        private IOperand? ReadOperand(ArmOperand operand, uint pc)
        {
            if (operand.Type == ArmOperandType.Immediate) return new Immediate(operand.Immediate);
            if (operand.Type == ArmOperandType.FloatingPoint) return new FloatLiteral((float)operand.FloatingPoint);
            if (operand.Type == ArmOperandType.Register)
            {
                IOperand value = operand.Register.Name == "pc" ? new Immediate(pc) : Reg(operand.Register.Name);
                if (operand.ShiftOperation == ArmShiftOperation.Invalid) return value;
                var shift = operand.ShiftOperation switch
                {
                    ArmShiftOperation.ARM_SFT_LSL => OpCode.ShiftLeft,
                    ArmShiftOperation.ARM_SFT_LSR => OpCode.ShiftRightUnsigned,
                    ArmShiftOperation.ARM_SFT_ASR => OpCode.ShiftRight,
                    _ => (OpCode?)null
                };
                if (shift == null || operand.ShiftValue >= 32) return null;
                var shifted = Reg("armShift" + _result.Count);
                Integer(shift.Value, shifted, value, new Immediate(operand.ShiftValue));
                return shifted;
            }
            if (operand.Type != ArmOperandType.Memory) return null;

            var memory = operand.Memory;
            var baseName = memory.Base?.Name;
            var indexName = memory.Index?.Name;
            if (indexName is "invalid" or "") indexName = null;
            var scale = 0;
            if (indexName != null)
            {
                if (memory.Scale < 0 || operand.IsSubtracted
                    || operand.ShiftOperation is not (ArmShiftOperation.ARM_SFT_LSL or ArmShiftOperation.Invalid)
                    || operand.ShiftOperation != ArmShiftOperation.Invalid && operand.ShiftValue > 8)
                    return null;
                scale = operand.ShiftOperation == ArmShiftOperation.ARM_SFT_LSL
                    ? 1 << (int)operand.ShiftValue : 1 << memory.LeftShit;
            }
            uint? baseValue = baseName == "pc" ? pc
                : baseName != null && _constants.TryGetValue(baseName, out var known) ? known : null;
            if (baseValue is { } absolute)
            {
                if (indexName == null)
                    return new MemoryOperand(addend: unchecked(absolute + (uint)memory.Displacement));
                if (_constants.TryGetValue(indexName, out var index))
                    return new MemoryOperand(addend: unchecked(absolute + index * (uint)scale + (uint)memory.Displacement));
            }
            if (baseName == "sp" && indexName == null) return new StackOffset(memory.Displacement);
            return baseName is null or "pc" or "invalid" ? null
                : new MemoryOperand(Reg(baseName), indexName == null ? null : Reg(indexName), memory.Displacement, scale);
        }

        private IOperand? ReadConstant(long address, int width, bool floating)
        {
            var binary = method.AppContext.Binary;
            if (!binary.TryMapVirtualAddressToRaw(unchecked((ulong)address), out var raw) || raw < 0 || raw > binary.RawLength - width)
                return null;
            var data = binary.GetRawBinaryContent().Slice((int)raw, width);
            if (width == 8 && floating)
                return new DoubleLiteral(BitConverter.Int64BitsToDouble(binary.IsLittleEndian
                    ? BinaryPrimitives.ReadInt64LittleEndian(data) : BinaryPrimitives.ReadInt64BigEndian(data)));
            if (width != 4) return null;
            var word = binary.IsLittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
            return floating ? new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(word), 0)) : new Immediate(word);
        }

        private void Call(ulong address)
        {
            if (!method.AppContext.MethodsByAddress.TryGetValue(address, out var candidates)
                && method.AppContext.MethodsByAddress.TryGetValue(address & ~1UL, out candidates))
                address &= ~1UL;
            var callee = candidates is { Count: 1 } ? candidates[0] : null;
            var call = callee?.IsVoid == true ? Add(OpCode.CallVoid, new Immediate((long)address))
                : Add(OpCode.Call, new Immediate((long)address), Reg("r0"));
            if (callee?.IsVoid == true) call.ImplicitDefinition = Reg("r0");
            call.AddOperands(callee != null ? Convention.ResolveForCall(callee) : Convention.ResolveForUnmanaged(method.AppContext, address));
            InvalidateCall();
        }

        private void IndirectTailBranch(IOperand target)
        {
            Add(OpCode.IndirectJump, target, Reg("r0"))
                .AddOperands(Convention.ResolveForUnmanaged(method.AppContext, 0));
        }

        private void InvalidateCall()
        {
            foreach (var name in new[] { "r0", "r1", "r2", "r3", "ip", "lr" }) _constants.Remove(name);
        }

        private void Return()
        {
            if (method.IsVoid) Add(OpCode.Return);
            else Add(OpCode.Return, Reg("r0"));
            _constants.Clear();
        }

        private void Branch(long address, IOperand? condition)
        {
            var branch = condition == null ? Add(OpCode.Jump, new Immediate(address))
                : Add(OpCode.ConditionalJump, new Immediate(address), condition);
            _branches.Add((branch, address));
        }

        private void LogicalFlags(IOperand value)
        {
            Add(OpCode.CheckEqual, Zero, value, new Immediate(0)).OperandWidth = 32;
            Add(OpCode.CheckLess, Negative, value, new Immediate(0)).OperandWidth = 32;
        }

        private void ArithmeticFlags(IOperand left, IOperand right, bool subtract)
        {
            var result = Reg("TEMP1");
            Integer(subtract ? OpCode.Subtract : OpCode.Add, result, left, right);
            var xor1 = Reg("armFlagXor1");
            var xor2 = Reg("armFlagXor2");
            var overflow = Reg("armFlagOverflow");
            Integer(OpCode.Xor, xor1, left, subtract ? right : result);
            Integer(OpCode.Xor, xor2, subtract ? left : right, result);
            Integer(OpCode.And, overflow, xor1, xor2);
            Add(OpCode.CheckLess, Negative, result, new Immediate(0)).OperandWidth = 32;
            Add(OpCode.CheckLess, Overflow, overflow, new Immediate(0)).OperandWidth = 32;
            Add(OpCode.CheckEqual, Zero, result, new Immediate(0)).OperandWidth = 32;
            if (subtract)
                Add(OpCode.CheckGreaterOrEqualUnsigned, Carry, left, right).OperandWidth = 32;
            else Add(OpCode.CheckLessUnsigned, Carry, result, left).OperandWidth = 32;
        }

        private void FloatFlags(IOperand left, IOperand right, bool wide)
        {
            var precision = new FloatingPointPrecision(wide);
            Add(OpCode.CheckEqual, Zero, left, right, precision);
            Add(OpCode.CheckLess, Negative, left, right, precision);
            Add(OpCode.CheckGreaterOrEqualUnsigned, Carry, left, right, precision);
            var leftNaN = Reg("armLeftNaN");
            var rightNaN = Reg("armRightNaN");
            Add(OpCode.CheckNotEqual, leftNaN, left, left, precision);
            Add(OpCode.CheckNotEqual, rightNaN, right, right, precision);
            Add(OpCode.Or, Overflow, leftNaN, rightNaN);
        }

        private IOperand Condition(ArmConditionCode condition)
        {
            IOperand SignedLess() => Not(Combine(OpCode.CheckEqual, Negative, Overflow));
            return condition switch
            {
                ArmConditionCode.ARM_CC_EQ => Zero,
                ArmConditionCode.ARM_CC_NE => Not(Zero),
                ArmConditionCode.ARM_CC_HS => Carry,
                ArmConditionCode.ARM_CC_LO => Not(Carry),
                ArmConditionCode.ARM_CC_MI => Negative,
                ArmConditionCode.ARM_CC_PL => Not(Negative),
                ArmConditionCode.ARM_CC_VS => Overflow,
                ArmConditionCode.ARM_CC_VC => Not(Overflow),
                ArmConditionCode.ARM_CC_HI => Combine(OpCode.And, Carry, Not(Zero)),
                ArmConditionCode.ARM_CC_LS => Combine(OpCode.Or, Not(Carry), Zero),
                ArmConditionCode.ARM_CC_LT => SignedLess(),
                ArmConditionCode.ARM_CC_GE => Combine(OpCode.CheckEqual, Negative, Overflow),
                ArmConditionCode.ARM_CC_GT => Combine(OpCode.And, Combine(OpCode.CheckEqual, Negative, Overflow), Not(Zero)),
                ArmConditionCode.ARM_CC_LE => Combine(OpCode.Or, SignedLess(), Zero),
                _ => new Immediate(1)
            };
        }

        private Register Not(IOperand source)
        {
            var result = Reg("armCondition" + _result.Count);
            Add(OpCode.Not, result, source);
            return result;
        }

        private Register Combine(OpCode code, IOperand left, IOperand right)
        {
            var result = Reg("armCondition" + _result.Count);
            Add(code, result, left, right);
            return result;
        }

        private Instruction Move(Register destination, IOperand source)
        {
            var constant = Constant(source);
            var instruction = Add(OpCode.Move, destination, source);
            if (constant is { } value) _constants[destination.Name] = value;
            return instruction;
        }

        private void Integer(OpCode op, Register destination, IOperand left, IOperand right)
        {
            if (Constant(left) is { } a && Constant(right) is { } b && op is OpCode.Add or OpCode.Subtract or OpCode.And or OpCode.Or or OpCode.Xor)
            {
                var result = op switch
                {
                    OpCode.Add => unchecked(a + b),
                    OpCode.Subtract => unchecked(a - b),
                    OpCode.And => a & b,
                    OpCode.Or => a | b,
                    _ => a ^ b,
                };
                Move(destination, new Immediate(result)).OperandWidth = 32;
                return;
            }
            Add(op, destination, left, right).OperandWidth = 32;
        }

        private uint? Constant(IOperand value) => value switch
        {
            Immediate immediate => unchecked((uint)immediate.Value),
            Register register when _constants.TryGetValue(register.Name, out var bits) => bits,
            _ => null
        };

        private Instruction Add(OpCode code, params IOperand[] operands)
        {
            var instruction = new Instruction(_result.Count, code, [.. operands]);
            _result.Add(instruction);
            if (instruction.Destination is Register reg) _constants.Remove(reg.Name);
            return instruction;
        }

        private void Unsupported(ArmInstruction instruction)
        {
            Add(OpCode.NotImplemented, new StringLiteral($"ARMv7: {instruction.Mnemonic} {instruction.Operand}"));
            _constants.Clear();
        }

        private static NumericConversionKind? Conversion(string mnemonic) => mnemonic switch
        {
            "vcvt.f32.s32" => NumericConversionKind.SignedInt32ToFloat32,
            "vcvt.f64.s32" => NumericConversionKind.SignedInt32ToFloat64,
            "vcvt.f32.u32" => NumericConversionKind.UnsignedInt32ToFloat32,
            "vcvt.f64.u32" => NumericConversionKind.UnsignedInt32ToFloat64,
            "vcvt.f32.f64" => NumericConversionKind.Float64ToFloat32,
            "vcvt.f64.f32" => NumericConversionKind.Float32ToFloat64,
            "vcvt.s32.f32" => NumericConversionKind.Float32ToInt32Signed,
            "vcvt.s32.f64" => NumericConversionKind.Float64ToInt32Signed,
            "vcvt.u32.f32" => NumericConversionKind.Float32ToInt32Unsigned,
            "vcvt.u32.f64" => NumericConversionKind.Float64ToInt32Unsigned,
            _ => null
        };
    }
}
