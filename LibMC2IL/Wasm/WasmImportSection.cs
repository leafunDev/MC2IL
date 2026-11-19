using System.Collections.Generic;
using LibMC2IL.Logging;

namespace LibMC2IL.Wasm;

public class WasmImportSection : WasmSection
{
    public ulong ImportCount;
    public readonly List<WasmImportEntry> Entries = [];

    internal WasmImportSection(WasmSectionId type, long pointer, ulong size, WasmFile readFrom) : base(type, pointer, size)
    {
        ImportCount = readFrom.BaseStream.ReadLEB128Unsigned();
        for (var i = 0UL; i < ImportCount; i++)
        {
            Entries.Add(new(readFrom));
        }

        LibLogger.VerboseNewline($"\t\tRead {Entries.Count} imports");
    }
}
