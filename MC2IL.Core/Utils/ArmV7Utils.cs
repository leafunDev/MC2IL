using System;
using System.Collections.Generic;
using System.Linq;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm;
using MC2IL.Core.Model.Contexts;

namespace MC2IL.Core.Utils;

public static class ArmV7Utils
{
    public static BinarySlice TryGetMethodBodyBytesFast(ApplicationAnalysisContext appContext, ulong virtAddress, bool isCAGen)
    {
        var binary = appContext.Binary;
        var start = virtAddress & ~1UL;
        var end = appContext.GetAddressOfNextFunctionStart(virtAddress) & ~1UL;
        if (end <= start || (isCAGen && end - start > 50_000)
            || !binary.TryMapVirtualAddressToRaw(start, out var rawStart)
            || !binary.TryMapVirtualAddressToRaw(end, out var rawEnd)
            || rawStart < 0 || rawEnd <= rawStart || rawEnd > binary.RawLength
            || (ulong)(rawEnd - rawStart) != end - start)
            return BinarySlice.Empty;
        return new BinarySlice(binary, (int)rawStart, (int)(rawEnd - rawStart));
    }

    internal static List<ArmInstruction> Decode(ApplicationAnalysisContext app, ulong address, BinarySlice bytes)
    {
        if (bytes.Length == 0)
            return [];
        var mode = (address & 1) == 0 ? ArmDisassembleMode.Arm : ArmDisassembleMode.Thumb;
        if (app.Binary.IsBigEndian)
            mode |= ArmDisassembleMode.BigEndian;
        // Recovery is parallel; do not share Capstone's native handle between workers.
        using var disassembler = CapstoneDisassembler.CreateArmDisassembler(mode);
        disassembler.EnableInstructionDetails = true;
        disassembler.EnableSkipDataMode = true;
        return disassembler.Iterate(bytes.ToArray(), (long)(address & ~1UL)).ToList();
    }

    public static List<ArmInstruction> GetArmV7MethodBodyAtVirtualAddress(ApplicationAnalysisContext appContext,
        ulong virtAddress, bool managed = true, int count = -1)
    {
        if (managed && TryGetMethodBodyBytesFast(appContext, virtAddress, false) is { Length: > 0 } slice)
        {
            var decoded = Decode(appContext, virtAddress, slice);
            return count > 0 ? decoded.Take(count).ToList() : decoded;
        }

        var binary = appContext.Binary;
        if (!binary.TryMapVirtualAddressToRaw(virtAddress & ~1UL, out var raw) || raw < 0 || raw >= binary.RawLength)
            return [];
        var maxBytes = count > 0 ? Math.Min(4096, 4L * count) : 4096;
        var length = (int)Math.Min(binary.RawLength - raw, maxBytes);
        var body = Decode(appContext, virtAddress, new BinarySlice(binary, (int)raw, length));
        var result = new List<ArmInstruction>();
        foreach (var instruction in body)
        {
            result.Add(instruction);
            if (instruction.Mnemonic is "b" or "bx" or ".byte"
                || instruction.Mnemonic == "pop" && instruction.Operand.Contains("pc")
                || count > 0 && result.Count >= count)
                break;
        }
        return result;
    }
}
