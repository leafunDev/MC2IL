<p align="center">
  <img src="docs/assets/mc2il-banner.svg" alt="MC2IL — Machine Code to IL, Unity IL2CPP analysis and method recovery" width="100%">
</p>

<p align="center">
  <a href="https://github.com/leafunDev/MC2IL/actions/workflows/dotnet-core.yml"><img src="https://github.com/leafunDev/MC2IL/actions/workflows/dotnet-core.yml/badge.svg?branch=main" alt="Build status"></a>
  <img src="https://img.shields.io/badge/version-1.0.0--preview.1-7468E8?style=flat-square" alt="Version 1.0.0 Preview 1">
  <img src="https://img.shields.io/badge/license-MIT-20A889?style=flat-square" alt="MIT license">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET 10">
</p>

**MC2IL (Machine Code to IL)** analyzes Unity IL2CPP builds and reconstructs managed assemblies with recovered method bodies. It can take a Unity game folder or an Android APK and write DLLs, decompiled C# files, or an instruction-level dump.

MC2IL is an early-stage tool. IL recovery is experimental: generated methods may still contain unresolved native values or incomplete control flow.

## From native instructions to C#

<p align="center">
  <img src="docs/assets/method-recovery.svg" alt="Illustrative ARM64 method recovery flow from native instructions through control-flow blocks to reconstructed C#" width="100%">
</p>

The example is illustrative, not output from a specific game. Actual recovery results and method sizes vary by Unity version, architecture, and native code.

## What it can do

- Read Unity game folders and Android APKs, locating the IL2CPP binary and metadata automatically.
- Parse Unity IL2CPP metadata and native binaries across supported architectures.
- Recover method bodies from 32-bit ARM Android libraries (`armeabi-v7a`). ARMv7 recovery is experimental and currently supports common scalar operations, branches, and calls.
- Produce managed DLLs with recovered method bodies, alongside stub DLLs and analysis dumps.
- Run as a command-line tool on Windows, Linux, and macOS with .NET 10.

## Quick start

Install the .NET 10 SDK, then build the solution:

    dotnet build MC2IL.slnx --configuration Release

Analyze a game APK:

    dotnet run --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --game-path "C:\Games\Example.apk" --output-as dll_il_recovery --output-to mc2il_out

On Windows, Linux, or macOS, launch MC2IL without arguments to open the interactive terminal wizard. It lets you choose an action, select APK/XAPK/APKM/IPA/TIPA packages with a filtered native file picker, browse game and output folders, and set common analysis options. Linux and macOS use their native picker when available and otherwise accept a typed path. The terminal progress display reports the actual method-processing count. Existing command-line arguments remain available.

    dotnet run --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0

For a Windows game, pass the game folder instead:

    dotnet run --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --game-path "C:\Games\Example" --output-as dll_il_recovery --output-to mc2il_out

List all output formats:

    dotnet run --no-build --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --list-output-formats

Choose **Decompile to C# source files** in the wizard, or select `csharp` on the command line:

    dotnet run --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --game-path "C:\Games\Example.apk" --output-as csharp --output-to mc2il_out

This writes `.cs` files under `mc2il_out/CSharp/<assembly>/`. Namespaces use nested folders, for example `Oxide.GUISystem.AnvilGUI` is written to `CSharp/Assembly-CSharp/Oxide/GUISystem/AnvilGUI.cs`. Types in the global namespace are written directly to the assembly folder. MC2IL recovers method bodies and uses the bundled ILSpy engine to generate C#. The intermediate DLLs are temporary. Use a fresh output directory when comparing different game versions, so files from removed types do not carry over. The generated source may need edits before it can compile.

Choose **Export both DLL and C# files** in the wizard or pass `--output-as dll_il_recovery csharp` to keep the DLLs in the output directory as well.

When working on one part of a game, use `--recover-only` with the DLL or C# recovery formats. Enter comma-separated full type names or `Type::Method` names (case sensitive). A type selects all its methods and nested types; a method name selects all overloads. The terminal wizard offers the same option under **Recovery targets**.

    dotnet run --no-build --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --game-path "C:\Games\Example.apk" --output-as csharp --recover-only "Oxide.Anvil::CanPickup,CanvasHelper" --output-to mc2il_focused

Scoped C# output includes only the containing types. Scoped DLLs keep all type and member declarations for reference resolution. Other game method bodies are marked as excluded; select the whole type when examining its constructors, accessors, or generated nested types.

To export only the game scripts, choose **Assembly-CSharp only** in the wizard or add `--csharp-assemblies Assembly-CSharp`. Use a comma-separated list for multiple assemblies, such as `--csharp-assemblies "Assembly-CSharp,MyGame.UI"`. Other assemblies are still available for resolving references. When exporting both formats, the assembly filter limits only the C# files. With `--recover-only`, the C# output contains the requested types within the selected assemblies.

To discover the assembly names in a game without exporting files:

    dotnet run --configuration Release --project MC2IL/MC2IL.csproj --framework net10.0 -- --game-path "C:\Games\Example.apk" --list-assemblies

For source reading, add `--reuse-singleton-instances` to reuse an earlier `MonoSingleton<T>.Instance` result for later reads of the same closed type. This can change behavior if the singleton changes between calls, so the default output preserves both calls.

### Extract strings

Select **Extract strings to a text file** in the wizard or run:

    MC2IL.exe --game-path "C:\Games\Example.apk" --output-as strings --output-to mc2il_strings
    MC2IL.exe --game-path "C:\Games\libil2cpp.so" --output-as strings --output-to mc2il_strings

Both commands write only `strings.txt` in the output directory. A package or game folder includes the IL2CPP string literal table (including empty, short and duplicate text values); a standalone binary needs no metadata. Native scanning looks for printable ASCII/UTF-8 and aligned UTF-16 (ASCII or Unicode text with a byte order mark). It records file offsets and defaults to four characters; use `--strings-min-length 1` to include shorter native runs. Entries made only of control characters and optional whitespace are omitted. Control characters in text are escaped so each entry stays on one line; original table indices and binary offsets are preserved. Strings generated at runtime or stored in compressed or encrypted data are not recovered by the native scan.

## Output formats

| Format | Output |
| --- | --- |
| dll_il_recovery | Managed DLLs with recovered method bodies |
| csharp | Decompiled C# source files with recovered method bodies |
| dll_default | Managed DLLs with default method bodies |
| dll_empty | Managed DLLs with empty method bodies |
| dll_throw_null | Managed DLLs with throw-null method bodies |
| diffable-cs | C#-oriented text output for comparing runs |
| isil | Instruction-set-independent analysis dump |
| strings | IL2CPP literals and native string candidates in `strings.txt` |
| wasmmappings | WebAssembly method mappings |
| wasm_name_section | WebAssembly name section |
| dummydll | Legacy-compatible dummy DLL output |

## Plugins

MC2IL can load third-party plugin DLLs from `Plugins` beside the executable or in the current directory. Use `--plugins-dir` to select another folder. Plugins can add processing layers and output formats through `MC2IL.Core`; see the [plugin guide](docs/PLUGINS.md) and the [Mirror serializer discovery template](PluginTemplates/MirrorSerializerDiscovery).

### Official plugins

- [Mirror Serialized Recovery](https://github.com/leafunDev/MC2IL-MirrorSerializedRecovery/releases)
- [Build Report](Plugins/BuildReport)
- [Control Flow Graph](Plugins/ControlFlowGraph)
- [Mfuscator Support](Plugins/Mfuscator)
- [Orbis PKG Support](Plugins/OrbisPkg)
- [PDB Output](Plugins/Pdb)
- [Stripped Code Registration Support](Plugins/StrippedCodeRegSupport)

## Project map

| Project | Role |
| --- | --- |
| MC2IL | Command-line application |
| MC2IL.Core | Analysis pipeline, method recovery, and output formats |
| MC2IL.ILanguageDecompiler | C# source export from recovered IL using ILSpy |
| LibMC2IL | IL2CPP metadata and native-binary parsing |
| PluginTemplates/* | Starter projects for third-party plugins |
| WasmDisassembler | WebAssembly instruction decoding |
| StableNameDotNet | Stable naming helpers for obfuscated types |

See the [architecture guide](docs/ARCHITECTURE.md) for the processing flow and where to make changes.

## Contributing

Contributions come in through pull requests against `main`. The maintainer reviews each proposal and merges accepted changes. See the [contribution guide](CONTRIBUTING.md), [Code of Conduct](CODE_OF_CONDUCT.md), [support guide](SUPPORT.md), [GitHub setup checklist](docs/GITHUB_SETUP.md), and [contributors list](CONTRIBUTORS.md).

## Development direction

- Improve ARM64 method-body recovery.
- Recover string literals, fields, and branch conditions more accurately.
- Add repeatable regression examples from real Unity APKs.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local build and contribution workflow. For bug reports, feature requests, and usage questions, follow [SUPPORT.md](SUPPORT.md) and use the matching [GitHub issue template](https://github.com/leafunDev/MC2IL/issues/new/choose).

For security reports, follow [SECURITY.md](SECURITY.md).

## License and notices

MC2IL is distributed under the MIT license. Required copyright and origin notices are retained in [LICENSE](LICENSE) and [NOTICE](NOTICE).
