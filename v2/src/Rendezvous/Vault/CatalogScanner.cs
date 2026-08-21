using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Builds the server-hosted catalogue by walking the media root.
///
/// The relay's other job is moving somebody else's bytes; this is the part where
/// the server itself owns a library. The disk is treated as read-only truth: the
/// scanner never writes to it, and the database is a disposable index that a
/// rescan rebuilds from scratch.
///
/// Durations, codecs and posters come from ffprobe/ffmpeg. Both are optional —
/// without them files still appear, just as a name and a size. That is the
/// difference between a library and a plain list, not between working and
/// broken.
/// </summary>
public class CatalogScanner(IServiceScopeFactory scopes, ILogger<CatalogScanner> log)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ogv", ".3gp", ".ts",
    };

    // Containers browsers demux. MKV is the notable absentee, and it is what most
    // rips are — which is why the desktop app stays the good path.
    private static readonly HashSet<string> WebContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm",
    };

    private static readonly HashSet<string> WebVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "vp8", "vp9", "av1",
    };

    private static readonly HashSet<string> WebAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp3", "opus", "vorbis",
    };

    // One sweep at a time. Two concurrent scans would race on the same upserts,
    // and asking for a rescan while the startup sweep is still probing a large
    // library is an easy way to arrive there.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Where the media lives. Read-only as far as this server is concerned.</summary>
    public static string MediaRoot =>
        Environment.GetEnvironmentVariable("VAULT_MEDIA") ?? "/media";

    /// <summary>Poster cache, kept beside the database so it sits on the same volume.</summary>
    public static string ArtRoot
    {
        get
        {
            var db = Environment.GetEnvironmentVariable("VAULT_DB")
                ?? Path.Combine(AppContext.BaseDirectory, "vault.db");
            return Path.Combine(Path.GetDirectoryName(db) ?? AppContext.BaseDirectory, "art");
        }
    }

    public bool IsScanning { get; private set; }

    /// <summary>Unix ms the last sweep finished, or 0 if none has.</summary>
    public long LastScanAt { get; private set; }

    /// <summary>Resolve a catalogue row to a real file, refusing anything outside the root.</summary>
    public static string? ResolveFile(CatalogItem item)
    {
        var root = Path.GetFullPath(MediaRoot);
        var full = Path.GetFullPath(
            Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        // The relative path came from our own scan, but this is the one place a
        // database value turns into a filesystem read — so prove containment
        // rather than assume it.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (!File.Exists(full)) return null;

        // Containment above proves the PATH is inside the root, not the file. The
        // media disk is a network share, so a link planted in it could still aim
        // outside; resolve to the real target and prove that too.
        var real = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        if (real is not null &&
            !Path.GetFullPath(real).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return full;
    }

    /// <summary>
    /// Walk the media root and reconcile the catalogue with what is actually
    /// there. Safe to call repeatedly: only new or changed files cost anything.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            log.LogInformation("Catalogue scan already running; ignoring this request.");
            return;
        }
        IsScanning = true;
        var started = Stopwatch.StartNew();
        try
        {
            var root = Path.GetFullPath(MediaRoot);
            if (!Directory.Exists(root))
            {
                log.LogWarning("Media root {Root} does not exist — catalogue left empty.", root);
                return;
            }
            Directory.CreateDirectory(ArtRoot);

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();
            var existing = await db.CatalogItems.ToDictionaryAsync(x => x.ItemKey, ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var seen = new HashSet<string>();
            var added = 0;

            // Not the SearchOption overload: that one aborts the whole sweep the
            // moment it meets a directory it cannot read, and every ext4 volume
            // has a root-only lost+found sitting in the media root. The failure
            // would be an empty library rather than an error, which is the worst
            // shape a failure can take here. Hidden/system directories are
            // skipped too, so Samba's .Trash-1000 never becomes a film.
            var walk = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", walk))
            {
                if (ct.IsCancellationRequested) break;
                if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                var key = KeyFor(relative);
                seen.Add(key);

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Skipping unreadable file {File}.", relative);
                    continue;
                }

                if (existing.TryGetValue(key, out var row))
                {
                    row.LastSeenAt = now;

                    // A changed size means a different encode behind the same
                    // name: the stored duration, codecs and poster all describe
                    // a file that no longer exists.
                    if (row.SizeBytes != info.Length)
                    {
                        row.SizeBytes = info.Length;
                        row.FilmKey = FilmKeyFor(file, info.Length);
                        row.DurationMs = null;
                        row.VideoCodec = row.AudioCodec = null;
                        DeleteArt(row);
                    }
                }
                else
                {
                    row = new CatalogItem
                    {
                        ItemKey = key,
                        RelativePath = relative,
                        Kind = "film",
                        Name = Path.GetFileNameWithoutExtension(file),
                        SizeBytes = info.Length,
                        ContentType = ContentTypeFor(Path.GetExtension(file)),
                        FilmKey = FilmKeyFor(file, info.Length),
                        AddedAt = now,
                        LastSeenAt = now,
                    };
                    db.CatalogItems.Add(row);
                    added++;
                }

                // Probing is the slow part, so it only runs for rows never probed
                // or whose file changed underneath us.
                if (row.DurationMs is null) await ProbeAsync(row, file, ct);
                // Regenerate when there is no poster, when the file has gone —
                // and when the one on disk predates the current poster
                // generation, which is how a quality change reaches a library
                // that already has a full set of the old ones.
                if (row.ArtFile is null
                    || !row.ArtFile.EndsWith(ArtGeneration, StringComparison.Ordinal)
                    || !File.Exists(Path.Combine(ArtRoot, row.ArtFile)))
                    await ExtractArtAsync(row, file, ct);
            }

            // Anything not seen this sweep is gone from the disk. Rows are cheap
            // to rebuild, so drop them rather than accumulate tombstones.
            var vanished = existing.Values.Where(x => !seen.Contains(x.ItemKey)).ToList();
            foreach (var row in vanished) DeleteArt(row);
            db.CatalogItems.RemoveRange(vanished);

            await db.SaveChangesAsync(ct);
            LastScanAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            log.LogInformation(
                "Catalogue scan finished in {Seconds:0.0}s: {Total} films ({Added} new, {Gone} removed).",
                started.Elapsed.TotalSeconds, seen.Count, added, vanished.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Catalogue scan failed.");
        }
        finally
        {
            IsScanning = false;
            _gate.Release();
        }
    }

    /// <summary>Stable per-file id: the relative path, hashed and shortened for URLs.</summary>
    private static string KeyFor(string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The app's cross-machine film identity, so a server copy and a local rip are one film.</summary>
    private static string FilmKeyFor(string file, long size) => Path.GetFileName(file) + "|" + size;

    private static void DeleteArt(CatalogItem row)
    {
        if (row.ArtFile is null) return;
        try
        {
            File.Delete(Path.Combine(ArtRoot, row.ArtFile));
        }
        catch
        {
            // A poster we cannot delete is clutter, not a failure.
        }
        row.ArtFile = null;
    }

    // ---- ffprobe / ffmpeg ---------------------------------------------------

    // Whether each tool exists on PATH. Probed once: a missing tool stays missing
    // for the life of the process, and asking per file would fork thousands of
    // processes to learn the same thing.
    private static bool? _haveFfprobe;
    private static bool? _haveFfmpeg;

    private static bool Have(string tool, ref bool? cache)
    {
        if (cache is not null) return cache.Value;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            probe!.WaitForExit(5000);
            cache = probe.ExitCode == 0;
        }
        catch
        {
            cache = false;
        }
        return cache.Value;
    }

    /// <summary>Fill in duration and codecs, and decide browser playability.</summary>
    private async Task ProbeAsync(CatalogItem row, string file, CancellationToken ct)
    {
        if (!Have("ffprobe", ref _haveFfprobe)) return;
        try
        {
            var json = await RunAsync("ffprobe", new[]
            {
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", file,
            }, TimeSpan.FromSeconds(60), ct);
            if (json is null) return;

            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var duration)
                && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                row.DurationMs = (long)(seconds * 1000);
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    if (type == "video" && row.VideoCodec is null) row.VideoCodec = codec;
                    else if (type == "audio" && row.AudioCodec is null) row.AudioCodec = codec;
                }
            }

            // Conservative on purpose, matching the rule the desktop app applies
            // to its own files: a wrong "yes" is a black rectangle for a web
            // viewer, while a wrong "no" only sends them to the app that works.
            row.WebPlayable =
                WebContainers.Contains(Path.GetExtension(file))
                && row.VideoCodec is not null && WebVideoCodecs.Contains(row.VideoCodec)
                && (row.AudioCodec is null || WebAudioCodecs.Contains(row.AudioCodec));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not probe {File}.", row.RelativePath);
        }
    }

    /// <summary>Grab a poster frame a little way in, so it is not a black title card.</summary>
    /// <summary>
    /// Suffix stamped on every poster, bumped whenever the way they are made
    /// changes. A poster is only cut when one is missing, so without a marker in
    /// the name a quality improvement would only ever reach films added after
    /// it — every existing library would keep the old ones forever.
    /// </summary>
    private const string ArtGeneration = "-p2.jpg";

    private async Task ExtractArtAsync(CatalogItem row, string file, CancellationToken ct)
    {
        if (!Have("ffmpeg", ref _haveFfmpeg)) return;

        // A fifth of the way in for anything with a known length, otherwise two
        // minutes — far enough past studio logos to show the actual film.
        //
        // The 3-second fallback is what happens when that seek lands past the
        // end: without ffprobe there is no duration to aim by, and a short item
        // (an extra, a home video) would otherwise never get a poster at all.
        var at = row.DurationMs is > 0 ? row.DurationMs.Value / 5000.0 : 120.0;

        // The suffix is a generation marker. Posters are only made when one is
        // missing, so raising the quality would otherwise never reach a library
        // that already has a full set of the old blurry ones — changing the name
        // is what makes every film regenerate exactly once.
        var name = row.ItemKey + ArtGeneration;
        var output = Path.Combine(ArtRoot, name);
        try
        {
            if (!await GrabFrameAsync(file, at, output, ct) && at > 3)
                await GrabFrameAsync(file, 3, output, ct);

            if (File.Exists(output) && new FileInfo(output).Length > 0)
            {
                // Take the superseded one with us rather than leaving a full
                // second set of posters on the disk forever.
                if (row.ArtFile is { Length: > 0 } old && old != name)
                {
                    try { File.Delete(Path.Combine(ArtRoot, old)); } catch { }
                }
                row.ArtFile = name;
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not extract a poster for {File}.", row.RelativePath);
        }
    }

    /// <summary>One frame at a given offset, written as a jpg. False if nothing came out.</summary>
    private static async Task<bool> GrabFrameAsync(string file, double seconds, string output, CancellationToken ct)
    {
        // -ss before -i is the fast seek: ffmpeg jumps rather than decoding
        // everything up to that point, which matters on a 30 GB remux.
        await RunAsync("ffmpeg", new[]
        {
            "-nostdin", "-v", "quiet", "-y",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", file, "-frames:v", "1",
            // 1280 wide, not 480. These are shown in a 2:3 portrait card, so a
            // 16:9 frame gets most of its width cropped away — at 480 that left
            // roughly 180x270 of real picture to fill a card three times that
            // size, which is why they looked soft. 1280 leaves about 480x720
            // after the crop, which is sharp on a television and still only a
            // couple of hundred kilobytes.
            //
            // thumbnail= picks the most representative frame out of the next
            // hundred instead of taking whatever the seek happened to land on,
            // so posters stop being fades, black frames and motion blur.
            "-vf", "thumbnail=100,scale=1280:-2",
            "-q:v", "3", output,
        }, TimeSpan.FromSeconds(60), ct);

        // Judge by the file, not the exit code: ffmpeg reports success while
        // writing an empty jpg when the seek lands past the last frame.
        return File.Exists(output) && new FileInfo(output).Length > 0;
    }

    /// <summary>Run a tool and return stdout, or null if it failed or timed out.</summary>
    private static async Task<string?> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // ArgumentList quotes each argument itself, which matters because film
        // filenames are full of spaces, quotes and brackets.
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return null;

        // Read stdout while the process runs. ffprobe's JSON for a file with many
        // streams can exceed the pipe buffer, and a full buffer deadlocks a
        // process nobody is draining.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            // One wedged file must not stall the whole sweep.
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        return process.ExitCode == 0 ? await stdout : null;
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".ogv" => "video/ogg",
        ".3gp" => "video/3gpp",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream",
    };
}
