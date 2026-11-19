using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using LibMC2IL.Logging;
using LibMC2IL.Metadata;

namespace LibMC2IL;

public sealed class LibMC2ILContextBuilder
{
    private readonly LibMC2ILContext _context = new(
        new LibMC2ILMain.LibMC2ILSettings // Snapshot settings at creation time.
        {
            AllowManualMetadataAndCodeRegInput = LibMC2ILMain.Settings.AllowManualMetadataAndCodeRegInput,
            DisableMethodPointerMapping = LibMC2ILMain.Settings.DisableMethodPointerMapping,
            DisableGlobalResolving = LibMC2ILMain.Settings.DisableGlobalResolving,
            MetadataFixupFunc = LibMC2ILMain.Settings.MetadataFixupFunc
        });

    private bool _metadataLoaded;
    private bool _binaryLoaded;

    public void LoadMetadata(byte[] metadataBytes, UnityVersion unityVersion)
    {
        var start = DateTime.Now;
        LibLogger.InfoNewline("Initializing Metadata...");

        Il2CppMetadata metadata;
        try
        {
            metadata = Il2CppMetadata.ReadFrom(metadataBytes, unityVersion);
        }
        catch (Exception e) when (_context.Settings.MetadataFixupFunc is { } fixupFunc)
        {
            LibLogger.WarnNewline("Metadata read failed, but a fixup function is registered. Calling fixup function and then will attempt to read again.");

            byte[]? fixedBytes;
            try
            {
                fixedBytes = fixupFunc(metadataBytes, unityVersion);
            }
            catch (Exception fixupError)
            {
                throw new AggregateException("The metadata fixup function failed.", e, fixupError);
            }

            if (fixedBytes == null)
            {
                LibLogger.VerboseNewline("No metadata fixup accepted the input.");
                throw;
            }

            try
            {
                metadata = Il2CppMetadata.ReadFrom(fixedBytes, unityVersion);
                LibLogger.InfoNewline("Metadata read succeeded after fixup.");
            }
            catch (Exception retryError)
            {
                throw new AggregateException("Metadata is unreadable after fixup.", e, retryError);
            }
        }

        LibLogger.InfoNewline($"Initialized Metadata in {(DateTime.Now - start).TotalMilliseconds:F0}ms");

        LoadMetadata(metadata);
    }

    public void LoadMetadata(Il2CppMetadata metadata)
    {
        _context.Metadata = metadata;
        metadata.SetOwningContext(_context);

        _metadataLoaded = true;
    }

    public void LoadBinary(byte[] binaryBytes)
    {
        if (!_metadataLoaded)
            throw new InvalidOperationException("Metadata must be loaded before the binary can be loaded.");

        LibMC2ILBinaryRegistry.CreateAndInit(binaryBytes, _context);

        _binaryLoaded = true;
    }

    public void LoadBinary(Il2CppBinary binary)
    {
        if (!_metadataLoaded)
            throw new InvalidOperationException("Metadata must be loaded before the binary can be loaded.");

        binary.Init(_context);

        _binaryLoaded = true;
    }

    public LibMC2ILContext Build()
    {
        if (!_metadataLoaded)
            throw new InvalidOperationException("Metadata must be loaded before context can be built.");

        if (!_binaryLoaded)
            throw new InvalidOperationException("Binary must be loaded before context can be built.");

        _context.ReflectionCache.Init(_context);

        if (!_context.Settings.DisableGlobalResolving && _context.Metadata.MetadataVersion < 27)
        {
            var start = DateTime.Now;
            LibLogger.Info("Mapping Globals...");
            _context.MapGlobalIdentifiers();
            LibLogger.InfoNewline($"OK ({(DateTime.Now - start).TotalMilliseconds:F0}ms)");
        }

        if (!_context.Settings.DisableMethodPointerMapping)
        {
            var start = DateTime.Now;
            LibLogger.Info("Mapping pointers to Il2CppMethodDefinitions...");
            foreach (var (method, ptr) in _context.Metadata.methodDefs.Select(method => (method, ptr: method.MethodPointer)))
            {
                if (!_context.MethodsByPtr.TryGetValue(ptr, out var list))
                    _context.MethodsByPtr[ptr] = list = [];

                list.Add(method);
            }

            LibLogger.InfoNewline($"Processed {_context.Metadata.methodDefs.Length} OK ({(DateTime.Now - start).TotalMilliseconds:F0}ms)");
        }

        return _context;
    }

    public static LibMC2ILContext Build(byte[] binaryBytes, byte[] metadataBytes, UnityVersion unityVersion)
    {
        LibMC2ILContextBuilder builder = new();

        builder.LoadMetadata(metadataBytes, unityVersion);
        builder.LoadBinary(binaryBytes);

        return builder.Build();
    }

    public static LibMC2ILContext BuildFromFiles(string pePath, string metadataPath, UnityVersion unityVersion)
    {
        var builder = new LibMC2ILContextBuilder();
        builder.LoadMetadata(File.ReadAllBytes(metadataPath), unityVersion);
        builder.LoadBinary(File.ReadAllBytes(pePath));
        return builder.Build();
    }
}
