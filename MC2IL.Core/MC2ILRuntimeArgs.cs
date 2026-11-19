using System.Collections.Generic;
using AssetRipper.Primitives;
using MC2IL.Core.Api;

namespace MC2IL.Core;

public class MC2ILRuntimeArgs
{
    //To determine easily if this struct is the default one or not.
    public bool Valid;

    //Core variables
    public UnityVersion UnityVersion;
    public string PathToAssembly = null!;
    public string PathToMetadata = null!;

    public string? WasmFrameworkJsFile;

    public List<MC2ILProcessingLayer> ProcessingLayersToRun = [];
    public readonly Dictionary<string, string> ProcessingLayerConfigurationOptions = new();

    public IEnumerable<MC2ILOutputFormat>? OutputFormats;
    public string OutputRootDirectory = null!;

    public bool LowMemoryMode;
    public bool ReuseSingletonInstances;
}
