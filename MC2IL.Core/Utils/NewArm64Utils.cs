using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Model.Contexts;
using Disarm;

namespace MC2IL.Core.Utils;

public static class NewArm64Utils
{
    public static List<Arm64Instruction> GetArm64MethodBodyAtVirtualAddress(ApplicationAnalysisContext appContext, ulong virtAddress, bool managed = true, int count = -1)
    {
        var binary = appContext.Binary;
        
        if (managed)
        {
            var startOfNext = appContext.GetAddressOfNextFunctionStart(virtAddress);

            //We have to fall through to default behavior for the last method because we cannot accurately pinpoint its end
            if (startOfNext > 0)
            {
                var rawStartOfNextMethod = binary.MapVirtualAddressToRaw(startOfNext);

                var rawStart = binary.MapVirtualAddressToRaw(virtAddress);
                if (rawStartOfNextMethod < rawStart)
                    rawStartOfNextMethod = binary.RawLength;

                var bytes = binary.GetRawBinaryContent().Slice((int)rawStart, (int)(rawStartOfNextMethod - rawStart));

                return Disassemble(bytes, virtAddress);
            }
        }

        //Unmanaged function, look for first b
        var pos = (int)binary.MapVirtualAddressToRaw(virtAddress);
        var allBytes = binary.GetRawBinaryContent();
        if (pos < 0 || pos > allBytes.Length)
            throw new ArgumentOutOfRangeException(nameof(virtAddress), $"Mapped raw offset {pos} is outside the binary.");

        // ARM64 instructions are fixed-width. Decode the available window once and stop as soon
        // as the thunk terminator is found instead of re-decoding every growing prefix.
        var availableInstructionCount = (allBytes.Length - pos) / 4;
        var instructionLimit = count == -1
            ? availableInstructionCount
            : Math.Min(Math.Max(count, 0), availableInstructionCount);

        if (instructionLimit == 0)
            return [];

        var scanBytes = allBytes.Slice(pos, instructionLimit * 4);
        return DisassembleUntilTerminator(scanBytes, virtAddress, instructionLimit);
    }

    private static List<Arm64Instruction> DisassembleUntilTerminator(ReadOnlySpan<byte> bytes, ulong virtAddress,
        int instructionLimit)
    {
        List<Arm64Instruction> instructions = [];

        try
        {
            var enumerator = Disassembler.Disassemble(bytes, virtAddress, new Disassembler.Options(true, true, false));
            while (instructions.Count < instructionLimit && enumerator.MoveNext())
            {
                var instruction = enumerator.Current;
                instructions.Add(instruction);

                if (instruction.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.INVALID)
                    break;
            }

            return instructions;
        }
        catch (Exception e)
        {
            // Avoid formatting a potentially large remaining binary when only a short prefix is
            // needed to diagnose a decoder failure.
            const int diagnosticPrefixLength = 64;
            var prefix = bytes[..Math.Min(bytes.Length, diagnosticPrefixLength)];
            var bytePreview = string.Join(", ", prefix.ToArray().Select(b => "0x" + b.ToString("X2")));
            if (bytes.Length > diagnosticPrefixLength)
                bytePreview += ", ...";

            throw new($"Failed to disassemble method body at 0x{virtAddress:X}; first bytes: {bytePreview}", e);
        }
    }

    private static List<Arm64Instruction> Disassemble(ReadOnlySpan<byte> bytes, ulong virtAddress)
    {
        try
        {
            return Disassembler.Disassemble(bytes, virtAddress, new Disassembler.Options(true, true, false)).ToList();
        }
        catch (Exception e)
        {
            throw new($"Failed to disassemble method body: {string.Join(", ", bytes.ToArray().Select(b => "0x" + b.ToString("X2")))}", e);
        }
    }

    // Disarm reports the SIMD FMOV immediate as an integer zero. Decode its imm8
    // from the original instruction instead of losing a repeated floating-point value.
    internal static double DecodeVectorFloatImmediate(uint encoding, bool doublePrecision)
    {
        var immediate = ((encoding >> 16) & 7) << 5 | ((encoding >> 5) & 31);
        var sign = immediate >> 7;
        var repeated = (immediate >> 6) & 1;
        var exponentLow = (immediate >> 4) & 3;
        var fraction = immediate & 15;
        if (doublePrecision)
        {
            var exponent = ((repeated ^ 1) << 10) | (repeated * 0x3FC) | exponentLow;
            var bits = ((ulong)sign << 63) | ((ulong)exponent << 52) | ((ulong)fraction << 48);
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }
        else
        {
            var exponent = ((repeated ^ 1) << 7) | (repeated * 0x7C) | exponentLow;
            var bits = (sign << 31) | (exponent << 23) | (fraction << 19);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }
    }

    public static List<Arm64Instruction> ToList(this Disassembler.SpanEnumerator enumerator)
    {
        var ret = new List<Arm64Instruction>();
        while (enumerator.MoveNext())
        {
            ret.Add(enumerator.Current);
        }

        return ret;
    }

    public static Arm64Instruction LastValid(this List<Arm64Instruction> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Mnemonic is not (Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED))
                return list[i];
        }

        return list[^1];
    }
}
