using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;
using MC2IL.ILanguageDecompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

[assembly: RegisterMC2ILPlugin(typeof(MC2IL.MirrorSerializedRecovery.MirrorSerializedRecoveryPlugin))]

namespace MC2IL.MirrorSerializedRecovery;

public sealed class MirrorSerializedRecoveryPlugin : MC2ILPlugin
{
    public override string Name => "Mirror serialized recovery";

    public override string Description =>
        "Exports game scripts while separating Mirror-generated networking and serialization support into partial files.";

    public override void OnLoad() => RegisterOutputFormat<MirrorSerializedRecoveryOutputFormat>();
}

public sealed class MirrorSerializedRecoveryOutputFormat : MC2ILOutputFormat
{
    public const string Id = "mirror-serialized-recovery";

    public override string OutputFormatId => Id;

    public override string OutputFormatName => "C# with Mirror serialization support separated";

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        var targetAssemblies = ResolveTargetAssemblies(context);
        Logger.InfoNewline($"Exporting {string.Join(", ", targetAssemblies)} with Mirror support separated.", "MirrorRecovery");

        var stagingRoot = Path.Combine(Path.GetTempPath(), "mc2il-mirror-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            new CSharpOutputFormat(keepDlls: false, targetAssemblies).DoOutput(context, stagingRoot);

            var generatedRoot = Path.Combine(stagingRoot, "CSharp");
            if (!Directory.Exists(generatedRoot))
                throw new DirectoryNotFoundException($"C# output was not created at {generatedRoot}.");

            var result = MirrorSerializationSourceCleaner.CleanDirectory(generatedRoot);
            Logger.InfoNewline(
                $"Parsed {result.FilesParsed} C# files; moved {result.MembersMoved} Mirror-generated members from {result.TypesMadePartial} types into {result.SidecarFiles} sidecar files. Restored {result.NetworkMethodBodiesRestored} original Command/RPC method bodies and {result.NetworkArgumentsRestored} Mirror-generated pointer arguments. Restored {result.SyncVarAccessorsRewritten} SyncVar accessors from IL2CPP pointer offsets. {result.FilesSkipped} files were left untouched.",
                "MirrorRecovery");

            var finalCSharpRoot = Path.Combine(outputRoot, "CSharp");
            RemovePreviousSidecars(finalCSharpRoot);
            CopyDirectory(generatedRoot, finalCSharpRoot);
        }
        finally
        {
            try
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.WarnNewline($"Could not remove temporary recovery output {stagingRoot}: {e.Message}", "MirrorRecovery");
            }
        }
    }

    private static string[] ResolveTargetAssemblies(ApplicationAnalysisContext context)
    {
        var gameAssembly = context.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase));
        if (gameAssembly != null)
            return new[] { gameAssembly.Name };

        var networkAssemblies = context.Assemblies
            .Where(assembly => assembly.Types.Any(IsMirrorNetworkBehaviour))
            .Select(assembly => assembly.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (networkAssemblies.Length > 0)
            return networkAssemblies;

        throw new InvalidOperationException(
            "Could not find Assembly-CSharp or any assembly containing a Mirror.NetworkBehaviour subclass.");
    }

    private static bool IsMirrorNetworkBehaviour(TypeAnalysisContext type)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        TypeAnalysisContext? current = type;
        while (current != null && seen.Add(current))
        {
            if (string.Equals(current.FullName, "Mirror.NetworkBehaviour", StringComparison.Ordinal))
                return true;

            var baseType = current.BaseType;
            if (baseType == null)
                return false;
            if (string.Equals(baseType.FullName, "Mirror.NetworkBehaviour", StringComparison.Ordinal))
                return true;

            current = baseType is GenericInstanceTypeAnalysisContext generic
                ? generic.GenericType
                : baseType;
        }

        return false;
    }

    private static void RemovePreviousSidecars(string csharpRoot)
    {
        if (!Directory.Exists(csharpRoot))
            return;

        foreach (var marker in Directory.GetFiles(csharpRoot, ".mc2il-mirror-serialization-owned", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(marker);
            if (directory == null)
                continue;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.WarnNewline($"Could not clear previous Mirror sidecars at {directory}: {e.Message}", "MirrorRecovery");
            }
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = directory.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destination = Path.Combine(destinationRoot, relative);
            var parent = Path.GetDirectoryName(destination);
            if (parent != null)
                Directory.CreateDirectory(parent);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

internal sealed class MirrorSerializationCleanupResult
{
    public int FilesParsed { get; set; }
    public int FilesSkipped { get; set; }
    public int MembersMoved { get; set; }
    public int TypesMadePartial { get; set; }
    public int SidecarFiles { get; set; }
    public int SyncVarAccessorsRewritten { get; set; }
    public int NetworkMethodBodiesRestored { get; set; }
    public int NetworkArgumentsRestored { get; set; }
}

internal sealed class ParsedSourceFile
{
    public string Path { get; }
    public string Text { get; }
    public CompilationUnitSyntax Root { get; }
    public List<ClassDeclarationSyntax> Classes { get; }

    public ParsedSourceFile(string path, string text, CompilationUnitSyntax root)
    {
        Path = path;
        Text = text;
        Root = root;
        Classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
    }
}

internal static class MirrorSerializationSourceCleaner
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public static MirrorSerializationCleanupResult CleanDirectory(string sourceRoot)
    {
        var result = new MirrorSerializationCleanupResult();
        var parsedFiles = new List<ParsedSourceFile>();

        foreach (var path in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path);
            var errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                result.FilesSkipped++;
                Logger.WarnNewline(
                    $"Leaving {path} unchanged because its syntax could not be parsed ({errors.Length} errors): {errors[0].GetMessage()}",
                    "MirrorRecovery");
                continue;
            }

            parsedFiles.Add(new ParsedSourceFile(path, text, tree.GetCompilationUnitRoot()));
        }

        result.FilesParsed = parsedFiles.Count;
        var classIndex = BuildClassIndex(parsedFiles);
        foreach (var parsedFile in parsedFiles)
            ProcessFile(sourceRoot, parsedFile, classIndex, result);

        return result;
    }

    private static Dictionary<string, List<ClassDeclarationSyntax>> BuildClassIndex(IEnumerable<ParsedSourceFile> files)
    {
        var index = new Dictionary<string, List<ClassDeclarationSyntax>>(StringComparer.Ordinal);
        foreach (var type in files.SelectMany(file => file.Classes))
        {
            var key = GetTypeFullName(type);
            if (!index.TryGetValue(key, out var definitions))
                index[key] = definitions = new List<ClassDeclarationSyntax>();
            definitions.Add(type);
        }

        return index;
    }

    private static void ProcessFile(
        string sourceRoot,
        ParsedSourceFile parsedFile,
        Dictionary<string, List<ClassDeclarationSyntax>> classIndex,
        MirrorSerializationCleanupResult result)
    {
        var textChanges = new List<TextChange>();
        var sidecars = new List<(ClassDeclarationSyntax Type, List<MemberDeclarationSyntax> Members)>();
        var movedCount = 0;
        var restoredNetworkBodyCount = 0;

        foreach (var type in parsedFile.Classes)
        {
            var networkType = IsNetworkBehaviour(type, classIndex, new HashSet<string>(StringComparer.Ordinal));
            if (networkType)
                restoredNetworkBodyCount += RestoreOriginalNetworkMethodBodies(type, parsedFile.Text, textChanges, result);

            var syncVarFields = GetSyncVarFieldTypes(type);
            var selectedMembers = type.Members
                .Where(member => ShouldMove(member, networkType, syncVarFields))
                .ToList();
            if (selectedMembers.Count == 0)
                continue;

            if (HasUnsupportedContainingType(type))
            {
                Logger.WarnNewline($"Leaving generated members in nested type {GetTypeFullName(type)}; partial extraction supports class-only nesting.", "MirrorRecovery");
                continue;
            }

            var movedMembers = new List<MemberDeclarationSyntax>(selectedMembers.Count);
            foreach (var member in selectedMembers)
            {
                if (member is PropertyDeclarationSyntax property
                    && property.Identifier.ValueText.StartsWith("Network", StringComparison.Ordinal)
                    && syncVarFields.TryGetValue(property.Identifier.ValueText.Substring("Network".Length), out var fieldType)
                    && TryRewriteSyncVarAccessor(property, property.Identifier.ValueText.Substring("Network".Length), fieldType, out var rewrittenProperty))
                {
                    movedMembers.Add(rewrittenProperty);
                    result.SyncVarAccessorsRewritten++;
                }
                else
                {
                    movedMembers.Add(member);
                }
            }

            sidecars.Add((type, movedMembers));
            foreach (var member in selectedMembers)
            {
                textChanges.Add(new TextChange(member.FullSpan, string.Empty));
                movedCount++;
            }

            for (ClassDeclarationSyntax? containing = type; containing != null; containing = containing.Parent as ClassDeclarationSyntax)
            {
                if (!containing.Modifiers.Any(SyntaxKind.PartialKeyword))
                    textChanges.Add(new TextChange(new TextSpan(containing.Keyword.SpanStart, 0), "partial "));
            }
        }

        if (textChanges.Count == 0)
            return;

        var orderedChanges = textChanges.OrderBy(change => change.Span.Start).ToArray();
        for (var i = 1; i < orderedChanges.Length; i++)
        {
            if (orderedChanges[i - 1].Span.End > orderedChanges[i].Span.Start)
            {
                result.FilesSkipped++;
                Logger.WarnNewline($"Leaving {parsedFile.Path} unchanged because generated member spans overlap.", "MirrorRecovery");
                return;
            }
        }

        var updatedSource = SourceText.From(parsedFile.Text, Encoding.UTF8).WithChanges(orderedChanges);
        File.WriteAllText(parsedFile.Path, updatedSource.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        result.TypesMadePartial += sidecars.Count;
        result.MembersMoved += movedCount;
        result.NetworkMethodBodiesRestored += restoredNetworkBodyCount;

        if (sidecars.Count == 0)
            return;

        var assemblyDirectory = GetAssemblyDirectory(sourceRoot, parsedFile.Path);
        var sidecarDirectory = Path.Combine(assemblyDirectory, "MirrorSerialization");
        Directory.CreateDirectory(sidecarDirectory);
        File.WriteAllText(
            Path.Combine(sidecarDirectory, ".mc2il-mirror-serialization-owned"),
            "Owned by MC2IL.MirrorSerializedRecovery.");
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, members) in sidecars)
        {
            var partialType = CreatePartialChain(type, members);
            var sidecar = CreateSidecar(parsedFile.Root, type, partialType);
            var fileName = BuildSidecarFileName(parsedFile.Path, type);
            if (!usedNames.Add(fileName))
                fileName = Path.GetFileNameWithoutExtension(fileName) + "_" + usedNames.Count + ".cs";
            File.WriteAllText(Path.Combine(sidecarDirectory, fileName), sidecar, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            result.SidecarFiles++;
        }
    }

    private static int RestoreOriginalNetworkMethodBodies(
        ClassDeclarationSyntax type,
        string sourceText,
        List<TextChange> textChanges,
        MirrorSerializationCleanupResult result)
    {
        var userCodeMethods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText.StartsWith("UserCode_", StringComparison.Ordinal)
                && method.Body != null)
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var restoredCount = 0;
        foreach (var networkMethod in type.Members.OfType<MethodDeclarationSyntax>())
        {
            if (networkMethod.Body == null
                || !HasNetworkSendAttribute(networkMethod)
                || !IsGeneratedNetworkSendWrapper(networkMethod))
                continue;

            var userCodeName = "UserCode_" + networkMethod.Identifier.ValueText;
            if (!userCodeMethods.TryGetValue(userCodeName, out var candidates))
                continue;

            var matchingMethods = candidates
                .Where(candidate => HasSameMethodSignature(networkMethod, candidate))
                .ToArray();
            if (matchingMethods.Length != 1)
                continue;

            var originalBody = matchingMethods[0].Body!;
            originalBody = RestoreGeneratedObjectArguments(type, networkMethod, originalBody, result);
            var restoredBodyText = Regex.Replace(
                originalBody.ToFullString(),
                @"^[ \t]*//IL_[^\r\n]*(?:\r\n|\n|\r)",
                string.Empty,
                RegexOptions.Multiline);
            restoredBodyText = Regex.Replace(restoredBodyText, @"(\r?\n)[ \t]*(?=\r?\n)", "$1");
            textChanges.Add(new TextChange(networkMethod.Body.FullSpan, restoredBodyText));
            if (!RequiresUnsafeContext(originalBody))
            {
                var unsafeModifier = networkMethod.Modifiers.FirstOrDefault(modifier => modifier.IsKind(SyntaxKind.UnsafeKeyword));
                if (unsafeModifier != default)
                {
                    var removalStart = unsafeModifier.SpanStart;
                    var removalEnd = unsafeModifier.Span.End;
                    while (removalStart > networkMethod.SpanStart
                        && sourceText[removalStart - 1] is ' ' or '\t')
                        removalStart--;
                    while (removalEnd < sourceText.Length
                        && sourceText[removalEnd] is ' ' or '\t')
                        removalEnd++;

                    textChanges.Add(new TextChange(TextSpan.FromBounds(removalStart, removalEnd), " "));
                }
            }

            restoredCount++;
        }

        return restoredCount;
    }

    private static BlockSyntax RestoreGeneratedObjectArguments(
        ClassDeclarationSyntax type,
        MethodDeclarationSyntax networkMethod,
        BlockSyntax body,
        MirrorSerializationCleanupResult result)
    {
        var restoredBody = body;
        var restoredObjectLocalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray())
        {
            var calledName = GetInvokedMethodName(invocation.Expression);
            if (string.IsNullOrEmpty(calledName))
                continue;

            var targetMethods = type.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == calledName && HasNetworkSendAttribute(method))
                .ToArray();
            if (targetMethods.Length != 1 || targetMethods[0].ParameterList.Parameters.Count != 1
                || invocation.ArgumentList.Arguments.Count != 1)
                continue;

            var targetType = NormalizeTypeName(targetMethods[0].ParameterList.Parameters[0].Type?.ToString() ?? string.Empty);
            var argument = invocation.ArgumentList.Arguments[0];
            if (NormalizeTypeName(GetCastType(argument.Expression)) != targetType
                || !TryGetAddressedObjectLocal(argument.Expression, out var objectLocalName)
                || !HasDefaultObjectLocal(body, objectLocalName))
                continue;

            var matchingParameters = networkMethod.ParameterList.Parameters
                .Where(parameter => NormalizeTypeName(parameter.Type?.ToString() ?? string.Empty) == targetType)
                .ToArray();
            if (matchingParameters.Length != 1)
                continue;

            var restoredArgument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(matchingParameters[0].Identifier.ValueText))
                .WithTriviaFrom(argument);
            restoredBody = restoredBody.ReplaceNode(argument, restoredArgument);
            restoredObjectLocalNames.Add(objectLocalName);
            result.NetworkArgumentsRestored++;
        }

        foreach (var local in restoredBody.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().ToArray())
        {
            var unusedVariables = local.Declaration.Variables
                .Where(variable => restoredObjectLocalNames.Contains(variable.Identifier.ValueText)
                    && IsDefaultObjectDeclaration(local.Declaration.Type, variable)
                    && !restoredBody.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(identifier => identifier.Identifier.ValueText == variable.Identifier.ValueText))
                .ToArray();
            if (unusedVariables.Length == 0)
                continue;

            if (unusedVariables.Length == local.Declaration.Variables.Count)
            {
                restoredBody = restoredBody.RemoveNode(local, SyntaxRemoveOptions.KeepExteriorTrivia) ?? restoredBody;
                continue;
            }

            var updatedDeclaration = local.Declaration.RemoveNodes(unusedVariables, SyntaxRemoveOptions.KeepNoTrivia);
            if (updatedDeclaration != null)
                restoredBody = restoredBody.ReplaceNode(local, local.WithDeclaration(updatedDeclaration));
        }

        return restoredBody;
    }

    private static string GetInvokedMethodName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty
        };
    }

    private static string GetCastType(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression is CastExpressionSyntax cast ? cast.Type.ToString() : string.Empty;
    }

    private static bool TryGetAddressedObjectLocal(ExpressionSyntax expression, out string localName)
    {
        localName = string.Empty;
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        if (expression is not CastExpressionSyntax cast)
            return false;

        ExpressionSyntax operand = cast.Expression;
        while (operand is ParenthesizedExpressionSyntax inner)
            operand = inner.Expression;
        if (operand is not PrefixUnaryExpressionSyntax addressOf
            || !addressOf.IsKind(SyntaxKind.AddressOfExpression)
            || addressOf.Operand is not IdentifierNameSyntax identifier)
            return false;

        localName = identifier.Identifier.ValueText;
        return true;
    }

    private static bool HasDefaultObjectLocal(BlockSyntax body, string name)
    {
        return body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(local => local.Declaration.Variables.Select(variable => (local.Declaration.Type, Variable: variable)))
            .Any(item => item.Variable.Identifier.ValueText == name && IsDefaultObjectDeclaration(item.Type, item.Variable));
    }

    private static bool IsDefaultObjectDeclaration(TypeSyntax type, VariableDeclaratorSyntax variable)
    {
        if (NormalizeTypeName(type.ToString()) != "object" || variable.Initializer == null)
            return false;

        return variable.Initializer.Value.IsKind(SyntaxKind.DefaultLiteralExpression)
            || variable.Initializer.Value is DefaultExpressionSyntax defaultExpression
                && NormalizeTypeName(defaultExpression.Type.ToString()) == "object";
    }

    private static bool HasNetworkSendAttribute(MethodDeclarationSyntax method)
    {
        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString();
            var genericStart = name.IndexOf('<');
            if (genericStart >= 0)
                name = name.Substring(0, genericStart);
            var lastDot = name.LastIndexOf('.');
            if (lastDot >= 0)
                name = name.Substring(lastDot + 1);
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Attribute".Length);

            if (name is "Command" or "ClientRpc" or "TargetRpc")
                return true;
        }

        return false;
    }

    private static bool IsGeneratedNetworkSendWrapper(MethodDeclarationSyntax method)
    {
        var invocationNames = method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression.ToString())
            .ToArray();
        return invocationNames.Any(name => name.EndsWith("NetworkWriterPool.GetWriter", StringComparison.Ordinal))
            && invocationNames.Any(name => name.EndsWith("NetworkWriterPool.Recycle", StringComparison.Ordinal))
            && invocationNames.Any(name => name.EndsWith("SendCommandInternal", StringComparison.Ordinal)
                || name.EndsWith("SendRPCInternal", StringComparison.Ordinal)
                || name.EndsWith("SendTargetRPCInternal", StringComparison.Ordinal));
    }

    private static bool HasSameMethodSignature(MethodDeclarationSyntax first, MethodDeclarationSyntax second)
    {
        if (!string.Equals(NormalizeTypeName(first.ReturnType.ToString()), NormalizeTypeName(second.ReturnType.ToString()), StringComparison.Ordinal)
            || first.ParameterList.Parameters.Count != second.ParameterList.Parameters.Count)
            return false;

        for (var i = 0; i < first.ParameterList.Parameters.Count; i++)
        {
            var firstParameter = first.ParameterList.Parameters[i];
            var secondParameter = second.ParameterList.Parameters[i];
            if (!string.Equals(NormalizeTypeName(firstParameter.Type?.ToString() ?? string.Empty), NormalizeTypeName(secondParameter.Type?.ToString() ?? string.Empty), StringComparison.Ordinal)
                || !string.Equals(GetParameterRefKind(firstParameter), GetParameterRefKind(secondParameter), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string GetParameterRefKind(ParameterSyntax parameter)
    {
        foreach (var modifier in parameter.Modifiers)
            if (modifier.IsKind(SyntaxKind.RefKeyword)
                || modifier.IsKind(SyntaxKind.OutKeyword)
                || modifier.IsKind(SyntaxKind.InKeyword))
                return modifier.ValueText;

        return string.Empty;
    }

    private static bool RequiresUnsafeContext(BlockSyntax body)
    {
        return body.DescendantNodesAndSelf().Any(node =>
            node is PointerTypeSyntax
            || node is FunctionPointerTypeSyntax
            || node is FixedStatementSyntax
            || node is SizeOfExpressionSyntax
            || node is PrefixUnaryExpressionSyntax prefix
                && (prefix.IsKind(SyntaxKind.AddressOfExpression) || prefix.IsKind(SyntaxKind.PointerIndirectionExpression))
            || node is PostfixUnaryExpressionSyntax postfix
                && postfix.IsKind(SyntaxKind.PointerIndirectionExpression)
            || node is MemberAccessExpressionSyntax memberAccess
                && memberAccess.IsKind(SyntaxKind.PointerMemberAccessExpression));
    }

    private static string GetAssemblyDirectory(string sourceRoot, string sourceFilePath)
    {
        var fullRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullFile = Path.GetFullPath(sourceFilePath);
        var relative = fullFile.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (separator < 0)
            return fullRoot;

        return Path.Combine(fullRoot, relative.Substring(0, separator));
    }
    private static bool HasUnsupportedContainingType(ClassDeclarationSyntax type)
    {
        foreach (var ancestor in type.Ancestors())
        {
            if (ancestor is RecordDeclarationSyntax or StructDeclarationSyntax)
                return true;
            if (ancestor is TypeDeclarationSyntax and not ClassDeclarationSyntax)
                return true;
        }

        return false;
    }

    private static bool ShouldMove(
        MemberDeclarationSyntax member,
        bool networkType,
        IReadOnlyDictionary<string, string> syncVarFields)
    {
        if (member is MethodDeclarationSyntax marker
            && marker.Identifier.ValueText == "MirrorProcessed"
            && marker.ReturnType.ToString() == "void"
            && marker.ParameterList.Parameters.Count == 0
            && marker.Body is { Statements.Count: 0 })
            return true;

        if (!networkType)
            return false;

        if (member is MethodDeclarationSyntax method)
        {
            var name = method.Identifier.ValueText;
            if (name.StartsWith("UserCode_", StringComparison.Ordinal)
                || name.StartsWith("InvokeUserCode_", StringComparison.Ordinal))
                return true;

            if (name == "SerializeSyncVars"
                && method.Modifiers.Any(SyntaxKind.OverrideKeyword)
                && method.ReturnType.ToString() is "bool" or "Boolean"
                && HasParameters(method, "NetworkWriter", "bool", "Boolean"))
                return true;

            if (name == "DeserializeSyncVars"
                && method.Modifiers.Any(SyntaxKind.OverrideKeyword)
                && method.ReturnType.ToString() == "void"
                && HasParameters(method, "NetworkReader", "bool", "Boolean"))
                return true;
        }

        if (member is PropertyDeclarationSyntax property
            && property.Identifier.ValueText.StartsWith("Network", StringComparison.Ordinal))
        {
            var backingFieldName = property.Identifier.ValueText.Substring("Network".Length);
            if (syncVarFields.ContainsKey(backingFieldName))
                return true;
        }

        if (member is ConstructorDeclarationSyntax constructor
            && constructor.Modifiers.Any(SyntaxKind.StaticKeyword)
            && constructor.Body?.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsRemoteCallRegistration) == true)
            return true;

        return false;
    }

    private static bool HasParameters(
        MethodDeclarationSyntax method,
        string firstTypeName,
        string secondTypeName,
        params string[] acceptedSecondNames)
    {
        if (method.ParameterList.Parameters.Count != 2)
            return false;

        var firstType = GetSimpleTypeName(method.ParameterList.Parameters[0].Type);
        var secondType = GetSimpleTypeName(method.ParameterList.Parameters[1].Type);
        var secondMatches = string.Equals(secondType, secondTypeName, StringComparison.Ordinal)
            || acceptedSecondNames.Contains(secondType, StringComparer.Ordinal);
        return string.Equals(firstType, firstTypeName, StringComparison.Ordinal) && secondMatches;
    }

    private static bool IsRemoteCallRegistration(InvocationExpressionSyntax invocation)
    {
        var text = invocation.Expression.ToString();
        return text.StartsWith("RemoteCallHelper.RegisterCommandDelegate", StringComparison.Ordinal)
            || text.StartsWith("RemoteCallHelper.RegisterRpcDelegate", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> GetSyncVarFieldTypes(ClassDeclarationSyntax type)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!declaration.AttributeLists.SelectMany(list => list.Attributes).Any(IsSyncVarAttribute))
                continue;
            foreach (var variable in declaration.Declaration.Variables)
                fields[variable.Identifier.ValueText] = declaration.Declaration.Type.ToString();
        }

        return fields;
    }

    private static bool TryRewriteSyncVarAccessor(
        PropertyDeclarationSyntax property,
        string fieldName,
        string fieldType,
        out PropertyDeclarationSyntax rewrittenProperty)
    {
        rewrittenProperty = property;
        var setter = property.AccessorList?.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (setter?.Body == null)
            return false;

        var expectedType = NormalizeTypeName(fieldType);
        foreach (var local in setter.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (NormalizeTypeName(local.Declaration.Type.ToString()) != expectedType)
                continue;

            foreach (var variable in local.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not RefExpressionSyntax refValue
                    || refValue.Expression is not PrefixUnaryExpressionSyntax dereference
                    || !dereference.IsKind(SyntaxKind.PointerIndirectionExpression)
                    || !ContainsThisOffset(dereference.Operand))
                    continue;

                var fieldReference = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(fieldName));
                var updatedVariable = variable.WithInitializer(
                    variable.Initializer.WithValue(refValue.WithExpression(fieldReference)));
                var updatedLocal = local.ReplaceNode(variable, updatedVariable);
                var updatedBody = setter.Body.ReplaceNode(local, updatedLocal);
                var ilComments = updatedBody.DescendantTrivia(descendIntoTrivia: true)
                    .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        && trivia.ToString().StartsWith("//IL_", StringComparison.Ordinal))
                    .ToArray();
                if (ilComments.Length > 0)
                    updatedBody = updatedBody.ReplaceTrivia(ilComments, (original, _) => default);

                rewrittenProperty = property.ReplaceNode(setter, setter.WithBody(updatedBody));
                return true;
            }
        }

        return false;
    }

    private static bool ContainsThisOffset(ExpressionSyntax expression)
    {
        foreach (var binary in expression.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.AddExpression))
                continue;

            ExpressionSyntax left = binary.Left;
            while (left is ParenthesizedExpressionSyntax parenthesized)
                left = parenthesized.Expression;
            if (left is ThisExpressionSyntax)
                return true;
        }

        return false;
    }

    private static string NormalizeTypeName(string typeName)
    {
        var simpleName = typeName.Trim().Replace("global::", string.Empty);
        if (simpleName.StartsWith("ref ", StringComparison.Ordinal))
            simpleName = simpleName.Substring("ref ".Length);
        if (simpleName.StartsWith("readonly ", StringComparison.Ordinal))
            simpleName = simpleName.Substring("readonly ".Length);
        switch (simpleName)
        {
            case "System.Boolean": return "bool";
            case "System.Byte": return "byte";
            case "System.SByte": return "sbyte";
            case "System.Int16": return "short";
            case "System.UInt16": return "ushort";
            case "System.Int32": return "int";
            case "System.UInt32": return "uint";
            case "System.Int64": return "long";
            case "System.UInt64": return "ulong";
            case "System.Single": return "float";
            case "System.Double": return "double";
            case "System.Decimal": return "decimal";
            case "System.Char": return "char";
            case "System.String": return "string";
            case "System.Object": return "object";
            default: return simpleName;
        }
    }

    private static bool IsSyncVarAttribute(AttributeSyntax attribute)
    {
        var simpleName = attribute.Name.ToString().Split('.').Last();
        return simpleName == "SyncVar" || simpleName == "SyncVarAttribute";
    }

    private static ClassDeclarationSyntax CreatePartialChain(
        ClassDeclarationSyntax type,
        IReadOnlyList<MemberDeclarationSyntax> movedMembers)
    {
        var movedType = StripTypeMetadata(type)
            .WithMembers(SyntaxFactory.List(movedMembers));

        foreach (var containingType in type.Ancestors().OfType<ClassDeclarationSyntax>())
        {
            movedType = StripTypeMetadata(containingType)
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(movedType));
        }

        return movedType;
    }

    private static ClassDeclarationSyntax StripTypeMetadata(ClassDeclarationSyntax type)
    {
        var modifiers = type.Modifiers;
        if (!modifiers.Any(SyntaxKind.PartialKeyword))
            modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword));

        return type
            .WithAttributeLists(default)
            .WithBaseList(null)
            .WithModifiers(modifiers)
            .WithLeadingTrivia(default(SyntaxTriviaList))
            .WithTrailingTrivia(default(SyntaxTriviaList));
    }

    private static string CreateSidecar(CompilationUnitSyntax root, ClassDeclarationSyntax originalType, ClassDeclarationSyntax partialType)
    {
        MemberDeclarationSyntax member = partialType;
        foreach (var containingNamespace in originalType.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            member = containingNamespace.WithMembers(SyntaxFactory.SingletonList(member));

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithExterns(root.Externs)
            .WithUsings(root.Usings)
            .WithMembers(SyntaxFactory.SingletonList(member))
            .WithLeadingTrivia(SyntaxFactory.TriviaList(
                SyntaxFactory.Comment("// Generated by MC2IL.MirrorSerializedRecovery. Mirror behavior is preserved in this partial file."),
                SyntaxFactory.EndOfLine(Environment.NewLine)));

        return compilationUnit.NormalizeWhitespace(indentation: "\t", eol: Environment.NewLine).ToFullString();
    }

    private static bool IsNetworkBehaviour(
        ClassDeclarationSyntax type,
        Dictionary<string, List<ClassDeclarationSyntax>> classIndex,
        HashSet<string> visited)
    {
        var fullName = GetTypeFullName(type);
        if (!visited.Add(fullName))
            return false;

        var baseTypes = type.BaseList?.Types ?? default;
        foreach (var baseType in baseTypes)
        {
            var rawName = baseType.Type.ToString();
            if (rawName.StartsWith("global::", StringComparison.Ordinal))
                rawName = rawName.Substring("global::".Length);
            var simpleName = GetSimpleTypeName(baseType.Type);
            if (simpleName == "NetworkBehaviour")
                return true;

            var candidates = ResolveBaseTypeCandidates(type, rawName, simpleName, classIndex);
            foreach (var candidate in candidates)
                if (IsNetworkBehaviour(candidate, classIndex, visited))
                    return true;
        }

        return false;
    }

    private static IEnumerable<ClassDeclarationSyntax> ResolveBaseTypeCandidates(
        ClassDeclarationSyntax type,
        string rawName,
        string simpleName,
        Dictionary<string, List<ClassDeclarationSyntax>> classIndex)
    {
        var genericStart = rawName.IndexOf('<');
        if (genericStart >= 0)
            rawName = rawName.Substring(0, genericStart);
        rawName = rawName.TrimEnd('?');

        var namespaceName = GetNamespaceName(type);
        var localName = string.IsNullOrEmpty(namespaceName) ? simpleName : namespaceName + "." + simpleName;
        if (classIndex.TryGetValue(rawName, out var exact))
            return exact;
        if (classIndex.TryGetValue(localName, out var local))
            return local;

        return classIndex
            .Where(entry => string.Equals(entry.Key.Split('.').Last().Split('+').Last(), simpleName, StringComparison.Ordinal))
            .SelectMany(entry => entry.Value)
            .ToArray();
    }

    private static string GetSimpleTypeName(TypeSyntax? type)
    {
        if (type == null)
            return string.Empty;

        var value = type.ToString();
        if (value.StartsWith("global::", StringComparison.Ordinal))
            value = value.Substring("global::".Length);
        var genericStart = value.IndexOf('<');
        if (genericStart >= 0)
            value = value.Substring(0, genericStart);
        value = value.TrimEnd('?');
        var lastDot = value.LastIndexOf('.');
        return lastDot >= 0 ? value.Substring(lastDot + 1) : value;
    }

    private static string GetTypeFullName(ClassDeclarationSyntax type)
    {
        var namespaceName = GetNamespaceName(type);
        var declaringTypes = type.Ancestors().OfType<ClassDeclarationSyntax>()
            .Reverse()
            .Select(declaring => declaring.Identifier.ValueText)
            .Concat(new[] { type.Identifier.ValueText });
        var name = string.Join("+", declaringTypes);
        return string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;
    }

    private static string GetNamespaceName(SyntaxNode node)
    {
        return string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(@namespace => @namespace.Name.ToString()));
    }

    private static string BuildSidecarFileName(string sourcePath, ClassDeclarationSyntax type)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var fullName = GetTypeFullName(type);
        var joined = sourceName + "." + fullName + ".MirrorSerialization.cs";
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return new string(joined.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
