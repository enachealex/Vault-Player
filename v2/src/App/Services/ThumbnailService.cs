using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// Poster frames via ffmpeg fast-seek frame grabs. Each thumbnail is cached on
/// disk keyed by file identity (path + size + mtime), so a library only pays
/// the extraction cost once — afterwards cards fill instantly from cache.
/// </summary>
public static class ThumbnailService
{
    private static readonly SemaphoreSlim Gate = new(3);

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayerV2", "thumbs");

    /// <summary>Best available ffmpeg.exe: next to the app, or the repo tools folder (dev).</summary>
    public static string? FindFfmpeg()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    /// <summary>
    /// The slot a hovered time falls into. Hovering sweeps across hundreds of
    /// positions a second and a distinct frame for each would be pointless;
    /// five seconds is finer than anyone can aim at on a scrub bar. The preview
    /// and the cache must agree on this, so both go through here.
    ///
    /// Never past the end: rounding the last pixel of the bar up lands beyond
    /// the final frame, where ffmpeg succeeds without writing anything, and the
    /// end of every film would be left without a preview.
    /// </summary>
    public static double ScrubBucket(double seconds, double durationSeconds = double.MaxValue) =>
        Math.Max(0, Math.Min(Math.Round(seconds / 5.0), Math.Floor(durationSeconds / 5.0)) * 5.0);

    /// <summary>
    /// Films where the keyframe grab keeps coming back empty while the plain grab
    /// works — streams that flag no keyframes. After a few such misses in a row
    /// the plain grab goes first, rather than paying for two launches per frame.
    /// Misses must be consecutive: the last few seconds of every film miss once
    /// too (there is no keyframe after the seek point), and that alone must not
    /// switch the fast path off for the whole film.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> KeyframeMisses = new();
    private const int KeyframeMissLimit = 3;

    /// <summary>
    /// A small frame at a given point in a film, for the timeline hover preview.
    /// Returns the cached file path, or null if it could not be produced.
    /// </summary>
    public static async Task<string?> ExtractFrameAsync(string videoPath, double atSeconds, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || !File.Exists(videoPath)) return null;

        var bucket = ScrubBucket(atSeconds);
        var key = $"{videoPath}|scrub|{bucket}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)));
        var cached = Path.Combine(CacheDir, hash + ".jpg");

        EnsureCacheDir();
        if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

        // Share the same gate as poster generation so a fast scrub cannot
        // saturate the machine with ffmpeg processes.
        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

            // The keyframe nearest the target is plenty for a preview and far
            // cheaper than decoding forward to the exact frame. The plain grab
            // is the fallback.
            var tryKeyframe = KeyframeMisses.GetValueOrDefault(videoPath) < KeyframeMissLimit;
            if (tryKeyframe
                && await RunFfmpegAsync(ffmpeg, videoPath, cached, bucket, ct, width: 240, keyframeOnly: true))
            {
                KeyframeMisses[videoPath] = 0;
                return cached;
            }
            if (!await RunFfmpegAsync(ffmpeg, videoPath, cached, bucket, ct, width: 240)) return null;
            if (tryKeyframe) KeyframeMisses.AddOrUpdate(videoPath, 1, (_, misses) => misses + 1);
            return cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task EnsureThumbsAsync(IEnumerable<MovieItem> items, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        EnsureCacheDir();

        var tasks = items.Select(async item =>
        {
            await Gate.WaitAsync(ct);
            try
            {
                var cached = CachePathFor(item);
                if (!File.Exists(cached) || new FileInfo(cached).Length == 0)
                {
                    if (ffmpeg is null) return; // no extractor available; keep placeholder icon
                    await ExtractAsync(ffmpeg, item, cached, ct);
                }
                if (File.Exists(cached) && new FileInfo(cached).Length > 0)
                    item.Thumbnail = LoadFrozen(cached);
            }
            catch
            {
                // A failed thumbnail must never break the library.
            }
            finally
            {
                Gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string CachePathFor(MovieItem item)
    {
        var info = new FileInfo(item.Path);
        var key = $"{item.Path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheDir, hash + ".jpg");
    }

    private static async Task ExtractAsync(string ffmpeg, MovieItem item, string outPath, CancellationToken ct)
    {
        // Grab a frame from ~10% in (movies open with studio logos/black); fall
        // back to 5s for short or misbehaving files.
        var primary = item.DurationMs is > 0
            ? Math.Clamp(item.DurationMs.Value / 1000.0 * 0.10, 5, 600)
            : 60;

        if (await RunFfmpegAsync(ffmpeg, item.Path, outPath, primary, ct)) return;
        await RunFfmpegAsync(ffmpeg, item.Path, outPath, 5, ct);
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string input, string output, double atSeconds,
        CancellationToken ct, int width = 460, bool keyframeOnly = false)
    {
        // ffmpeg writes to a temporary name that is renamed into place only once
        // the frame is complete. Writing straight to the cache path let a reader
        // load a half-written jpg, and a grab stopped part-way would leave one
        // behind that passes the "exists and is not empty" check for good. The
        // temporary name keeps the .jpg ending because ffmpeg picks its output
        // format from it.
        var temp = Path.Combine(Path.GetDirectoryName(output)!,
            $"{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.part.jpg");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList quotes each argument itself, so no film name can end one
        // argument early and start another.
        foreach (var arg in FfmpegArgs(input, temp, atSeconds, width, keyframeOnly)) psi.ArgumentList.Add(arg);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Cancelling the wait does not stop ffmpeg. Left alone it keeps
            // seeking and decoding for a frame nobody will see — one quick sweep
            // across the timeline used to leave dozens running at once, all
            // reading the same disk as the film being watched. The kill is
            // registered on the token rather than done after the await, so it
            // happens inside Cancel() itself: while the app is shutting down no
            // further continuation runs, and ffmpeg would outlive the app.
            using var kill = ct.Register(static p =>
            {
                try { ((Process)p!).Kill(); } catch { /* already gone */ }
            }, proc);
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Let the kill land, so the file below is free to delete.
                try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                throw;
            }

            if (proc.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0) return false;
            File.Move(temp, output, overwrite: true);
            return true;
        }
        finally
        {
            // Nothing is left here after a successful rename, and deleting a
            // file that does not exist is a no-op.
            try { File.Delete(temp); } catch { }
        }
    }

    private static List<string> FfmpegArgs(string input, string output, double atSeconds, int width,
        bool keyframeOnly)
    {
        // Input options, so they reach the demuxer and decoder: land on the
        // keyframe at or before the target and decode that one frame, on one
        // thread — a multithreaded decoder takes in a dozen or more frames before
        // it hands back the first. Measured on this library, a 4K HEVC grab drops
        // from 0.4–1.1 s to a steady 0.3 s.
        string[] keyframeGrab = keyframeOnly
            ? ["-noaccurate_seek", "-skip_frame:v", "nokey", "-threads", "1"]
            : [];
        return
        [
            "-hide_banner", "-loglevel", "error",
            .. keyframeGrab,
            "-ss", atSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", input,
            "-frames:v", "1",
            "-vf", $"scale={width}:-2",
            "-q:v", "4",
            // One image under a literal name. Without it the image writer treats
            // a '%' anywhere in the path as a frame-number pattern.
            "-update", "1",
            "-y", output,
        ];
    }

    private static int _cacheDirReady;

    /// <summary>
    /// Create the cache folder, and once per run clear out temporary frames an
    /// earlier run was writing when it crashed or was killed — nothing else ever
    /// renames or deletes them.
    /// </summary>
    private static void EnsureCacheDir()
    {
        Directory.CreateDirectory(CacheDir);
        if (Interlocked.Exchange(ref _cacheDirReady, 1) != 0) return;
        _ = Task.Run(() =>
        {
            // An hour old, so a grab still running from this session is never
            // pulled out from under ffmpeg.
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var temp in Directory.EnumerateFiles(CacheDir, "*.part.jpg"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(temp) < cutoff) File.Delete(temp);
                }
                catch
                {
                    // Housekeeping; never the reason a thumbnail fails.
                }
            }
        });
    }

    /// <summary>Decode without holding the file open, and frozen so any thread may use it.</summary>
    internal static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
