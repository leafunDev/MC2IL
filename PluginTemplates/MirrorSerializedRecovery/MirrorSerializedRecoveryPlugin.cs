using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                $"Parsed {result.FilesParsed} C# files; moved {result.MembersMoved} Mirror-generated members from {result.TypesMadePartial} types into {result.SidecarFiles} sidecar files. Restored {result.NetworkArgumentsRestored} serialized method arguments; rebuilt {result.NetworkDispatchHandlersRebuilt} generated dispatch handlers with {result.NetworkDispatchArgumentsRestored} deserialized arguments. Restored {result.SyncVarAccessorsRewritten} SyncVar accessors from IL2CPP pointer offsets. {result.FilesSkipped} files were left untouched.",
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
    public int NetworkArgumentsRestored { get; set; }
    public int NetworkDispatchArgumentsRestored { get; set; }
    public int NetworkDispatchHandlersRebuilt { get; set; }
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

        foreach (var type in parsedFile.Classes)
        {
            var networkType = IsNetworkBehaviour(type, classIndex, new HashSet<string>(StringComparer.Ordinal));
            if (networkType)
            {
                foreach (var networkMethod in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (networkMethod.Body == null
                        || !HasNetworkSendAttribute(networkMethod)
                        || !IsGeneratedNetworkSendWrapper(networkMethod))
                        continue;

                    var updatedBody = RestoreGeneratedObjectArguments(
                        type,
                        networkMethod,
                        networkMethod.Body,
                        result,
                        restoreSerializationWriterArguments: true);
                    if (!ReferenceEquals(updatedBody, networkMethod.Body)
                        && updatedBody.ToFullString() != networkMethod.Body.ToFullString())
                        textChanges.Add(new TextChange(networkMethod.Body.FullSpan, updatedBody.ToFullString()));
                }
            }

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
                else if (networkType && member is MethodDeclarationSyntax method
                    && method.Body != null
                    && method.Identifier.ValueText.StartsWith("InvokeUserCode_", StringComparison.Ordinal))
                {
                    movedMembers.Add(RestoreNetworkDispatchArguments(type, method, result));
                }
                else if (networkType && member is MethodDeclarationSyntax userCodeMethod
                    && userCodeMethod.Body != null
                    && userCodeMethod.Identifier.ValueText.StartsWith("UserCode_", StringComparison.Ordinal))
                {
                    var updatedBody = RestoreGeneratedObjectArguments(
                        type,
                        userCodeMethod,
                        userCodeMethod.Body,
                        result,
                        restoreSerializationWriterArguments: false);
                    movedMembers.Add(ReferenceEquals(updatedBody, userCodeMethod.Body)
                        ? userCodeMethod
                        : userCodeMethod.WithBody(updatedBody));
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

    private static BlockSyntax RestoreGeneratedObjectArguments(
        ClassDeclarationSyntax type,
        MethodDeclarationSyntax networkMethod,
        BlockSyntax body,
        MirrorSerializationCleanupResult result,
        bool restoreSerializationWriterArguments)
    {
        var restoredBody = body;
        var restoredObjectLocalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray())
        {
            var calledName = GetInvokedMethodName(invocation.Expression);
            if (string.IsNullOrEmpty(calledName))
                continue;

            ArgumentSyntax argument;
            string targetType;
            if (restoreSerializationWriterArguments
                && calledName.StartsWith("_Write_", StringComparison.Ordinal)
                && invocation.ArgumentList.Arguments.Count == 2)
            {
                argument = invocation.ArgumentList.Arguments[1];
                targetType = NormalizeTypeName(GetCastType(argument.Expression));
            }
            else
            {
                var targetMethods = type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(method => method.Identifier.ValueText == calledName && HasNetworkSendAttribute(method))
                    .ToArray();
                if (targetMethods.Length != 1 || targetMethods[0].ParameterList.Parameters.Count != 1
                    || invocation.ArgumentList.Arguments.Count != 1)
                    continue;

                targetType = NormalizeTypeName(targetMethods[0].ParameterList.Parameters[0].Type?.ToString() ?? string.Empty);
                argument = invocation.ArgumentList.Arguments[0];
            }

            if (string.IsNullOrEmpty(targetType)
                || NormalizeTypeName(GetCastType(argument.Expression)) != targetType
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

        return RemoveRecoveredObjectLocals(restoredBody, restoredObjectLocalNames);
    }

    private static BlockSyntax RemoveRecoveredObjectLocals(BlockSyntax body, HashSet<string> recoveredNames)
    {
        var updatedBody = body;
        foreach (var local in updatedBody.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().ToArray())
        {
            var unusedVariables = local.Declaration.Variables
                .Where(variable => recoveredNames.Contains(variable.Identifier.ValueText)
                    && IsDefaultObjectDeclaration(local.Declaration.Type, variable)
                    && !updatedBody.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(identifier => identifier.Identifier.ValueText == variable.Identifier.ValueText))
                .ToArray();
            if (unusedVariables.Length == 0)
                continue;

            if (unusedVariables.Length == local.Declaration.Variables.Count)
            {
                updatedBody = updatedBody.RemoveNode(local, SyntaxRemoveOptions.KeepExteriorTrivia) ?? updatedBody;
                continue;
            }

            var updatedDeclaration = local.Declaration.RemoveNodes(unusedVariables, SyntaxRemoveOptions.KeepNoTrivia);
            if (updatedDeclaration != null)
                updatedBody = updatedBody.ReplaceNode(local, local.WithDeclaration(updatedDeclaration));
        }

        return updatedBody;
    }

    private static MethodDeclarationSyntax RestoreNetworkDispatchArguments(
        ClassDeclarationSyntax type,
        MethodDeclarationSyntax dispatchMethod,
        MirrorSerializationCleanupResult result)
    {
        const string invokePrefix = "InvokeUserCode_";
        var dispatchName = dispatchMethod.Identifier.ValueText;
        if (!dispatchName.StartsWith(invokePrefix, StringComparison.Ordinal))
            return dispatchMethod;

        var userCodeName = "UserCode_" + dispatchName.Substring(invokePrefix.Length);
        var userCodeMethods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == userCodeName && method.Body != null)
            .ToArray();
        if (userCodeMethods.Length != 1)
            return dispatchMethod;

        var userCode = userCodeMethods[0];
        var userCodeCalls = dispatchMethod.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => GetInvokedMethodName(invocation.Expression) == userCodeName)
            .ToArray();
        if (userCodeCalls.Length > 1
            || userCodeCalls.Length == 1
                && userCodeCalls[0].ArgumentList.Arguments.Count != userCode.ParameterList.Parameters.Count)
            return dispatchMethod;

        var readerParameters = dispatchMethod.ParameterList.Parameters
            .Where(parameter => GetSimpleTypeName(parameter.Type) == "NetworkReader")
            .ToArray();
        if (readerParameters.Length != 1)
            return dispatchMethod;

        var senderParameters = dispatchMethod.ParameterList.Parameters
            .Where(parameter => GetSimpleTypeName(parameter.Type) == "NetworkConnectionToClient")
            .ToArray();
        var serializedParameterIndexes = new List<int>();
        for (var i = 0; i < userCode.ParameterList.Parameters.Count; i++)
        {
            var parameter = userCode.ParameterList.Parameters[i];
            if (parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword)
                || modifier.IsKind(SyntaxKind.OutKeyword)
                || modifier.IsKind(SyntaxKind.InKeyword)))
                return dispatchMethod;

            if (GetSimpleTypeName(parameter.Type) == "NetworkConnectionToClient")
            {
                if (senderParameters.Length != 1
                    || userCodeCalls.Length == 1
                        && (userCodeCalls[0].ArgumentList.Arguments[i].Expression is not IdentifierNameSyntax senderArgument
                            || senderArgument.Identifier.ValueText != senderParameters[0].Identifier.ValueText))
                    return dispatchMethod;
                continue;
            }

            serializedParameterIndexes.Add(i);
        }

        var readerName = readerParameters[0].Identifier.ValueText;
        var readInvocations = dispatchMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsNetworkReaderReadInvocation(invocation, readerName))
            .OrderBy(invocation => invocation.SpanStart)
            .ToArray();
        if (readInvocations.Length != serializedParameterIndexes.Count)
            return dispatchMethod;

        var readStatements = dispatchMethod.Body.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(statement => statement.Expression is InvocationExpressionSyntax invocation
                && GetInvokedMethodName(invocation.Expression).StartsWith("_Read_", StringComparison.Ordinal)
                && IsNetworkReaderReadInvocation(invocation, readerName))
            .OrderBy(statement => statement.SpanStart)
            .ToArray();
        var rebuiltMethod = RebuildNetworkDispatchCall(
            type,
            dispatchMethod,
            userCodeName,
            userCode,
            userCodeCalls,
            serializedParameterIndexes,
            readInvocations,
            result);
        if (!ReferenceEquals(rebuiltMethod, dispatchMethod))
            return rebuiltMethod;

        return RestoreDispatchArgumentsOnly(
            dispatchMethod,
            userCode,
            userCodeCalls,
            serializedParameterIndexes,
            readStatements,
            result);
    }

    private static MethodDeclarationSyntax RestoreDispatchArgumentsOnly(
        MethodDeclarationSyntax dispatchMethod,
        MethodDeclarationSyntax userCode,
        InvocationExpressionSyntax[] userCodeCalls,
        List<int> serializedParameterIndexes,
        ExpressionStatementSyntax[] readStatements,
        MirrorSerializationCleanupResult result)
    {
        if (userCodeCalls.Length != 1 || readStatements.Length != serializedParameterIndexes.Count)
            return dispatchMethod;

        var usedNames = new HashSet<string>(dispatchMethod.Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(variable => variable.Identifier.ValueText), StringComparer.Ordinal);
        foreach (var parameter in dispatchMethod.ParameterList.Parameters)
            usedNames.Add(parameter.Identifier.ValueText);

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var recoveredObjectNames = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < serializedParameterIndexes.Count; ordinal++)
        {
            var parameterIndex = serializedParameterIndexes[ordinal];
            var userParameter = userCode.ParameterList.Parameters[parameterIndex];
            var callArgument = userCodeCalls[0].ArgumentList.Arguments[parameterIndex];
            var expectedType = NormalizeTypeName(userParameter.Type?.ToString() ?? string.Empty);
            if (NormalizeTypeName(GetCastType(callArgument.Expression)) != expectedType
                || !TryGetAddressedObjectLocal(callArgument.Expression, out var objectLocalName)
                || !HasDefaultObjectLocal(dispatchMethod.Body, objectLocalName)
                || readStatements[ordinal].Expression is not InvocationExpressionSyntax readInvocation)
                return dispatchMethod;

            var localName = "__mc2ilMirrorArg" + ordinal;
            var suffix = 0;
            while (!usedNames.Add(localName))
                localName = "__mc2ilMirrorArg" + ordinal + "_" + (++suffix);

            var local = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        userParameter.Type!.WithoutTrivia(),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(localName)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(readInvocation.WithoutTrivia())))))
                .WithTriviaFrom(readStatements[ordinal]);
            replacements[readStatements[ordinal]] = local;
            replacements[callArgument] = callArgument.WithExpression(
                SyntaxFactory.IdentifierName(localName).WithTriviaFrom(callArgument.Expression));
            recoveredObjectNames.Add(objectLocalName);
        }

        if (replacements.Count == 0)
            return dispatchMethod;

        var updatedBody = dispatchMethod.Body!.ReplaceNodes(
            replacements.Keys,
            (original, _) => replacements[original]);
        updatedBody = RemoveRecoveredObjectLocals(updatedBody, recoveredObjectNames);
        result.NetworkDispatchArgumentsRestored += serializedParameterIndexes.Count;
        return dispatchMethod.WithBody(updatedBody);
    }

    private static MethodDeclarationSyntax RebuildNetworkDispatchCall(
        ClassDeclarationSyntax type,
        MethodDeclarationSyntax dispatchMethod,
        string userCodeName,
        MethodDeclarationSyntax userCode,
        InvocationExpressionSyntax[] userCodeCalls,
        List<int> serializedParameterIndexes,
        InvocationExpressionSyntax[] readInvocations,
        MirrorSerializationCleanupResult result)
    {
        // Rebuild only Mirror's unambiguous generated shape: one active-state guard,
        // one inactive-side error log, a matching attributed send wrapper, and one
        // reader call for every serialized parameter.
        if (dispatchMethod.Body == null || dispatchMethod.Body.Statements.Count == 0)
            return dispatchMethod;

        var wrapperName = dispatchMethod.Identifier.ValueText.Substring("InvokeUserCode_".Length);
        var wrappers = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == wrapperName
                && method.Body != null
                && HasNetworkSendAttribute(method)
                && IsGeneratedNetworkSendWrapper(method))
            .ToArray();
        if (wrappers.Length != 1)
            return dispatchMethod;

        var wrapper = wrappers[0];
        var networkAttribute = GetNetworkSendAttributeName(wrapper);
        // TargetRpc has version-specific target-connection handling. Keep it unchanged
        // until its generated signature can be proven from the input.
        if (networkAttribute is not ("Command" or "ClientRpc" or "TargetRpc")
            || !IsVoidType(userCode.ReturnType)
            || !IsVoidType(wrapper.ReturnType)
            || wrapper.ParameterList.Parameters.Count != userCode.ParameterList.Parameters.Count)
            return dispatchMethod;

        for (var i = 0; i < wrapper.ParameterList.Parameters.Count; i++)
        {
            var wrapperParameter = wrapper.ParameterList.Parameters[i];
            var userParameter = userCode.ParameterList.Parameters[i];
            if (NormalizeTypeName(wrapperParameter.Type?.ToString() ?? string.Empty)
                    != NormalizeTypeName(userParameter.Type?.ToString() ?? string.Empty)
                || GetParameterRefKind(wrapperParameter) != GetParameterRefKind(userParameter))
                return dispatchMethod;
        }

        var parameters = dispatchMethod.ParameterList.Parameters;
        if (parameters.Count is not (2 or 3)
            || GetSimpleTypeName(parameters[0].Type) != "NetworkBehaviour"
            || GetSimpleTypeName(parameters[1].Type) != "NetworkReader"
            || parameters.Count == 3
                && GetSimpleTypeName(parameters[2].Type) != "NetworkConnectionToClient")
            return dispatchMethod;

        var receiverName = parameters[0].Identifier.ValueText;
        var readerName = parameters[1].Identifier.ValueText;
        var senderName = parameters.Count == 3 ? parameters[2].Identifier.ValueText : null;
        var expectedActiveType = networkAttribute == "Command" ? "NetworkServer" : "NetworkClient";
        var activeGuards = dispatchMethod.Body.Statements.OfType<IfStatementSyntax>()
            .Where(guard => IsExpectedNetworkActiveCondition(
                guard.Condition,
                dispatchMethod.Body,
                expectedActiveType,
                out _))
            .ToArray();
        if (activeGuards.Length != 1
            || activeGuards[0].Statement is not BlockSyntax generatedBlock)
            return dispatchMethod;
        var activeGuard = activeGuards[0];
        var activeGuardIndex = dispatchMethod.Body.Statements.IndexOf(activeGuard);
        if (!IsExpectedNetworkActiveCondition(
                activeGuard.Condition,
                dispatchMethod.Body,
                expectedActiveType,
                out var activeLocal))
            return dispatchMethod;

        var prefixStatements = new List<StatementSyntax>();
        if (activeLocal != null)
        {
            if (activeGuardIndex != 1 || !ReferenceEquals(dispatchMethod.Body.Statements[0], activeLocal))
                return dispatchMethod;
            prefixStatements.Add(activeLocal);
        }
        else if (activeGuardIndex != 0)
        {
            return dispatchMethod;
        }

        var expectedError = networkAttribute switch
        {
            "Command" => "Command " + wrapperName + " called on client.",
            "TargetRpc" => "TargetRPC " + wrapperName + " called on server.",
            _ => "RPC " + wrapperName + " called on server."
        };
        var inactiveLogs = dispatchMethod.Body.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(statement => IsExpectedInactiveNetworkLog(statement, expectedError))
            .ToArray();
        if (inactiveLogs.Length != 1 || generatedBlock.Span.Contains(inactiveLogs[0].Span))
            return dispatchMethod;
        var inactiveLog = inactiveLogs[0];

        foreach (var statement in dispatchMethod.Body.Statements)
        {
            if (ReferenceEquals(statement, activeGuard)
                || ReferenceEquals(statement, activeLocal)
                || ReferenceEquals(statement, inactiveLog))
                continue;

            if (statement is ReturnStatementSyntax
                || statement is GotoStatementSyntax gotoStatement
                    && gotoStatement.Expression is IdentifierNameSyntax label
                    && label.Identifier.ValueText.StartsWith("IL_", StringComparison.Ordinal)
                || IsNullReferenceThrowLabel(statement))
                continue;

            return dispatchMethod;
        }

        var inlinedUserCodeCalls = userCode.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => GetInvokedMethodName(invocation.Expression))
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var observedInlinedCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        var dispatchInvocations = generatedBlock.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .ToArray();
        foreach (var invocation in dispatchInvocations)
        {
            var invokedName = GetInvokedMethodName(invocation.Expression);
            if (IsNetworkReaderReadInvocation(invocation, readerName)
                || invokedName == "GetTypeFromHandle"
                || invokedName == userCodeName)
                continue;

            observedInlinedCalls.TryGetValue(invokedName, out var observedCount);
            inlinedUserCodeCalls.TryGetValue(invokedName, out var userCodeCount);
            if (observedCount >= userCodeCount)
                return dispatchMethod;
            observedInlinedCalls[invokedName] = observedCount + 1;
        }

        if (generatedBlock.DescendantNodes().OfType<TypeOfExpressionSyntax>()
                .All(expression => GetSimpleTypeName(expression.Type) != type.Identifier.ValueText)
            || generatedBlock.DescendantNodes().OfType<IdentifierNameSyntax>()
                .All(identifier => identifier.Identifier.ValueText != receiverName))
            return dispatchMethod;

        if (readInvocations.Length != serializedParameterIndexes.Count
            || readInvocations.Any(invocation => !generatedBlock.Span.Contains(invocation.Span)))
            return dispatchMethod;

        var localNames = new HashSet<string>(parameters.Select(parameter => parameter.Identifier.ValueText), StringComparer.Ordinal);
        foreach (var variable in dispatchMethod.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            localNames.Add(variable.Identifier.ValueText);

        var recoveredStatements = new List<StatementSyntax>();
        var localsByParameter = new Dictionary<int, string>();
        for (var ordinal = 0; ordinal < serializedParameterIndexes.Count; ordinal++)
        {
            var parameterIndex = serializedParameterIndexes[ordinal];
            var userParameter = userCode.ParameterList.Parameters[parameterIndex];
            if (userParameter.Type == null
                || !IsNetworkReaderReadInvocation(readInvocations[ordinal], readerName))
                return dispatchMethod;

            var localName = "__mc2ilMirrorArg" + ordinal;
            var suffix = 0;
            while (!localNames.Add(localName))
                localName = "__mc2ilMirrorArg" + ordinal + "_" + (++suffix);

            var local = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    userParameter.Type.WithoutTrivia(),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(localName)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(readInvocations[ordinal].WithoutTrivia())))));
            recoveredStatements.Add(local.WithTriviaFrom(readInvocations[ordinal]));
            localsByParameter.Add(parameterIndex, localName);
        }

        var nullCheck = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.CastExpression(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                        SyntaxFactory.IdentifierName(receiverName))),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.Block(
                SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("NullReferenceException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList()))));
        recoveredStatements.Add(nullCheck);

        var selfType = SyntaxFactory.ParseTypeName(
            type.Identifier.ToString() + (type.TypeParameterList?.ToString() ?? string.Empty));
        var userCodeArguments = new List<ArgumentSyntax>();
        for (var i = 0; i < userCode.ParameterList.Parameters.Count; i++)
        {
            var parameter = userCode.ParameterList.Parameters[i];
            var argumentName = GetSimpleTypeName(parameter.Type) == "NetworkConnectionToClient"
                ? senderName
                : localsByParameter.TryGetValue(i, out var localName) ? localName : null;
            if (argumentName == null)
                return dispatchMethod;

            userCodeArguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(argumentName)));
        }

        var userCodeCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.CastExpression(selfType, SyntaxFactory.IdentifierName(receiverName))),
                SyntaxFactory.IdentifierName(userCodeName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(userCodeArguments)));
        recoveredStatements.Add(SyntaxFactory.ExpressionStatement(userCodeCall));
        recoveredStatements.Add(SyntaxFactory.ReturnStatement());

        var rebuiltGuard = SyntaxFactory.IfStatement(
                activeGuard.Condition.WithoutTrivia(),
                SyntaxFactory.Block(recoveredStatements).WithTriviaFrom(generatedBlock))
            .WithElse(SyntaxFactory.ElseClause(SyntaxFactory.Block(inactiveLog)));
        prefixStatements.Add(rebuiltGuard.WithTriviaFrom(activeGuard));
        var rebuiltBody = SyntaxFactory.Block(SyntaxFactory.List(prefixStatements))
            .WithTriviaFrom(dispatchMethod.Body);
        result.NetworkDispatchArgumentsRestored += serializedParameterIndexes.Count;
        result.NetworkDispatchHandlersRebuilt++;
        return dispatchMethod.WithBody(rebuiltBody);
    }

    private static bool IsVoidType(TypeSyntax type)
    {
        return type is PredefinedTypeSyntax predefinedType
            && predefinedType.Keyword.IsKind(SyntaxKind.VoidKeyword);
    }

    private static bool IsExpectedNetworkActiveCondition(
        ExpressionSyntax condition,
        BlockSyntax methodBody,
        string expectedActiveType,
        out LocalDeclarationStatementSyntax? activeLocal)
    {
        activeLocal = null;
        while (condition is ParenthesizedExpressionSyntax parenthesized)
            condition = parenthesized.Expression;

        if (IsNetworkActiveMember(condition, expectedActiveType))
            return true;

        if (condition is not IdentifierNameSyntax identifier)
            return false;

        var matchingLocals = methodBody.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Where(local => NormalizeTypeName(local.Declaration.Type.ToString()) == "bool"
                && local.Declaration.Variables.Count == 1
                && local.Declaration.Variables[0].Identifier.ValueText == identifier.Identifier.ValueText
                && local.Declaration.Variables[0].Initializer != null
                && IsNetworkActiveMember(local.Declaration.Variables[0].Initializer!.Value, expectedActiveType))
            .ToArray();
        if (matchingLocals.Length != 1)
            return false;

        activeLocal = matchingLocals[0];
        return true;
    }

    private static bool IsNetworkActiveMember(ExpressionSyntax expression, string expectedActiveType)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.ValueText == "active"
            && memberAccess.Expression is IdentifierNameSyntax typeName
            && typeName.Identifier.ValueText == expectedActiveType;
    }

    private static bool IsNullReferenceThrowLabel(StatementSyntax statement)
    {
        if (statement is not LabeledStatementSyntax label)
            return false;

        StatementSyntax body = label.Statement;
        if (body is BlockSyntax block)
        {
            if (block.Statements.Count != 1)
                return false;
            body = block.Statements[0];
        }

        return body is ThrowStatementSyntax throwStatement
            && throwStatement.Expression is ObjectCreationExpressionSyntax creation
            && GetSimpleTypeName(creation.Type) == "NullReferenceException";
    }

    private static bool IsNetworkReaderReadInvocation(InvocationExpressionSyntax invocation, string readerName)
    {
        var methodName = GetInvokedMethodName(invocation.Expression);
        if (methodName.StartsWith("_Read_", StringComparison.Ordinal))
            return invocation.ArgumentList.Arguments.Count == 1
                && invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax readerArgument
                && readerArgument.Identifier.ValueText == readerName;

        return methodName.StartsWith("Read", StringComparison.Ordinal)
            && invocation.ArgumentList.Arguments.Count == 0
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression is IdentifierNameSyntax target
            && target.Identifier.ValueText == readerName;
    }

    private static bool IsExpectedInactiveNetworkLog(StatementSyntax statement, string expectedMessage)
    {
        if (statement is BlockSyntax block)
            return block.Statements.Count == 1
                && IsExpectedInactiveNetworkLog(block.Statements[0], expectedMessage);

        if (statement is not ExpressionStatementSyntax expressionStatement
            || expressionStatement.Expression is not InvocationExpressionSyntax invocation
            || GetInvokedMethodName(invocation.Expression) != "LogError"
            || invocation.ArgumentList.Arguments.Count != 1
            || invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return false;

        return literal.Token.ValueText == expectedMessage;
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
        return GetNetworkSendAttributeName(method) != string.Empty;
    }

    private static string GetNetworkSendAttributeName(MethodDeclarationSyntax method)
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
                return name;
        }

        return string.Empty;
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
