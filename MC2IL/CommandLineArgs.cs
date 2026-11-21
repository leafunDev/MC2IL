using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommandLine;

namespace MC2IL;

[SuppressMessage("ReSharper", "NotNullMemberIsNotInitialized")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class CommandLineArgs
{
    [Option("plugins-dir", HelpText = "Directory containing trusted MC2IL plugin DLLs. Defaults to Plugins beside MC2IL and in the current directory.")]
    public string? PluginsDirectory { get; set; }

    [Option("game-path", HelpText = "Specify path to the game folder (containing the exe)")]
    public string? GamePath { get; set; } = null!; //Suppressed because it's set by CommandLineParser.

    [Option("exe-name", HelpText = "Specify an override for the unity executable name in case the auto-detection doesn't work.")]
    public string? ExeName { get; set; }

    [Option("android-abi", HelpText = "Select the native IL2CPP binary in an APK, XAPK or APKM: armeabi-v7a, arm64-v8a, x86 or x86_64.")]
    public string? AndroidAbi { get; set; }

    //Force options

    [Option("force-binary-path", HelpText = "Force the path to the il2cpp binary. Don't use unless you know what you're doing, and use in conjunction with the other force options.")]
    public string? ForcedBinaryPath { get; set; }

    [Option("force-metadata-path", HelpText = "Force the path to the il2cpp metadata file. Don't use unless you know what you're doing, and use in conjunction with the other force options.")]
    public string? ForcedMetadataPath { get; set; }

    [Option("force-unity-version", HelpText = "Override the unity version detection. Don't use unless you know what you're doing, and use in conjunction with the other force options.")]
    public string? ForcedUnityVersion { get; set; }

    //Processor options

    [Option("list-processors", HelpText = "List the available processing layers and exit.")]
    public bool ListProcessors { get; set; }

    [Option("use-processor", HelpText = "Specify the ID of a processing layer to use. This argument can contain a comma-separated list of processing layers, in which case they will be executed in the order they are specified.", Separator = ',')]
    public IEnumerable<string> ProcessorsToUse { get; set; } = new List<string>();

    [Option("processor-config", HelpText = "Specify a configuration option for one of the processors you have selected to use, in the format key=value. Multiple key-value pairs can be separated using the backtick character (`). The configuration options are used as needed by the selected processors.", Separator = '`')]
    public IEnumerable<string> ProcessorConfigOptions { get; set; } = new List<string>();

    //Output options

    [Option("list-output-formats", HelpText = "List the available output formats and exit.")]
    public bool ListOutputFormats { get; set; }

    [Option("list-assemblies", HelpText = "List the assemblies in the specified game and exit without exporting.")]
    public bool ListAssemblies { get; set; }

    [Option("output-as", HelpText = "Specify the ID of the output format you wish to use.")]
    public IEnumerable<string> OutputFormatIds { get; set; } = new List<string>();

    [Option("recover-only", HelpText = "Recover only the specified types or methods (Full.Type or Full.Type::Method; comma-separated). Applies to csharp and dll_il_recovery.", Separator = ',')]
    public IEnumerable<string> RecoverySelectors { get; set; } = new List<string>();

    [Option("csharp-assemblies", HelpText = "Limit C# output to a comma-separated list of assembly names. Use with --output-as csharp.")]
    public string? CSharpAssemblies { get; set; }

    [Option("strings-min-length", HelpText = "Minimum character count for native strings (default: 4). This limit does not apply to IL2CPP literals.")]
    public int? StringsMinimumLength { get; set; }

    [Option("output-to", HelpText = "Root directory to output to. Defaults to mc2il_out in the current working directory.")]
    public string OutputRootDir { get; set; } = Path.GetFullPath("mc2il_out");

    //Flags

    [Option("verbose", HelpText = "Enable Verbose Logging.")]
    public bool Verbose { get; set; }

    [Option("low-memory-mode", HelpText = "Enable Low Memory Mode. This will attempt to reduce memory usage at the cost of performance.")]
    public bool LowMemoryMode { get; set; }

    [Option("reuse-singleton-instances", HelpText = "Reuse an earlier MonoSingleton.Instance result in readable output. This may change behavior if the singleton changes between reads.")]
    public bool ReuseSingletonInstances { get; set; }

    [Option("wasm-framework-file", HelpText = "Path to the wasm *.framework.js file. Only needed if your binary is a WASM file. If provided, it can be used to remap obfuscated dynCall function names in order to correct method pointers.")]
    public string? WasmFrameworkFilePath { get; set; }

    internal bool AreForceOptionsValid
    {
        get
        {
            if (ForcedBinaryPath != null && ForcedMetadataPath != null && ForcedUnityVersion != null)
                return true;
            if (ForcedBinaryPath == null && ForcedMetadataPath == null && ForcedUnityVersion == null)
                return true;

            return false;
        }
    }
}
