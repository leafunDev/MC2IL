using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Core.Logging;
using LibMC2IL;

namespace MC2IL.Core;

public static class MC2ILPluginManager
{
    private static readonly List<MC2ILPlugin> _loadedPlugins = [];
    private static readonly HashSet<Type> _initializedPluginTypes = [];
    private static readonly HashSet<string> _pluginDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _loadingPluginPaths = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dependencyResolverRegistered;

    internal static List<LibMC2ILMain.MetadataFixupFunc>? MetadataFixupFuncs;

    [RequiresUnreferencedCode("Plugins are loaded dynamically.")]
    internal static void LoadFromDirectory(string pluginsDir)
    {
        var fullPath = Path.GetFullPath(pluginsDir);
        Logger.InfoNewline($"Loading plugins from {fullPath}...", "Plugins");

        if (!Directory.Exists(fullPath))
            return;

        _pluginDirectories.Add(fullPath);
        if (!_dependencyResolverRegistered)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginDependency;
            _dependencyResolverRegistered = true;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(fullPath)
                .Where(file => string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.ErrorNewline($"Could not enumerate plugin directory {fullPath}: {e.Message}", "Plugins");
            return;
        }

        foreach (var file in files)
        {
            Logger.VerboseNewline($"\tLoading {Path.GetFileName(file)}...", "Plugins");
            try
            {
                Assembly.LoadFrom(file);
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Could not load plugin assembly {file}: {e}. Other plugins will still be loaded.", "Plugins");
            }
        }
    }

    [RequiresUnreferencedCode("Plugin dependencies are loaded dynamically.")]
    private static Assembly? ResolvePluginDependency(object? sender, ResolveEventArgs args)
    {
        AssemblyName requestedName;
        try
        {
            requestedName = new AssemblyName(args.Name);
        }
        catch (Exception)
        {
            return null;
        }

        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), requestedName));
        if (alreadyLoaded != null)
            return alreadyLoaded;

        var candidateDirectories = new List<string>();
        var requestingAssembly = args.RequestingAssembly;
        if (requestingAssembly != null)
        {
#pragma warning disable IL3000 // Plugin assemblies loaded from disk have a location; single-file host assemblies may not.
            var requestingLocation = requestingAssembly.Location;
#pragma warning restore IL3000
            if (!string.IsNullOrEmpty(requestingLocation))
                candidateDirectories.Add(Path.GetDirectoryName(requestingLocation)!);
        }
        candidateDirectories.AddRange(_pluginDirectories);

        foreach (var directory in candidateDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, requestedName.Name + ".dll"));
            if (!File.Exists(candidate) || !_loadingPluginPaths.Add(candidate))
                continue;

            try
            {
                return Assembly.LoadFrom(candidate);
            }
            catch (Exception e)
            {
                Logger.WarnNewline($"Could not resolve plugin dependency {requestedName.Name} from {candidate}: {e.Message}", "Plugins");
            }
            finally
            {
                _loadingPluginPaths.Remove(candidate);
            }
        }

        return null;
    }

    [RequiresUnreferencedCode("Plugins are loaded dynamically.")]
    internal static void InitAll()
    {
        var newPlugins = new List<MC2ILPlugin>();
        Logger.VerboseNewline("Collecting and instantiating plugins...", "Plugins");

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            List<RegisterMC2ILPluginAttribute> attrs;
            try
            {
                attrs = assembly.GetCustomAttributes<RegisterMC2ILPluginAttribute>().ToList();
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Could not inspect plugin registrations in {assembly.GetName().Name}: {e}. Other plugins will still be initialized.", "Plugins");
                continue;
            }

            foreach (var registration in attrs)
            {
                var pluginType = registration.PluginType;
                if (!_initializedPluginTypes.Add(pluginType))
                    continue;

                try
                {
                    Logger.VerboseNewline($"\tLoading plugin {pluginType.FullName} from assembly: {assembly.GetName().Name}.dll", "Plugins");
                    newPlugins.Add((MC2ILPlugin)Activator.CreateInstance(pluginType)!);
                }
                catch (Exception e)
                {
                    Logger.ErrorNewline($"Plugin {pluginType.FullName} from assembly {assembly.GetName().Name} threw an exception during construction: {e}. It will not be loaded.", "Plugins");
                }
            }
        }

        Logger.VerboseNewline("Invoking OnLoad on " + newPlugins.Count + " plugins.", "Plugins");
        var activePlugins = new List<MC2ILPlugin>();
        foreach (var plugin in newPlugins)
        {
            try
            {
                plugin.OnLoad();
                activePlugins.Add(plugin);
                Logger.InfoNewline($"Using Plugin: {plugin.Name}", "Plugins");
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Plugin {plugin.GetType().FullName} threw an exception during OnLoad: {e}. It will not receive further events; continuing with other plugins.", "Plugins");
            }
        }

        _loadedPlugins.AddRange(activePlugins);
        Logger.VerboseNewline("OnLoad complete", "Plugins");
    }

    /// <summary>
    /// Attempts to handle the given game path and populate the runtime arguments by passing them to plugins.
    /// </summary>
    /// <param name="gamePath">The path provided by the user for their game.</param>
    /// <param name="args">The arguments to populate with the result, if the game can be handled</param>
    /// <returns>True if the path was handled, and the game can be loaded based on the arguments, otherwise false.</returns>
    public static bool TryProcessGamePath(string gamePath, ref MC2ILRuntimeArgs args)
    {
        foreach (var plugin in _loadedPlugins)
        {
            try
            {
                if (plugin.HandleGamePath(gamePath, ref args))
                    return true;
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Plugin {plugin.Name} failed while handling {gamePath}: {e}. Trying remaining plugins.", "Plugins");
            }
        }

        return false;
    }

    public static void CallOnFinish()
    {
        foreach (var plugin in _loadedPlugins)
        {
            try
            {
                plugin.CallOnFinish();
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Plugin {plugin.GetType().FullName} failed during shutdown: {e}. Continuing shutdown.", "Plugins");
            }
        }
    }
}
