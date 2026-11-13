using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VideoPlayer.App.Services;

/// <summary>
/// Whether libVLC's plugin cache still describes the plugins beside it.
///
/// plugins.dat records each plugin's size and modified time, and libVLC drops
/// any entry whose file no longer matches, loads that plugin the slow way, and
/// never writes the cache back. Velopack re-stamps every file's modified time
/// when it extracts an install or update, and the portable zip keeps none, so a
/// cache that travels inside the package arrives stale on every machine and
/// each launch rescans all ~320 plugins.
///
/// libVLC has no way to ask whether its cache matched, so this keeps its own
/// record of the plugins as they were when the cache was written: the same
/// path, size and modified second that libVLC compares.
/// </summary>
public static class PluginCache
{
    private static string PluginsDir =>
        Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins");

    private static string CachePath => Path.Combine(PluginsDir, "plugins.dat");

    private static string StampPath => Path.Combine(PluginsDir, "plugins.dat.stamp");

    /// <summary>True when plugins.dat exists and no plugin has changed since it was written.</summary>
    public static bool IsCurrent()
    {
        try { return File.Exists(CachePath) && File.ReadAllText(StampPath) == Fingerprint(); }
        catch { return false; }
    }

    /// <summary>Record the plugins as they are now. Call right after libVLC wrote the cache.</summary>
    public static void MarkCurrent()
    {
        try { File.WriteAllText(StampPath, Fingerprint()); }
        catch { /* a read-only install: libVLC could not write the cache either */ }
    }

    private static string Fingerprint()
    {
        var text = new StringBuilder();
        foreach (var dll in new DirectoryInfo(PluginsDir)
                     .EnumerateFiles("*.dll", SearchOption.AllDirectories)
                     .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(Path.GetRelativePath(PluginsDir, dll.FullName))
                .Append('|').Append(dll.Length)
                .Append('|').Append(new DateTimeOffset(dll.LastWriteTimeUtc).ToUnixTimeSeconds())
                .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
