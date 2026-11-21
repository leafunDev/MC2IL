using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using AsmResolver.DotNet;
using AssetRipper.CIL;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
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
    private readonly string[] _assemblyNames;

    public CSharpOutputFormat() : this(keepDlls: false) { }

    public CSharpOutputFormat(bool keepDlls) : this(keepDlls, assemblyNames: null) { }

    public CSharpOutputFormat(bool keepDlls, IEnumerable<string>? assemblyNames)
    {
        _keepDlls = keepDlls;
        _assemblyNames = assemblyNames?.Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? Array.Empty<string>();
        if (_assemblyNames.Any(string.IsNullOrEmpty))
            throw new AssemblySelectionException("Assembly names must not be empty.");
    }

    public const string FormatId = "csharp";
    public override string OutputFormatId => FormatId;
    public override string OutputFormatName => _keepDlls
        ? "DLL and C# source files with recovered method bodies"
        : "C# source files with recovered method bodies";

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        // Resolve before creating output or recovering methods, so typos never trigger an unfiltered export.
        var selectedAssemblies = ResolveSelectedAssemblies(context);
        var selectedFiles = selectedAssemblies == null
            ? null
            : new HashSet<string>(selectedAssemblies.Select(assembly => assembly.CleanAssemblyName + ".dll"), StringComparer.Ordinal);
        var scope = context.RecoveryScope;
        if (selectedFiles != null && scope != null && !selectedFiles.Any(file => scope.TopLevelTypesIn(file).Any()))
            throw new AssemblySelectionException("None of the --recover-only targets belong to the selected C# assemblies.");

        var sourceRoot = Path.Combine(outputRoot, "CSharp");
        Directory.CreateDirectory(sourceRoot);

        // Keep all recovered assemblies together so ILSpy can resolve game and Unity references.
        // Do not use DLLs that may already exist in the user's output directory.
        var recoveryDirectory = Path.Combine(Path.GetTempPath(), $"mc2il-csharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recoveryDirectory);

        try
        {
            var recovery = selectedAssemblies != null && !_keepDlls
                ? new ScopedIlRecovery(selectedAssemblies)
                : new AsmResolverDllOutputFormatIlRecovery();
            recovery.DoOutput(context, recoveryDirectory);
            var assemblies = Directory.GetFiles(recoveryDirectory, "*.dll");
            Array.Sort(assemblies, StringComparer.Ordinal);
            if (_keepDlls)
                foreach (var path in assemblies)
                    File.Copy(path, Path.Combine(outputRoot, Path.GetFileName(path)), overwrite: true);

            var sourceAssemblies = assemblies
                .Where(path => selectedFiles == null || selectedFiles.Contains(Path.GetFileName(path)))
                .Select(path => (Path: path, Types: scope?.TopLevelTypesIn(Path.GetFileName(path)).ToArray()))
                .Where(assembly => assembly.Types is not { Length: 0 })
                .ToArray();

            var usedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sourceAssemblies.Length; i++)
            {
                var (path, selectedTypes) = sourceAssemblies[i];
                var name = Path.GetFileNameWithoutExtension(path);
                Logger.InfoNewline($"Writing C# for {name} ({i + 1}/{sourceAssemblies.Length})", "CSharpOutput");

                var directoryName = WholeProjectDecompiler.CleanUpFileName(name, null);
                var uniqueName = directoryName;
                for (var suffix = 2; !usedDirectories.Add(uniqueName); suffix++)
                    uniqueName = $"{directoryName}_{suffix}";
                WriteAssembly(path, Path.Combine(sourceRoot, uniqueName), selectedTypes);
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

    private HashSet<AssemblyAnalysisContext>? ResolveSelectedAssemblies(ApplicationAnalysisContext context)
    {
        if (_assemblyNames.Length == 0)
            return null;

        var selected = new HashSet<AssemblyAnalysisContext>();
        foreach (var name in _assemblyNames)
        {
            var matches = context.Assemblies.Where(assembly => string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0 && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                matches = context.Assemblies.Where(assembly =>
                    string.Equals(assembly.Name, name.Substring(0, name.Length - 4), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                var problem = matches.Length == 0 ? "was not found" : "is ambiguous";
                throw new AssemblySelectionException($"Assembly '{name}' {problem}. Run --list-assemblies with the same game input to see its assembly names.");
            }

            selected.Add(matches[0]);
        }
        return selected;
    }

    private static void WriteAssembly(string path, string outputDirectory, string[]? selectedTypes)
    {
        using var module = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
        var resolver = new UniversalAssemblyResolver(path, throwOnError: false,
            module.DetectTargetFrameworkId(), module.DetectRuntimePack(), PEStreamOptions.PrefetchMetadata);
        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            UseNestedDirectoriesForNamespaces = true,
        };
        Directory.CreateDirectory(outputDirectory);

        if (selectedTypes != null)
        {
            var remaining = new HashSet<string>(selectedTypes, StringComparer.Ordinal);
            var source = new CSharpDecompiler(module, resolver, settings);
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var metadata = module.Metadata;
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if (!type.GetDeclaringType().IsNil)
                    continue;
                var ns = metadata.GetString(type.Namespace);
                var name = metadata.GetString(type.Name);
                var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (!remaining.Remove(fullName))
                    continue;

                var relativePath = WholeProjectDecompiler.GetFileNameForType(ns, name, ".cs",
                    settings.UseNestedDirectoriesForNamespaces);
                var directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(relativePath);
                var uniquePath = relativePath;
                for (var suffix = 2; !usedPaths.Add(uniquePath); suffix++)
                    uniquePath = Path.Combine(directory, $"{stem}_{suffix}.cs");

                var text = source.DecompileTypesAsString(new[] { handle });
                Directory.CreateDirectory(Path.Combine(outputDirectory, directory));
                File.WriteAllText(Path.Combine(outputDirectory, uniquePath),
                    "// Scoped export: only selected method bodies were recovered." + Environment.NewLine + text);
            }

            foreach (var type in remaining.OrderBy(type => type, StringComparer.Ordinal))
                Logger.WarnNewline($"Selected type {type} was not found in {module.Name}.", "CSharpOutput");
            return;
        }

        var decompiler = new SourceProjectDecompiler(settings, resolver);
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

    private sealed class ScopedIlRecovery(HashSet<AssemblyAnalysisContext> selectedAssemblies) : AsmResolverDllOutputFormatIlRecovery
    {
        protected override void FillMethodBody(MethodDefinition definition, MethodAnalysisContext method)
        {
            if (method.DeclaringType is { } type && selectedAssemblies.Contains(type.DeclaringAssembly))
                base.FillMethodBody(definition, method);
            else if (definition.IsManagedMethodWithBody())
                FillMethodBodyWithStub(definition);
        }
    }

    public sealed class AssemblySelectionException(string message) : Exception(message);
}
