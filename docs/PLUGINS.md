# Writing MC2IL plugins

MC2IL loads trusted plugin assemblies from a `Plugins` directory at startup. A plugin is a class library that references `MC2IL.Core`, registers one or more classes with `RegisterMC2ILPluginAttribute`, and derives from `MC2ILPlugin`.

## Quick start

Start from [`PluginTemplates/MirrorSerializerDiscovery`](../PluginTemplates/MirrorSerializerDiscovery). It builds inside the MC2IL source checkout and switches to the `MC2IL.Core` package if you copy the template elsewhere. The example registers a processing layer that finds likely Mirror serialization methods and reports them without changing the recovered output.

Build the sample DLL:

```powershell
dotnet build PluginTemplates/MirrorSerializerDiscovery/MirrorSerializerDiscovery.csproj -c Release
```

Copy only `MC2IL.MirrorSerializerDiscovery.dll` from its `bin/Release/netstandard2.0` directory into the `Plugins` folder beside `MC2IL.exe`. If your plugin uses private dependencies, copy those DLLs beside it too. Do not copy `MC2IL.Core.dll`, `LibMC2IL.dll`, `StableNameDotNet.dll`, `WasmDisassembler.dll`, or the generated `.deps.json`; MC2IL supplies its own copies.

By default MC2IL scans `Plugins` beside the executable and `Plugins` under the current working directory. Override this with `--plugins-dir`:

```powershell
MC2IL.exe --plugins-dir "C:\MC2IL-plugins" --list-processors
```

The sample processing layer is selected with its ID:

```powershell
MC2IL.exe --plugins-dir "C:\MC2IL-plugins" --game-path "C:\Games\Example.apk" --use-processor mirror-serializer-discovery --output-as dll_il_recovery --output-to mc2il_out
```

Use `--list-processors` to check that MC2IL loaded the plugin and registered its processing layer.
A full Mirror source export is available in PluginTemplates/MirrorSerializedRecovery. It separates Weaver-generated SyncVar serialization, empty markers, and RPC dispatch into partial sidecar files while leaving the regular Assembly-CSharp scripts readable. The generated network members stay available under CSharp/Assembly-CSharp/MirrorSerialization. Select the format with --output-as mirror-serialized-recovery.

## Plugin shape

```csharp
[assembly: RegisterMC2ILPlugin(typeof(MyPlugin))]

public sealed class MyPlugin : MC2ILPlugin
{
    public override string Name => "My plugin";
    public override string Description => "Game-specific recovery helpers.";

    public override void OnLoad()
    {
        RegisterProcessingLayer<MyProcessingLayer>();
        // Also available: RegisterOutputFormat<T>(), RegisterInstructionSet<T>(id),
        // RegisterMetadataFixupFunc(...) and binary-format registration helpers.
    }
}
```

The registration attribute must be at assembly scope, and the plugin class must have a public parameterless constructor. A processing layer is a good fit for game-specific transformations: it receives `ApplicationAnalysisContext` after metadata/model initialization and before output. It can inspect `Assemblies`, `Types`, and `Methods`; call `MethodAnalysisContext.Analyze()` only for candidate methods to obtain their recovered `ControlFlowGraph`; then make a narrowly-scoped edit before selecting `dll_il_recovery` or `csharp` output. Processing layers run only when selected with `--use-processor`.

Use `ApplicationAnalysisContext.PutExtraData<T>` and `GetExtraData<T>` to keep plugin state attached to one analysis run instead of storing it in static fields. `appContext.UnityVersion`, `MetadataVersion`, `Binary`, and `Metadata` expose the input information used by version-specific rules.

## Target framework and dependencies

Target `netstandard2.0` for plugins that should work with both supported MC2IL hosts (`net10.0` and `net472`). Reference the `MC2IL.Core` NuGet package when building outside the repository, and do not deploy a private copy of `MC2IL.Core.dll` or `LibMC2IL.dll`; the host supplies those shared API assemblies. Plugin-specific dependencies can live beside the plugin DLL and are resolved from that directory.

Plugins run in-process with the same permissions as MC2IL. They are not sandboxed. Install only DLLs you trust. A broken plugin assembly or a failing plugin callback is logged without preventing other plugin assemblies from loading; a failing selected processing layer still fails that analysis run so partially applied transformations are not silently exported.

## Available extension points

- `MC2ILPlugin.OnLoad()` registers extensions at startup; `OnFinish()` is available for cleanup.
- `MC2ILProcessingLayer` inspects or transforms the loaded analysis context.
- `MC2ILOutputFormat` writes a custom output.
- `MC2ILInstructionSet` adds architecture-specific instruction interpretation.
- `HandleGamePath` and metadata-fixup/binary registration helpers support custom inputs.

These APIs are part of `MC2IL.Core`. Keep plugin-specific registrations and processor IDs unique so users can select them unambiguously.
