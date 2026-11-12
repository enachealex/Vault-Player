namespace VideoPlayer.App.Helpers;

public enum VideoClickAction
{
    None,
    /// <summary>Play or pause straight away.</summary>
    TogglePlay,
    /// <summary>Second click in the middle: take back the first click's toggle and go fullscreen.</summary>
    UndoToggleAndFullscreen,
    /// <summary>Second click: fullscreen, leaving the first click's toggle alone.</summary>
    Fullscreen,
    /// <summary>Second click on a side: take back the first click's toggle and begin skipping.</summary>
    UndoToggleAndStartSkip,
    /// <summary>Another click on the side already skipping.</summary>
    ContinueSkip,
}

/// <summary>
/// Decides what a click on the picture means. A click plays or pauses, a
/// double-click on the left or right third skips, each further click on that
/// side skips again, and a double-click in the middle goes fullscreen.
///
/// Kept apart from the view so the rules can be exercised without a window.
/// </summary>
public sealed class VideoClickTracker
{
    /// <summary>How long after a skip another click on that side still counts as skipping.</summary>
    public const long StreakWindowMs = 1000;

    private int _streakSide;
    private long _streakUntil;

    /// <summary>The side of the current streak: -1 left, 1 right, 0 none.</summary>
    public int Side => _streakSide;

    public VideoClickAction Click(double fraction, int clickCount, bool ownsTimeline, long nowMs)
    {
        var side = fraction < 1.0 / 3 ? -1 : fraction > 2.0 / 3 ? 1 : 0;

        // Checked before the click count: WPF numbers a run of quick clicks
        // 1, 2, 3... but a slower one starts again from 1, and both have to
        // keep skipping.
        if (_streakSide != 0 && side == _streakSide && nowMs < _streakUntil)
        {
            _streakUntil = nowMs + StreakWindowMs;
            return VideoClickAction.ContinueSkip;
        }
        _streakSide = 0;

        return clickCount switch
        {
            1 => VideoClickAction.TogglePlay,
            // A guest's click is a request sent to the host, not a toggle, so
            // there is nothing to take back, and they cannot skip.
            2 when !ownsTimeline => VideoClickAction.Fullscreen,
            2 when side == 0 => VideoClickAction.UndoToggleAndFullscreen,
            2 => StartStreak(side, nowMs),
            _ => VideoClickAction.None,
        };
    }

    private VideoClickAction StartStreak(int side, long nowMs)
    {
        _streakSide = side;
        _streakUntil = nowMs + StreakWindowMs;
        return VideoClickAction.UndoToggleAndStartSkip;
    }
}
