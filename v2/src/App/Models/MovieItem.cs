using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VideoPlayer.App.Models;

public enum MovieKind
{
    /// <summary>A video file on disk that this app plays itself.</summary>
    Local,
    /// <summary>
    /// A title the user owns on a streaming service. DRM means we can never
    /// play it here — the card just launches the service.
    /// </summary>
    Shortcut,
    /// <summary>
    /// A film held on the Vault server and streamed from it. Plays exactly like
    /// a local file — libVLC does not care that the bytes arrive over HTTP —
    /// but nothing about it exists on this machine.
    /// </summary>
    Server,
}

/// <summary>A row in the library: either a local video file or a service shortcut.</summary>
public class MovieItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    /// <summary>File path for local movies; the launch URL for shortcuts.</summary>
    public required string Path { get; init; }
    public long SizeBytes { get; init; }

    public MovieKind Kind { get; init; } = MovieKind.Local;
    /// <summary>Service display name, e.g. "Prime Video". Shortcuts only.</summary>
    public string? Service { get; init; }

    public bool IsShortcut => Kind == MovieKind.Shortcut;
    public bool IsOnServer => Kind == MovieKind.Server;

    /// <summary>The server's id for this film. Server items only.</summary>
    public string? ItemKey { get; init; }

    /// <summary>Poster URL on the server, fetched with the account token. Server items only.</summary>
    public string? ArtUrl { get; init; }

    /// <summary>
    /// Cross-machine identity: filename + exact byte size. The same rip in a
    /// different folder on another PC produces the same key, which is how synced
    /// progress and watch counts follow the film rather than the path.
    ///
    /// A server film carries the key the server computed from the real file, so
    /// streaming a film and playing your own copy of it are the same film.
    /// </summary>
    public string FilmKey => ServerFilmKey ?? $"{System.IO.Path.GetFileName(Path)}|{SizeBytes}";

    /// <summary>Set from the catalogue; null for anything local.</summary>
    public string? ServerFilmKey { get; init; }

    /// <summary>
    /// What the local per-film stores (resume positions, watch counts, chapters)
    /// are keyed on.
    ///
    /// For a local file that is its path. It cannot be for a server film: the
    /// URL carries a signed token that is reissued every time the catalogue is
    /// fetched, so keying on it would file every session under a new film and
    /// lose the resume point each time. Film identity is stable, so use that.
    /// </summary>
    public string StoreKey => IsOnServer ? "vault:" + FilmKey : Path;

    public Visibility ShortcutBadgeVisibility =>
        IsShortcut ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Marks a card as living on the server rather than in this folder.</summary>
    public Visibility ServerBadgeVisibility =>
        IsOnServer ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _thumbnail;
    /// <summary>Poster frame; a frozen bitmap so it can be built off-thread.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    private long? _durationMs;
    public long? DurationMs
    {
        get => _durationMs;
        set
        {
            _durationMs = value;
            Notify(nameof(DurationMs), nameof(MetaText), nameof(ProgressFraction), nameof(ResumeText));
        }
    }

    private long _resumeMs;
    /// <summary>Saved playback position (0 = not started / finished).</summary>
    public long ResumeMs
    {
        get => _resumeMs;
        set
        {
            _resumeMs = value;
            Notify(nameof(ResumeMs), nameof(ProgressFraction), nameof(ResumeText));
        }
    }

    private int _watchCount;
    /// <summary>How many times this movie has been watched through.</summary>
    public int WatchCount
    {
        get => _watchCount;
        set
        {
            _watchCount = value;
            Notify(nameof(WatchCount), nameof(WatchBadgeText), nameof(WatchBadgeVisibility), nameof(WatchTooltip));
        }
    }

    public string WatchBadgeText => WatchCount > 0 ? $"{WatchCount}×" : "";

    public Visibility WatchBadgeVisibility => WatchCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string WatchTooltip => WatchCount switch
    {
        <= 0 => "Not watched yet",
        1 => "Watched once",
        2 => "Watched twice",
        _ => $"Watched {WatchCount} times",
    };

    /// <summary>How far through the movie the viewer is, 0..1.</summary>
    public double ProgressFraction =>
        DurationMs is > 0 && ResumeMs > 0 ? Math.Clamp(ResumeMs / (double)DurationMs.Value, 0, 1) : 0;

    /// <summary>"38 min left" style label for the continue-watching row.</summary>
    public string ResumeText
    {
        get
        {
            if (DurationMs is not > 0 || ResumeMs <= 0) return "";
            var left = TimeSpan.FromMilliseconds(Math.Max(0, DurationMs.Value - ResumeMs));
            if (left.TotalMinutes < 1) return "almost done";
            return left.TotalHours >= 1
                ? $"{(int)left.TotalHours}h {left.Minutes}m left"
                : $"{(int)left.TotalMinutes} min left";
        }
    }

    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public string MetaText
    {
        get
        {
            // Shortcuts have no file behind them — name the service instead.
            if (IsShortcut) return Service ?? "Streaming";

            var size = SizeBytes >= 1L << 30
                ? $"{SizeBytes / (double)(1L << 30):0.##} GB"
                : $"{SizeBytes / (double)(1L << 20):0} MB";
            if (DurationMs is not > 0) return size;
            var t = TimeSpan.FromMilliseconds(DurationMs.Value);
            var dur = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            return $"{dur}  ·  {size}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
