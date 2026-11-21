# MC2IL Mirror Serialized Recovery

This plugin exports game scripts to C# and moves Mirror Weaver support code out of gameplay scripts into CSharp/Assembly-CSharp/MirrorSerialization.

The moved declarations remain members of the same types through partial classes. Their bodies, attributes, and registrations are retained so SyncVar replication and Command/RPC dispatch continue to work. The plugin does not remove SyncVar fields, serializers, readers/writers, command/RPC entry points, or generated handlers.

## Build

    dotnet build .\MC2IL.MirrorSerializedRecovery.csproj -c Release

The project copies the plugin and its private Roslyn dependencies into the Plugins directory at the root of this checkout.

## Run

    MC2IL.exe --game-path "C:\Games\Oxide.apk" --output-as mirror-serialized-recovery --output-to "C:\Games\Oxide-Mirror-clean"

The plugin prefers Assembly-CSharp. If that assembly is absent, it exports assemblies containing Mirror.NetworkBehaviour subclasses. Generated Mirror support members are placed in the MirrorSerialization subdirectory.

## What moves

- Empty MirrorProcessed() markers.
- SerializeSyncVars and DeserializeSyncVars overrides.
- Network<Field> properties corresponding to SyncVar fields.
- Mirror-generated UserCode_* and InvokeUserCode_* methods.
- Static constructors that register RemoteCallHelper command/RPC delegates.

The generated source declarations are relocated into partial classes. A SyncVar setter is rewritten from a this-plus-offset IL2CPP access to the matching field only when the SyncVar attribute, field type, and pointer-dereference shape all agree. Mirror.GeneratedNetworkCode.cs and ordinary gameplay Command/RPC methods remain in place.
