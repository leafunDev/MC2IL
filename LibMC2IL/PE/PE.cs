using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using LibMC2IL.Logging;

namespace LibMC2IL.PE;

public sealed class PE : Il2CppBinary
{
    //Initialized in constructor
    private readonly byte[] raw;

    //PE-Specific Stuff
    private readonly SectionHeader[] peSectionHeaders;
    private readonly ulong peImageBase;
    private readonly OptionalHeader64? peOptionalHeader64;
    private readonly OptionalHeader? peOptionalHeader32;

    //Disable null check because this stuff is initialized post-constructor
#pragma warning disable 8618
    private uint[]? peExportedFunctionPointers;
    private uint[] peExportedFunctionNamePtrs;
    private ushort[] peExportedFunctionOrdinals;

    //Il2cpp binary fields:

    //Top-level structs

    //Pointers

    public PE(MemoryStream input) : this(GetFileBytes(input))
    {
    }

    private PE(byte[] fileBytes) : base(CreateFileStream(fileBytes))
    {
        raw = fileBytes;
        LibLogger.Verbose("\tReading PE File Header...");
        var start = DateTime.Now;

        if (ReadUInt16() != 0x5A4D) //Magic number
            throw new FormatException("ERROR: Magic number mismatch.");
        Position = 0x3C; //Signature position position (lol)
        Position = ReadUInt32(); //Signature position
        if (ReadUInt32() != 0x00004550) //Signature
            throw new FormatException("ERROR: Invalid PE file signature");

        var fileHeader = ReadReadable<FileHeader>();
        if (fileHeader.Machine == 0x014c) //Intel 386
        {
            is32Bit = true;
            InstructionSetId = DefaultInstructionSets.X86_32;
            peOptionalHeader32 = ReadReadable<OptionalHeader>();
            peImageBase = peOptionalHeader32.ImageBase;
        }
        else if (fileHeader.Machine == 0x8664) //AMD64
        {
            InstructionSetId = DefaultInstructionSets.X86_64;
            peOptionalHeader64 = ReadReadable<OptionalHeader64>();
            peImageBase = peOptionalHeader64.ImageBase;
        }
        else
        {
            throw new NotSupportedException("ERROR: Unsupported machine.");
        }

        peSectionHeaders = ReadReadableArrayAtRawAddr<SectionHeader>(-1, fileHeader.NumberOfSections);

        LibLogger.VerboseNewline($"OK ({(DateTime.Now - start).TotalMilliseconds} ms)");
        LibLogger.VerboseNewline($"\t\tImage Base at 0x{peImageBase:X}");
        LibLogger.VerboseNewline($"\t\tDLL is {(is32Bit ? "32" : "64")}-bit");
    }
#pragma warning restore 8618

    public override long MapVirtualAddressToRaw(ulong uiAddr, bool throwOnError = true)
    {
        if (uiAddr < peImageBase)
            if (throwOnError)
                throw new OverflowException($"Provided address, 0x{uiAddr:X}, was less than image base, 0x{peImageBase:X}");
            else
                return VirtToRawInvalidNoMatch;

        var rva = uiAddr - peImageBase;
        if (rva > uint.MaxValue)
        {
            if (throwOnError)
                throw new ArgumentOutOfRangeException(nameof(uiAddr), $"Provided address maps to RVA 0x{rva:X}, which exceeds the PE RVA range.");

            return VirtToRawInvalidOutOfBounds;
        }

        // A virtual section can include zero-filled bytes beyond SizeOfRawData. Those bytes have
        // no file offset, so only return addresses backed by both the virtual and raw section data.
        // Use widened arithmetic: section headers are not guaranteed to be sorted, and adding two
        // uint values before widening can wrap at the end of the RVA or raw-file range.
        var hasVirtualOnlyMatch = false;
        foreach (var section in peSectionHeaders)
        {
            var sectionStart = (ulong)section.VirtualAddress;
            if (rva < sectionStart)
                continue;

            var sectionOffset = rva - sectionStart;
            if (sectionOffset >= section.VirtualSize)
                continue;

            if (sectionOffset >= section.SizeOfRawData)
            {
                hasVirtualOnlyMatch = true;
                continue;
            }

            var rawOffset = (ulong)section.PointerToRawData + sectionOffset;
            if (rawOffset >= (ulong)raw.LongLength)
            {
                hasVirtualOnlyMatch = true;
                continue;
            }

            return (long)rawOffset;
        }

        var reason = hasVirtualOnlyMatch
            ? $"Provided address maps to RVA 0x{rva:X} in a PE section range without corresponding bytes in the file."
            : $"Provided address maps to RVA 0x{rva:X}, which is outside every PE section range.";

        if (throwOnError)
            throw new ArgumentOutOfRangeException(nameof(uiAddr), reason);

        return VirtToRawInvalidOutOfBounds;
    }

    public override ulong MapRawAddressToVirtual(uint offset, bool throwOnError = true)
    {
        foreach (var section in peSectionHeaders)
        {
            var sectionRawStart = (ulong)section.PointerToRawData;
            if (offset < sectionRawStart)
                continue;

            var sectionOffset = (ulong)offset - sectionRawStart;
            if (sectionOffset >= section.SizeOfRawData || sectionOffset >= section.VirtualSize
                || offset >= (ulong)raw.LongLength)
                continue;

            var rva = (ulong)section.VirtualAddress + sectionOffset;
            if (rva > uint.MaxValue || peImageBase > ulong.MaxValue - rva)
                continue;

            return peImageBase + rva;
        }

        if (throwOnError)
            throw new InvalidOperationException($"Provided offset, 0x{offset:X}, does not map to file-backed virtual data in any PE section.");

        return 0;
    }

    [MemberNotNull(nameof(peExportedFunctionPointers))]
    private void LoadPeExportTable()
    {
        uint addrExportTable;
        if (is32Bit)
        {
            if (peOptionalHeader32?.DataDirectory == null || peOptionalHeader32.DataDirectory.Length == 0)
                throw new InvalidDataException("Could not load 32-bit optional header or data directory, or data directory was empty!");

            //We assume, per microsoft guidelines, that the first datadirectory is the export table.
            addrExportTable = peOptionalHeader32.DataDirectory.First().VirtualAddress;
        }
        else
        {
            if (peOptionalHeader64?.DataDirectory == null || peOptionalHeader64.DataDirectory.Length == 0)
                throw new InvalidDataException("Could not load 64-bit optional header or data directory, or data directory was empty!");

            //We assume, per microsoft guidelines, that the first datadirectory is the export table.
            addrExportTable = peOptionalHeader64.DataDirectory.First().VirtualAddress;
        }

        try
        {
            //Non-virtual addresses for these
            var directoryEntryExports = ReadReadableAtVirtualAddress<PeDirectoryEntryExport>(addrExportTable + peImageBase);

            peExportedFunctionPointers = ReadClassArrayAtVirtualAddress<uint>(directoryEntryExports.RawAddressOfExportTable + peImageBase, directoryEntryExports.NumberOfExports);
            peExportedFunctionNamePtrs = ReadClassArrayAtVirtualAddress<uint>(directoryEntryExports.RawAddressOfExportNameTable + peImageBase, directoryEntryExports.NumberOfExportNames);
            peExportedFunctionOrdinals = ReadClassArrayAtVirtualAddress<ushort>(directoryEntryExports.RawAddressOfExportOrdinalTable + peImageBase, directoryEntryExports.NumberOfExportNames); //This uses the name count per MSoft spec
        }
        catch (EndOfStreamException)
        {
            LibLogger.WarnNewline($"PE does not appear to contain a valid export table! It would be apparently located at virt address 0x{addrExportTable + peImageBase:X}, raw 0x{MapVirtualAddressToRaw(addrExportTable + peImageBase):X}, but that's beyond the end of the binary. No exported functions will be accessible.");
            peExportedFunctionPointers = [];
            peExportedFunctionNamePtrs = [];
            peExportedFunctionOrdinals = [];
        }
    }

    public override ulong GetVirtualAddressOfExportedFunctionByName(string toFind)
    {
        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        var index = Array.FindIndex(peExportedFunctionNamePtrs, stringAddress =>
        {
            var rawStringAddress = MapVirtualAddressToRaw(stringAddress + peImageBase);
            var exportName = ReadStringToNull(rawStringAddress);
            return exportName == toFind;
        });

        if (index < 0)
            return 0;

        var ordinal = peExportedFunctionOrdinals[index];
        var functionPointer = peExportedFunctionPointers[ordinal];

        return functionPointer + peImageBase;
    }

    public override bool IsExportedFunction(ulong addr)
    {
        if (addr <= peImageBase)
            return false;

        var rva = GetRva(addr);
        if (rva > uint.MaxValue)
            return false;

        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        return Array.IndexOf(peExportedFunctionPointers, (uint)rva) >= 0;
    }

    public override bool TryGetExportedFunctionName(ulong addr, [NotNullWhen(true)] out string? name)
    {
        if (addr <= peImageBase)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }

        var rva = GetRva(addr);
        if (rva > uint.MaxValue)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }

        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        var index = Array.IndexOf(peExportedFunctionPointers, (uint)rva);
        if (index < 0)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }
        else
        {
            var rawStringAddress = MapVirtualAddressToRaw(peExportedFunctionNamePtrs[index] + peImageBase);
            name = ReadStringToNull(rawStringAddress);
            return true;
        }
    }

    public override IEnumerable<KeyValuePair<string, ulong>> GetExportedFunctions()
    {
        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        for (var i = 0; i < peExportedFunctionPointers.Length; i++)
        {
            var functionPointer = peExportedFunctionPointers[i];
            var namePointer = peExportedFunctionNamePtrs[i];

            var name = ReadStringToNull(MapVirtualAddressToRaw(namePointer + peImageBase));
            yield return new(name, functionPointer + peImageBase);
        }
    }

    public override ulong GetRva(ulong pointer)
    {
        return pointer - peImageBase;
    }

    public override ReadOnlySpan<byte> GetEntirePrimaryExecutableSection()
    {
        var primarySection = peSectionHeaders.FirstOrDefault(s => s.Name == ".text");

        if (primarySection == null)
            return ReadOnlySpan<byte>.Empty;

        return GetRawBinaryContent().Slice((int)primarySection.PointerToRawData, (int)primarySection.SizeOfRawData);
    }

    public override ulong GetVirtualAddressOfPrimaryExecutableSection() => peSectionHeaders.FirstOrDefault(s => s.Name == ".text")?.VirtualAddress + peImageBase ?? 0;

    public override byte GetByteAtRawAddress(ulong addr) => raw[addr];

    public override long RawLength => raw.Length;

    public override ReadOnlySpan<byte> GetRawBinaryContent() => raw;

    public Stream ToStream() => new MemoryStream(raw, false);
}
