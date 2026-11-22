using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using MC2IL.Core.Api;
using MC2IL.Core.Il2CppApiFunctions;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace MC2IL.Core.InstructionSets;

public class NewArmV8InstructionSet : MC2ILInstructionSet
{
    private static readonly Arm64CallingConventionResolver CallingConventions = new();

    public override BaseCallingConventionResolver CallingConventionResolver => CallingConventions;

    private static Immediate Imm(long value) => new(value);
    private static Immediate Imm(ulong value) => new(unchecked((long)value));

    private static string NormalizeRegister(Arm64Register reg) => reg switch
    {
        >= Arm64Register.W0 and <= Arm64Register.W31 => "X" + (reg - Arm64Register.W0),
        >= Arm64Register.X0 and <= Arm64Register.X31 => "X" + (reg - Arm64Register.X0),
        >= Arm64Register.V0 and <= Arm64Register.V31 => "V" + (reg - Arm64Register.V0),
        >= Arm64Register.D0 and <= Arm64Register.D31 => "V" + (reg - Arm64Register.D0),
        >= Arm64Register.S0 and <= Arm64Register.S31 => "V" + (reg - Arm64Register.S0),
        >= Arm64Register.H0 and <= Arm64Register.H31 => "V" + (reg - Arm64Register.H0),
        >= Arm64Register.B0 and <= Arm64Register.B31 => "V" + (reg - Arm64Register.B0),
        _ => reg.ToString()
    };

    private static Register Reg(Arm64Register reg) => new(null, NormalizeRegister(reg));

    // integer register 31 is SP or ZR depending on context, callers must decide which
    private static bool IsReg31(Arm64Register reg) => reg is Arm64Register.X31 or Arm64Register.W31;

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var binary = context.AppContext.Binary;

        if (context is not ConcreteGenericMethodAnalysisContext)
        {
            //Managed method or attr gen => grab raw byte range between a and b
            var startOfNextFunction = context.AppContext.GetAddressOfNextFunctionStart(context.UnderlyingPointer);
            var count = (int)(startOfNextFunction - context.UnderlyingPointer);

            if (startOfNextFunction > 0)
            {
                var startRaw = (int)binary.MapVirtualAddressToRaw(context.UnderlyingPointer);
                if (startRaw > 0 && startRaw + count <= binary.RawLength)
                    return new BinarySlice(binary, startRaw, count);
            }
        }

        var result = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);
        var lastInsn = result.LastValid();

        var start = (int)binary.MapVirtualAddressToRaw(context.UnderlyingPointer);
        // Map the last instruction (always within segment) and add 4 (ARM64 instruction size).
        // This avoids mapping endVa which may land exactly at a segment boundary gap.
        var end = (int)binary.MapVirtualAddressToRaw(lastInsn.Address) + 4;

        //Sanity check
        if (start < 0 || end < 0 || start >= binary.RawLength || end >= binary.RawLength)
            throw new Exception($"Failed to map virtual address 0x{context.UnderlyingPointer:X} to raw address for method {context!.DeclaringType?.FullName}/{context.Name} - start: 0x{start:X}, end: 0x{end:X} are out of bounds for length {binary.RawLength}.");

        return new BinarySlice(binary, start, end - start);
    }

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        return CallingConventions.ResolveForManaged(context).ToList();
    }

    public override (IReadOnlyList<ulong> DataReferences, IReadOnlyList<ulong> CallTargets) InspectPotentialThrowHelper(ApplicationAnalysisContext context, ulong address)
        => InspectPotentialThrowHelper(context, address, false);

    internal (IReadOnlyList<ulong> DataReferences, IReadOnlyList<ulong> CallTargets) InspectPotentialThrowHelper(
        ApplicationAnalysisContext context, ulong address, bool stopAtAdjacentHelper)
    {
        //Deliberately not calling NewArm64Utils here, it's too slow
        const int maxInstructions = 48;

        var binary = context.Binary;
        var rawStart = (int)binary.MapVirtualAddressToRaw(address);
        if (rawStart <= 0)
            return ([], []);

        var content = binary.GetRawBinaryContent();
        var window = System.Math.Min(maxInstructions * 4, content.Length - rawStart);
        if (window < 4)
            return ([], []);

        List<Arm64Instruction> body;
        try
        {
            body = Disassembler.Disassemble(content.Slice(rawStart, window), address, new Disassembler.Options(true, true, false)).ToList();
        }
        catch
        {
            return ([], []);
        }

        var dataReferences = new List<ulong>();
        var callTargets = new List<ulong>();
        var pages = new Dictionary<Arm64Register, ulong>();

        foreach (var insn in body)
        {
            // Non-returning helpers often sit immediately before another function without a
            // RET. A new frame setup or a forward call target starts a separate helper.
            if (stopAtAdjacentHelper && callTargets.Count > 0
                && (callTargets.Contains(insn.Address)
                    || (insn.Mnemonic is Arm64Mnemonic.STR or Arm64Mnemonic.STP
                        && (insn.Op0Reg == Arm64Register.X30 || insn.Op1Reg == Arm64Register.X30)
                        && IsReg31(insn.MemBase)
                        && insn.MemIndexMode == Arm64MemoryIndexMode.PreIndex && insn.MemOffset < 0)))
                break;

            switch (insn.Mnemonic)
            {
                case Arm64Mnemonic.ADRP:
                    pages[insn.Op0Reg] = (ulong)((long)(insn.Address & ~0xFFFUL) + insn.Op1Imm);
                    break;
                case Arm64Mnemonic.ADD when insn.Op2Kind == Arm64OperandKind.Immediate && pages.TryGetValue(insn.Op1Reg, out var page):
                    dataReferences.Add(page + (ulong)insn.Op2Imm);
                    break;
                case Arm64Mnemonic.ADR:
                    dataReferences.Add((ulong)((long)insn.Address + insn.Op1Imm));
                    break;
                case Arm64Mnemonic.BL:
                    callTargets.Add(insn.BranchTarget);
                    break;
            }

            if (insn.Mnemonic != Arm64Mnemonic.ADRP && insn.Op0Kind == Arm64OperandKind.Register)
                pages.Remove(insn.Op0Reg);

            if (insn.Mnemonic is Arm64Mnemonic.RET or Arm64Mnemonic.RETAA or Arm64Mnemonic.RETAB or Arm64Mnemonic.BR or Arm64Mnemonic.INVALID
                || (insn.Mnemonic == Arm64Mnemonic.B && insn.MnemonicConditionCode is Arm64ConditionCode.NONE or Arm64ConditionCode.AL))
                break;
        }

        return (dataReferences, callTargets);
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();

        foreach (var instruction in insns)
            ConvertInstructionStatement(instruction, instructions, addresses, context);

        // Add return if the function doesn't end with one already
        if (instructions.Count > 0 && instructions[^1].OpCode != OpCode.Return)
        {
            var index = instructions[^1].Index + 1;

            if (context.IsVoid)
                instructions.Add(new Instruction(index, OpCode.Return));
            else
                instructions.Add(new Instruction(index, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context)));
        }

        // fix branches
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode != OpCode.Jump && instruction.OpCode != OpCode.ConditionalJump)
                continue;

            var targetAddress = ((Immediate)instruction.Operands[0]).UnsignedValue;
            var targetIndex = addresses.FindIndex(addr => addr == targetAddress);

            if (targetIndex == -1)
            {
                instruction.OpCode = OpCode.Invalid;
                instruction.SetOperands(new StringLiteral($"Jump target not found in method: 0x{targetAddress:X4}"));
                continue;
            }

            var targetInstruction = instructions[targetIndex];

            instruction.SetOperand(0, targetInstruction);
        }

        if (instructions.Count != 0)
            BindIncomingAggregateLanes(context, instructions);

        return instructions;
    }

    private static void BindIncomingAggregateLanes(MethodAnalysisContext context, List<Instruction> instructions)
    {
        if (context.Parameters.Count == 0)
            return;

        List<Instruction>? entry = null;
        CallingConventions.ResolveForManagedEntry(context, (argument, fields, lanes) =>
        {
            entry ??= [];
            for (var i = 0; i < fields.Length; i++)
                entry.Add(new Instruction(entry.Count, OpCode.Move, lanes[i],
                    new MemoryOperand(argument, addend: fields[i].Offset)));
        });
        if (entry == null)
            return;

        // Existing branches target the original instructions, so a loop never reloads its arguments.
        foreach (var instruction in instructions)
            instruction.Index += entry.Count;
        instructions.InsertRange(0, entry);
    }

    private void ConvertInstructionStatement(Arm64Instruction instruction, List<Instruction> instructions, List<ulong> addresses, MethodAnalysisContext context)
    {
        var address = instruction.Address;

        Instruction Add(ulong address, OpCode opCode, params List<IOperand> operands)
        {
            addresses.Add(address);
            var newInstruction = new Instruction(instructions.Count, opCode, operands);
            if (instruction.Op0Kind == Arm64OperandKind.Register
                && instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31
                && instruction.Mnemonic is not (Arm64Mnemonic.FMOV or Arm64Mnemonic.SXTB or Arm64Mnemonic.SXTH
                    or Arm64Mnemonic.SXTW or Arm64Mnemonic.UXTB or Arm64Mnemonic.UXTH))
            {
                newInstruction.OperandWidth = 32;
            }
            else if (instruction.Mnemonic == Arm64Mnemonic.MOV
                && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31
                && instruction.Op1Kind == Arm64OperandKind.Register
                && instruction.Op1Reg is >= Arm64Register.W0 and <= Arm64Register.W31)
            {
                // MOV Xd, Wn reads the low 32 bits and zero-extends them to Xd.
                newInstruction.OperandWidth = 32;
            }
            instructions.Add(newInstruction);
            return newInstruction;
        }

        Register? AddCallAt(ulong target)
        {
            if (context.AppContext.MethodsByAddress.TryGetValue(target, out var possibleMethods) && possibleMethods.Count > 0)
            {
                MethodAnalysisContext ctx;
                if (possibleMethods.Count == 1)
                {
                    ctx = possibleMethods[0];
                }
                else
                {
                    // multiple methods folded onto one address, pick the one with the most arguments so nothing gets truncated
                    ctx = possibleMethods[0];
                    var mostArguments = -1;
                    foreach (var method in possibleMethods)
                    {
                        var candidateArgumentCount = method.Parameters.Count + (method.IsStatic ? 0 : 1);
                        if (candidateArgumentCount > mostArguments)
                        {
                            mostArguments = candidateArgumentCount;
                            ctx = method;
                        }
                    }
                }

                var aggregateIndex = 0;
                var callArguments = possibleMethods.Count == 1
                    ? CallingConventions.ResolveForManagedCall(ctx, (type, lanes) =>
                    {
                        var packed = new Register(null, $"TEMP_CALL_AGGREGATE{aggregateIndex++}");
                        var pack = Add(address, OpCode.PackFloatAggregate, packed, type);
                        pack.AddOperands(lanes);
                        return packed;
                    })
                    : CallingConventions.ResolveForUnmanaged(context.AppContext, target);

                var returnFields = possibleMethods.Count == 1 && !ctx.IsVoid
                    ? Arm64CallingConventionResolver.GetFloatAggregateFields(ctx.ReturnType)
                    : null;
                var aggregateResult = new Register(null, "TEMP_CALL_RESULT");
                var call = ctx.IsVoid
                    ? Add(address, OpCode.CallVoid, Imm(target))
                    : Add(address, OpCode.Call, Imm(target), returnFields == null
                        ? CallingConventions.ReturnRegister(ctx)
                        : aggregateResult);

                call.AddOperands(callArguments);

                if (returnFields != null)
                {
                    // An HFA returns one member in each of V0-V3. Keep the managed result whole
                    // and expose its lanes to the subsequent native instructions.
                    for (var i = 0; i < returnFields.Length; i++)
                        Add(address, OpCode.Move, new Register(null, $"V{i}"),
                            new MemoryOperand(aggregateResult, addend: returnFields[i].Offset));
                    return ctx.ReturnType == context.ReturnType ? aggregateResult : null;
                }
            }
            else
            {
                // Not a managed method, so we don't know its signature, preserve all argument registers
                var call = Add(address, OpCode.Call, Imm(target), new Register(null, "X0"));
                call.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, target));
            }

            return null;
        }

        void AddFloatArithmetic(OpCode opCode)
        {
            var vectorLaneCount = instruction.Op0Arrangement switch
            {
                Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.TwoD => 2,
                Arm64ArrangementSpecifier.FourS => 4,
                _ => 0,
            };
            var vectorIsDouble = instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD;
            if (instruction.Mnemonic is Arm64Mnemonic.FMUL or Arm64Mnemonic.FDIV or Arm64Mnemonic.FADD or Arm64Mnemonic.FSUB
                && instruction.Op0Reg is >= Arm64Register.V0 and <= Arm64Register.V31
                && vectorLaneCount > 0)
            {
                var baseName = NormalizeRegister(instruction.Op0Reg);
                var elementWidth = vectorIsDouble ? "D" : "S";
                Register Lane(int lane) => new(null, $"{baseName}.{elementWidth}{lane}");

                IOperand VectorOperand(int operand, int lane)
                {
                    var kind = operand == 1 ? instruction.Op1Kind : instruction.Op2Kind;
                    var reg = operand == 1 ? instruction.Op1Reg : instruction.Op2Reg;
                    if (kind == Arm64OperandKind.VectorRegisterElement)
                    {
                        var element = operand == 1 ? instruction.Op1VectorElement : instruction.Op2VectorElement;
                        var width = element.Width switch
                        {
                            Arm64VectorElementWidth.S => "S",
                            Arm64VectorElementWidth.D => "D",
                            _ => elementWidth,
                        };
                        return new Register(null, $"{NormalizeRegister(reg)}.{width}{element.Index}");
                    }

                    if (kind == Arm64OperandKind.Register
                        && reg is >= Arm64Register.V0 and <= Arm64Register.V31)
                        return new Register(null, $"{NormalizeRegister(reg)}.{elementWidth}{lane}");

                    return ConvertOperand(instruction, operand);
                }

                for (var lane = 0; lane < vectorLaneCount; lane++)
                    Add(address, opCode, Lane(lane), VectorOperand(1, lane), VectorOperand(2, lane),
                        new FloatingPointPrecision(vectorIsDouble));

                // The ABI exposes the low lane as the scalar Vn value used by aggregate
                // call setup. Keep that view in sync with the explicit lane values.
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), Lane(0));
                return;
            }

            var operands = new List<IOperand>
            {
                ConvertOperand(instruction, 0),
                ConvertOperand(instruction, 1),
                ConvertOperand(instruction, 2)
            };

            if (instruction.Op0Reg is >= Arm64Register.S0 and <= Arm64Register.S31)
                operands.Add(new FloatingPointPrecision(IsDouble: false));
            else if (instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31)
                operands.Add(new FloatingPointPrecision(IsDouble: true));

            Add(address, opCode, operands);
        }

        void AddFloatNegate()
        {
            var laneCount = instruction.Op0Arrangement switch
            {
                Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.TwoD => 2,
                Arm64ArrangementSpecifier.FourS => 4,
                _ => 0,
            };
            var isDouble = instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD
                || instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31;
            var precision = new FloatingPointPrecision(isDouble);

            if (instruction.Op0Reg is >= Arm64Register.V0 and <= Arm64Register.V31 && laneCount > 0)
            {
                var baseName = NormalizeRegister(instruction.Op0Reg);
                var elementWidth = isDouble ? "D" : "S";
                for (var lane = 0; lane < laneCount; lane++)
                {
                    var destination = new Register(null, $"{baseName}.{elementWidth}{lane}");
                    var source = instruction.Op1Kind == Arm64OperandKind.VectorRegisterElement
                        ? ConvertOperand(instruction, 1)
                        : new Register(null, $"{NormalizeRegister(instruction.Op1Reg)}.{elementWidth}{lane}");
                    Add(address, OpCode.Negate, destination, source, precision);
                }
                Add(address, OpCode.Move, ConvertOperand(instruction, 0),
                    new Register(null, $"{baseName}.{elementWidth}0"));
                return;
            }

            Add(address, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), precision);
        }

        void AddReturn(Register? tailCallResult = null, bool readFloatLanes = false)
        {
            if (context.IsVoid)
            {
                Add(address, OpCode.Return);
            }
            else if (readFloatLanes && Arm64CallingConventionResolver.GetFloatAggregateFields(context.ReturnType) is { } fields)
            {
                // Homogeneous floating-point aggregates leave one field in each of V0-V3.
                var result = new Register(null, "TEMP_RETURN_AGGREGATE");
                var pack = Add(address, OpCode.PackFloatAggregate, result, context.ReturnType);
                for (var lane = 0; lane < fields.Length; lane++)
                    pack.AddOperands([new Register(null, $"V{lane}")]);
                Add(address, OpCode.Return, result);
            }
            else
            {
                Add(address, OpCode.Return, tailCallResult ?? CallingConventions.ReturnValueSourceRegister(context));
            }
        }

        bool DiscardZeroRegisterDestination()
        {
            if (instruction.Op0Kind != Arm64OperandKind.Register || !IsReg31(instruction.Op0Reg))
                return false;

            Add(address, OpCode.Nop);
            return true;
        }

        // for pre/post indexed accesses, apply the base register update on the correct side of the access
        void EmitWriteback(bool beforeAccess)
        {
            var isPre = instruction.MemIndexMode == Arm64MemoryIndexMode.PreIndex;
            var isPost = instruction.MemIndexMode == Arm64MemoryIndexMode.PostIndex;

            if (!(beforeAccess ? isPre : isPost))
                return;

            if (IsReg31(instruction.MemBase))
                Add(address, OpCode.ShiftStack, Imm(instruction.MemOffset));
            else
                Add(address, OpCode.Add, Reg(instruction.MemBase), Reg(instruction.MemBase), Imm(instruction.MemOffset));
        }

        // the memory operand for the current instruction's access, offset by extraOffset (for the second reg of a pair)
        IOperand MemOperand(long extraOffset = 0)
        {
            var baseReg = instruction.MemBase;
            // writeback modes apply the offset to the base register itself, the access is at [base]
            var offset = (instruction.MemIndexMode == Arm64MemoryIndexMode.Offset ? instruction.MemOffset : 0) + extraOffset;

            if (baseReg == Arm64Register.INVALID)
                return new MemoryOperand(addend: offset);

            if (IsReg31(baseReg))
                return new StackOffset((int)offset);

            if (instruction.MemAddendReg != Arm64Register.INVALID)
                return new MemoryOperand(Reg(baseReg), Reg(instruction.MemAddendReg), offset, 1 << instruction.MemExtendOrShiftAmount);

            return new MemoryOperand(Reg(baseReg), addend: offset);
        }

        var flagN = new Register(null, "N");
        var flagZ = new Register(null, "Z");
        var flagC = new Register(null, "C");
        var flagV = new Register(null, "V");

        // models op0 - op1, which CMP and friends are defined in terms of
        void EmitCompareFlags(IOperand op0, IOperand op1)
        {
            var temp1 = new Register(null, "TEMP1");
            var temp2 = new Register(null, "TEMP2");
            var temp3 = new Register(null, "TEMP3");
            var temp4 = new Register(null, "TEMP4");

            Add(address, OpCode.CheckGreaterOrEqualUnsigned, flagC, op0, op1); // C is set when no unsigned borrow occurs
            Add(address, OpCode.Subtract, temp1, op0, op1);
            Add(address, OpCode.CheckLess, flagN, temp1, Imm(0));
            Add(address, OpCode.CheckEqual, flagZ, temp1, Imm(0));
            Add(address, OpCode.Xor, temp2, op0, op1);
            Add(address, OpCode.Xor, temp3, op0, temp1);
            Add(address, OpCode.And, temp4, temp2, temp3);
            Add(address, OpCode.CheckLess, flagV, temp4, Imm(0));
        }

        void EmitFloatCompareFlags(IOperand op0, IOperand op1)
        {
            var precision = new FloatingPointPrecision(instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31);
            if (op1 is Immediate { Value: 0 } or DoubleLiteral { Value: 0 })
                op1 = precision.IsDouble ? (IOperand)new DoubleLiteral(0) : new FloatLiteral(0);

            // FCMP/FCMPE set N/Z/C/V from the relation itself. Deriving flags from a-b is
            // incorrect for infinities, while the integer overflow formula is invalid for floats.
            Add(address, OpCode.CheckLess, flagN, op0, op1, precision);
            Add(address, OpCode.CheckEqual, flagZ, op0, op1, precision);
            Add(address, OpCode.CheckGreaterOrEqualUnsigned, flagC, op0, op1, precision);

            // ARM sets both C and V when either operand is NaN.
            var unordered0 = new Register(null, "TEMPFCMP_UNORDERED0");
            var unordered1 = new Register(null, "TEMPFCMP_UNORDERED1");
            Add(address, OpCode.CheckNotEqual, unordered0, op0, op0, precision);
            Add(address, OpCode.CheckNotEqual, unordered1, op1, op1, precision);
            Add(address, OpCode.Or, flagV, unordered0, unordered1);
        }

        void EmitResultFlags(IOperand result)
        {
            Add(address, OpCode.CheckLess, flagN, result, Imm(0));
            Add(address, OpCode.CheckEqual, flagZ, result, Imm(0));
            Add(address, OpCode.Move, flagC, Imm(0));
            Add(address, OpCode.Move, flagV, Imm(0));
        }

        // emits any instructions needed to evaluate the condition, returning an operand that is nonzero when it holds
        IOperand EmitCondition(Arm64ConditionCode condition)
        {
            var temp = new Register(null, "TEMPCOND");
            var temp2 = new Register(null, "TEMPCOND2");

            switch (condition)
            {
                case Arm64ConditionCode.EQ:
                    return flagZ;
                case Arm64ConditionCode.NE:
                    Add(address, OpCode.Not, temp, flagZ);
                    return temp;
                case Arm64ConditionCode.GE:
                    Add(address, OpCode.CheckEqual, temp, flagN, flagV);
                    return temp;
                case Arm64ConditionCode.LT:
                    Add(address, OpCode.CheckEqual, temp, flagN, flagV);
                    Add(address, OpCode.Not, temp, temp);
                    return temp;
                case Arm64ConditionCode.GT:
                    Add(address, OpCode.CheckEqual, temp, flagN, flagV);
                    Add(address, OpCode.Not, temp2, flagZ);
                    Add(address, OpCode.And, temp, temp, temp2);
                    return temp;
                case Arm64ConditionCode.LE:
                    Add(address, OpCode.CheckEqual, temp, flagN, flagV);
                    Add(address, OpCode.Not, temp, temp);
                    Add(address, OpCode.Or, temp, temp, flagZ);
                    return temp;
                case Arm64ConditionCode.CS: // unsigned >=
                    return flagC;
                case Arm64ConditionCode.CC: // unsigned <
                    Add(address, OpCode.Not, temp, flagC);
                    return temp;
                case Arm64ConditionCode.HI: // unsigned >
                    Add(address, OpCode.Not, temp, flagZ);
                    Add(address, OpCode.And, temp, flagC, temp);
                    return temp;
                case Arm64ConditionCode.LS: // unsigned <=
                    Add(address, OpCode.Not, temp, flagC);
                    Add(address, OpCode.Or, temp, temp, flagZ);
                    return temp;
                case Arm64ConditionCode.MI:
                    return flagN;
                case Arm64ConditionCode.PL:
                    Add(address, OpCode.Not, temp, flagN);
                    return temp;
                case Arm64ConditionCode.VS:
                    return flagV;
                case Arm64ConditionCode.VC:
                    Add(address, OpCode.Not, temp, flagV);
                    return temp;
                default: // AL/NV are both unconditional
                    return Imm(1);
            }
        }

        IOperand? EmitInverseCondition(Arm64ConditionCode condition)
        {
            if (condition is Arm64ConditionCode.AL or Arm64ConditionCode.NV)
                return Imm(0);

            var inverse = condition switch
            {
                Arm64ConditionCode.EQ => Arm64ConditionCode.NE,
                Arm64ConditionCode.NE => Arm64ConditionCode.EQ,
                Arm64ConditionCode.GE => Arm64ConditionCode.LT,
                Arm64ConditionCode.LT => Arm64ConditionCode.GE,
                Arm64ConditionCode.GT => Arm64ConditionCode.LE,
                Arm64ConditionCode.LE => Arm64ConditionCode.GT,
                Arm64ConditionCode.CS => Arm64ConditionCode.CC,
                Arm64ConditionCode.CC => Arm64ConditionCode.CS,
                Arm64ConditionCode.HI => Arm64ConditionCode.LS,
                Arm64ConditionCode.LS => Arm64ConditionCode.HI,
                Arm64ConditionCode.MI => Arm64ConditionCode.PL,
                Arm64ConditionCode.PL => Arm64ConditionCode.MI,
                Arm64ConditionCode.VS => Arm64ConditionCode.VC,
                Arm64ConditionCode.VC => Arm64ConditionCode.VS,
                _ => (Arm64ConditionCode?)null
            };

            return inverse is { } inverseCondition ? EmitCondition(inverseCondition) : null;
        }

        // dest = cond ? <emitTrueValue into dest> : <emitFalseValue into dest>
        void EmitConditionalAssign(Arm64ConditionCode condition, Action emitTrueValue, Action<ulong> emitFalseValue)
        {
            var inverse = new Register(null, "TEMPCSEL");
            Add(address, OpCode.Not, inverse, EmitCondition(condition));
            Add(address, OpCode.ConditionalJump, Imm(address + 1), inverse);
            emitTrueValue();
            Add(address, OpCode.Jump, Imm(address + 2));
            emitFalseValue(address + 1);
            Add(address + 2, OpCode.Nop);
        }

        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.MOV:
            case Arm64Mnemonic.MOVZ:
            case Arm64Mnemonic.SXTB:
            case Arm64Mnemonic.SXTH:
            case Arm64Mnemonic.SXTW:
            case Arm64Mnemonic.UXTB:
            case Arm64Mnemonic.UXTH:
                if (instruction.Op0Kind == Arm64OperandKind.Register && IsReg31(instruction.Op0Reg))
                {
                    Add(address, OpCode.Nop); // write to xzr, discard
                    break;
                }

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Arm64Mnemonic.FMOV:
                if (DiscardZeroRegisterDestination())
                    break;

                var bitcast = (instruction.Op0Reg, instruction.Op1Reg) switch
                {
                    (>= Arm64Register.S0 and <= Arm64Register.S31, >= Arm64Register.W0 and <= Arm64Register.W31)
                        => NumericConversionKind.I32ReinterpretFloat32,
                    (>= Arm64Register.D0 and <= Arm64Register.D31, >= Arm64Register.X0 and <= Arm64Register.X31)
                        => NumericConversionKind.I64ReinterpretFloat64,
                    (>= Arm64Register.W0 and <= Arm64Register.W31, >= Arm64Register.S0 and <= Arm64Register.S31)
                        => NumericConversionKind.Float32ReinterpretI32,
                    (>= Arm64Register.X0 and <= Arm64Register.X31, >= Arm64Register.D0 and <= Arm64Register.D31)
                        => NumericConversionKind.Float64ReinterpretI64,
                    _ => (NumericConversionKind?)null,
                };
                if (instruction.Op1Kind == Arm64OperandKind.Register && bitcast is { } bitcastKind)
                    Add(address, OpCode.Convert, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1),
                        new NumericConversion(bitcastKind));
                else
                {
                    var source = ConvertOperand(instruction, 1);
                    if (instruction.Op1Kind is Arm64OperandKind.Immediate or Arm64OperandKind.FloatingPointImmediate
                        && instruction.Op0Arrangement is Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.FourS or Arm64ArrangementSpecifier.TwoD
                        && context.AppContext.Binary.TryMapVirtualAddressToRaw(address, out var raw)
                        && raw >= 0 && raw <= context.AppContext.Binary.RawLength - 4)
                    {
                        var encoding = BinaryPrimitives.ReadUInt32LittleEndian(context.AppContext.Binary.GetRawBinaryContent().Slice((int)raw, 4));
                        var immediate = NewArm64Utils.DecodeVectorFloatImmediate(encoding, instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD);
                        source = instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD
                            ? new DoubleLiteral(immediate) : new FloatLiteral((float)immediate);
                    }
                    var move = Add(address, OpCode.Move, ConvertOperand(instruction, 0), source);
                    move.VectorElementCount = instruction.Op0Arrangement switch
                    {
                        Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.TwoD => 2,
                        Arm64ArrangementSpecifier.FourS => 4,
                        _ => null,
                    };
                }
                break;
            case Arm64Mnemonic.DUP when instruction.Op1Kind == Arm64OperandKind.Register
                && (instruction.Op0Arrangement is Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.FourS
                    && instruction.Op1Reg is >= Arm64Register.W0 and <= Arm64Register.W31
                    || instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD
                    && instruction.Op1Reg is >= Arm64Register.X0 and <= Arm64Register.X31):
            {
                var conversion = instruction.Op0Arrangement == Arm64ArrangementSpecifier.TwoD
                    ? NumericConversionKind.I64ReinterpretFloat64 : NumericConversionKind.I32ReinterpretFloat32;
                var duplicate = Add(address, OpCode.Convert, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1),
                    new NumericConversion(conversion));
                duplicate.VectorElementCount = instruction.Op0Arrangement == Arm64ArrangementSpecifier.FourS ? 4 : 2;
                break;
            }
            case Arm64Mnemonic.FCVT:
                if (DiscardZeroRegisterDestination())
                    break;

                if (TryGetFloatConversion(instruction, out var floatConversion))
                    Add(address, OpCode.Convert, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), floatConversion);
                else
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM64 FCVT form: {instruction}"));
                break;
            case Arm64Mnemonic.SCVTF:
            case Arm64Mnemonic.UCVTF:
                if (DiscardZeroRegisterDestination())
                    break;

                if (TryGetIntegerToFloatConversion(instruction, context, out var integerToFloatConversion, out var integerSourceRegister))
                    Add(address, OpCode.Convert, ConvertOperand(instruction, 0),
                        IsReg31(integerSourceRegister) ? Imm(0) : Reg(integerSourceRegister), integerToFloatConversion);
                else
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM64 {instruction.Mnemonic} form: {instruction}"));
                break;
            case Arm64Mnemonic.FCVTZS:
            case Arm64Mnemonic.FCVTZU:
            case Arm64Mnemonic.FCVTMS:
            case Arm64Mnemonic.FCVTMU:
            case Arm64Mnemonic.FCVTNS:
            case Arm64Mnemonic.FCVTNU:
            case Arm64Mnemonic.FCVTPS:
            case Arm64Mnemonic.FCVTPU:
            case Arm64Mnemonic.FCVTAS:
            case Arm64Mnemonic.FCVTAU:
                if (DiscardZeroRegisterDestination())
                    break;

                if (TryGetFloatToIntegerConversion(instruction, out var floatToIntegerConversion))
                    Add(address, OpCode.Convert, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), floatToIntegerConversion);
                else
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM64 {instruction.Mnemonic} form: {instruction}"));
                break;
            case Arm64Mnemonic.MOVI:
            case Arm64Mnemonic.MVNI when instruction.Op1Kind == Arm64OperandKind.Immediate:
                {
                    if (instruction.Op0Arrangement is Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.FourS
                        && instruction.Op1Kind == Arm64OperandKind.Immediate
                        && instruction.Op2Imm is >= 0 and < 32)
                    {
                        var laneCount = instruction.Op0Arrangement == Arm64ArrangementSpecifier.FourS ? 4 : 2;
                        var bits = unchecked((uint)instruction.Op1Imm) << (int)instruction.Op2Imm;
                        if (instruction.Mnemonic == Arm64Mnemonic.MVNI)
                            bits = ~bits;
                        var laneValue = Imm(unchecked((int)bits));
                        var destination = ConvertOperand(instruction, 0);
                        Add(address, OpCode.Move, destination, laneValue);
                        for (var lane = 0; lane < laneCount; lane++)
                            Add(address, OpCode.Move,
                                new Register(null, $"{NormalizeRegister(instruction.Op0Reg)}.S{lane}"), laneValue);
                        break;
                    }

                    var value = instruction.Mnemonic == Arm64Mnemonic.MVNI ? ~instruction.Op1Imm : instruction.Op1Imm;
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(value));
                    break;
                }
            case Arm64Mnemonic.MOVK:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    var destinationWidth = instruction.Op0Reg switch
                    {
                        >= Arm64Register.W0 and <= Arm64Register.W31 => 32,
                        >= Arm64Register.X0 and <= Arm64Register.X31 => 64,
                        _ => 0
                    };

                    if (destinationWidth == 0 || !TryGetMoveKeepShift(instruction, context, out var shift) || shift >= destinationWidth)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Could not determine valid MOVK destination width or shift."));
                        break;
                    }

                    var registerMask = destinationWidth == 32 ? uint.MaxValue : ulong.MaxValue;
                    var fieldMask = 0xFFFFUL << shift;
                    var preservedMask = registerMask & ~fieldMask;
                    var insertedBits = unchecked((ulong)instruction.Op1Imm) & fieldMask;
                    var destination = ConvertOperand(instruction, 0);

                    // MOVK replaces its selected 16-bit field and keeps every other destination bit.
                    // Masking with the W-register width also models its required zero-extension to X.
                    Add(address, OpCode.And, destination, destination, Imm(preservedMask));
                    Add(address, OpCode.Or, destination, destination, Imm(insertedBits));
                    break;
                }
            case Arm64Mnemonic.MOVN:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.Move, temp, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Not, temp, temp);
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), temp);
                    break;
                }
            case Arm64Mnemonic.MVN:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Not, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Arm64Mnemonic.ADR:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)).IsPcRelativeAddress = true;
                break;
            case Arm64Mnemonic.ADRP:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    var target = (long)(address & ~0xFFFUL) + instruction.Op1Imm;
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(target)).IsPcRelativeAddress = true;
                    break;
                }
            case Arm64Mnemonic.LDR:
            case Arm64Mnemonic.LDRB:
            case Arm64Mnemonic.LDRH:
            case Arm64Mnemonic.LDRSB:
            case Arm64Mnemonic.LDRSH:
            case Arm64Mnemonic.LDRSW:
            case Arm64Mnemonic.LDUR:
            case Arm64Mnemonic.LDURB:
            case Arm64Mnemonic.LDURH:
            case Arm64Mnemonic.LDURSB:
            case Arm64Mnemonic.LDURSH:
            case Arm64Mnemonic.LDURSW:
                {
                    EmitWriteback(beforeAccess: true);

                    var source = instruction.Op1Kind == Arm64OperandKind.ImmediatePcRelative
                        ? new MemoryOperand(addend: (long)address + instruction.Op1Imm)
                        : MemOperand();

                    if (instruction.Op0Kind == Arm64OperandKind.Register && IsReg31(instruction.Op0Reg))
                        Add(address, OpCode.Nop); // load to xzr = prefetch, discard
                    else
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), source).MemoryAccessSize = ScalarMemoryAccessSize(instruction);

                    EmitWriteback(beforeAccess: false);
                    break;
                }
            case Arm64Mnemonic.STR:
            case Arm64Mnemonic.STRB:
            case Arm64Mnemonic.STRH:
            case Arm64Mnemonic.STUR:
            case Arm64Mnemonic.STURB:
            case Arm64Mnemonic.STURH:
                EmitWriteback(beforeAccess: true);
                Add(address, OpCode.Move, MemOperand(), ConvertOperand(instruction, 0)).MemoryAccessSize = ScalarMemoryAccessSize(instruction);
                EmitWriteback(beforeAccess: false);
                break;
            case Arm64Mnemonic.LDP:
            case Arm64Mnemonic.LDPSW:
            case Arm64Mnemonic.STP:
                {
                    var pairSize = instruction.Op0Reg switch
                    {
                        >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
                        >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
                        >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
                        >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
                        _ => 8
                    };

                    EmitWriteback(beforeAccess: true);

                    if (instruction.Mnemonic == Arm64Mnemonic.STP)
                    {
                        var first = Add(address, OpCode.Move, MemOperand(), ConvertOperand(instruction, 0));
                        var second = Add(address, OpCode.Move, MemOperand(pairSize), ConvertOperand(instruction, 1));
                        if (instruction.Op0Reg is >= Arm64Register.V0 and <= Arm64Register.V31
                            or >= Arm64Register.S0 and <= Arm64Register.S31
                            or >= Arm64Register.D0 and <= Arm64Register.D31)
                            first.MemoryAccessSize = second.MemoryAccessSize = pairSize;
                        else
                        {
                            // STP XZR, XZR can clear several adjacent fields in one instruction.
                            if (IsReg31(instruction.Op0Reg))
                                first.MemoryAccessSize = pairSize;
                            if (IsReg31(instruction.Op1Reg))
                                second.MemoryAccessSize = pairSize;
                        }
                    }
                    else
                    {
                        // The two S/D destinations receive separate scalar loads. Keep
                        // their widths so offset zero resolves to the first nested field.
                        int? scalarSize = instruction.Op0Reg is >= Arm64Register.V0 and <= Arm64Register.V31
                            or >= Arm64Register.S0 and <= Arm64Register.S31
                            or >= Arm64Register.D0 and <= Arm64Register.D31 ? pairSize : null;
                        if (instruction.Op0Kind == Arm64OperandKind.Register && IsReg31(instruction.Op0Reg))
                            Add(address, OpCode.Nop); // discard the first loaded value into WZR/XZR
                        else
                            Add(address, OpCode.Move, ConvertOperand(instruction, 0), MemOperand()).MemoryAccessSize = scalarSize;

                        if (instruction.Op1Kind == Arm64OperandKind.Register && IsReg31(instruction.Op1Reg))
                            Add(address, OpCode.Nop); // discard the second loaded value into WZR/XZR
                        else
                            Add(address, OpCode.Move, ConvertOperand(instruction, 1), MemOperand(pairSize)).MemoryAccessSize = scalarSize;
                    }

                    EmitWriteback(beforeAccess: false);
                    break;
                }
            case Arm64Mnemonic.ADD:
            case Arm64Mnemonic.SUB:
            case Arm64Mnemonic.ADDS:
            case Arm64Mnemonic.SUBS:
                {
                    var isSubtract = instruction.Mnemonic is Arm64Mnemonic.SUB or Arm64Mnemonic.SUBS;
                    var setsFlags = instruction.Mnemonic is Arm64Mnemonic.ADDS or Arm64Mnemonic.SUBS;

                    // stack pointer adjustment
                    if (IsReg31(instruction.Op0Reg) && IsReg31(instruction.Op1Reg) && instruction.Op2Kind == Arm64OperandKind.Immediate && !setsFlags)
                    {
                        Add(address, OpCode.ShiftStack, Imm(isSubtract ? -instruction.Op2Imm : instruction.Op2Imm));
                        break;
                    }

                    // in the immediate forms register 31 is sp, so this takes the address of a stack slot
                    if (IsReg31(instruction.Op1Reg) && instruction.Op2Kind == Arm64OperandKind.Immediate && !IsReg31(instruction.Op0Reg))
                    {
                        var slot = new StackOffset((int)(isSubtract ? -instruction.Op2Imm : instruction.Op2Imm));
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), new AddressOf(slot));
                        break;
                    }

                    var src1 = ConvertOperand(instruction, 1);
                    var src2 = ConvertOperand(instruction, 2);
                    if (instruction.Op2Kind == Arm64OperandKind.Register)
                    {
                        var width = instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31 ? 32 : 64;
                        if (width == 64 && instruction.FinalOpExtendType is Arm64ExtendType.SXTW or Arm64ExtendType.UXTW)
                        {
                            var extended = new Register(null, "TEMP_ADD_EXTEND");
                            var kind = instruction.FinalOpExtendType == Arm64ExtendType.SXTW
                                ? NumericConversionKind.I64ExtendI32Signed
                                : NumericConversionKind.I64ExtendI32Unsigned;
                            Add(address, OpCode.Convert, extended, src2, new NumericConversion(kind));
                            src2 = extended;
                        }

                        var shift = instruction.FinalOpShiftType switch
                        {
                            Arm64ShiftType.LSL => OpCode.ShiftLeft,
                            Arm64ShiftType.LSR => OpCode.ShiftRightUnsigned,
                            Arm64ShiftType.ASR => OpCode.ShiftRight,
                            _ when instruction.FinalOpExtendType is Arm64ExtendType.SXTW or Arm64ExtendType.UXTW
                                or Arm64ExtendType.SXTX or Arm64ExtendType.UXTX => OpCode.ShiftLeft,
                            _ => (OpCode?)null,
                        };

                        if (shift is { } shiftOpCode && instruction.Op3Kind == Arm64OperandKind.Immediate && instruction.Op3Imm != 0)
                        {
                            var shifted = new Register(null, "TEMP_ADD_SHIFT");
                            Add(address, shiftOpCode, shifted, src2, Imm(instruction.Op3Imm)).OperandWidth = width;
                            src2 = shifted;
                        }
                    }
                    // a discarded result means this is only about the flags
                    var dest = IsReg31(instruction.Op0Reg) ? new Register(null, "TEMP") : ConvertOperand(instruction, 0);

                    Add(address, isSubtract ? OpCode.Subtract : OpCode.Add, dest, src1, src2);

                    if (setsFlags)
                    {
                        if (isSubtract)
                            EmitCompareFlags(src1, src2);
                        else
                            EmitResultFlags(dest);
                    }

                    break;
                }
            case Arm64Mnemonic.CMP:
                EmitCompareFlags(ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Arm64Mnemonic.FCMP:
            case Arm64Mnemonic.FCMPE:
                EmitFloatCompareFlags(ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Arm64Mnemonic.CMN:
                // cmp against the negated operand
                if (instruction.Op1Kind == Arm64OperandKind.Immediate)
                {
                    EmitCompareFlags(ConvertOperand(instruction, 0), Imm(-instruction.Op1Imm));
                }
                else
                {
                    var negated = new Register(null, "TEMP");
                    Add(address, OpCode.Negate, negated, ConvertOperand(instruction, 1));
                    EmitCompareFlags(ConvertOperand(instruction, 0), negated);
                }

                break;
            case Arm64Mnemonic.TST:
                {
                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.And, temp, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                    EmitResultFlags(temp);
                    break;
                }
            case Arm64Mnemonic.ORR when instruction.Op0Arrangement != Arm64ArrangementSpecifier.None
                                       && instruction.Op1Kind == Arm64OperandKind.Immediate:
                if (TryGetPackedOrrImmediate(instruction, out var orrMask))
                {
                    // Op2, when present, is a lane shift. The input is the old destination.
                    var vector = ConvertOperand(instruction, 0);
                    Add(address, OpCode.Or, vector, vector, Imm(orrMask)).OperandWidth = 64;
                }
                else
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM64 ORR immediate form: {instruction}"));
                break;
            case Arm64Mnemonic.AND:
            case Arm64Mnemonic.ANDS:
            case Arm64Mnemonic.ORR:
            case Arm64Mnemonic.EOR:
                {
                    var opCode = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.ORR => OpCode.Or,
                        Arm64Mnemonic.EOR => OpCode.Xor,
                        _ => OpCode.And
                    };

                    var dest = IsReg31(instruction.Op0Reg) ? new Register(null, "TEMP") : ConvertOperand(instruction, 0);
                    Add(address, opCode, dest, ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));

                    if (instruction.Mnemonic == Arm64Mnemonic.ANDS)
                        EmitResultFlags(dest);

                    break;
                }
            case Arm64Mnemonic.BIC:
            case Arm64Mnemonic.BICS:
            case Arm64Mnemonic.ORN:
            case Arm64Mnemonic.EON:
                {
                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.Not, temp, ConvertOperand(instruction, 2));
                    var opCode = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.ORN => OpCode.Or,
                        Arm64Mnemonic.EON => OpCode.Xor,
                        _ => OpCode.And
                    };
                    var dest = IsReg31(instruction.Op0Reg) ? new Register(null, "TEMP") : ConvertOperand(instruction, 0);
                    Add(address, opCode, dest, ConvertOperand(instruction, 1), temp);

                    if (instruction.Mnemonic == Arm64Mnemonic.BICS)
                        EmitResultFlags(dest);

                    break;
                }
            case Arm64Mnemonic.CCMP:
            case Arm64Mnemonic.CCMN:
            case Arm64Mnemonic.FCCMP:
            case Arm64Mnemonic.FCCMPE:
                {
                    // if cond holds the flags come from the comparison, else straight from the nzcv immediate
                    var inverse = new Register(null, "TEMPCSEL");
                    Add(address, OpCode.Not, inverse, EmitCondition(instruction.FinalOpConditionCode));
                    Add(address, OpCode.ConditionalJump, Imm(address + 1), inverse);

                    var op1 = ConvertOperand(instruction, 1);
                    if (instruction.Mnemonic == Arm64Mnemonic.CCMN)
                    {
                        var negated = new Register(null, "TEMP");
                        Add(address, OpCode.Negate, negated, op1);
                        op1 = negated;
                    }

                    EmitCompareFlags(ConvertOperand(instruction, 0), op1);
                    Add(address, OpCode.Jump, Imm(address + 2));

                    var nzcv = instruction.Op2Imm;
                    Add(address + 1, OpCode.Move, flagN, Imm((nzcv >> 3) & 1));
                    Add(address + 1, OpCode.Move, flagZ, Imm((nzcv >> 2) & 1));
                    Add(address + 1, OpCode.Move, flagC, Imm((nzcv >> 1) & 1));
                    Add(address + 1, OpCode.Move, flagV, Imm(nzcv & 1));

                    Add(address + 2, OpCode.Nop);
                    break;
                }
            case Arm64Mnemonic.NEG:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Arm64Mnemonic.FNEG:
                if (DiscardZeroRegisterDestination())
                    break;

                AddFloatNegate();
                break;
            case Arm64Mnemonic.FABS:
            case Arm64Mnemonic.FSQRT:
            case Arm64Mnemonic.FMIN:
            case Arm64Mnemonic.FMAX:
            case Arm64Mnemonic.FMINNM:
            case Arm64Mnemonic.FMAXNM:
            case Arm64Mnemonic.FRINTM:
            case Arm64Mnemonic.FRINTP:
            case Arm64Mnemonic.FABD:
                {
                    var floatTypeName = instruction.Op0Reg switch
                    {
                        >= Arm64Register.S0 and <= Arm64Register.S31 => "System.Single",
                        >= Arm64Register.D0 and <= Arm64Register.D31 => "System.Double",
                        _ => null // Vector and half-precision forms need separate recovery.
                    };

                    var methodName = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.FABS => "Abs",
                        Arm64Mnemonic.FSQRT => "Sqrt",
                        Arm64Mnemonic.FMIN => "Min",
                        Arm64Mnemonic.FMAX => "Max",
                        Arm64Mnemonic.FMINNM => "Min",
                        Arm64Mnemonic.FMAXNM => "Max",
                        Arm64Mnemonic.FRINTM => "Floor",
                        Arm64Mnemonic.FRINTP => "Ceiling",
                        Arm64Mnemonic.FABD => "Abs",
                        _ => throw new InvalidOperationException($"Unexpected floating-point instruction {instruction.Mnemonic}")
                    };
                    var isBinaryMinMax = instruction.Mnemonic is Arm64Mnemonic.FMIN or Arm64Mnemonic.FMAX
                        or Arm64Mnemonic.FMINNM or Arm64Mnemonic.FMAXNM;
                    var parameterCount = isBinaryMinMax ? 2 : 1;
                    var isNumberVariant = instruction.Mnemonic is Arm64Mnemonic.FMINNM or Arm64Mnemonic.FMAXNM;
                    var mathMethod = floatTypeName == null
                        ? null
                        : isNumberVariant
                            ? FindFloatMathMethod(context.AppContext, floatTypeName, methodName + "Number", parameterCount)
                            : FindFloatMathMethod(context.AppContext, floatTypeName, methodName, parameterCount);

                    if (mathMethod != null)
                    {
                        if (instruction.Mnemonic == Arm64Mnemonic.FABD)
                        {
                            var difference = new Register(null, "TEMP_FABD");
                            Add(address, OpCode.Subtract, difference, ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                            Add(address, OpCode.Call, mathMethod, ConvertOperand(instruction, 0), difference);
                        }
                        else if (parameterCount == 1)
                            Add(address, OpCode.Call, mathMethod, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1))
                                .IsLiftedFloatAbs = instruction.Mnemonic == Arm64Mnemonic.FABS;
                        else
                            Add(address, OpCode.Call, mathMethod, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2))
                                .IsLiftedFloatMin = instruction.Mnemonic == Arm64Mnemonic.FMIN;
                    }
                    else
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} not yet implemented."));

                    break;
                }
            case Arm64Mnemonic.LSL:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.ShiftLeft, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.LSR:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.ShiftRightUnsigned, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.ASR:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.ShiftRight, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.UBFX:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    // dest = (src >> lsb) & ((1 << width) - 1)
                    var dest = ConvertOperand(instruction, 0);
                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var width = (int)instruction.Op3Imm;
                    var mask = width >= regSize ? -1L : (1L << width) - 1;
                    if (regSize == 32)
                        mask = unchecked((int)mask);

                    Add(address, OpCode.ShiftRightUnsigned, dest, ConvertOperand(instruction, 1), Imm(instruction.Op2Imm));
                    if (width < regSize)
                        Add(address, OpCode.And, dest, dest, Imm(mask));
                    break;
                }
            case Arm64Mnemonic.SBFX:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    // Sign-extend the selected field by shifting it to the sign bit and back.
                    var dest = ConvertOperand(instruction, 0);
                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var leftShift = regSize - (int)instruction.Op2Imm - (int)instruction.Op3Imm;
                    var rightShift = regSize - (int)instruction.Op3Imm;
                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.ShiftLeft, temp, ConvertOperand(instruction, 1), Imm(leftShift));
                    Add(address, OpCode.ShiftRight, dest, temp, Imm(rightShift));
                    break;
                }
            case Arm64Mnemonic.UBFIZ:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    // dest = (src & ((1 << width) - 1)) << shift
                    var dest = ConvertOperand(instruction, 0);
                    var temp = new Register(null, "TEMP");
                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var width = (int)instruction.Op3Imm;
                    var mask = width >= regSize ? -1L : (1L << width) - 1;
                    if (regSize == 32)
                        mask = unchecked((int)mask);

                    if (width < regSize)
                        Add(address, OpCode.And, temp, ConvertOperand(instruction, 1), Imm(mask));
                    else
                        Add(address, OpCode.Move, temp, ConvertOperand(instruction, 1));
                    Add(address, OpCode.ShiftLeft, dest, temp, Imm(instruction.Op2Imm));
                    break;
                }
            case Arm64Mnemonic.SBFIZ:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    // Sign-extend the low width bits, then move them into the selected destination field.
                    var dest = ConvertOperand(instruction, 0);
                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var shift = (int)instruction.Op2Imm;
                    var width = (int)instruction.Op3Imm;
                    var extensionShift = regSize - width;
                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.ShiftLeft, temp, ConvertOperand(instruction, 1), Imm(extensionShift));
                    Add(address, OpCode.ShiftRight, temp, temp, Imm(extensionShift));
                    Add(address, OpCode.ShiftLeft, dest, temp, Imm(shift));
                    break;
                }
            case Arm64Mnemonic.BFI:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var lsb = (int)instruction.Op2Imm;
                    var width = (int)instruction.Op3Imm;
                    if (lsb < 0 || width <= 0 || width > regSize - lsb)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} has invalid bitfield range."));
                        break;
                    }

                    var registerMask = regSize == 32 ? uint.MaxValue : ulong.MaxValue;
                    var fieldMask = width == regSize ? registerMask : (1UL << width) - 1;
                    var insertedMask = fieldMask << lsb;
                    var preservedMask = registerMask & ~insertedMask;
                    var destination = ConvertOperand(instruction, 0);
                    var insertedValue = new Register(null, "TEMP_BFI");

                    // Snapshot the source before touching the destination; BFI permits both operands
                    // to name the same register.
                    Add(address, OpCode.Move, insertedValue, ConvertOperand(instruction, 1));
                    if (width < regSize)
                        Add(address, OpCode.And, insertedValue, insertedValue, BitMask(fieldMask));
                    if (lsb > 0)
                        Add(address, OpCode.ShiftLeft, insertedValue, insertedValue, Imm(lsb));

                    if (preservedMask == 0)
                        Add(address, OpCode.Move, destination, insertedValue);
                    else
                    {
                        Add(address, OpCode.And, destination, destination, BitMask(preservedMask));
                        Add(address, OpCode.Or, destination, destination, insertedValue);
                    }

                    Immediate BitMask(ulong value) => regSize == 32
                        ? Imm(unchecked((int)value))
                        : Imm(unchecked((long)value));

                    break;
                }
            case Arm64Mnemonic.BFXIL:
                {
                    if (IsReg31(instruction.Op0Reg))
                    {
                        Add(address, OpCode.Nop);
                        break;
                    }

                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var lsb = (int)instruction.Op2Imm;
                    var width = (int)instruction.Op3Imm;
                    if (lsb < 0 || width <= 0 || width > regSize - lsb)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} has invalid bitfield range."));
                        break;
                    }

                    var registerMask = regSize == 32 ? uint.MaxValue : ulong.MaxValue;
                    var fieldMask = width == regSize ? registerMask : (1UL << width) - 1;
                    var preservedMask = registerMask & ~fieldMask;
                    var destination = ConvertOperand(instruction, 0);
                    var insertedValue = new Register(null, "TEMP_BFXIL");

                    if (lsb > 0)
                        Add(address, OpCode.ShiftRightUnsigned, insertedValue, ConvertOperand(instruction, 1), Imm(lsb));
                    else
                        Add(address, OpCode.Move, insertedValue, ConvertOperand(instruction, 1));

                    if (width < regSize)
                        Add(address, OpCode.And, insertedValue, insertedValue, BitMask(fieldMask));

                    if (preservedMask == 0)
                        Add(address, OpCode.Move, destination, insertedValue);
                    else
                    {
                        Add(address, OpCode.And, destination, destination, BitMask(preservedMask));
                        Add(address, OpCode.Or, destination, destination, insertedValue);
                    }

                    Immediate BitMask(ulong value) => regSize == 32
                        ? Imm(unchecked((int)value))
                        : Imm(unchecked((long)value));

                    break;
                }
            case Arm64Mnemonic.MUL:
            case Arm64Mnemonic.FMUL:
            case Arm64Mnemonic.SMULL:
            case Arm64Mnemonic.UMULL:
                if (DiscardZeroRegisterDestination())
                    break;

                AddFloatArithmetic(OpCode.Multiply);
                break;
            case Arm64Mnemonic.MNEG:
            case Arm64Mnemonic.SMNEGL:
            case Arm64Mnemonic.UMNEGL:
            case Arm64Mnemonic.FNMUL:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    var dest = ConvertOperand(instruction, 0);
                    AddFloatArithmetic(OpCode.Multiply);
                    Add(address, OpCode.Negate, dest, dest);
                    break;
                }
            case Arm64Mnemonic.MADD:
            case Arm64Mnemonic.MSUB:
            case Arm64Mnemonic.SMADDL:
            case Arm64Mnemonic.UMADDL:
            case Arm64Mnemonic.SMSUBL:
            case Arm64Mnemonic.UMSUBL:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    // rd = ra +/- rn * rm
                    var isAdd = instruction.Mnemonic is Arm64Mnemonic.MADD or Arm64Mnemonic.SMADDL or Arm64Mnemonic.UMADDL;
                    var temp = new Register(null, "TEMP");
                    Add(address, OpCode.Multiply, temp, ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                    Add(address, isAdd ? OpCode.Add : OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 3), temp);
                    break;
                }
            case Arm64Mnemonic.EXTR:
                {
                    if (DiscardZeroRegisterDestination())
                        break;

                    // dest = (rn:rm) >> lsb
                    var dest = ConvertOperand(instruction, 0);
                    var lsb = instruction.Op3Imm;

                    if (lsb == 0)
                    {
                        Add(address, OpCode.Move, dest, ConvertOperand(instruction, 2));
                        break;
                    }

                    var regSize = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31 ? 64 : 32;
                    var temp = new Register(null, "TEMP");
                    var temp2 = new Register(null, "TEMP2");
                    Add(address, OpCode.ShiftRightUnsigned, temp, ConvertOperand(instruction, 2), Imm(lsb));
                    Add(address, OpCode.ShiftLeft, temp2, ConvertOperand(instruction, 1), Imm(regSize - lsb));
                    Add(address, OpCode.Or, dest, temp, temp2);
                    break;
                }
            case Arm64Mnemonic.SDIV:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Divide, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.FDIV:
                if (DiscardZeroRegisterDestination())
                    break;

                AddFloatArithmetic(OpCode.Divide);
                break;
            case Arm64Mnemonic.UDIV:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.DivideUnsigned, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.FADD:
                AddFloatArithmetic(OpCode.Add);
                break;
            case Arm64Mnemonic.FSUB:
                AddFloatArithmetic(OpCode.Subtract);
                break;
            case Arm64Mnemonic.BL:
                AddCallAt(instruction.BranchTarget);
                break;
            case Arm64Mnemonic.BLR:
                {
                    var call = Add(address, OpCode.IndirectCall, ConvertOperand(instruction, 0), new Register(null, "X0") /* return value */);
                    call.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, address));
                    break;
                }
            case Arm64Mnemonic.BR:
                {
                    // tail call or jump table, either way it leaves the method
                    var jump = Add(address, OpCode.IndirectJump, ConvertOperand(instruction, 0), new Register(null, "X0") /* return value */);
                    jump.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, address));
                    break;
                }
            case Arm64Mnemonic.B:
            case Arm64Mnemonic.BC:
                {
                    var target = instruction.Mnemonic == Arm64Mnemonic.B ? instruction.BranchTarget : instruction.Op0PcRelImm;

                    if (instruction.MnemonicConditionCode != Arm64ConditionCode.NONE
                        && instruction.MnemonicConditionCode != Arm64ConditionCode.AL
                        && instruction.MnemonicConditionCode != Arm64ConditionCode.NV)
                    {
                        Add(address, OpCode.ConditionalJump, Imm(target), EmitCondition(instruction.MnemonicConditionCode));
                        break;
                    }

                    if (target < context.UnderlyingPointer || target >= context.UnderlyingPointer + (ulong)context.RawBytes.Length)
                    {
                        // unconditional branch out of the method is a tail call
                        AddReturn(AddCallAt(target));
                    }
                    else
                    {
                        Add(address, OpCode.Jump, Imm(target));
                    }

                    break;
                }
            case Arm64Mnemonic.RET:
            case Arm64Mnemonic.RETAA:
            case Arm64Mnemonic.RETAB:
                AddReturn(readFloatLanes: true);
                break;
            case Arm64Mnemonic.CBZ:
            case Arm64Mnemonic.CBNZ:
                {
                    var target = (ulong)((long)address + instruction.Op1Imm);
                    var temp = new Register(null, "TEMP");

                    Add(address, OpCode.CheckEqual, temp, ConvertOperand(instruction, 0), Imm(0));

                    if (instruction.Mnemonic == Arm64Mnemonic.CBNZ)
                        Add(address, OpCode.Not, temp, temp);

                    Add(address, OpCode.ConditionalJump, Imm(target), temp);
                    break;
                }
            case Arm64Mnemonic.TBZ:
            case Arm64Mnemonic.TBNZ:
                {
                    var target = (ulong)((long)address + instruction.Op2Imm);
                    var temp = new Register(null, "TEMP");

                    if (instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31
                        && instruction.Op1Imm == 31)
                    {
                        Add(address, instruction.Mnemonic == Arm64Mnemonic.TBNZ ? OpCode.CheckLess : OpCode.CheckGreaterOrEqual,
                            temp, ConvertOperand(instruction, 0), Imm(0));
                        Add(address, OpCode.ConditionalJump, Imm(target), temp);
                        break;
                    }

                    Add(address, OpCode.And, temp, ConvertOperand(instruction, 0), Imm(1L << (int)instruction.Op1Imm));
                    Add(address, OpCode.CheckEqual, temp, temp, Imm(0));

                    if (instruction.Mnemonic == Arm64Mnemonic.TBNZ)
                        Add(address, OpCode.Not, temp, temp);

                    Add(address, OpCode.ConditionalJump, Imm(target), temp);
                    break;
                }
            case Arm64Mnemonic.CSEL:
            case Arm64Mnemonic.FCSEL:
                if (DiscardZeroRegisterDestination())
                    break;

                var selectedTrue = ConvertOperand(instruction, 1);
                var selectedFalse = ConvertOperand(instruction, 2);
                if (instruction.Mnemonic == Arm64Mnemonic.CSEL
                    && selectedTrue is Immediate { Value: 1 }
                    && selectedFalse is Immediate { Value: 0 })
                {
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), EmitCondition(instruction.FinalOpConditionCode));
                    break;
                }

                if (instruction.Mnemonic == Arm64Mnemonic.CSEL
                    && selectedTrue is Immediate { Value: 0 }
                    && selectedFalse is Immediate { Value: 1 }
                    && EmitInverseCondition(instruction.FinalOpConditionCode) is { } inverseSelection)
                {
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), inverseSelection);
                    break;
                }

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)),
                    falseAddress => Add(falseAddress, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 2)));
                break;
            case Arm64Mnemonic.CSINC:
                if (DiscardZeroRegisterDestination())
                    break;

                // CSINC Rd, ZR, ZR, cond is CSET's inverse-condition form: 0 when cond
                // holds and 1 otherwise. Keep it as a boolean value instead of building a
                // branch/phi pair that later decompiles as `(cond ? 1 : 0)`.
                if (ConvertOperand(instruction, 1) is Immediate { Value: 0 }
                    && ConvertOperand(instruction, 2) is Immediate { Value: 0 }
                    && EmitInverseCondition(instruction.FinalOpConditionCode) is { } inverseIncrement)
                {
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), inverseIncrement);
                    break;
                }

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)),
                    falseAddress => Add(falseAddress, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 2), Imm(1)));
                break;
            case Arm64Mnemonic.CSINV:
                if (DiscardZeroRegisterDestination())
                    break;

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)),
                    falseAddress => Add(falseAddress, OpCode.Not, ConvertOperand(instruction, 0), ConvertOperand(instruction, 2)));
                break;
            case Arm64Mnemonic.CSNEG:
                if (DiscardZeroRegisterDestination())
                    break;

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)),
                    falseAddress => Add(falseAddress, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 2)));
                break;
            case Arm64Mnemonic.CSET:
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), EmitCondition(instruction.FinalOpConditionCode));
                break;
            case Arm64Mnemonic.CSETM:
                if (DiscardZeroRegisterDestination())
                    break;

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(-1L)),
                    falseAddress => Add(falseAddress, OpCode.Move, ConvertOperand(instruction, 0), Imm(0)));
                break;
            case Arm64Mnemonic.CINC:
                if (DiscardZeroRegisterDestination())
                    break;

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), Imm(1)),
                    falseAddress => Add(falseAddress, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)));
                break;
            case Arm64Mnemonic.CNEG:
                if (DiscardZeroRegisterDestination())
                    break;

                EmitConditionalAssign(instruction.FinalOpConditionCode,
                    () => Add(address, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)),
                    falseAddress => Add(falseAddress, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)));
                break;
            case Arm64Mnemonic.NOP:
            // pointer auth and branch target hints are meaningless for analysis but might be jump targets
            case Arm64Mnemonic.BTI:
            case Arm64Mnemonic.BTI_C:
            case Arm64Mnemonic.BTI_J:
            case Arm64Mnemonic.BTI_JC:
            case Arm64Mnemonic.PACIASP:
            case Arm64Mnemonic.PACIBSP:
            case Arm64Mnemonic.AUTIASP:
            case Arm64Mnemonic.AUTIBSP:
                Add(address, OpCode.Nop);
                break;
            case Arm64Mnemonic.BRK:
            case Arm64Mnemonic.UDF:
                Add(address, OpCode.Interrupt);
                break;
            case Arm64Mnemonic.MRS:
                // system register read (thread pointer etc), value is opaque to analysis
                if (DiscardZeroRegisterDestination())
                    break;

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), new Register(null, "SYSREG"));
                break;
            default:
                Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} not yet implemented."));
                break;
        }
    }

    private static bool TryGetPackedOrrImmediate(Arm64Instruction instruction, out ulong mask)
    {
        mask = 0;
        // FourS and EightH modify 128 bits and don't fit in one ISIL integer.
        var laneWidth = instruction.Op0Arrangement switch
        {
            Arm64ArrangementSpecifier.TwoS => 32,
            Arm64ArrangementSpecifier.FourH => 16,
            _ => 0
        };
        var shift = instruction.Op2Kind == Arm64OperandKind.None ? 0 : instruction.Op2Imm;
        if (laneWidth == 0 || instruction.Op1Imm is < 0 or > byte.MaxValue
            || instruction.Op2Kind is not (Arm64OperandKind.None or Arm64OperandKind.Immediate)
            || shift < 0 || shift >= laneWidth || shift % 8 != 0)
            return false;

        var lane = (ulong)instruction.Op1Imm << (int)shift;
        for (var offset = 0; offset < 64; offset += laneWidth)
            mask |= lane << offset;
        return true;
    }

    private static int? ScalarMemoryAccessSize(Arm64Instruction instruction) => instruction.Mnemonic switch
    {
        Arm64Mnemonic.LDRB or Arm64Mnemonic.LDRSB or Arm64Mnemonic.LDURB or Arm64Mnemonic.LDURSB
            or Arm64Mnemonic.STRB or Arm64Mnemonic.STURB => 1,
        Arm64Mnemonic.LDRH or Arm64Mnemonic.LDRSH or Arm64Mnemonic.LDURH or Arm64Mnemonic.LDURSH
            or Arm64Mnemonic.STRH or Arm64Mnemonic.STURH => 2,
        Arm64Mnemonic.LDRSW or Arm64Mnemonic.LDURSW => 4,
        _ => instruction.Op0Reg switch
        {
            >= Arm64Register.B0 and <= Arm64Register.B31 => 1,
            >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
            >= Arm64Register.W0 and <= Arm64Register.W31 or >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
            >= Arm64Register.X0 and <= Arm64Register.X31 or >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
            >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
            _ => null,
        },
    };

    private static MethodAnalysisContext? FindFloatMathMethod(ApplicationAnalysisContext context, string typeName,
        string methodName, int parameterCount)
    {
        var candidateTypes = typeName == "System.Single"
            ? new[] { "UnityEngine.Mathf", "System.MathF", "System.Math" }
            : new[] { "System.Math" };
        // Core library assemblies are reachable through the resolved primitive types even when
        // they are not included in ApplicationAnalysisContext.Assemblies. Wasm's equivalent
        // resolver uses this route; include it here so ARM64 math intrinsics do not degrade into
        // NotImplemented markers just because the caller's assembly list omits the core library.
        var candidateAssemblies = new[]
            {
                context.SystemTypes.SystemSingleType.DeclaringAssembly,
                context.SystemTypes.SystemDoubleType.DeclaringAssembly
            }
            .Concat(context.Assemblies)
            .Distinct();

        foreach (var candidateTypeName in candidateTypes)
        {
            foreach (var assembly in candidateAssemblies)
            {
                var type = assembly.GetTypeByFullName(candidateTypeName);
                var method = type?.Methods.FirstOrDefault(candidate =>
                    (candidate.Name == methodName
                     || methodName == "Ceiling" && candidateTypeName == "UnityEngine.Mathf" && candidate.Name == "Ceil")
                    && candidate.IsStatic
                    && candidate.ReturnType.FullName == typeName
                    && candidate.Parameters.Count == parameterCount
                    && candidate.Parameters.All(parameter => parameter.ParameterType.FullName == typeName));

                if (method != null)
                    return method;
            }
        }

        return null;
    }

    private static bool TryGetMoveKeepShift(Arm64Instruction instruction, MethodAnalysisContext context, out int shift)
    {
        shift = 0;
        if (instruction.Address < context.UnderlyingPointer)
            return false;

        var instructionOffset = instruction.Address - context.UnderlyingPointer;
        var rawBytes = context.RawBytes.AsSpan();
        if (rawBytes.Length < sizeof(uint) || instructionOffset > (ulong)(rawBytes.Length - sizeof(uint)))
            return false;

        var encodedInstruction = BinaryPrimitives.ReadUInt32LittleEndian(rawBytes.Slice((int)instructionOffset, sizeof(uint)));
        shift = (int)((encodedInstruction >> 21) & 0b11) * 16;
        return true;
    }

    private static bool TryGetFloatConversion(Arm64Instruction instruction, out NumericConversion conversion)
    {
        conversion = default;

        if (instruction.Op0Kind != Arm64OperandKind.Register || instruction.Op1Kind != Arm64OperandKind.Register)
            return false;

        var kind = (instruction.Op1Reg, instruction.Op0Reg) switch
        {
            (>= Arm64Register.S0 and <= Arm64Register.S31, >= Arm64Register.D0 and <= Arm64Register.D31)
                => NumericConversionKind.Float32ToFloat64,
            (>= Arm64Register.D0 and <= Arm64Register.D31, >= Arm64Register.S0 and <= Arm64Register.S31)
                => NumericConversionKind.Float64ToFloat32,
            _ => (NumericConversionKind?)null,
        };

        if (kind is not { } conversionKind)
            return false;

        conversion = new NumericConversion(conversionKind);
        return true;
    }

    private static bool TryGetIntegerToFloatConversion(Arm64Instruction instruction, MethodAnalysisContext context,
        out NumericConversion conversion, out Arm64Register integerSourceRegister)
    {
        conversion = default;
        integerSourceRegister = Arm64Register.INVALID;

        if (instruction.Op0Kind != Arm64OperandKind.Register || instruction.Op1Kind != Arm64OperandKind.Register)
            return false;

        if (!TryGetIntegerToFloatSourceRegister(instruction, context, out integerSourceRegister))
            return false;

        var sourceIs64Bit = integerSourceRegister is >= Arm64Register.X0 and <= Arm64Register.X31
            or >= Arm64Register.D0 and <= Arm64Register.D31;

        var fixedPointScale = 0;
        if (instruction.Op2Kind != default)
        {
            var maximumScale = sourceIs64Bit ? 64 : 32;
            if (instruction.Op2Kind != Arm64OperandKind.Immediate || instruction.Op2Imm < 0 || instruction.Op2Imm > maximumScale)
                return false;

            fixedPointScale = (int)instruction.Op2Imm;
        }

        var isSigned = instruction.Mnemonic == Arm64Mnemonic.SCVTF;
        var kind = (integerSourceRegister, instruction.Op0Reg) switch
        {
            (>= Arm64Register.W0 and <= Arm64Register.W31, >= Arm64Register.S0 and <= Arm64Register.S31)
                => isSigned ? NumericConversionKind.SignedInt32ToFloat32 : NumericConversionKind.UnsignedInt32ToFloat32,
            (>= Arm64Register.W0 and <= Arm64Register.W31, >= Arm64Register.D0 and <= Arm64Register.D31)
                => isSigned ? NumericConversionKind.SignedInt32ToFloat64 : NumericConversionKind.UnsignedInt32ToFloat64,
            (>= Arm64Register.X0 and <= Arm64Register.X31, >= Arm64Register.S0 and <= Arm64Register.S31)
                => isSigned ? NumericConversionKind.SignedInt64ToFloat32 : NumericConversionKind.UnsignedInt64ToFloat32,
            (>= Arm64Register.X0 and <= Arm64Register.X31, >= Arm64Register.D0 and <= Arm64Register.D31)
                => isSigned ? NumericConversionKind.SignedInt64ToFloat64 : NumericConversionKind.UnsignedInt64ToFloat64,
            (>= Arm64Register.S0 and <= Arm64Register.S31, >= Arm64Register.S0 and <= Arm64Register.S31)
                => isSigned ? NumericConversionKind.SignedInt32ToFloat32 : NumericConversionKind.UnsignedInt32ToFloat32,
            (>= Arm64Register.D0 and <= Arm64Register.D31, >= Arm64Register.D0 and <= Arm64Register.D31)
                => isSigned ? NumericConversionKind.SignedInt64ToFloat64 : NumericConversionKind.UnsignedInt64ToFloat64,
            _ => (NumericConversionKind?)null,
        };

        if (kind is not { } conversionKind)
            return false;

        conversion = new NumericConversion(conversionKind, FixedPointScale: fixedPointScale);
        return true;
    }

    private static bool TryGetIntegerToFloatSourceRegister(Arm64Instruction instruction, MethodAnalysisContext context,
        out Arm64Register sourceRegister)
    {
        sourceRegister = instruction.Op1Reg;
        if (sourceRegister is >= Arm64Register.W0 and <= Arm64Register.W31
            or >= Arm64Register.X0 and <= Arm64Register.X31)
            return true;

        // Disarm can print the same register names for scalar SIMD and GPR-source forms.
        var sourceIsSingleAlias = sourceRegister is >= Arm64Register.S0 and <= Arm64Register.S31;
        var sourceIsDoubleAlias = sourceRegister is >= Arm64Register.D0 and <= Arm64Register.D31;
        if ((!sourceIsSingleAlias && !sourceIsDoubleAlias)
            || !TryGetEncodedInstruction(instruction, context, out var encoded))
            return false;

        var encodedDestination = (int)(encoded & 0x1F);
        var encodedSource = (int)((encoded >> 5) & 0x1F);
        var decodedDestination = RegisterIndex(instruction.Op0Reg);
        var decodedSourceAlias = RegisterIndex(instruction.Op1Reg);
        if (decodedDestination != encodedDestination || decodedSourceAlias != encodedSource)
            return false;

        // Scalar Advanced SIMD SCVTF/UCVTF use the signed/unsigned 0x5E/0x7E encoding.
        // Mask U, the S/D size bit, and the two register fields.
        if ((encoded & 0xDFBF_FC00u) == 0x5E21_D800u)
            return true;

        // Keep fixed-point scalar SIMD separate from the GPR form as well.
        if ((encoded & 0x5F00_0000u) != 0x1E00_0000u)
            return false;

        var is64BitSource = (encoded & 0x8000_0000u) != 0;
        sourceRegister = (Arm64Register)((int)(is64BitSource ? Arm64Register.X0 : Arm64Register.W0) + encodedSource);
        return true;
    }

    private static int RegisterIndex(Arm64Register register) => register switch
    {
        >= Arm64Register.W0 and <= Arm64Register.W31 => (int)register - (int)Arm64Register.W0,
        >= Arm64Register.X0 and <= Arm64Register.X31 => (int)register - (int)Arm64Register.X0,
        >= Arm64Register.S0 and <= Arm64Register.S31 => (int)register - (int)Arm64Register.S0,
        >= Arm64Register.D0 and <= Arm64Register.D31 => (int)register - (int)Arm64Register.D0,
        _ => -1,
    };

    private static bool TryGetEncodedInstruction(Arm64Instruction instruction, MethodAnalysisContext context, out uint encoded)
    {
        encoded = 0;
        if (instruction.Address < context.UnderlyingPointer)
            return false;

        var instructionOffset = instruction.Address - context.UnderlyingPointer;
        var rawBytes = context.RawBytes.AsSpan();
        if (rawBytes.Length < sizeof(uint) || instructionOffset > (ulong)(rawBytes.Length - sizeof(uint)))
            return false;

        encoded = BinaryPrimitives.ReadUInt32LittleEndian(rawBytes.Slice((int)instructionOffset, sizeof(uint)));
        return true;
    }

    private static bool TryGetFloatToIntegerConversion(Arm64Instruction instruction, out NumericConversion conversion)
    {
        conversion = default;

        if (instruction.Op0Kind != Arm64OperandKind.Register || instruction.Op1Kind != Arm64OperandKind.Register)
            return false;

        var isSigned = instruction.Mnemonic is Arm64Mnemonic.FCVTZS or Arm64Mnemonic.FCVTMS
            or Arm64Mnemonic.FCVTNS or Arm64Mnemonic.FCVTPS or Arm64Mnemonic.FCVTAS;
        var rounding = instruction.Mnemonic switch
        {
            Arm64Mnemonic.FCVTZS or Arm64Mnemonic.FCVTZU => NumericConversionRounding.TowardZero,
            Arm64Mnemonic.FCVTMS or Arm64Mnemonic.FCVTMU => NumericConversionRounding.TowardNegativeInfinity,
            Arm64Mnemonic.FCVTNS or Arm64Mnemonic.FCVTNU => NumericConversionRounding.NearestTiesToEven,
            Arm64Mnemonic.FCVTPS or Arm64Mnemonic.FCVTPU => NumericConversionRounding.TowardPositiveInfinity,
            Arm64Mnemonic.FCVTAS or Arm64Mnemonic.FCVTAU => NumericConversionRounding.NearestTiesAwayFromZero,
            _ => (NumericConversionRounding?)null,
        };

        if (rounding is not { } conversionRounding)
            return false;

        var kind = (instruction.Op1Reg, instruction.Op0Reg) switch
        {
            (>= Arm64Register.S0 and <= Arm64Register.S31, >= Arm64Register.W0 and <= Arm64Register.W31)
                => isSigned ? NumericConversionKind.Float32ToInt32Signed : NumericConversionKind.Float32ToInt32Unsigned,
            (>= Arm64Register.D0 and <= Arm64Register.D31, >= Arm64Register.W0 and <= Arm64Register.W31)
                => isSigned ? NumericConversionKind.Float64ToInt32Signed : NumericConversionKind.Float64ToInt32Unsigned,
            (>= Arm64Register.S0 and <= Arm64Register.S31, >= Arm64Register.X0 and <= Arm64Register.X31)
                => isSigned ? NumericConversionKind.Float32ToInt64Signed : NumericConversionKind.Float32ToInt64Unsigned,
            (>= Arm64Register.D0 and <= Arm64Register.D31, >= Arm64Register.X0 and <= Arm64Register.X31)
                => isSigned ? NumericConversionKind.Float64ToInt64Signed : NumericConversionKind.Float64ToInt64Unsigned,
            _ => (NumericConversionKind?)null,
        };

        if (kind is not { } conversionKind)
            return false;

        conversion = new NumericConversion(conversionKind, conversionRounding, Saturating: true);
        return true;
    }

    private IOperand ConvertOperand(Arm64Instruction instruction, int operand)
    {
        var kind = operand switch
        {
            0 => instruction.Op0Kind,
            1 => instruction.Op1Kind,
            2 => instruction.Op2Kind,
            3 => instruction.Op3Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
        };

        if (kind is Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative)
        {
            var imm = operand switch
            {
                0 => instruction.Op0Imm,
                1 => instruction.Op1Imm,
                2 => instruction.Op2Imm,
                3 => instruction.Op3Imm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            if (kind == Arm64OperandKind.ImmediatePcRelative)
                imm += (long)instruction.Address;

            return new Immediate(imm);
        }

        if (kind == Arm64OperandKind.FloatingPointImmediate)
        {
            var imm = operand switch
            {
                0 => instruction.Op0FpImm,
                1 => instruction.Op1FpImm,
                2 => instruction.Op2FpImm,
                3 => instruction.Op3FpImm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            return instruction.Op0Reg is >= Arm64Register.S0 and <= Arm64Register.S31
                || instruction.Op0Arrangement is Arm64ArrangementSpecifier.TwoS or Arm64ArrangementSpecifier.FourS
                ? (IOperand)new FloatLiteral((float)imm)
                : new DoubleLiteral(imm);
        }

        if (kind == Arm64OperandKind.Register)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            // reads of integer register 31 are the zero register (sp-based reads are special-cased by callers)
            if (IsReg31(reg))
                return new Immediate(0);

            return Reg(reg);
        }

        if (kind == Arm64OperandKind.Memory)
        {
            var reg = instruction.MemBase;
            var offset = instruction.MemOffset;

            if (reg == Arm64Register.INVALID)
                //Offset only
                return new MemoryOperand(addend: offset);

            if (IsReg31(reg))
                return new StackOffset((int)offset);

            return new MemoryOperand(Reg(reg), addend: offset);
        }

        if (kind == Arm64OperandKind.VectorRegisterElement)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var vectorElement = operand switch
            {
                0 => instruction.Op0VectorElement,
                1 => instruction.Op1VectorElement,
                2 => instruction.Op2VectorElement,
                3 => instruction.Op3VectorElement,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var width = vectorElement.Width switch
            {
                Arm64VectorElementWidth.B => "B",
                Arm64VectorElementWidth.H => "H",
                Arm64VectorElementWidth.S => "S",
                Arm64VectorElementWidth.D => "D",
                _ => throw new ArgumentOutOfRangeException(nameof(vectorElement.Width), $"Unknown vector element width {vectorElement.Width}")
            };

            var name = $"{NormalizeRegister(reg)}.{width}{vectorElement.Index}";
            return new Register(null, name);
        }

        return new StringLiteral($"<UNIMPLEMENTED OPERAND TYPE {kind}>");
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new NewArm64KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context) => context.RawBytes.Length <= 0 ? "" : string.Join("\n", Disassembler.Disassemble(context.RawBytes.AsSpan(), context.UnderlyingPointer, new Disassembler.Options(true, true, false)).ToList());
}
