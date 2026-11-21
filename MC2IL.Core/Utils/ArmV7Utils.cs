using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Model.Contexts;
using LibMC2IL.Elf;

namespace MC2IL.Core.Utils;

public static class ArmV7Utils
{
    private static readonly object DisassemblerLock = new();
    private static readonly CapstoneArmDisassembler?[] Disassemblers = new CapstoneArmDisassembler?[4];

    /// <summary>ARM32 function pointers use bit zero to indicate Thumb state.</summary>
    public static ulong NormalizeCodeAddress(ulong address) => address & ~1UL;

    private static CapstoneArmDisassembler GetDisassembler(bool thumb, bool bigEndian)
    {
        lock (DisassemblerLock)
        {
            var index = (thumb ? 1 : 0) | (bigEndian ? 2 : 0);
            var disassembler = Disassemblers[index];
            if (disassembler == null)
            {
                var mode = thumb ? ArmDisassembleMode.Thumb : ArmDisassembleMode.Arm;
                if (bigEndian)
                    mode |= ArmDisassembleMode.BigEndian;
                disassembler = CapstoneDisassembler.CreateArmDisassembler(mode);
                disassembler.EnableInstructionDetails = true;
                disassembler.EnableSkipDataMode = true;
                disassembler.DisassembleSyntax = DisassembleSyntax.Intel;
                Disassemblers[index] = disassembler;
            }

            return disassembler;
        }
    }

    private static List<ArmInstruction> Disassemble(byte[] bytes, ulong address, bool thumb, bool bigEndian, int count = -1)
    {
        // Capstone handles are mutable native state. Serialize iteration as MC2IL analyzes methods in parallel.
        lock (DisassemblerLock)
        {
            var instructions = GetDisassembler(thumb, bigEndian).Iterate(bytes, (long)address);
            if (count > 0)
                instructions = instructions.Take(count);
            return instructions.ToList();
        }
    }

    public static BinarySlice TryGetMethodBodyBytesFast(ApplicationAnalysisContext appContext, ulong virtAddress, bool isCAGen)
    {
        var binary = appContext.Binary;
        var codeAddress = NormalizeCodeAddress(virtAddress);
        var startOfNext = appContext.GetAddressOfNextFunctionStart(codeAddress);

        if (startOfNext == 0 || startOfNext <= codeAddress)
            // The final method has no metadata boundary, so the caller must use bounded decoding.
            return BinarySlice.Empty;

        if (isCAGen && startOfNext - codeAddress > 50_000)
            return BinarySlice.Empty;

        var rawStartOfNextMethod = binary.MapVirtualAddressToRaw(startOfNext);
        var rawStart = binary.MapVirtualAddressToRaw(codeAddress);
        if (rawStart < 0 || rawStart >= binary.RawLength)
            return BinarySlice.Empty;
        if (rawStartOfNextMethod <= rawStart || rawStartOfNextMethod > binary.RawLength)
            rawStartOfNextMethod = binary.RawLength;

        return new BinarySlice(binary, (int)rawStart, (int)(rawStartOfNextMethod - rawStart));
    }

    public static List<ArmInstruction> GetArmV7MethodBodyAtVirtualAddress(ApplicationAnalysisContext appContext, ulong virtAddress, bool managed = true, int count = -1)
    {
        var binary = appContext.Binary;
        var thumb = (virtAddress & 1) != 0;
        var bigEndian = binary is ElfFile { IsBigEndian: true };
        var codeAddress = NormalizeCodeAddress(virtAddress);

        // Managed method boundaries come from metadata. Thumb instructions may be 16 or 32 bits;
        // Capstone consumes the complete mixed-width stream in one pass.
        if (managed)
        {
            var startOfNext = appContext.GetAddressOfNextFunctionStart(codeAddress);
            if (startOfNext > codeAddress)
            {
                var rawStart = binary.MapVirtualAddressToRaw(codeAddress);
                var rawEnd = binary.MapVirtualAddressToRaw(startOfNext);
                if (rawStart < 0 || rawStart >= binary.RawLength)
                    return [];
                if (rawEnd <= rawStart || rawEnd > binary.RawLength)
                    rawEnd = binary.RawLength;

                var bytes = binary.GetRawBinaryContent()[(int)rawStart..(int)rawEnd];
                return Disassemble(bytes.ToArray(), codeAddress, thumb, bigEndian, count);
            }
        }

        // Unmanaged functions have no metadata boundary. Decode variable-width instructions and stop
        // at returns or unconditional tail branches. Never step through raw bytes at a guessed width.
        var pos = (int)binary.MapVirtualAddressToRaw(codeAddress);
        var allBytes = binary.GetRawBinaryContent();
        if (pos < 0 || pos >= allBytes.Length)
            return [];

        var maxBytes = System.Math.Min(4096, allBytes.Length - pos);
        var decoded = Disassemble(allBytes[pos..(pos + maxBytes)].ToArray(), codeAddress, thumb, bigEndian, count);
        var ret = new List<ArmInstruction>();
        foreach (var instruction in decoded)
        {
            ret.Add(instruction);
            if (IsTerminal(instruction))
                break;
        }

        return ret;
    }

    private static bool IsTerminal(ArmInstruction instruction)
    {
        if (instruction.IsSkippedData || !instruction.HasDetails)
            return true;

        var mnemonic = instruction.Mnemonic.ToLowerInvariant();
        var operands = instruction.Details.Operands;
        var unconditional = instruction.Details.ConditionCode.ToString() is "Invalid" or "AL";

        if (mnemonic == "bx" && unconditional && operands.Length > 0
            && operands[0].Type == ArmOperandType.Register
            && operands[0].Register.ToString().Equals("lr", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (unconditional && (mnemonic.StartsWith("pop", System.StringComparison.Ordinal)
            || mnemonic.StartsWith("ldm", System.StringComparison.Ordinal)))
            return operands.Any(o => o.Type == ArmOperandType.Register
                && o.Register.ToString().Equals("pc", System.StringComparison.OrdinalIgnoreCase));

        if (unconditional && (mnemonic.StartsWith("mov", System.StringComparison.Ordinal)
                || mnemonic.StartsWith("ldr", System.StringComparison.Ordinal)) && operands.Length > 0
            && operands[0].Type == ArmOperandType.Register
            && operands[0].Register.ToString().Equals("pc", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (unconditional && mnemonic == "bx")
            return true;

        var branchMnemonic = mnemonic.Split('.')[0];
        if (unconditional && (branchMnemonic == "b" || branchMnemonic.StartsWith("b", System.StringComparison.Ordinal)
            && branchMnemonic.Length > 2 && branchMnemonic[^2..] is "eq" or "ne" or "cs" or "hs" or "cc" or "lo"
                or "mi" or "pl" or "vs" or "vc" or "hi" or "ls" or "ge" or "lt" or "gt" or "le"))
            return true;

        return false;
    }
}
