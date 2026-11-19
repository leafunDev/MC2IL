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
    private static List<MC2ILPlugin> _loadedPlugins = [];
    
    internal static List<LibMC2ILMain.MetadataFixupFunc>? MetadataFixupFuncs;

    [RequiresUnreferencedCode("Plugins are loaded dynamically.")]
    internal static void LoadFromDirectory(string pluginsDir)
    {
        Logger.InfoNewline($"Loading plugins from {pluginsDir}...", "Plugins");

        if (!Directory.Exists(pluginsDir))
            return;

        foreach (var file in Directory.EnumerateFiles(pluginsDir))
        {
            if (Path.GetExtension(file) == ".dll")
            {
                Logger.VerboseNewline($"\tLoading {Path.GetFileName(file)}...", "Plugins");
                Assembly.LoadFrom(file);
            }
        }
    }

    internal static void InitAll()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        Logger.VerboseNewline("Collecting and instantiating plugins...", "Plugins");

        foreach (var assembly in assemblies)
        {
            var attrs = assembly.GetCustomAttributes<RegisterMC2ILPluginAttribute>().ToList();

            if (attrs.Count == 0)
                continue;

            foreach (var registerMC2ILPluginAttribute in attrs)
            {
                MC2ILPlugin plugin;
                try
                {
                    Logger.VerboseNewline($"\tLoading plugin {registerMC2ILPluginAttribute.PluginType.FullName} from assembly: {assembly.GetName().Name}.dll", "Plugins");
                    plugin = (MC2ILPlugin)Activator.CreateInstance(registerMC2ILPluginAttribute.PluginType)!;
                }
                catch (Exception e)
                {
                    Logger.ErrorNewline($"Plugin {registerMC2ILPluginAttribute.PluginType.FullName} from assembly {assembly.GetName().Name} threw an exception during construction: {e}. It will not be loaded.", "Plugins");
                    continue;
                }

                _loadedPlugins.Add(plugin);
            }
        }

        Logger.VerboseNewline("Invoking OnLoad on " + _loadedPlugins.Count + " plugins.", "Plugins");
        foreach (var plugin in _loadedPlugins)
        {
            try
            {
                plugin.OnLoad();
                Logger.InfoNewline($"Using Plugin: {plugin.Name}", "Plugins");
            }
            catch (Exception e)
            {
                Logger.ErrorNewline($"Plugin {plugin.GetType().FullName} threw an exception during OnLoad: {e}. It will not receive any further events.", "Plugins");
                _loadedPlugins.Remove(plugin);
            }
        }

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
        foreach (var mc2IlPlugin in _loadedPlugins)
        {
            if (mc2IlPlugin.HandleGamePath(gamePath, ref args))
                return true;
        }

        return false;
    }

    public static void CallOnFinish()
    {
        foreach (var mc2IlPlugin in _loadedPlugins)
        {
            mc2IlPlugin.CallOnFinish();
        }
    }
}
