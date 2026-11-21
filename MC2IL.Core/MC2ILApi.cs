using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetRipper.Primitives;
using MC2IL.Core.Exceptions;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using LibMC2IL;
using LibMC2IL.Logging;

[assembly: InternalsVisibleTo("MC2IL.Core.Tests")]

namespace MC2IL.Core;

public static class MC2ILApi
{
    public static ApplicationAnalysisContext? CurrentAppContext;

    public static MC2ILRuntimeArgs? RuntimeOptions;

    internal static bool LowMemoryMode => RuntimeOptions?.LowMemoryMode ?? false;

    [RequiresUnreferencedCode("Plugins are loaded dynamically.")]
    public static void Init(string? pluginsDir = null)
    {
        var pluginDirectories = pluginsDir == null
            ? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Plugins"),
                Path.Combine(Environment.CurrentDirectory, "Plugins")
            }
            : [Path.GetFullPath(pluginsDir)];

        foreach (var directory in pluginDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            MC2ILPluginManager.LoadFromDirectory(directory);
        MC2ILPluginManager.InitAll();
    }

    public static UnityVersion DetermineUnityVersion(string? unityPlayerPath, string? gameDataPath)
        => LibMC2ILMain.DetermineUnityVersion(unityPlayerPath, gameDataPath);

    public static UnityVersion GetVersionFromGlobalGameManagers(byte[] ggmBytes)
        => LibMC2ILMain.GetVersionFromGlobalGameManagers(ggmBytes);

    public static UnityVersion GetVersionFromDataUnity3D(Stream fileStream)
        => LibMC2ILMain.GetVersionFromDataUnity3D(fileStream);

    public static void ConfigureLib(bool allowUserToInputAddresses)
    {
        //Set this flag from the options
        LibMC2ILMain.Settings.AllowManualMetadataAndCodeRegInput = allowUserToInputAddresses;

        //We have to have this on, despite the cost, because we need them for attribute restoration
        LibMC2ILMain.Settings.DisableMethodPointerMapping = false;

        LibMC2ILMain.Settings.MetadataFixupFunc = MC2ILPluginManager.MetadataFixupFuncs is { } funcs ? (originalBytes, version) =>
        {
            Logger.InfoNewline("Received request for metadata fixup from LibMC2IL. Calling registered plugin fixup functions...");

            foreach (var func in funcs)
            {
                try
                {
                    var result = func(originalBytes, version);
                    if (result != null)
                    {
                        Logger.InfoNewline("Metadata fixup function returned non-null result, using this as fixed metadata.");
                        return result;
                    }
                }
                catch (Exception e)
                {
                    Logger.ErrorNewline($"Metadata fixup function threw an exception: {e}. Ignoring and trying next fixup function, if any...");
                }
            }

            //only get here if every fixup function returns null or throws.
            return null;
        } : null;

        LibLogger.Writer = new LibLogWriter();
    }

    [MemberNotNull(nameof(CurrentAppContext))]
    public static void InitializeLibMC2IL(string assemblyPath, string metadataPath, UnityVersion unityVersion,
        bool allowUserToInputAddresses = false)
    {
        if (IsLibInitialized())
            ResetInternalState();

        ConfigureLib(allowUserToInputAddresses);

#if !DEBUG
        try
#endif
        {
            var context = LibMC2ILMain.LoadFromFileAsContext(assemblyPath, metadataPath, unityVersion);
            OnLibInitialized(context);
        }
#if !DEBUG
        catch (Exception e)
        {
            throw new LibMC2ILInitializationException("Fatal Exception initializing LibMC2IL!", e);
        }
#endif

    }

    [MemberNotNull(nameof(CurrentAppContext))]
    public static void InitializeLibMC2IL(byte[] assemblyData, byte[] metadataData, UnityVersion unityVersion,
        bool allowUserToInputAddresses = false)
    {
        if (IsLibInitialized())
            ResetInternalState();

        ConfigureLib(allowUserToInputAddresses);

        try
        {
            var context = LibMC2ILMain.InitializeAsContext(assemblyData, metadataData, unityVersion);
            OnLibInitialized(context);
        }
        catch (Exception e)
        {
            throw new LibMC2ILInitializationException("Fatal Exception initializing LibMC2IL!", e);
        }
    }

    [MemberNotNull(nameof(CurrentAppContext))]
    private static void OnLibInitialized(LibMC2ILContext libContext)
    {
        var start = DateTime.Now;
        Logger.InfoNewline("Creating application model...");
        CurrentAppContext = new(libContext);
        Logger.InfoNewline($"Application model created in {(DateTime.Now - start).TotalMilliseconds}ms");
    }

    public static void ResetInternalState()
    {
        CurrentAppContext = null;
    }

    private static bool IsLibInitialized()
    {
        return CurrentAppContext != null;
    }
}
