using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MC2IL;

internal static class ConsoleUi
{
    private static readonly object ConsoleLock = new();
    private static bool _color;
    private static bool _ansi;
    private static bool _progressActive;
    private static int _percent;
    private static string _stage = "Preparing analysis";
    private static string _methodCount = string.Empty;

    private static readonly OutputAction[] Actions =
    [
        new("Recover managed DLLs with method bodies", "dll_il_recovery", "Full IL recovery · recommended"),
        new("Decompile to C# source files", ILanguageDecompiler.CSharpOutputFormat.FormatId, "C# with recovered method bodies"),
        new("Export both DLL and C# files", "dll_il_recovery", "Both formats from a single recovery pass", ILanguageDecompiler.CSharpOutputFormat.FormatId),
        new("Export managed DLL metadata", "dll_default", "DLLs with default method bodies"),
        new("Export DLLs with empty method bodies", "dll_empty", "Metadata and signatures only"),
        new("Export DLLs with throw-null method bodies", "dll_throw_null", "Stub methods that throw on invocation"),
        new("Export C#-oriented source", "diffable-cs", "Readable source output for comparison"),
        new("Export ISIL analysis dump", "isil", "Instruction-level method analysis"),
        new("Export WebAssembly method mappings", "wasmmappings", "Method names mapped to WASM functions"),
        new("Export WebAssembly name section", "wasm_name_section", "Names extracted from a WASM module")
    ];

    public static void Initialize()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }

        _color = Environment.GetEnvironmentVariable("NO_COLOR") == null && !Console.IsOutputRedirected;
        _ansi = _color && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

        if (_ansi && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _ansi = EnableWindowsVirtualTerminalProcessing();
    }

    public static void PrintBanner()
    {
        if (_color && !_ansi)
            Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine("╭──────────────────────────────────────────────────────────────────────────────╮");
        Console.WriteLine("│  MC2IL   /   UNITY IL2CPP WORKBENCH                         Machine Code → IL │");
        Console.WriteLine("│  Recover managed assemblies and method bodies                               │");
        Console.WriteLine("╰──────────────────────────────────────────────────────────────────────────────╯");
        if (_color && !_ansi)
            Console.ResetColor();
        else if (_ansi)
            Console.Write("\u001b[0m");
        Console.WriteLine($"  Version {Program.MC2ILVersionString.Split('+')[0]}    ·    {RuntimeInformation.OSDescription}");
        Console.WriteLine();
    }

    public static void ReportStartupError(Exception error)
    {
        WriteLog("ERROR", "Startup", error.ToString(), ConsoleColor.Red, "1;38;2;255;115;115");
    }

    public static string[]? CollectArguments()
    {
        var actionIndex = Select("Choose an action", Array.ConvertAll(Actions, action => action.Title + "    " + action.Description), 0);
        if (actionIndex < 0)
            return null;

        var inputKind = Select("Select game input", new[]
        {
            "Browse for a package file    APK / XAPK / APKM / IPA / TIPA",
            "Browse for a Unity game folder",
            "Enter or paste a path manually"
        }, 0);
        if (inputKind < 0)
            return null;

        var gamePath = SelectInputPath(inputKind);
        if (gamePath == null)
            return null;

        var action = Actions[actionIndex];
        var exportsCSharp = action.FormatId == ILanguageDecompiler.CSharpOutputFormat.FormatId
            || action.AdditionalFormatId == ILanguageDecompiler.CSharpOutputFormat.FormatId;
        string? sourceAssemblies = null;
        if (exportsCSharp)
        {
            var assemblyMode = Select("Choose assemblies for C# export", new[]
            {
                "All assemblies",
                "Assembly-CSharp only    Typical Unity game scripts",
                "Enter assembly names"
            }, 0);
            if (assemblyMode < 0)
                return null;

            if (assemblyMode == 1)
                sourceAssemblies = "Assembly-CSharp";
            else if (assemblyMode == 2)
            {
                sourceAssemblies = Prompt("C# ASSEMBLIES", "Comma-separated assembly names (e.g. Assembly-CSharp,MyGame.UI)");
                if (string.IsNullOrWhiteSpace(sourceAssemblies))
                    return null;
            }
        }

        var defaultOutput = Path.Combine(Environment.CurrentDirectory, "mc2il_out");
        var outputKind = Select("Select output folder", new[]
        {
            "Browse for an output folder",
            "Use the default folder    " + defaultOutput,
            "Enter or paste a path manually"
        }, 1);
        if (outputKind < 0)
            return null;

        string outputPath;
        if (outputKind == 1)
            outputPath = defaultOutput;
        else if (outputKind == 0)
        {
            var folderResult = NativeFilePicker.PickFolder("Select an output folder");
            if (folderResult.Status == NativePickerStatus.Cancelled)
                return null;
            if (folderResult.Status == NativePickerStatus.Unavailable)
            {
                WriteStatus("A native folder picker is unavailable. Enter a path below.", ConsoleColor.Yellow);
                outputPath = Prompt("OUTPUT FOLDER", "Destination folder");
            }
            else
                outputPath = folderResult.Path!;
        }
        else
            outputPath = Prompt("OUTPUT FOLDER", "Destination folder");

        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        Console.WriteLine();
        WriteAccent("ANALYSIS OPTIONS");
        var lowMemory = AskYesNo("Use low-memory mode?", defaultValue: false);
        var verbose = AskYesNo("Show detailed diagnostic log?", defaultValue: false);
        var reuseSingletons = AskYesNo("Reuse singleton instances for later reads?", defaultValue: false);
        var recoveryTargets = Actions[actionIndex].FormatId is "dll_il_recovery" or ILanguageDecompiler.CSharpOutputFormat.FormatId
            ? Prompt("RECOVERY TARGETS", "Leave blank for all methods, or enter Full.Type or Full.Type::Method (comma-separated).")
            : string.Empty;

        Console.WriteLine();
        WriteAccent("READY TO START");
        WriteLabel("Action", action.Title);
        WriteLabel("Input", gamePath);
        WriteLabel("Output", outputPath);
        if (exportsCSharp)
            WriteLabel("C# assemblies", sourceAssemblies ?? "All");
        WriteLabel("Low memory", lowMemory ? "On" : "Off");
        WriteLabel("Verbose log", verbose ? "On" : "Off");
        WriteLabel("Reuse singletons", reuseSingletons ? "On" : "Off");
        if (recoveryTargets.Length != 0)
            WriteLabel("Recovery targets", recoveryTargets);
        Console.WriteLine();

        if (!AskYesNo("Start recovery now?", defaultValue: true))
            return null;

        var args = new System.Collections.Generic.List<string>
        {
            "--game-path", gamePath,
            "--output-as", action.FormatId
        };
        if (action.AdditionalFormatId is { } additionalFormat) args.Add(additionalFormat);
        args.Add("--output-to");
        args.Add(outputPath);
        if (sourceAssemblies != null)
        {
            args.Add("--csharp-assemblies");
            args.Add(sourceAssemblies);
        }
        if (lowMemory) args.Add("--low-memory-mode");
        if (verbose) args.Add("--verbose");
        if (reuseSingletons) args.Add("--reuse-singleton-instances");
        if (recoveryTargets.Length != 0)
        {
            args.Add("--recover-only");
            args.Add(recoveryTargets);
        }
        return args.ToArray();
    }

    private static string? SelectInputPath(int inputKind)
    {
        if (inputKind == 2)
            return PromptValidInputPath();

        var result = inputKind == 0
            ? NativeFilePicker.PickGamePackage()
            : NativeFilePicker.PickFolder("Select a Unity game folder");

        if (result.Status == NativePickerStatus.Cancelled)
            return null;
        if (result.Status == NativePickerStatus.Selected && result.Path != null)
            return result.Path;

        WriteStatus("A native picker is unavailable on this system. Enter a path below.", ConsoleColor.Yellow);
        return PromptValidInputPath();
    }

    private static string? PromptValidInputPath()
    {
        while (true)
        {
            var path = Prompt("GAME PATH", "APK / XAPK / APKM / IPA / TIPA or Unity game folder");
            if (File.Exists(path) || Directory.Exists(path))
                return path;
            WriteStatus("That path does not exist. Choose a file/folder or press Ctrl+C to cancel.", ConsoleColor.Yellow);
        }
    }

    private static string Prompt(string label, string hint)
    {
        Console.WriteLine();
        WriteAccent(label);
        WriteMuted("  " + hint);
        Console.Write("  › ");
        return (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
    }

    private static bool AskYesNo(string question, bool defaultValue)
    {
        var options = defaultValue ? new[] { "Yes", "No" } : new[] { "No", "Yes" };
        var selected = Select(question, options, 0);
        return selected >= 0 && (defaultValue ? selected == 0 : selected == 1);
    }

    private static int Select(string title, string[] options, int selected)
    {
        WriteAccent("◆  " + title);
        var top = Console.CursorTop;
        RenderOptions(options, selected);

        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.UpArrow)
                selected = (selected + options.Length - 1) % options.Length;
            else if (key == ConsoleKey.DownArrow)
                selected = (selected + 1) % options.Length;
            else if (key is ConsoleKey.Enter or ConsoleKey.Spacebar)
            {
                Console.WriteLine();
                Console.WriteLine();
                return selected;
            }
            else if (key == ConsoleKey.Escape || key == ConsoleKey.Q)
            {
                Console.WriteLine();
                return -1;
            }
            else
                continue;

            try
            {
                Console.SetCursorPosition(0, top);
                RenderOptions(options, selected);
            }
            catch (IOException)
            {
                return SelectByNumber(options, selected);
            }
            catch (ArgumentOutOfRangeException)
            {
                return SelectByNumber(options, selected);
            }
        }
    }

    private static void RenderOptions(string[] options, int selected)
    {
        var maxWidth = Math.Max(30, TerminalWidth() - 8);
        for (var i = 0; i < options.Length; i++)
        {
            var prefix = i == selected ? "  › " : "    ";
            var color = i == selected ? ConsoleColor.Green : ConsoleColor.Gray;
            WriteColoredLine(prefix + Fit(options[i], maxWidth), color, i == selected && _ansi ? "1;38;2;71;232;143" : null);
        }
    }

    private static int SelectByNumber(string[] options, int defaultValue)
    {
        Console.WriteLine("Enter a number to select:");
        for (var i = 0; i < options.Length; i++)
            Console.WriteLine($"  {i + 1}. {options[i]}");
        Console.Write($"  [{defaultValue + 1}] › ");
        var input = Console.ReadLine();
        return int.TryParse(input, out var value) && value >= 1 && value <= options.Length ? value - 1 : defaultValue;
    }

    private static void WriteLabel(string label, string value)
    {
        WriteMuted("  " + label.PadRight(18));
        Console.WriteLine(value);
    }

    private static void WriteAccent(string text) => WriteColoredLine(text, ConsoleColor.Green, _ansi ? "1;38;2;71;232;143" : null);

    private static void WriteMuted(string text)
    {
        if (_ansi)
            Console.Write("\u001b[38;2;140;157;148m" + text + "\u001b[0m");
        else if (_color)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(text);
            Console.ForegroundColor = old;
        }
    }

    private static void WriteStatus(string text, ConsoleColor color) => WriteColoredLine("  !  " + text, color, _ansi ? "38;2;245;190;90" : null);

    internal static void WriteLog(string level, string source, string message, ConsoleColor fallbackColor, string ansiColor)
    {
        lock (ConsoleLock)
        {
            ClearProgressLine();
            var cleanMessage = message.TrimEnd('\r', '\n');
            if (_ansi)
                Console.WriteLine($"\u001b[{ansiColor}m{level.PadLeft(5)}\u001b[0m  \u001b[38;2;126;144;134m{source}\u001b[0m  {cleanMessage}");
            else if (_color)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = fallbackColor;
                Console.Write(level.PadLeft(5));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  " + source + "  ");
                Console.ForegroundColor = old;
                Console.WriteLine(cleanMessage);
            }
            else
                Console.WriteLine($"{level.PadLeft(5)}  {source}  {cleanMessage}");

            if (_progressActive)
                RenderProgressLine();
        }
    }

    internal static bool ObserveInfo(string message, string source)
    {
        if (source == "Progress" && TryReadCounts(message, out var completed, out var total))
        {
            SetProgress(70 + (int)Math.Round(completed * 27d / total), "Recovering method bodies", $"{completed:N0} / {total:N0} methods");
            return true;
        }

        var line = message.Trim();
        if (line.IndexOf("Attempting to extract", StringComparison.Ordinal) >= 0)
            SetProgress(5, "Extracting game files", "Reading the Unity package.");
        else if (line.IndexOf("Reading globalgamemanagers", StringComparison.Ordinal) >= 0)
            SetProgress(10, "Detecting Unity version", "Inspecting the Player data.");
        else if (line.IndexOf("Initializing Metadata", StringComparison.Ordinal) >= 0)
            SetProgress(18, "Loading IL2CPP metadata", "Parsing global metadata.");
        else if (line.IndexOf("Searching Binary", StringComparison.Ordinal) >= 0)
            SetProgress(28, "Analyzing native binary", "Locating IL2CPP structures and methods.");
        else if (line.IndexOf("Creating application model", StringComparison.Ordinal) >= 0)
            SetProgress(42, "Building game model", "Linking assemblies, types and methods.");
        else if (line.IndexOf("Pre-processing processing layers", StringComparison.Ordinal) >= 0)
            SetProgress(57, "Preparing analysis", "Running selected processors.");
        else if (line.IndexOf("Invoking processing layers", StringComparison.Ordinal) >= 0)
            SetProgress(63, "Analyzing game", "Applying processing layers.");
        else if (line.IndexOf("Finding key function addresses", StringComparison.Ordinal) >= 0)
            SetProgress(68, "Preparing method recovery", "Resolving native function addresses.");
        else if (line.StartsWith("Outputting as ", StringComparison.Ordinal))
            SetProgress(70, "Generating output", "Building managed assemblies.");
        else if (line.IndexOf("successfully decompiled (", StringComparison.Ordinal) >= 0)
            SetProgress(98, "Writing final DLL files", line);
        else if (line.StartsWith("Done. Total execution time", StringComparison.Ordinal))
            SetProgress(100, "Complete", "MC2IL finished processing.");
        return false;
    }

    public static void BeginProgress()
    {
        lock (ConsoleLock)
        {
            _progressActive = true;
            _percent = 0;
            _stage = "Starting analysis";
            _methodCount = string.Empty;
            RenderProgressLine();
        }
    }

    public static void FinishProgress(bool success, string? outputPath, string completionMessage = "Recovery finished")
    {
        lock (ConsoleLock)
        {
            if (!_progressActive)
                return;
            ClearProgressLine();
            if (success)
            {
                _percent = 100;
                _stage = "Complete";
                _methodCount = string.Empty;
            }
            RenderProgressLine();
            Console.WriteLine();
            _progressActive = false;
            Console.WriteLine();
            if (success)
            {
                WriteColoredLine("  ✓  " + completionMessage, ConsoleColor.Green, _ansi ? "1;38;2;71;232;143" : null);
                if (!string.IsNullOrWhiteSpace(outputPath))
                    Console.WriteLine("     Output: " + outputPath);
            }
            else
                WriteColoredLine("  ✕  Recovery stopped. Review the log above.", ConsoleColor.Red, _ansi ? "1;38;2;255;115;115" : null);
            Console.WriteLine();
        }
    }

    private static void SetProgress(int percent, string stage, string methodCount)
    {
        lock (ConsoleLock)
        {
            _percent = Math.Min(100, Math.Max(_percent, percent));
            _stage = stage;
            _methodCount = methodCount;
            if (_progressActive)
                RenderProgressLine();
        }
    }

    private static void RenderProgressLine()
    {
        var stage = Fit(_stage, 28);
        var methodCount = Fit(_methodCount, 24);
        var availableWidth = TerminalWidth() - stage.Length - methodCount.Length - 13;
        var barWidth = Math.Max(8, Math.Min(32, availableWidth));
        var filled = Math.Min(barWidth, _percent * barWidth / 100);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        ClearProgressLine();
        if (_ansi)
            Console.Write($"\r\u001b[38;2;71;232;143m{bar[..filled]}\u001b[38;2;67;81;74m{bar[filled..]}\u001b[0m  {_percent,3}%  {stage}  {methodCount}");
        else if (_color || !Console.IsOutputRedirected)
        {
            Console.Write("\r");
            if (_color)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(bar[..filled]);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(bar[filled..]);
                Console.ResetColor();
            }
            else
                Console.Write(bar);
            Console.Write($"  {_percent,3}%  {stage}  {methodCount}");
        }
        else
            Console.WriteLine($"[{new string('#', filled)}{new string('-', barWidth - filled)}] {_percent,3}%  {stage}  {methodCount}");
    }

    private static void ClearProgressLine()
    {
        if (!_progressActive || Console.IsOutputRedirected)
            return;
        if (_ansi)
            Console.Write("\r\u001b[2K");
        else
            Console.Write("\r" + new string(' ', Math.Max(20, TerminalWidth() - 1)) + "\r");
    }

    private static bool TryReadCounts(string message, out int completed, out int total)
    {
        completed = 0;
        total = 0;
        const string marker = "Decompiling methods:";
        var index = message.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var counts = message.Substring(index + marker.Length).Trim().Split('/');
        return counts.Length == 2 && int.TryParse(counts[0].Trim(), out completed) && int.TryParse(counts[1].Trim(), out total) && total > 0;
    }

    private static void WriteColoredLine(string text, ConsoleColor fallbackColor, string? ansiColor)
    {
        if (_ansi && ansiColor != null)
            Console.WriteLine($"\u001b[{ansiColor}m{text}\u001b[0m");
        else if (_color)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = fallbackColor;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
        else
            Console.WriteLine(text);
    }

    private static bool EnableWindowsVirtualTerminalProcessing()
    {
        try
        {
            var handle = GetStdHandle(-11);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode))
                return false;
            return SetConsoleMode(handle, mode | 0x0004);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static int TerminalWidth()
    {
        try { return Math.Max(60, Console.WindowWidth); }
        catch (IOException) { return 100; }
        catch (PlatformNotSupportedException) { return 100; }
        catch (InvalidOperationException) { return 100; }
    }

    private static string Fit(string text, int width)
        => text.Length <= width ? text : text.Substring(0, Math.Max(1, width - 1)) + "…";

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private sealed record OutputAction(string Title, string FormatId, string Description, string? AdditionalFormatId = null);
}
