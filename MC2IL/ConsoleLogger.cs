using MC2IL.Core.Logging;

namespace MC2IL;

internal static class ConsoleLogger
{
    internal static bool ShowVerbose { private get; set; }

    public static void Initialize()
    {
        Logger.InfoLog += WriteInfo;
        Logger.WarningLog += (message, source) => ConsoleUi.WriteLog("WARN", source, message, System.ConsoleColor.Yellow, "1;38;2;245;190;90");
        Logger.ErrorLog += (message, source) => ConsoleUi.WriteLog("ERROR", source, message, System.ConsoleColor.Red, "1;38;2;255;115;115");
        Logger.VerboseLog += (message, source) =>
        {
            if (ShowVerbose)
                ConsoleUi.WriteLog("TRACE", source, message, System.ConsoleColor.DarkGray, "38;2;140;157;148");
        };
    }

    private static void WriteInfo(string message, string source)
    {
        if (ConsoleUi.ObserveInfo(message, source))
            return;
        ConsoleUi.WriteLog("INFO", source, message, System.ConsoleColor.Gray, "38;2;190;207;197");
    }
}
