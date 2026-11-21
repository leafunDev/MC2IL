# MC2IL.Core

`MC2IL.Core` contains the analysis model and method-recovery pipeline used by the [MC2IL command-line application](../README.md). The public API is evolving while recovery support is developed.

## Advanced initialization

Most users should run MC2IL through its CLI. For integrations that need the core API directly, `MC2ILApi.InitializeLibMC2IL` accepts either file paths or byte arrays and creates the current application context:

```csharp
using AssetRipper.Primitives;
using MC2IL.Core;
using MC2IL.Core.Model.Contexts;

var unityVersion = new UnityVersion(2019, 4, 34, UnityVersionType.Final, 1);

MC2ILApi.Init();
MC2ILApi.InitializeLibMC2IL(gameAssemblyPath, metadataPath, unityVersion);

ApplicationAnalysisContext context = MC2ILApi.CurrentAppContext;
```

For in-memory inputs, use the overload that accepts the binary and metadata byte arrays. `MC2ILApi.DetermineUnityVersion` can help identify the Unity version from a game executable and data directory.

To write an installable plugin DLL, see the [plugin development guide](../docs/PLUGINS.md) and its [Mirror serializer discovery template](../PluginTemplates/MirrorSerializerDiscovery). Plugins reference `MC2IL.Core`, register a class with `RegisterMC2ILPluginAttribute`, and can add processing layers or output formats from `OnLoad`.

The core project builds with the solution from the repository root:

```sh
dotnet build MC2IL.slnx --configuration Release
```

See the [architecture guide](../docs/ARCHITECTURE.md) for the pipeline and project boundaries. Recovery results are experimental and depend on the binary, Unity version, architecture, and available metadata.
