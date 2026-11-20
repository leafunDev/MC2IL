using System;
using MC2IL.Core;
using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Core.InstructionSets;
using MC2IL.Core.OutputFormats;
using MC2IL.Core.ProcessingLayers;
//Need this for the assembly attribute definition below.
using LibMC2IL;

[assembly: RegisterMC2ILPlugin(typeof(MC2ILCorePlugin))]

namespace MC2IL.Core;

public class MC2ILCorePlugin : MC2ILPlugin
{
    public override string Name => "MC2IL Built-In";

    public override string Description => "Core MC2IL plugin containing built-in instruction sets, binaries, and other core functionality.";

    public override void OnLoad()
    {
        Logger.VerboseNewline("Initializing...", "Core Plugin");
        var start = DateTime.Now;

        Logger.VerboseNewline("\tRegistering built-in instruction set handlers...", "Core Plugin");

        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
        InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM);
        InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7);

        InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);

        Logger.VerboseNewline("\tRegistering built-in binary parsers...", "Core Plugin");

        LibMC2ILBinaryRegistry.RegisterBuiltInBinarySupport();

        Logger.VerboseNewline("\tRegistering built-in output formats...", "Core Plugin");

        OutputFormatRegistry.Register<AsmResolverDllOutputFormatLegacy>();
        OutputFormatRegistry.Register<AsmResolverDllOutputFormatDefault>();
        OutputFormatRegistry.Register<AsmResolverDllOutputFormatEmpty>();
        OutputFormatRegistry.Register<AsmResolverDllOutputFormatThrowNull>();
        OutputFormatRegistry.Register<AsmResolverDllOutputFormatIlRecovery>();
        OutputFormatRegistry.Register<DiffableCsOutputFormat>();
        OutputFormatRegistry.Register<IsilDumpOutputFormat>();
        OutputFormatRegistry.Register<StringTableOutputFormat>();
        OutputFormatRegistry.Register<WasmMappingOutputFormat>();
        OutputFormatRegistry.Register<WasmNameSectionOutputFormat>();

        Logger.VerboseNewline("\tRegistering built-in processing layers", "Core Plugin");

        ProcessingLayerRegistry.Register<AttributeAnalysisProcessingLayer>();
        ProcessingLayerRegistry.Register<AttributeInjectorProcessingLayer>();
        ProcessingLayerRegistry.Register<CallAnalysisProcessingLayer>();
        ProcessingLayerRegistry.Register<NativeMethodDetectionProcessingLayer>();
        ProcessingLayerRegistry.Register<StableRenamingProcessingLayer>();
        ProcessingLayerRegistry.Register<DeobfuscationMapProcessingLayer>();

        var elapsed = DateTime.Now - start;
        Logger.VerboseNewline($"Core plugin loaded in {elapsed.Ticks} ticks ({elapsed.TotalMilliseconds}ms)", "Core Plugin");
    }

    private sealed class AsmResolverDllOutputFormatLegacy : AsmResolverDllOutputFormatDefault
    {
        public override string OutputFormatId => "dummydll";
        public override string OutputFormatName => "DLL output format for backwards compatibility.";
    }
}
