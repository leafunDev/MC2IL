# MC2IL Mirror Serialized Recovery

This plugin exports game scripts to C# and moves Mirror Weaver support code out of gameplay scripts into CSharp/Assembly-CSharp/MirrorSerialization. The release is a single self-contained DLL; its Roslyn dependencies are embedded.

The moved declarations remain members of the same types through partial classes. Their bodies, attributes, and registrations are retained so SyncVar replication and Command/RPC dispatch continue to work. The plugin does not remove SyncVar fields, serializers, readers/writers, command/RPC entry points, or generated handlers.

## Build

    dotnet build .\MC2IL.MirrorSerializedRecovery.csproj -c Release

The project copies only `MC2IL.MirrorSerializedRecovery.dll` into the Plugins directory at the root of this checkout.

## Run

    MC2IL.exe --plugins-dir "C:\MC2IL\Plugins" --game-path "C:\Games\Oxide.apk" --output-as mirror-serialized-recovery --output-to "C:\Games\Oxide-Mirror-clean"

To confirm that MC2IL loaded the plugin, list the registered output formats:

    MC2IL.exe --plugins-dir "C:\MC2IL\Plugins" --list-output-formats

The list should include `mirror-serialized-recovery`. Copying the single plugin DLL into the default Plugins directory beside MC2IL also works; `--plugins-dir` is only needed for a custom location.

The plugin prefers Assembly-CSharp. If that assembly is absent, it exports assemblies containing Mirror.NetworkBehaviour subclasses. Generated Mirror support members are placed in the MirrorSerialization subdirectory.

## What moves

- Empty MirrorProcessed() markers.
- SerializeSyncVars and DeserializeSyncVars overrides.
- Network<Field> properties corresponding to SyncVar fields.
- Mirror-generated UserCode_* and InvokeUserCode_* methods.
- Static constructors that register RemoteCallHelper command/RPC delegates.

The generated source declarations are relocated into partial classes. A SyncVar setter is rewritten from a this-plus-offset IL2CPP access to the matching field only when the SyncVar attribute, field type, and pointer-dereference shape all agree. Mirror.GeneratedNetworkCode.cs and ordinary gameplay Command/RPC methods remain in place.

## Third-party notices

The single-file plugin embeds Roslyn assemblies and the Costura assembly loader. Their MIT license notices are available in the [Roslyn license](https://github.com/dotnet/roslyn/blob/main/License.txt) and the [Costura license](https://github.com/Fody/Costura/blob/develop/LICENSE).
