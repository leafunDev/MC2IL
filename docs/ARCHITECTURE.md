# Architecture

MC2IL takes IL2CPP metadata and a native game binary, analyzes their relationship, and writes managed assemblies or analysis output. Method-body recovery is experimental; the amount of recoverable information depends on the input build.

## Processing flow

```mermaid
flowchart LR
    Game[Unity folder or APK] --> CLI[MC2IL command-line app]
    CLI --> Parse[LibMC2IL context]
    Binary[Native binary and metadata] --> Parse
    Parse --> Core[MC2IL.Core analysis and recovery]
    Core --> Output[Output format or plugin]
    Output --> Result[Managed DLLs or analysis files]
```

The CLI discovers the IL2CPP binary and metadata from supported inputs. `LibMC2IL` parses them into a per-game context. `MC2IL.Core` uses metadata, native code, and architecture-specific decoding to reconstruct method bodies and prepare output. Output formats serialize the resulting model; optional plugins add formats or input handling.

## Projects

| Project | Responsibility |
| --- | --- |
| `MC2IL` | Command-line options, application startup, and output selection |
| `MC2IL.Core` | Analysis pipeline, method-body recovery, and shared output model |
| `LibMC2IL` | IL2CPP metadata, native binary parsing, and per-game contexts |
| `Plugins/*` | Optional input handlers, analysis extensions, and output formats |
| `WasmDisassembler` | WebAssembly instruction decoding support |
| `StableNameDotNet` | Stable names for generated and obfuscated types |
| `LibMC2ILTests`, `MC2IL.Core.Tests` | Unit and regression tests |

## Where to make a change

- Change command-line behavior in `MC2IL` and keep its documentation in the root README current.
- Change metadata or native-binary parsing in `LibMC2IL`; prefer context-based APIs for state associated with a game.
- Change instruction analysis or recovered method bodies in `MC2IL.Core` and include a focused regression case when possible.
- Add an output format or input extension in the relevant plugin project, following the existing plugin structure.

Test fixtures live under `TestFiles/`. Only add files that can be redistributed. Never add proprietary game binaries or metadata without permission; see [CONTRIBUTING.md](../CONTRIBUTING.md).

## ARMv7 stack transfers

The ARMv7 lifter expands `LDM` and `STM` with `sp` as the base into four-byte ISIL stack loads and stores. IA, IB, DA, and DB addressing modes place registers in ascending address order. Writeback changes the tracked stack only after all transfers; conditional instructions use the existing predicate handling. This preserves outgoing stack arguments that compilers pack into a single native instruction. Both ARM and Thumb decoding feed this path.

Transfers through other base registers remain explicitly unsupported until packed value-type field recovery can preserve their managed layout. User-mode register banks and non-alias transfers involving `pc` or `sp` in the register list also retain a diagnostic. Existing `PUSH` and `POP` aliases keep their dedicated handling.

`ArmV7BlockTransferTests` uses synthetic machine instructions to check addressing modes, widths, stack writeback, predicates, Thumb decoding, and unsupported cases without requiring a game binary.
