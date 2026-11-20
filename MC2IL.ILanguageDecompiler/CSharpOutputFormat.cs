using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using MC2IL.Core.Api;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.OutputFormats;

namespace MC2IL.ILanguageDecompiler;

public sealed class CSharpOutputFormat : MC2ILOutputFormat
{
    private readonly bool _keepDlls;

    public CSharpOutputFormat() : this(keepDlls: false) { }

    public CSharpOutputFormat(bool keepDlls) => _keepDlls = keepDlls;

    public const string FormatId = "csharp";
    public override string OutputFormatId => FormatId;
    public override string OutputFormatName => _keepDlls
        ? "DLL and C# source files with recovered method bodies"
        : "C# source files with recovered method bodies";

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        var sourceRoot = Path.Combine(outputRoot, "CSharp");
        Directory.CreateDirectory(sourceRoot);

        // Keep all recovered assemblies together so ILSpy can resolve game and Unity references.
        // Do not use DLLs that may already exist in the user's output directory.
        var recoveryDirectory = Path.Combine(Path.GetTempPath(), $"mc2il-csharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recoveryDirectory);

        try
        {
            new AsmResolverDllOutputFormatIlRecovery().DoOutput(context, recoveryDirectory);
            var assemblies = Directory.GetFiles(recoveryDirectory, "*.dll");
            Array.Sort(assemblies, StringComparer.Ordinal);
            if (_keepDlls)
                foreach (var path in assemblies)
                    File.Copy(path, Path.Combine(outputRoot, Path.GetFileName(path)), overwrite: true);

            var usedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < assemblies.Length; i++)
            {
                var path = assemblies[i];
                var name = Path.GetFileNameWithoutExtension(path);
                Logger.InfoNewline($"Writing C# for {name} ({i + 1}/{assemblies.Length})", "CSharpOutput");

                var directoryName = WholeProjectDecompiler.CleanUpFileName(name, null);
                var uniqueName = directoryName;
                for (var suffix = 2; !usedDirectories.Add(uniqueName); suffix++)
                    uniqueName = $"{directoryName}_{suffix}";
                WriteAssembly(path, Path.Combine(sourceRoot, uniqueName));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(recoveryDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.WarnNewline($"Could not remove temporary DLLs from {recoveryDirectory}: {ex.Message}", "CSharpOutput");
            }
        }
    }

    private static void WriteAssembly(string path, string outputDirectory)
    {
        using var module = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
        var resolver = new UniversalAssemblyResolver(path, throwOnError: false,
            module.DetectTargetFrameworkId(), module.DetectRuntimePack(), PEStreamOptions.PrefetchMetadata);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new SourceProjectDecompiler(settings, resolver);

        Directory.CreateDirectory(outputDirectory);
        decompiler.DecompileProject(module, outputDirectory, TextWriter.Null);

        if (decompiler.Errors.Count > 0)
        {
            Logger.WarnNewline($"{module.Name}: {decompiler.Errors.Count} C# export errors. See the generated files or use --verbose for details.", "CSharpOutput");
            foreach (var error in decompiler.Errors)
                Logger.VerboseNewline(error.ToString(), "CSharpOutput");
        }
    }

    // Reuse ILSpy's type grouping and file naming while exporting only C#.
    private sealed class SourceProjectDecompiler(DecompilerSettings settings, UniversalAssemblyResolver resolver)
        : WholeProjectDecompiler(settings, resolver, projectWriter: null, assemblyReferenceClassifier: resolver, debugInfoProvider: null)
    {
        protected override IEnumerable<ProjectItemInfo> WriteResourceFilesInProject(MetadataFile module) => Enumerable.Empty<ProjectItemInfo>();

        protected override IEnumerable<ProjectItemInfo> WriteMiscellaneousFilesInProject(PEFile module) => Enumerable.Empty<ProjectItemInfo>();
    }
}
