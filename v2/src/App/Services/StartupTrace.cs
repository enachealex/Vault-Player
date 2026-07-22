using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoPlayer.App.Services;

/// <summary>
/// On-demand startup profiler. Records elapsed-ms marks for each launch phase;
/// writes them to %APPDATA%\VideoPlayerV2\startup.log only when the environment
/// variable VAULT_STARTUP_TRACE is set, so it stays silent (and free) on normal
/// runs but is there the moment someone reports a slow launch. Recording a mark
/// is a Stopwatch read plus a StringBuilder append — negligible.
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly StringBuilder Log = new();
    private static long _last;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("VAULT_STARTUP_TRACE") is { Length: > 0 };

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoPlayerV2", "startup.log");

    public static void Mark(string phase)
    {
        var now = Clock.ElapsedMilliseconds;
        Log.AppendLine($"  +{now - _last,6} ms   (t={now,6} ms)   {phase}");
        _last = now;
    }

    /// <summary>Append a one-off line (e.g. a background task finishing after Flush).</summary>
    public static void Note(string text)
    {
        if (!Enabled) return;
        try { lock (Log) File.AppendAllText(LogPath, $"  (t={Clock.ElapsedMilliseconds,6} ms)   {text}\n"); }
        catch { }
    }

    public static void Flush()
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Wall time since the process actually started captures the pre-main
            // cost (CLR + assembly load) the Stopwatch can't see.
            var uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            lock (Log)
                File.AppendAllText(LogPath,
                    $"=== launch {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                    $"(traced {Clock.ElapsedMilliseconds} ms; process uptime {uptime:0} ms to window) ===\n{Log}\n");
        }
        catch { }
    }
}
