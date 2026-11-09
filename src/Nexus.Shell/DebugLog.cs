using System.IO;

namespace Nexus.Shell;

/// <summary>Developer trace, written to %TEMP%\nexus-debug.log only when NEXUS_DEBUG=1.</summary>
internal static class DebugLog
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("NEXUS_DEBUG") == "1";
    private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "nexus-debug.log");

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch (IOException)
        {
        }
    }
}
