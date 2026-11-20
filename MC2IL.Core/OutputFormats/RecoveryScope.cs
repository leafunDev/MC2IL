using System;
using System.Collections.Generic;
using System.Linq;
using MC2IL.Core.Model.Contexts;
using MC2IL.Core.Utils.AsmResolver;

namespace MC2IL.Core.OutputFormats;

/// <summary>Resolved method selection for IL recovery and its C# export.</summary>
public sealed class RecoveryScope
{
    private readonly HashSet<MethodAnalysisContext> _methods = [];
    private readonly Dictionary<string, HashSet<string>> _topLevelTypes = new(StringComparer.Ordinal);

    private RecoveryScope() { }

    public int MethodCount => _methods.Count;

    public bool Includes(MethodAnalysisContext method) => _methods.Contains(method);

    public IEnumerable<string> TopLevelTypesIn(string assemblyFileName) =>
        _topLevelTypes.TryGetValue(assemblyFileName, out var types) ? types : Enumerable.Empty<string>();

    public static RecoveryScope Resolve(ApplicationAnalysisContext context, IEnumerable<string> selectors)
    {
        var wholeTypes = new HashSet<string>(StringComparer.Ordinal);
        var methodsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var missing = new HashSet<(string Type, string? Method)>();

        foreach (var selector in selectors)
        {
            var parts = selector.Trim().Split(new[] { "::" }, StringSplitOptions.None);
            if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Invalid recovery selector '{selector}'. Use a full type name or Type::Method.");

            var type = parts[0].Trim();
            var method = parts.Length == 2 ? parts[1].Trim() : null;
            missing.Add((type, method));

            if (method == null)
                wholeTypes.Add(type);
            else
            {
                if (!methodsByType.TryGetValue(type, out var methods))
                    methodsByType[type] = methods = new HashSet<string>(StringComparer.Ordinal);
                methods.Add(method);
            }
        }

        if (missing.Count == 0)
            throw new ArgumentException("At least one recovery selector is required.");

        var result = new RecoveryScope();
        foreach (var type in context.AllTypes)
        {
            if (AsmResolverAssemblyPopulator.IsTypeContextModule(type))
                continue;

            var typeName = type.FullName;
            var includedType = false;
            var root = type;
            for (var owner = type; owner != null; owner = owner.DeclaringType)
            {
                root = owner;
                if (wholeTypes.Contains(owner.FullName))
                {
                    includedType = true;
                    missing.Remove((owner.FullName, null));
                }
            }

            methodsByType.TryGetValue(typeName, out var methodNames);
            if (!includedType && methodNames == null)
                continue;

            var exportType = includedType;
            foreach (var method in type.Methods)
            {
                var includedMethod = methodNames?.Contains(method.Name) == true;
                if (includedMethod)
                {
                    missing.Remove((typeName, method.Name));
                    exportType = true;
                }

                if (includedType || includedMethod)
                    result._methods.Add(method);
            }

            if (exportType)
            {
                var fileName = root.DeclaringAssembly.CleanAssemblyName + ".dll";
                if (!result._topLevelTypes.TryGetValue(fileName, out var types))
                    result._topLevelTypes[fileName] = types = new HashSet<string>(StringComparer.Ordinal);
                types.Add(root.FullName);
            }
        }

        if (missing.Count > 0)
        {
            var names = missing.Select(item => item.Method == null ? item.Type : item.Type + "::" + item.Method);
            throw new ArgumentException("Recovery target not found: " + string.Join(", ", names.OrderBy(name => name, StringComparer.Ordinal)));
        }

        return result;
    }
}
