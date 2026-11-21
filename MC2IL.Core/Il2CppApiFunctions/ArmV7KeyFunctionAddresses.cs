using System;
using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm;
using LibMC2IL.Elf;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Il2CppApiFunctions;

public class ArmV7KeyFunctionAddresses : BaseKeyFunctionAddresses
{
    private const ulong BridgeSearchSize = 1024;
    private Dictionary<ulong, List<ulong>>? _bridges;

    protected override void Init(ApplicationAnalysisContext context)
    {
        base.Init(context);
        _bridges = null;
    }

    protected override ulong FindFirstCallTargetInMethod(ulong methodVa)
    {
        var code = Decode(methodVa, 128);
        var thumb = IsThumb(methodVa);
        ArmInstruction? flagLoad = null;
        var comparedFlag = false;
        for (var i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (instruction.Id == ArmInstructionId.ARM_INS_LDRB && IsUnconditional(instruction))
            {
                flagLoad = instruction;
                comparedFlag = false;
                continue;
            }

            if (instruction.Id is ArmInstructionId.ARM_INS_BL or ArmInstructionId.ARM_INS_BLX
                || IsReturn(instruction) || instruction.Id == ArmInstructionId.ARM_INS_B && IsUnconditional(instruction))
                return 0;

            if (flagLoad?.Details.Operands is not
                [{ Register.Name: { } flagRegister }, { Type: ArmOperandType.Memory } flag])
                continue;

            if (instruction.Id == ArmInstructionId.ARM_INS_CMP)
            {
                comparedFlag = instruction.Details.Operands is
                    [{ Register.Name: var register }, { Type: ArmOperandType.Immediate, Immediate: 0 }]
                    && register == flagRegister;
                continue;
            }

            var conditionalBranch = instruction.Id == ArmInstructionId.ARM_INS_B
                && instruction.Details.ConditionCode == ArmConditionCode.ARM_CC_NE && comparedFlag;
            var testAndBranch = instruction.Id == ArmInstructionId.ARM_INS_CBNZ
                && instruction.Details.Operands.FirstOrDefault()?.Register?.Name == flagRegister;
            if ((!conditionalBranch && !testAndBranch)
                || instruction.Details.Operands.LastOrDefault() is not { Type: ArmOperandType.Immediate } exit)
                continue;

            // Match the guarded region through the byte store. Newer IL2CPP calls
            // the same initializer once for each metadata slot before setting the flag.
            var exitAddress = (long)unchecked((uint)exit.Immediate) & ~1L;
            ulong target = 0;
            for (var j = i + 1; j < code.Count && code[j].Address < exitAddress; j++)
            {
                var body = code[j];
                if (!IsUnconditional(body))
                    break;
                if (body.Id is ArmInstructionId.ARM_INS_BL or ArmInstructionId.ARM_INS_BLX)
                {
                    var callee = DirectTarget(body, thumb);
                    if (!IsExecutable(callee) || target != 0 && callee != target)
                        break;
                    target = callee;
                }
                else if (body.Id == ArmInstructionId.ARM_INS_STRB && target != 0 && j > i + 1
                    && body.Address + body.Bytes.Length == exitAddress
                    && body.Details.Operands is [{ Register.Name: { } storedValue }, { Type: ArmOperandType.Memory } store]
                    && flag.Memory.Base?.Name == store.Memory.Base?.Name && flag.Memory.Index == null
                    && store.Memory.Index == null && flag.Memory.Displacement == store.Memory.Displacement
                    && code[j - 1].Id == ArmInstructionId.ARM_INS_MOV
                    && code[j - 1].Details.Operands is [{ Register.Name: var writtenValue }, { Type: ArmOperandType.Immediate, Immediate: 1 }]
                    && writtenValue == storedValue)
                    return target;
                else if (body.Id is not (ArmInstructionId.ARM_INS_LDR or ArmInstructionId.ARM_INS_MOV
                    or ArmInstructionId.ARM_INS_ADD or ArmInstructionId.ARM_INS_ADR or ArmInstructionId.ARM_INS_NOP))
                    break;
            }

            return 0;
        }

        return 0;
    }

    protected override ulong FindFunctionThisIsAThunkOf(ulong thunkPtr, bool prioritiseCall = false)
    {
        var code = Decode(thunkPtr, 32);
        var thumb = IsThumb(thunkPtr);
        for (var i = 0; i < code.Count && i < 5; i++)
        {
            var instruction = code[i];
            if (!IsUnconditional(instruction))
                break;

            var target = DirectTarget(instruction, thumb);
            if (target != 0)
            {
                if (!IsExecutable(target) || (target & ~1UL) == (thunkPtr & ~1UL))
                    return 0;

                if (instruction.Id == ArmInstructionId.ARM_INS_B)
                    return target;

                // The exported API can also be a call wrapper with a saved return
                // address. Accept a call only when it is followed by an epilogue.
                if (prioritiseCall && i + 1 < code.Count && IsReturn(code[i + 1]))
                    return target;
                return 0;
            }

            if (instruction.Id is not (ArmInstructionId.ARM_INS_PUSH or ArmInstructionId.ARM_INS_MOV or ArmInstructionId.ARM_INS_NOP)
                || instruction.Id == ArmInstructionId.ARM_INS_MOV
                    && instruction.Details.Operands.FirstOrDefault()?.Register?.Name == "pc")
                break;
        }

        return 0;
    }

    protected override IEnumerable<ulong> FindAllThunkFunctions(ulong addr, uint maxBytesBack = 0, params ulong[] addressesToIgnore)
    {
        _bridges ??= FindBridgeEntries();
        if (_bridges.TryGetValue(addr, out var found))
        {
            // The managed wrappers we can prove here contain a single tail-branch.
            // An ambiguous candidate list is safer to leave unresolved.
            var distinct = found.Where(candidate => !addressesToIgnore.Contains(candidate)).Distinct().Take(2).ToArray();
            if (distinct.Length == 1)
                return distinct;
        }

        return [];
    }

    protected override ulong GetObjectIsInstFromSystemType() => 0;
    protected override int GetCallerCount(ulong toWhere) => 0;

    private Dictionary<ulong, List<ulong>> FindBridgeEntries()
    {
        var result = new Dictionary<ulong, List<ulong>>();
        var anchor = il2cpp_codegen_initialize_runtime_metadata != 0
            ? il2cpp_codegen_initialize_runtime_metadata : il2cpp_codegen_initialize_method;
        if (anchor == 0)
            return result;

        var thumb = IsThumb(anchor);
        // IL2CPP places these tiny bridge functions together near the metadata-init
        // bridge. Restrict the search to that region and to entry-like boundaries.
        // Thumb must start at a known instruction boundary, so only scan forward.
        var start = thumb ? anchor : Math.Max(anchor, BridgeSearchSize) - BridgeSearchSize;
        var code = Decode(start, (int)(thumb ? BridgeSearchSize : 2 * BridgeSearchSize));
        for (var i = 1; i < code.Count; i++)
        {
            var previous = code[i - 1];
            var instruction = code[i];
            if (instruction.Id != ArmInstructionId.ARM_INS_B || !IsUnconditional(instruction)
                || !IsReturn(previous) && !(previous.Id == ArmInstructionId.ARM_INS_B && IsUnconditional(previous)))
                continue;

            var entry = (ulong)instruction.Address | (thumb ? 1UL : 0UL);
            var target = DirectTarget(instruction, thumb);
            if (target == 0 || target == entry || !IsExecutable(target) || _appContext.MethodsByAddress.ContainsKey(entry))
                continue;

            if (!result.TryGetValue(target, out var entries))
                result[target] = entries = [];
            entries.Add(entry);
        }

        return result;
    }

    private List<ArmInstruction> Decode(ulong address, int maximumBytes)
    {
        var available = ExecutableBytesAvailable(address);
        var binary = _appContext.Binary;
        var start = address & ~1UL;
        if (available == 0 || !binary.TryMapVirtualAddressToRaw(start, out var raw) || raw < 0 || raw >= binary.RawLength)
            return [];

        var length = (int)Math.Min((ulong)maximumBytes, Math.Min((ulong)(binary.RawLength - raw), available));
        var bytes = binary.GetRawBinaryContent().Slice((int)raw, length).ToArray();
        var mode = IsThumb(address) ? ArmDisassembleMode.Thumb : ArmDisassembleMode.Arm;
        if (binary.IsBigEndian)
            mode |= ArmDisassembleMode.BigEndian;
        using var disassembler = CapstoneDisassembler.CreateArmDisassembler(mode);
        disassembler.EnableInstructionDetails = true;
        disassembler.EnableSkipDataMode = true;
        return disassembler.Iterate(bytes, (long)start).ToList();
    }

    private bool IsExecutable(ulong address) => ExecutableBytesAvailable(address) != 0;

    private ulong ExecutableBytesAvailable(ulong address)
    {
        if (address == 0)
            return 0;
        var physical = address & ~1UL;
        if (_appContext.Binary is ElfFile elf)
            return elf.GetExecutableFileBytesAvailable(physical);

        var start = _appContext.Binary.GetVirtualAddressOfPrimaryExecutableSection();
        var size = (ulong)_appContext.Binary.GetEntirePrimaryExecutableSection().Length;
        return physical >= start && physical - start < size ? size - (physical - start) : 0;
    }

    private static bool IsThumb(ulong address) => (address & 1) != 0;

    private static bool IsUnconditional(ArmInstruction instruction) =>
        instruction.Id != ArmInstructionId.Invalid && instruction.Mnemonic != ".byte"
        && instruction.Details.ConditionCode is ArmConditionCode.ARM_CC_AL or ArmConditionCode.Invalid;

    private static bool IsReturn(ArmInstruction instruction) => IsUnconditional(instruction)
        && (instruction.Id == ArmInstructionId.ARM_INS_BX && instruction.Operand == "lr"
            || instruction.Id == ArmInstructionId.ARM_INS_POP
                && instruction.Details.Operands.Any(op => op.Register?.Name == "pc"));

    private static ulong DirectTarget(ArmInstruction instruction, bool thumb)
    {
        if (instruction.Id is not (ArmInstructionId.ARM_INS_B or ArmInstructionId.ARM_INS_BL or ArmInstructionId.ARM_INS_BLX)
            || instruction.Details.Operands.FirstOrDefault() is not { Type: ArmOperandType.Immediate } operand)
            return 0;
        var target = (ulong)unchecked((uint)operand.Immediate);
        return target | ((instruction.Id == ArmInstructionId.ARM_INS_BLX ? !thumb : thumb) ? 1UL : 0UL);
    }
}
