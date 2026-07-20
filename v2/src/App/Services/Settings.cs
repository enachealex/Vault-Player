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

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Corrupt settings should never block startup.
        }
        return new Settings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
