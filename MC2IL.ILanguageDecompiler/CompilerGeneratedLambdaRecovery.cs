using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MC2IL.ILanguageDecompiler;

/// <summary>
/// Converts ILSpy's cached non-capturing lambda pattern back to a direct lambda call.
/// A generated singleton type is removed only after all references to it are gone.
/// </summary>
internal static class CompilerGeneratedLambdaRecovery
{
    private static readonly Regex CachedDelegateLocals = new(
        @"(?m)^(?<indent>[\t ]*)(?<delegateType>[^\r\n;=]+?)\s+(?<cacheLocal>[A-Za-z_]\w*)\s*=\s*(?<generatedType>[A-Za-z_<][A-Za-z0-9_<>]*)\s*\.\s*(?<cacheField>[A-Za-z_<][A-Za-z0-9_<>]*)\s*;\r?\n(?<nextIndent>[\t ]*)(?:System\.)?MulticastDelegate\s+(?<delegateLocal>[A-Za-z_]\w*)\s*;\r?\n",
        RegexOptions.CultureInvariant);

    public static string Rewrite(string source)
    {
        var generatedTypes = new HashSet<string>();
        var searchFrom = 0;

        while (searchFrom < source.Length)
        {
            var declaration = CachedDelegateLocals.Match(source, searchFrom);
            if (!declaration.Success)
                break;

            if (!TryRewriteCache(source, declaration, out var rewritten, out var generatedType))
            {
                searchFrom = declaration.Index + declaration.Length;
                continue;
            }

            source = rewritten;
            generatedTypes.Add(generatedType);
            searchFrom = 0;
        }

        foreach (var generatedType in generatedTypes)
            source = RemoveUnreferencedGeneratedType(source, generatedType);

        return source;
    }

    private static bool TryRewriteCache(string source, Match declaration, out string rewritten, out string generatedType)
    {
        rewritten = source;
        generatedType = declaration.Groups["generatedType"].Value;
        var cacheField = declaration.Groups["cacheField"].Value;
        var cacheLocal = declaration.Groups["cacheLocal"].Value;
        var delegateType = declaration.Groups["delegateType"].Value.Trim();
        var delegateLocal = declaration.Groups["delegateLocal"].Value;

        // Require the compiler's complete cache shape and exactly one call-site use.
        if (FindEnclosingBlock(source, declaration.Index) is not { } scope
            || CountIdentifier(source.Substring(scope.Start, scope.End - scope.Start + 1), cacheLocal) != 3
            || CountIdentifier(source.Substring(scope.Start, scope.End - scope.Start + 1), delegateLocal) != 4)
            return false;

        var afterDeclarations = declaration.Index + declaration.Length;
        var ifPattern = new Regex(@"[\t \r\n]*if\s*\(\s*" + Regex.Escape(cacheLocal)
            + @"\s*==\s*null\s*\)\s*\{", RegexOptions.CultureInvariant);
        var ifMatch = ifPattern.Match(source, afterDeclarations);
        if (!ifMatch.Success)
            return false;

        var ifOpen = ifMatch.Index + ifMatch.Length - 1;
        var ifClose = FindMatchingDelimiter(source, ifOpen, '{', '}');
        if (ifClose < 0)
            return false;

        var elsePattern = new Regex(@"[\t \r\n]*else\s*\{", RegexOptions.CultureInvariant);
        var elseMatch = elsePattern.Match(source, ifClose + 1);
        if (!elseMatch.Success)
            return false;

        var elseOpen = elseMatch.Index + elseMatch.Length - 1;
        var elseClose = FindMatchingDelimiter(source, elseOpen, '{', '}');
        if (elseClose < 0)
            return false;

        var assignmentPattern = new Regex(@"\b" + Regex.Escape(delegateLocal) + @"\s*=\s*(?<open>\()",
            RegexOptions.CultureInvariant);
        var assignment = assignmentPattern.Match(source, ifOpen + 1);
        if (!assignment.Success || assignment.Index >= ifClose)
            return false;

        var lambdaOpen = assignment.Groups["open"].Index;
        var lambdaClose = FindMatchingDelimiter(source, lambdaOpen, '(', ')');
        if (lambdaClose < 0 || lambdaClose >= ifClose)
            return false;

        var cachedExpression = source.Substring(lambdaOpen + 1, lambdaClose - lambdaOpen - 1).Trim();
        var cacheAssignment = Regex.Match(cachedExpression,
            @"^" + Regex.Escape(generatedType) + @"\s*\.\s*" + Regex.Escape(cacheField) + @"\s*=\s*",
            RegexOptions.CultureInvariant);
        if (!cacheAssignment.Success)
            return false;

        var lambda = cachedExpression.Substring(cacheAssignment.Length).Trim();
        if (lambda.Length == 0 || !lambda.Contains("=>"))
            return false;

        var castPattern = @"\(\s*" + Regex.Escape(delegateType) + @"\s*\)\s*"
            + Regex.Escape(delegateLocal) + @"\b";
        var castMatcher = new Regex(castPattern, RegexOptions.CultureInvariant);
        var cast = castMatcher.Match(source, elseClose + 1);
        if (!cast.Success || castMatcher.Match(source, cast.Index + cast.Length).Success)
            return false;

        var removeEnd = elseClose + 1;
        if (removeEnd < source.Length && source[removeEnd] == '\r')
            removeEnd++;
        if (removeEnd < source.Length && source[removeEnd] == '\n')
            removeEnd++;

        var removeLength = removeEnd - declaration.Index;
        var castIndex = cast.Index - removeLength;
        rewritten = source.Remove(declaration.Index, removeLength)
            .Remove(castIndex, cast.Length)
            .Insert(castIndex, lambda);
        return true;
    }

    private static string RemoveUnreferencedGeneratedType(string source, string generatedType)
    {
        var declaration = Regex.Match(source, @"\bclass\s+" + Regex.Escape(generatedType), RegexOptions.CultureInvariant);
        if (!declaration.Success)
            return source;

        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        if (open < 0)
            return source;

        var close = FindMatchingDelimiter(source, open, '{', '}');
        if (close < 0)
            return source;

        var start = source.LastIndexOf('\n', declaration.Index) + 1;
        var outsideType = source.Remove(start, close + 1 - start);
        if (CountIdentifier(outsideType, generatedType) != 0)
            return source;

        // Include immediately preceding attributes such as [Serializable].
        while (start > 0)
        {
            var previousEnd = start - 1;
            if (source[previousEnd] == '\n')
                previousEnd--;
            if (previousEnd >= 0 && source[previousEnd] == '\r')
                previousEnd--;
            if (previousEnd < 0)
                break;

            var previousStart = source.LastIndexOf('\n', previousEnd) + 1;
            var previousLine = source.Substring(previousStart, previousEnd - previousStart + 1).Trim();
            if (!previousLine.StartsWith("[", System.StringComparison.Ordinal)
                || !previousLine.EndsWith("]", System.StringComparison.Ordinal))
                break;
            start = previousStart;
        }

        var end = close + 1;
        if (end < source.Length && source[end] == '\r')
            end++;
        if (end < source.Length && source[end] == '\n')
            end++;
        return source.Remove(start, end - start);
    }

    private static int CountIdentifier(string source, string identifier)
        => Regex.Matches(source, @"(?<![A-Za-z0-9_])" + Regex.Escape(identifier) + @"(?![A-Za-z0-9_])")
            .Count;

    private static (int Start, int End)? FindEnclosingBlock(string source, int position)
    {
        var openings = new List<int>();
        var state = ParseState.Code;
        var verbatimString = false;

        for (var index = 0; index < position; index++)
        {
            var current = source[index];
            var next = index + 1 < position ? source[index + 1] : '\0';

            switch (state)
            {
                case ParseState.LineComment:
                    if (current is '\r' or '\n') state = ParseState.Code;
                    continue;
                case ParseState.BlockComment:
                    if (current == '*' && next == '/') { state = ParseState.Code; index++; }
                    continue;
                case ParseState.String:
                    if (verbatimString && current == '"' && next == '"') { index++; continue; }
                    if (!verbatimString && current == '\\') { index++; continue; }
                    if (current == '"') state = ParseState.Code;
                    continue;
                case ParseState.Character:
                    if (current == '\\') { index++; continue; }
                    if (current == '\'') state = ParseState.Code;
                    continue;
            }

            if (current == '/' && next == '/') { state = ParseState.LineComment; index++; continue; }
            if (current == '/' && next == '*') { state = ParseState.BlockComment; index++; continue; }
            if (current == '"')
            {
                verbatimString = index > 0 && source[index - 1] == '@'
                    || index > 1 && source[index - 1] == '$' && source[index - 2] == '@';
                state = ParseState.String;
                continue;
            }
            if (current == '\'') { state = ParseState.Character; continue; }
            if (current == '{') openings.Add(index);
            else if (current == '}' && openings.Count > 0) openings.RemoveAt(openings.Count - 1);
        }

        if (openings.Count == 0)
            return null;

        var start = openings[openings.Count - 1];
        var end = FindMatchingDelimiter(source, start, '{', '}');
        return end < 0 ? null : (start, end);
    }

    private static int FindMatchingDelimiter(string source, int start, char opening, char closing)
    {
        var depth = 0;
        var state = ParseState.Code;
        var verbatimString = false;

        for (var index = start; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            switch (state)
            {
                case ParseState.LineComment:
                    if (current is '\r' or '\n') state = ParseState.Code;
                    continue;
                case ParseState.BlockComment:
                    if (current == '*' && next == '/') { state = ParseState.Code; index++; }
                    continue;
                case ParseState.String:
                    if (verbatimString && current == '"' && next == '"') { index++; continue; }
                    if (!verbatimString && current == '\\') { index++; continue; }
                    if (current == '"') state = ParseState.Code;
                    continue;
                case ParseState.Character:
                    if (current == '\\') { index++; continue; }
                    if (current == '\'') state = ParseState.Code;
                    continue;
            }

            if (current == '/' && next == '/') { state = ParseState.LineComment; index++; continue; }
            if (current == '/' && next == '*') { state = ParseState.BlockComment; index++; continue; }
            if (current == '"')
            {
                verbatimString = index > 0 && source[index - 1] == '@'
                    || index > 1 && source[index - 1] == '$' && source[index - 2] == '@';
                state = ParseState.String;
                continue;
            }
            if (current == '\'') { state = ParseState.Character; continue; }
            if (current == opening) depth++;
            else if (current == closing && --depth == 0) return index;
        }

        return -1;
    }

    private enum ParseState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        Character,
    }
}

