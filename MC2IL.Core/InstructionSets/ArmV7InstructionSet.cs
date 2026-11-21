using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Api;
using MC2IL.Core.Il2CppApiFunctions;
using MC2IL.Core.ISIL;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils;

namespace MC2IL.Core.InstructionSets;

public class ArmV7InstructionSet : MC2ILInstructionSet
{
    private static readonly Arm32CallingConventionResolver CallingConventions = new();

    public override BaseCallingConventionResolver CallingConventionResolver => CallingConventions;

    public override ulong GetPointerForMethod(MethodAnalysisContext context)
        => ArmV7Utils.NormalizeCodeAddress(context.UnderlyingPointer);

    public override ulong NormalizeCodeAddress(ulong address) => ArmV7Utils.NormalizeCodeAddress(address);

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var slice = ArmV7Utils.TryGetMethodBodyBytesFast(context.AppContext, context.UnderlyingPointer, isAttributeGenerator);
        if (slice.Length > 0)
            return slice;

        var instructions = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);

        return new BinarySlice(instructions.SelectMany(i => i.Bytes).ToArray());
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var nativeInstructions = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);
        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();

        Instruction Emit(ulong address, OpCode opCode, params IOperand[] operands)
        {
            addresses.Add(address);
            var emitted = new Instruction(instructions.Count, opCode, [.. operands]);
            if (opCode is not (OpCode.Call or OpCode.CallVoid or OpCode.Return or OpCode.Jump or OpCode.ConditionalJump
                or OpCode.Invalid or OpCode.NotImplemented or OpCode.Nop or OpCode.ShiftStack))
                emitted.OperandWidth = operands.OfType<Register>().Any(register => register.Name.StartsWith("D")) ? 64 : 32;
            instructions.Add(emitted);
            return emitted;
        }

        Register Reg(ArmRegister register) => new(null, register.ToString().ToUpperInvariant());
        Immediate Imm(long value) => new(value);
        IOperand ToOperand(ArmInstruction native, ArmOperand operand)
        {
            switch (operand.Type)
            {
                case ArmOperandType.Register:
                    return Reg(operand.Register);
                case ArmOperandType.Immediate:
                case ArmOperandType.CImmediate:
                case ArmOperandType.PImmediate:
                    return Imm(operand.Immediate);
                case ArmOperandType.FloatingPoint:
                    return native.Operand.IndexOf(".f32", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? new FloatLiteral((float)operand.FloatingPoint)
                        : new DoubleLiteral(operand.FloatingPoint);
                case ArmOperandType.Memory:
                {
                    var memory = operand.Memory;
                    var baseName = memory.Base.ToString().ToUpperInvariant();
                    var hasIndex = memory.Index.ToString().ToUpperInvariant() is not ("INVALID" or "INVALID_REG");
                    if (baseName == "SP" && !hasIndex)
                        return new StackOffset(memory.Displacement);
                    if (baseName == "PC")
                    {
                        var pc = (ulong)native.Address + ((native.DisassembleMode == ArmDisassembleMode.Thumb) ? 4UL : 8UL);
                        if (native.DisassembleMode == ArmDisassembleMode.Thumb)
                            pc &= ~3UL;
                        return new MemoryOperand(addend: (long)pc + memory.Displacement);
                    }

                    IOperand? baseRegister = baseName is "INVALID" or "INVALID_REG" ? null : new Register(null, baseName);
                    IOperand? indexRegister = !hasIndex
                        ? null : Reg(memory.Index);
                    return new MemoryOperand(baseRegister, indexRegister,
                        operand.IsSubtracted ? -memory.Displacement : memory.Displacement,
                        memory.Scale > 1 ? memory.Scale : 0);
                }
                default:
                    return new StringLiteral($"unsupported ARM operand {operand.Type}: {native.Mnemonic} {native.Operand}");
            }
        }

        var flagN = new Register(null, "ARM_N");
        var flagZ = new Register(null, "ARM_Z");
        var flagC = new Register(null, "ARM_C");
        var flagV = new Register(null, "ARM_V");

        void EmitSubFlags(ulong address, IOperand left, IOperand right)
        {
            var result = new Register(null, "ARM_CMP_RESULT");
            var xorLeftRight = new Register(null, "ARM_CMP_XOR0");
            var xorLeftResult = new Register(null, "ARM_CMP_XOR1");
            var overflowBits = new Register(null, "ARM_CMP_OV_BITS");
            Emit(address, OpCode.CheckGreaterOrEqualUnsigned, flagC, left, right);
            Emit(address, OpCode.Subtract, result, left, right);
            Emit(address, OpCode.CheckLess, flagN, result, Imm(0));
            Emit(address, OpCode.CheckEqual, flagZ, result, Imm(0));
            Emit(address, OpCode.Xor, xorLeftRight, left, right);
            Emit(address, OpCode.Xor, xorLeftResult, left, result);
            Emit(address, OpCode.And, overflowBits, xorLeftRight, xorLeftResult);
            Emit(address, OpCode.CheckLess, flagV, overflowBits, Imm(0));
        }

        void EmitAddFlags(ulong address, IOperand left, IOperand right)
        {
            var result = new Register(null, "ARM_CMN_RESULT");
            var xorLeftRight = new Register(null, "ARM_CMN_XOR0");
            var xorLeftResult = new Register(null, "ARM_CMN_XOR1");
            var invertedXor = new Register(null, "ARM_CMN_INV_XOR");
            var overflowBits = new Register(null, "ARM_CMN_OV_BITS");
            Emit(address, OpCode.Add, result, left, right);
            Emit(address, OpCode.CheckLessUnsigned, flagC, result, left);
            Emit(address, OpCode.CheckLess, flagN, result, Imm(0));
            Emit(address, OpCode.CheckEqual, flagZ, result, Imm(0));
            Emit(address, OpCode.Xor, xorLeftRight, left, right);
            Emit(address, OpCode.Not, invertedXor, xorLeftRight);
            Emit(address, OpCode.Xor, xorLeftResult, left, result);
            Emit(address, OpCode.And, overflowBits, invertedXor, xorLeftResult);
            Emit(address, OpCode.CheckLess, flagV, overflowBits, Imm(0));
        }

        static List<string> RegisterList(ArmInstruction native)
        {
            var open = native.Operand.IndexOf('{');
            var close = native.Operand.LastIndexOf('}');
            if (open < 0 || close <= open)
                return [];
            return native.Operand[(open + 1)..close]
                .Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim().ToUpperInvariant())
                .ToList();
        }

        IOperand? Condition(ulong address, string condition)
        {
            var invertedOverflow = new Register(null, "ARM_N_XOR_V");
            var inverseZero = new Register(null, "ARM_NOT_Z");
            var inverseCarry = new Register(null, "ARM_NOT_C");
            switch (condition.ToUpperInvariant())
            {
                case "INVALID":
                case "AL":
                    return null;
                case "EQ": return flagZ;
                case "NE": Emit(address, OpCode.Not, new Register(null, "ARM_NE"), flagZ); return new Register(null, "ARM_NE");
                case "GE":
                    Emit(address, OpCode.Xor, invertedOverflow, flagN, flagV);
                    Emit(address, OpCode.Not, new Register(null, "ARM_GE"), invertedOverflow);
                    return new Register(null, "ARM_GE");
                case "LT": Emit(address, OpCode.Xor, invertedOverflow, flagN, flagV); return invertedOverflow;
                case "GT":
                    Emit(address, OpCode.Xor, invertedOverflow, flagN, flagV);
                    Emit(address, OpCode.Not, inverseZero, flagZ);
                    Emit(address, OpCode.Not, new Register(null, "ARM_EQ_SIGN"), invertedOverflow);
                    Emit(address, OpCode.And, new Register(null, "ARM_GT"), inverseZero, new Register(null, "ARM_EQ_SIGN"));
                    return new Register(null, "ARM_GT");
                case "LE":
                    Emit(address, OpCode.Xor, invertedOverflow, flagN, flagV);
                    Emit(address, OpCode.Or, new Register(null, "ARM_LE"), flagZ, invertedOverflow);
                    return new Register(null, "ARM_LE");
                case "CS": case "HS": return flagC;
                case "CC": case "LO": Emit(address, OpCode.Not, inverseCarry, flagC); return inverseCarry;
                case "HI":
                    Emit(address, OpCode.Not, inverseZero, flagZ);
                    Emit(address, OpCode.And, new Register(null, "ARM_HI"), flagC, inverseZero);
                    return new Register(null, "ARM_HI");
                case "LS":
                    Emit(address, OpCode.Not, inverseCarry, flagC);
                    Emit(address, OpCode.Or, new Register(null, "ARM_LS"), inverseCarry, flagZ);
                    return new Register(null, "ARM_LS");
                case "MI": return flagN;
                case "PL": Emit(address, OpCode.Not, new Register(null, "ARM_PL"), flagN); return new Register(null, "ARM_PL");
                case "VS": return flagV;
                case "VC": Emit(address, OpCode.Not, new Register(null, "ARM_VC"), flagV); return new Register(null, "ARM_VC");
                default: return null;
            }
        }

        Register? EmitCall(ulong address, ulong target, bool isIndirect, IOperand? indirectTarget = null)
        {
            if (!isIndirect && context.AppContext.MethodsByAddress.TryGetValue(target, out var candidates) && candidates.Count > 0)
            {
                var callee = candidates.OrderByDescending(candidate => candidate.Parameters.Count + (candidate.IsStatic ? 0 : 1)).First();
                if (CallingConventions.ReturnsViaHiddenBuffer(callee))
                {
                    Emit(address, OpCode.NotImplemented, new StringLiteral("ARM32 call returning a struct through hidden result storage"));
                    return null;
                }

                var args = CallingConventions.ResolveForManaged(callee);
                var result = CallingConventions.ReturnRegister(callee);
                var call = callee.IsVoid
                    ? Emit(address, OpCode.CallVoid, Imm(unchecked((long)target)))
                    : Emit(address, OpCode.Call, Imm(unchecked((long)target)), result);
                call.AddOperands(args);
                return callee.IsVoid ? null : result;
            }

            var rawArguments = CallingConventions.ResolveForUnmanaged(context.AppContext, target);
            if (isIndirect)
            {
                var call = Emit(address, OpCode.IndirectCall, indirectTarget ?? new Register(null, "R0"), new Register(null, "R0"));
                call.AddOperands(rawArguments);
            }
            else
            {
                var call = Emit(address, OpCode.Call, Imm(unchecked((long)target)), new Register(null, "R0"));
                call.AddOperands(rawArguments);
            }
            return new Register(null, "R0");
        }

        for (var nativeIndex = 0; nativeIndex < nativeInstructions.Count; nativeIndex++)
        {
            var native = nativeInstructions[nativeIndex];
            var address = unchecked((ulong)native.Address);
            if (native.IsSkippedData || !native.HasDetails)
            {
                Emit(address, OpCode.NotImplemented, new StringLiteral($"ARM decoder skipped bytes at 0x{address:X}"));
                continue;
            }
            var operands = native.Details.Operands;
            var (mnemonic, conditionCode) = NormalizeMnemonic(native);
            var isBranch = mnemonic is "b" or "b.w" or "bx" or "bl" or "blx" or "cbz" or "cbnz";

            if (isBranch)
            {
                if ((mnemonic is "cbz" or "cbnz") && operands.Length >= 2)
                {
                    var condition = new Register(null, "ARM_CBZ");
                    Emit(address, mnemonic == "cbz" ? OpCode.CheckEqual : OpCode.CheckNotEqual,
                        condition, ToOperand(native, operands[0]), Imm(0));
                    Emit(address, OpCode.ConditionalJump,
                        Imm(unchecked((long)(unchecked((ulong)(uint)operands[1].Immediate) & ~1UL))), condition);
                    continue;
                }

                if (mnemonic is "bl" or "blx")
                {
                    if (operands.Length > 0 && operands[0].Type == ArmOperandType.Register)
                        EmitCall(address, 0, isIndirect: true, Reg(operands[0].Register));
                    else if (operands.Length > 0)
                        EmitCall(address, unchecked((ulong)(uint)operands[0].Immediate) & ~1UL, isIndirect: false);
                    continue;
                }

                if (mnemonic == "bx" && operands.Length > 0
                    && operands[0].Type == ArmOperandType.Register
                    && operands[0].Register.ToString().Equals("lr", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (context.IsVoid)
                        Emit(address, OpCode.Return);
                    else
                        Emit(address, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
                    continue;
                }

                if (mnemonic == "bx" && operands.Length > 0 && operands[0].Type == ArmOperandType.Register)
                {
                    Emit(address, OpCode.IndirectJump, Reg(operands[0].Register));
                    continue;
                }

                if (operands.Length > 0 && operands[0].Type == ArmOperandType.Immediate)
                {
                    var target = unchecked((ulong)(uint)operands[0].Immediate) & ~1UL;
                    var condition = Condition(address, conditionCode);
                    if (condition == null && (conditionCode is "Invalid" or "AL"))
                    {
                        if (target == context.AppContext.InstructionSet.NormalizeCodeAddress(context.UnderlyingPointer))
                            Emit(address, OpCode.Jump, Imm(unchecked((long)target)));
                        else if (context.AppContext.MethodsByAddress.ContainsKey(target))
                        {
                            EmitCall(address, target, isIndirect: false);
                            if (context.IsVoid)
                                Emit(address, OpCode.Return);
                            else
                                Emit(address, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
                        }
                        else
                            Emit(address, OpCode.Jump, Imm(unchecked((long)target)));
                    }
                    else if (condition != null)
                        Emit(address, OpCode.ConditionalJump, Imm(unchecked((long)target)), condition);
                    else
                        Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM branch condition {conditionCode}"));
                    continue;
                }

                Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM branch: {native.Mnemonic} {native.Operand}"));
                continue;
            }

            if (mnemonic is "bx" && operands.Length > 0
                && operands[0].Type == ArmOperandType.Register
                && operands[0].Register.ToString().Equals("lr", System.StringComparison.OrdinalIgnoreCase))
            {
                if (context.IsVoid) Emit(address, OpCode.Return);
                else Emit(address, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
                continue;
            }

            if (conditionCode is not ("Invalid" or "AL") && nativeIndex + 1 < nativeInstructions.Count)
            {
                var inverted = conditionCode switch
                {
                    "EQ" => "NE", "NE" => "EQ", "GE" => "LT", "LT" => "GE", "GT" => "LE", "LE" => "GT",
                    "CS" or "HS" => "CC", "CC" or "LO" => "CS", "HI" => "LS", "LS" => "HI",
                    "MI" => "PL", "PL" => "MI", "VS" => "VC", "VC" => "VS", _ => "Invalid"
                };
                var skipCondition = Condition(address, inverted);
                if (skipCondition != null)
                    Emit(address, OpCode.ConditionalJump, Imm(nativeInstructions[nativeIndex + 1].Address), skipCondition);
            }

            if (mnemonic is "nop" or "it" or "ite" or "itt" or "dmb" or "dsb" or "isb")
            {
                Emit(address, OpCode.Nop);
                continue;
            }

            if ((mnemonic is "cmp" or "cmn") && operands.Length >= 2)
            {
                var left = ToOperand(native, operands[0]);
                var right = ToOperand(native, operands[1]);
                if (mnemonic == "cmp") EmitSubFlags(address, left, right);
                else EmitAddFlags(address, left, right);
                continue;
            }

            if ((mnemonic is "tst" or "teq") && operands.Length >= 2)
            {
                var result = new Register(null, "ARM_TEST_RESULT");
                Emit(address, mnemonic == "tst" ? OpCode.And : OpCode.Xor, result,
                    ToOperand(native, operands[0]), ToOperand(native, operands[1]));
                Emit(address, OpCode.CheckLess, flagN, result, Imm(0));
                Emit(address, OpCode.CheckEqual, flagZ, result, Imm(0));
                continue;
            }

            if ((mnemonic is "add" or "adds" or "sub" or "subs" or "rsb" or "mul" or "and" or "ands" or "orr" or "orrs" or "eor" or "eors" or "bic")
                && operands.Length is 2 or 3)
            {
                var destination = ToOperand(native, operands[0]);
                var left = operands.Length == 2 ? destination : ToOperand(native, operands[1]);
                var right = ToOperand(native, operands[^1]);
                if (destination is Register { Name: "SP" } && left is Register { Name: "SP" }
                    && right is Immediate stackAmount && (mnemonic is "add" or "adds" or "sub" or "subs"))
                {
                    var positive = mnemonic is "add" or "adds";
                    Emit(address, OpCode.ShiftStack, Imm(positive ? stackAmount.Value : -stackAmount.Value));
                    continue;
                }

                var opcode = mnemonic switch
                {
                    "add" or "adds" => OpCode.Add,
                    "sub" or "subs" => OpCode.Subtract,
                    "rsb" => OpCode.Subtract,
                    "mul" => OpCode.Multiply,
                    "and" or "ands" => OpCode.And,
                    "orr" or "orrs" => OpCode.Or,
                    "eor" or "eors" => OpCode.Xor,
                    "bic" => OpCode.And,
                    _ => OpCode.NotImplemented
                };
                if (mnemonic == "rsb")
                    Emit(address, opcode, destination, right, left);
                else if (mnemonic == "bic")
                {
                    var inverted = new Register(null, "ARM_BIC_MASK");
                    Emit(address, OpCode.Not, inverted, right);
                    Emit(address, opcode, destination, left, inverted);
                }
                else
                    Emit(address, opcode, destination, left, right);

                if (mnemonic == "adds") EmitAddFlags(address, left, right);
                else if (mnemonic == "subs") EmitSubFlags(address, left, right);
                else if (mnemonic is "ands" or "orrs" or "eors")
                {
                    Emit(address, OpCode.CheckLess, flagN, destination, Imm(0));
                    Emit(address, OpCode.CheckEqual, flagZ, destination, Imm(0));
                }
                continue;
            }

            if (mnemonic is "push" or "pop" or "vpush" or "vpop" or "stmdb" or "ldmia")
            {
                var registers = RegisterList(native);
                if (registers.Count == 0)
                {
                    Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM register-list instruction: {native.Mnemonic} {native.Operand}"));
                    continue;
                }

                var isLoad = mnemonic is "pop" or "vpop" or "ldmia";
                var isStackOperation = mnemonic is "push" or "pop" or "vpush" or "vpop"
                    || operands.Any(operand => operand.Type == ArmOperandType.Register
                        && operand.Register.ToString().Equals("sp", System.StringComparison.OrdinalIgnoreCase));
                if (!isStackOperation)
                {
                    Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported non-stack ARM register-list operation: {native.Mnemonic} {native.Operand}"));
                    continue;
                }
                var wordSize = mnemonic.StartsWith("v") && registers.Any(register => register.StartsWith("D")) ? 8 : 4;
                var totalSize = registers.Count * wordSize;
                var shiftsStack = true;

                if (!isLoad && shiftsStack)
                    Emit(address, OpCode.ShiftStack, Imm(-totalSize));

                var hasPc = registers.Contains("PC");
                for (var registerIndex = 0; registerIndex < registers.Count; registerIndex++)
                {
                    var register = new Register(null, registers[registerIndex]);
                    var slot = new StackOffset(registerIndex * wordSize);
                    if (isLoad && registers[registerIndex] == "PC")
                        continue;
                    Emit(address, OpCode.Move,
                        isLoad ? register : slot,
                        isLoad ? slot : register).MemoryAccessSize = wordSize;
                }

                if (isLoad && shiftsStack)
                    Emit(address, OpCode.ShiftStack, Imm(totalSize));

                if (hasPc)
                {
                    if (context.IsVoid) Emit(address, OpCode.Return);
                    else Emit(address, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
                }
                continue;
            }

            if ((mnemonic is "mov" or "movs" or "movw" or "movt" or "mvn" or "vmov") && operands.Length >= 2)
            {
                var destination = ToOperand(native, operands[0]);
                var source = ToOperand(native, operands[1]);
                if (destination is Register { Name: "PC" } && source is Register { Name: "LR" })
                {
                    if (context.IsVoid) Emit(address, OpCode.Return);
                    else Emit(address, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
                    continue;
                }
                if (mnemonic == "mvn")
                    Emit(address, OpCode.Not, destination, source);
                else if (mnemonic == "movt" && destination is Register destinationRegister)
                {
                    var lowHalf = new Register(null, "ARM_MOVT_LOW");
                    var highHalf = new Register(null, "ARM_MOVT_HIGH");
                    Emit(address, OpCode.And, lowHalf, destinationRegister, Imm(0xFFFF));
                    Emit(address, OpCode.ShiftLeft, highHalf, source, Imm(16));
                    Emit(address, OpCode.Or, destinationRegister, lowHalf, highHalf);
                }
                else
                    Emit(address, OpCode.Move, destination, source);
                if (mnemonic == "movs")
                {
                    Emit(address, OpCode.CheckLess, flagN, destination, Imm(0));
                    Emit(address, OpCode.CheckEqual, flagZ, destination, Imm(0));
                }
                continue;
            }

            if ((mnemonic is "lsl" or "lsls" or "lsr" or "lsrs" or "asr" or "asrs" or "ror") && operands.Length >= 2)
            {
                var opcode = mnemonic.StartsWith("lsl") ? OpCode.ShiftLeft
                    : mnemonic.StartsWith("lsr") ? OpCode.ShiftRightUnsigned
                    : mnemonic.StartsWith("asr") ? OpCode.ShiftRight : OpCode.ShiftRight;
                var destination = ToOperand(native, operands[0]);
                var source = operands.Length == 2 ? destination : ToOperand(native, operands[1]);
                var amount = ToOperand(native, operands[^1]);
                Emit(address, opcode, destination, source, amount);
                continue;
            }

            if (mnemonic == "adr" && operands.Length >= 2)
            {
                Emit(address, OpCode.Move, ToOperand(native, operands[0]), ToOperand(native, operands[1]));
                continue;
            }

            if ((mnemonic.StartsWith("ldr") || mnemonic.StartsWith("str") || mnemonic.StartsWith("vldr") || mnemonic.StartsWith("vstr")) && operands.Length >= 2)
            {
                var isLoad = mnemonic.StartsWith("ldr") || mnemonic.StartsWith("vldr");
                var data = ToOperand(native, operands[0]);
                var memory = ToOperand(native, operands[1]);
                if (isLoad && data is Register { Name: "PC" })
                {
                    Emit(address, OpCode.IndirectJump, memory);
                    continue;
                }
                var emitted = isLoad
                    ? Emit(address, OpCode.Move, data, memory)
                    : Emit(address, OpCode.Move, memory, data);
                emitted.MemoryAccessSize = mnemonic.EndsWith("b") ? 1 : mnemonic.EndsWith("h") ? 2
                    : data is Register { Name: var regName } && regName.StartsWith("D") ? 8 : 4;
                continue;
            }

            if ((mnemonic is "vadd" or "vsub" or "vmul" or "vdiv") && operands.Length >= 3)
            {
                var opcode = mnemonic switch
                {
                    "vadd" => OpCode.Add,
                    "vsub" => OpCode.Subtract,
                    "vmul" => OpCode.Multiply,
                    _ => OpCode.Divide
                };
                var precision = new FloatingPointPrecision(operands[0].Register.ToString().StartsWith("d", System.StringComparison.OrdinalIgnoreCase));
                Emit(address, opcode, ToOperand(native, operands[0]), ToOperand(native, operands[1]),
                    ToOperand(native, operands[2]), precision);
                continue;
            }

            if ((mnemonic is "vcmp" or "vcmpe") && operands.Length >= 2)
            {
                var precision = new FloatingPointPrecision(operands[0].Register.ToString().StartsWith("d", System.StringComparison.OrdinalIgnoreCase));
                var left = ToOperand(native, operands[0]);
                var right = ToOperand(native, operands[1]);
                Emit(address, OpCode.CheckLess, flagN, left, right, precision);
                Emit(address, OpCode.CheckEqual, flagZ, left, right, precision);
                Emit(address, OpCode.CheckGreaterOrEqual, flagC, left, right, precision);
                // ISIL does not yet carry the VFP unordered flag. Clear it rather than accidentally
                // reusing an integer overflow value from an earlier instruction.
                Emit(address, OpCode.Move, flagV, Imm(0));
                continue;
            }

            if (mnemonic == "vmrs")
            {
                Emit(address, OpCode.Nop);
                continue;
            }

            if (mnemonic is "vcvt" or "vcvtr")
            {
                var rawMnemonic = native.Mnemonic.ToLowerInvariant();
                var kind = rawMnemonic switch
                {
                    var value when value.IndexOf("f64.f32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float32ToFloat64,
                    var value when value.IndexOf("f32.f64", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float64ToFloat32,
                    var value when value.IndexOf("s32.f32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float32ToInt32Truncate,
                    var value when value.IndexOf("u32.f32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float32ToInt32UnsignedTruncate,
                    var value when value.IndexOf("s32.f64", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float64ToInt32Truncate,
                    var value when value.IndexOf("u32.f64", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.Float64ToInt32UnsignedTruncate,
                    var value when value.IndexOf("f32.s32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.SignedInt32ToFloat32,
                    var value when value.IndexOf("f32.u32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.UnsignedInt32ToFloat32,
                    var value when value.IndexOf("f64.s32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.SignedInt32ToFloat64,
                    var value when value.IndexOf("f64.u32", System.StringComparison.Ordinal) >= 0 => NumericConversionKind.UnsignedInt32ToFloat64,
                    _ => (NumericConversionKind?)null
                };
                if (kind is { } conversionKind && operands.Length >= 2)
                {
                    var rounding = mnemonic == "vcvtr"
                        ? NumericConversionRounding.NearestTiesToEven
                        : NumericConversionRounding.TowardZero;
                    Emit(address, OpCode.Convert, ToOperand(native, operands[0]), ToOperand(native, operands[1]), new NumericConversion(conversionKind, rounding));
                }
                else
                    Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported VFP conversion: {native.Mnemonic} {native.Operand}"));
                continue;
            }

            if ((mnemonic is "vneg" or "vabs" or "vsqrt") && operands.Length >= 2)
            {
                var precision = new FloatingPointPrecision(operands[0].Register.ToString().StartsWith("d", System.StringComparison.OrdinalIgnoreCase));
                if (mnemonic is "vabs" or "vsqrt")
                {
                    var methodName = mnemonic == "vabs" ? "Abs" : "Sqrt";
                    var typeName = precision.IsDouble ? "System.Double" : "System.Single";
                    var method = FindFloatMathMethod(context.AppContext, typeName, methodName);
                    if (method == null)
                        Emit(address, OpCode.NotImplemented, new StringLiteral($"VFP {methodName} intrinsic is not resolved: {native.Mnemonic} {native.Operand}"));
                    else
                        Emit(address, OpCode.Call, method, ToOperand(native, operands[0]), ToOperand(native, operands[1]));
                }
                else
                    Emit(address, OpCode.Subtract, ToOperand(native, operands[0]),
                        precision.IsDouble ? new DoubleLiteral(0) : new FloatLiteral(0), ToOperand(native, operands[1]), precision);
                continue;
            }

            Emit(address, OpCode.NotImplemented, new StringLiteral($"Unsupported ARM instruction: {native.Mnemonic} {native.Operand}"));
        }

        if (instructions.Count > 0 && instructions[^1].OpCode is not (OpCode.Return or OpCode.Jump or OpCode.ConditionalJump or OpCode.IndirectJump))
        {
            if (context.IsVoid)
                Emit((ulong)nativeInstructions[^1].Address + (ulong)nativeInstructions[^1].Bytes.Length, OpCode.Return);
            else
                Emit((ulong)nativeInstructions[^1].Address + (ulong)nativeInstructions[^1].Bytes.Length, OpCode.Return, CallingConventions.ReturnValueSourceRegister(context));
        }

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode is not (OpCode.Jump or OpCode.ConditionalJump))
                continue;

            var targetAddress = ((Immediate)instruction.Operands[0]).UnsignedValue;
            var targetIndex = addresses.FindIndex(address => address == targetAddress);
            if (targetIndex < 0)
            {
                instruction.OpCode = OpCode.Invalid;
                instruction.SetOperands(new StringLiteral($"ARM branch target is outside the recovered method: 0x{targetAddress:X}"));
            }
            else
                instruction.SetOperand(0, instructions[targetIndex]);
        }

        return instructions;
    }

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        return CallingConventions.ResolveForManaged(context).ToList();
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new ArmV7KeyFunctionAddresses();

    private static (string Mnemonic, string Condition) NormalizeMnemonic(ArmInstruction instruction)
    {
        var raw = instruction.Mnemonic.ToLowerInvariant();
        var dot = raw.IndexOf('.');
        var stem = dot < 0 ? raw : raw[..dot];
        var condition = instruction.Details.ConditionCode.ToString();
        var hasExplicitCondition = condition is not ("Invalid" or "AL");

        foreach (var candidate in new[] { "eq", "ne", "cs", "hs", "cc", "lo", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le" })
        {
            if (!stem.EndsWith(candidate, System.StringComparison.Ordinal)
                || (hasExplicitCondition && condition != candidate.ToUpperInvariant()))
                continue;

            var possibleBase = stem[..^2];
            if (!IsKnownArmMnemonic(possibleBase))
                continue;

            stem = possibleBase;
            if (!hasExplicitCondition)
                condition = candidate.ToUpperInvariant();
            break;
        }

        return (stem, condition);
    }

    private static bool IsKnownArmMnemonic(string mnemonic)
        => mnemonic is "b" or "bl" or "blx" or "bx" or "cbz" or "cbnz" or "cmp" or "cmn" or "tst" or "teq"
            or "add" or "adds" or "sub" or "subs" or "rsb" or "mul" or "and" or "ands" or "orr" or "orrs" or "eor" or "eors"
            or "bic" or "mov" or "movs" or "movw" or "movt" or "mvn" or "ldr" or "ldrb" or "ldrh" or "ldrsb"
            or "ldrsh" or "ldrd" or "str" or "strb" or "strh" or "strd" or "push" or "pop" or "stmdb" or "ldmia" or "adr"
            or "lsl" or "lsls" or "lsr" or "lsrs" or "asr" or "asrs" or "ror" or "nop"
            or "vadd" or "vsub" or "vmul" or "vdiv" or "vneg" or "vabs" or "vsqrt" or "vmov" or "vldr" or "vstr" or "vcmp" or "vcmpe" or "vcvt" or "vcvtr" or "vmrs";

    private static MethodAnalysisContext? FindFloatMathMethod(ApplicationAnalysisContext context, string typeName, string methodName)
    {
        var candidateTypes = typeName == "System.Single"
            ? new[] { "UnityEngine.Mathf", "System.MathF", "System.Math" }
            : new[] { "System.Math" };
        var assemblies = new[]
            {
                context.SystemTypes.SystemSingleType.DeclaringAssembly,
                context.SystemTypes.SystemDoubleType.DeclaringAssembly
            }
            .Concat(context.Assemblies)
            .Distinct();

        foreach (var candidateTypeName in candidateTypes)
        foreach (var assembly in assemblies)
        {
            var method = assembly.GetTypeByFullName(candidateTypeName)?.Methods.FirstOrDefault(candidate =>
                candidate.Name == methodName
                && candidate.IsStatic
                && candidate.ReturnType.FullName == typeName
                && candidate.Parameters.Count == 1
                && candidate.Parameters[0].ParameterType.FullName == typeName);
            if (method != null)
                return method;
        }

        return null;
    }

    public override string PrintAssembly(MethodAnalysisContext context)
    {
        var sb = new StringBuilder();

        var instructions = ArmV7Utils.GetArmV7MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);

        var first = true;
        foreach (var instruction in instructions)
        {
            if (!first)
                sb.AppendLine();

            first = false;
            sb.Append("0x").Append(instruction.Address.ToString("X")).Append(" ").Append(instruction.Mnemonic).Append(" ").Append(instruction.Operand);
        }

        return sb.ToString();
    }
}
