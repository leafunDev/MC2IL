# MC2IL Mirror Serialized Recovery

This plugin exports game scripts to C# and moves Mirror Weaver support code out of gameplay scripts into CSharp/Assembly-CSharp/MirrorSerialization. The release is a single self-contained DLL; its Roslyn dependencies are embedded.

The plugin keeps SyncVar fields and network attributes in gameplay scripts while moving Mirror-generated serializers, handlers, and registration members into partial sidecar files. The generated support members remain available under the same types.

Attributed `[Command]`, `[ClientRpc]`, and `[TargetRpc]` methods keep their Mirror send wrappers and options. When the generated serializer receives a `(T)(&objectLocal)` placeholder, the plugin replaces it only if a unique same-type method parameter and a default `object` local prove the intended value. For a generated `InvokeUserCode_*` method, the plugin pairs its wrapper and `UserCode_*` by name and signature, reads serialized values in order into typed locals, checks the receiver, and calls the paired handler. It also restores dispatchers whose `UserCode_*` call was lost or inlined by the decompiler, but only when the active-state branch, inactive-side error log, reader calls, and any inlined calls match the generated Mirror shape. Ambiguous or nonstandard methods stay untouched. The original gameplay implementation remains in its `UserCode_*` companion in the sidecar.

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
