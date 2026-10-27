using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer.App.Helpers;

/// <summary>
/// Feeds the timeline hover preview: one frame is fetched at a time, and when
/// it lands the next fetch is for wherever the pointer has got to by then.
///
/// The obvious design — cancel the fetch in progress whenever the pointer
/// reaches a new position — starves completely. A frame grab takes 100 ms to a
/// second, while a moving pointer crosses into a new five-second slot every
/// 10–20 ms, so every grab is cancelled before it can finish and nothing
/// appears until the pointer stops. Here a grab in progress is never abandoned
/// for a newer position; only the latest position is remembered, and it is
/// fetched next. The preview keeps moving at the speed ffmpeg can deliver.
///
/// Single-threaded by design: call it from the UI thread, which is also where
/// the awaited fetches resume.
/// </summary>
public sealed class ScrubFrameLoader : IDisposable
{
    private readonly Func<double, CancellationToken, Task<string?>> _fetch;
    private readonly Action<double, string> _show;

    private CancellationTokenSource? _cts;
    private double? _inFlight;
    private double? _next;
    private double? _shown;

    /// <summary>
    /// Positions that produced no frame, and when. Asking again straight away
    /// would only start ffmpeg to fail again — but a failure can be passing (a
    /// disk still spinning up), so after a while the position gets another try.
    /// </summary>
    private readonly Dictionary<double, long> _failedAt = new();
    private const long RetryFailedAfterMs = 30_000;

    /// <summary>Bumped by <see cref="Cancel"/> so a fetch that lands afterwards is ignored.</summary>
    private int _generation;
    private bool _disposed;

    /// <param name="fetch">Produces the frame for a position, or null if there is none.</param>
    /// <param name="show">Puts a fetched frame on screen.</param>
    public ScrubFrameLoader(Func<double, CancellationToken, Task<string?>> fetch, Action<double, string> show)
    {
        _fetch = fetch;
        _show = show;
    }

    /// <summary>The pointer is over this position, already bucketed by the caller.</summary>
    public void Request(double bucket)
    {
        if (_disposed) return;
        if (_inFlight is not null)
        {
            // Busy: remember only where the pointer is now. Coming back to the
            // position already being fetched needs nothing further.
            _next = bucket == _inFlight || RecentlyFailed(bucket) ? null : bucket;
            return;
        }
        if (bucket == _shown || RecentlyFailed(bucket)) return;
        _ = FetchAsync(bucket);
    }

    private bool RecentlyFailed(double bucket) =>
        _failedAt.TryGetValue(bucket, out var at) && Environment.TickCount64 - at < RetryFailedAfterMs;

    /// <summary>
    /// The preview is gone — pointer left, or a drag took over. Stops the grab
    /// in progress and forgets what was on screen, so coming back shows a frame
    /// again even for the same position.
    /// </summary>
    public void Cancel()
    {
        _generation++;
        _cts?.Cancel();
        _cts = null;
        _inFlight = null;
        _next = null;
        _shown = null;
    }

    public void Dispose()
    {
        Cancel();
        _disposed = true;
    }

    private async Task FetchAsync(double bucket)
    {
        var generation = _generation;
        _cts ??= new CancellationTokenSource();
        var token = _cts.Token;
        _inFlight = bucket;

        string? path = null;
        try
        {
            path = await _fetch(bucket, token);
        }
        catch
        {
            // Cancelled, or the grab failed. Either way there is no frame.
        }

        // Cancel() already reset everything for a fetch it stopped.
        if (generation != _generation) return;
        _inFlight = null;

        if (path is null)
        {
            _failedAt[bucket] = Environment.TickCount64;
        }
        else
        {
            _shown = bucket;
            try
            {
                _show(bucket, path);
            }
            catch
            {
                // A frame that cannot be displayed leaves the previous one up.
            }
        }

        if (_next is double next)
        {
            _next = null;
            Request(next);
        }
    }
}
