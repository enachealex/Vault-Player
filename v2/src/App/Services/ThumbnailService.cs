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

    public static async Task EnsureThumbsAsync(IEnumerable<MovieItem> items, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        Directory.CreateDirectory(CacheDir);

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

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string input, string output, double atSeconds, CancellationToken ct)
    {
        var ss = atSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -ss {ss} -i \"{input}\" -frames:v 1 -vf \"scale=460:-2\" -q:v 4 -y \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
    }

    private static BitmapImage LoadFrozen(string path)
    {
        // OnLoad + Freeze: no file locks, safe to hand to bindings from any thread.
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
