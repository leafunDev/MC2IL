using System;
using System.Collections.Generic;
using System.IO;
using LibMC2IL;

namespace MC2IL.Core.Api;

public abstract class MC2ILPlugin
{
    protected readonly PluginLogger Logger;

    private readonly List<string> temporaryFilePaths = [];

    protected MC2ILPlugin()
    {
        Logger = new(this);
    }

    public string GetTemporaryFilePath()
    {
        var ret = Path.GetTempFileName();
        File.Delete(ret);
        temporaryFilePaths.Add(ret);
        return ret;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }

    public abstract void OnLoad();

    protected void RegisterBinaryFormat<T>(string name, Func<byte[], bool> isValid, Func<MemoryStream, T> factory) where T : Il2CppBinary =>
        LibMC2ILBinaryRegistry.Register(name, Name, isValid, factory);

    protected void RegisterBinaryRegistrationFuncFallbackHandler(Il2CppBinary.RegistrationStructLocationFailureHandler handler) =>
        Il2CppBinary.OnRegistrationStructLocationFailure += handler;

    protected void RegisterMetadataFixupFunc(LibMC2ILMain.MetadataFixupFunc func)
    {
        MC2ILPluginManager.MetadataFixupFuncs ??= [];
        MC2ILPluginManager.MetadataFixupFuncs.Add(func);
    }

    /// <summary>
    /// Attempt to handle the given game path and populate the runtime arguments. For example, unpacking and populating the paths to the binary and metadata in a container format such as an APK.
    /// </summary>
    /// <param name="gamePath">The path provided by the user for their game.</param>
    /// <param name="args">The arguments to populate with the result, if the game can be handled</param>
    /// <returns>True if the path was handled, and the game can be loaded based on the arguments, otherwise false.</returns>
    public virtual bool HandleGamePath(string gamePath, ref MC2ILRuntimeArgs args) => false;

    internal void CallOnFinish()
    {
        //Clean up temporary files
        foreach (var file in temporaryFilePaths)
        {
            File.Delete(file);
        }

        OnFinish();
    }

    /// <summary>
    /// Runs when the application has finished.
    /// </summary>
    protected virtual void OnFinish()
    {
    }
}
