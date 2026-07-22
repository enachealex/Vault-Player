using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoPlayer.App.Services;

/// <summary>A user-created chapter mark within a movie.</summary>
public class ChapterMark
{
    public string Name { get; set; } = "";
    public long TimeMs { get; set; }
}

/// <summary>One film's synced activity, keyed by "filename|size" in SyncedLibrary.</summary>
public class SyncedItem
{
    public string Name { get; set; } = "";
    public long ResumeMs { get; set; }
    public int WatchCount { get; set; }
    public long LastWatchedAt { get; set; }
    public string? ChaptersJson { get; set; }
    /// <summary>Unix ms of the last local change; drives last-writer-wins.</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// A title the user owns on a streaming service. We can't play DRM-protected
/// content, so this is purely a launcher entry that sits in the library beside
/// the local files.
/// </summary>
public class StreamingShortcut
{
    public string Title { get; set; } = "";
    public string Service { get; set; } = "";
    /// <summary>Direct link to the title. Blank = fall back to a service search.</summary>
    public string? Url { get; set; }
    public long AddedAt { get; set; }
}

/// <summary>
/// Simple JSON settings in %APPDATA%\VideoPlayerV2. (Moves to SQLite when the
/// library grows richer metadata.)
/// </summary>
public class Settings
{
    public string? LastFolder { get; set; }

    // Playback preferences (v1 parity).
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public double Rate { get; set; } = 1.0;
    /// <summary>PlayOnce | AutoplayNext | LoopOne | LoopAll</summary>
    public string PlaybackMode { get; set; } = "AutoplayNext";

    /// <summary>
    /// Chosen audio output device (libVLC device identifier), or null for the
    /// system default. Lets someone route sound to headphones when libVLC picked
    /// the wrong device.
    /// </summary>
    public string? AudioOutputDevice { get; set; }

    /// <summary>Resume positions (ms) keyed by file path — the recents store.</summary>
    public Dictionary<string, long> ResumePositions { get; set; } = new();

    /// <summary>When each movie was last watched (unix ms) — orders "Continue watching".</summary>
    public Dictionary<string, long> LastWatchedAt { get; set; } = new();

    /// <summary>How many times each movie has been watched through.</summary>
    public Dictionary<string, int> WatchCounts { get; set; } = new();

    /// <summary>User-created chapter marks keyed by file path.</summary>
    public Dictionary<string, List<ChapterMark>> CustomChapters { get; set; } = new();

    /// <summary>Streaming titles shown in the library as launch-only cards.</summary>
    public List<StreamingShortcut> Shortcuts { get; set; } = new();

    // ---- Caption styling (text subtitles only; bitmap/PGS can't be restyled) ----
    /// <summary>Text size as a percentage of the default. 50–250.</summary>
    public int SubtitleScalePct { get; set; } = 110;
    /// <summary>Outline weight around each glyph, 0–8. This is the readability knob.</summary>
    public int SubtitleOutline { get; set; } = 4;
    public bool SubtitleBold { get; set; } = true;
    /// <summary>Opacity of the box behind the text, 0 (none) – 255 (solid).</summary>
    public int SubtitleBackgroundOpacity { get; set; }

    // ---- Account (optional; null = signed out, everything works offline) ----
    /// <summary>Backend base URL, e.g. https://party.thejumpvault.com. Null = built-in default.</summary>
    public string? AuthServer { get; set; }
    public string? AccountEmail { get; set; }
    public string? AccountName { get; set; }
    /// <summary>Long-lived refresh token; the only credential kept on disk.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Library activity keyed by film identity ("filename|size") rather than
    /// path, so it can sync across machines. Maintained alongside the path-keyed
    /// stores above; this is the copy that goes to and from the server.
    /// </summary>
    public Dictionary<string, SyncedItem> SyncedLibrary { get; set; } = new();

    // Watch Party.
    /// <summary>Most recently used nickname.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Nicknames offered as cards next time, most recent first.</summary>
    public List<string> SavedNames { get; set; } = new();
    public string? LastPartyAddress { get; set; }
    /// <summary>Deployed rendezvous to host through (e.g. "party.example.com"); null = run one locally.</summary>
    public string? RendezvousServer { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayerV2", "settings.json");

    private static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Load settings, falling back to the previous good copy before ever
    /// falling back to defaults.
    ///
    /// This used to return fresh defaults whenever the file failed to parse,
    /// and the next Save would then write those defaults straight over the
    /// user's data. Combined with a non-atomic write (see Save), an update
    /// restarting the app mid-write was enough to lose saved names, resume
    /// positions and watch counts permanently.
    /// </summary>
    public static Settings Load()
    {
        if (TryRead(FilePath, out var settings)) return settings!;

        // The main file is missing or unreadable. Keep whatever is there for
        // inspection rather than letting the next Save overwrite it.
        if (File.Exists(FilePath))
        {
            try { File.Move(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
        }

        if (TryRead(BackupPath, out var backup))
        {
            // Write the recovered state straight back. Otherwise it exists only
            // in the backup until something happens to trigger a save, and a
            // crash before that would lose it after all.
            backup!.Save();
            return backup;
        }
        return new Settings();
    }

    private static bool TryRead(string path, out Settings? settings)
    {
        settings = null;
        try
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return false;   // truncated write
            settings = JsonSerializer.Deserialize<Settings>(text);
            return settings is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        try
        {
            // Keep the resume store bounded.
            if (ResumePositions.Count > 200)
            {
                foreach (var key in ResumePositions.Keys.Take(ResumePositions.Count - 200).ToList())
                    ResumePositions.Remove(key);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // Write somewhere else first, then swap it in. A direct write leaves
            // a half-written file if the process dies mid-save -- which is
            // exactly what an update does when it restarts the app. File.Replace
            // also keeps the previous copy, which Load falls back to.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else File.Move(temp, FilePath);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
