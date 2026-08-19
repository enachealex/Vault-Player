using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Makes the awkward half of the library playable in a browser.
///
/// Most of what people actually own is an MKV holding H.264 or HEVC beside DTS
/// or TrueHD, and no browser will open that container. Re-encoding the video
/// would fix it and cost hours of CPU per film; the video, though, is already in
/// a form browsers can decode — it is only in the wrong box. So the video is
/// copied bit-for-bit into fragmented MP4 and only the audio is re-encoded,
/// which is cheap. A two-hour film remuxes in minutes rather than hours, and the
/// picture is the original rather than a second-generation copy of it.
///
/// The output is HLS because it is the one format televisions, iOS and Android
/// all demux natively, and because a playlist of small segments lets a viewer
/// start watching while the far end of the film is still being written.
///
/// One job per film, ever — not one per viewer. The result is a cache on disk,
/// so the second person to watch something starts instantly, and a phone and a
/// television watching the same film share the same segments.
/// </summary>
public class HlsRemuxer
{
    private readonly ILogger<HlsRemuxer> log;

    public HlsRemuxer(ILogger<HlsRemuxer> log, IHostApplicationLifetime lifetime)
    {
        this.log = log;

        // ffmpeg must not outlive the server that started it. A restart —
        // a deploy, a crash, a container coming back — would otherwise leave it
        // writing segments nobody is waiting for, holding the cache folder open
        // so the next attempt at that film cannot clear it, and failing the film
        // for as long as it runs.
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var proc in _running.Values)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        });
    }

    private readonly ConcurrentDictionary<int, Process> _running = new();

    /// <summary>Video codecs that are legal inside fragmented MP4 and can be copied.</summary>
    private static readonly HashSet<string> CopyableVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "hevc", "av1",
    };

    /// <summary>Audio codecs already fine in fMP4; anything else is re-encoded.</summary>
    private static readonly HashSet<string> CopyableAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac",
    };

    /// <summary>
    /// Six seconds is Apple's recommendation and what television players are
    /// tuned for. Segments land on keyframes, so this is a target rather than a
    /// promise — a film with a twelve-second GOP produces twelve-second parts.
    /// </summary>
    private const int SegmentSeconds = 6;

    /// <summary>
    /// How much of the film must exist before a viewer is let in. Starting at
    /// the first segment means outrunning the writer within seconds on a slow
    /// disk, and a player that runs out of playlist tends to stall for good
    /// rather than wait.
    /// </summary>
    private const int StartAfterSeconds = 30;

    public const string PlaylistName = "index.m3u8";

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private static bool? _haveFfmpeg;

    /// <summary>
    /// Where segments are cached: beside the poster art, which is already on the
    /// writable volume. The media disk itself is mounted read-only.
    /// </summary>
    public static string CacheRoot =>
        Path.Combine(Path.GetDirectoryName(CatalogScanner.ArtRoot) ?? AppContext.BaseDirectory, "hls");

    /// <summary>
    /// How large the segment cache may grow before the least recently watched
    /// film is dropped. Segments are roughly the size of the films themselves,
    /// so this is the one setting a small server is likely to need.
    /// </summary>
    private static long CacheLimitBytes =>
        long.TryParse(Environment.GetEnvironmentVariable("VAULT_HLS_CACHE_GB"), out var gb) && gb > 0
            ? gb * 1024L * 1024L * 1024L
            : 50L * 1024 * 1024 * 1024;

    // ---- What a film can expect --------------------------------------------

    /// <summary>
    /// Whether this film could be remuxed at all. False for the exotic — MPEG-2
    /// off a DVD rip, VP8 in a container nothing else wants — where copying the
    /// video is not an option and the honest answer is to use the app.
    /// </summary>
    public static bool CanRemux(CatalogItem item) =>
        item.VideoCodec is not null && CopyableVideo.Contains(item.VideoCodec) && HaveFfmpeg();

    /// <summary>
    /// The codec the remux will contain. Clients need this because it decides
    /// whether they can play the result: a television decodes HEVC happily and a
    /// desktop browser generally does not, and neither can be asked after the
    /// fact without showing someone a black rectangle first.
    /// </summary>
    public static string? RemuxVideoCodec(CatalogItem item) =>
        CanRemux(item) ? item.VideoCodec : null;

    // ---- Progress ----------------------------------------------------------

    public record Status(string State, int Percent, bool Ready, string? Error);

    /// <summary>
    /// Where a film stands. "ready" means the whole thing is on disk, "running"
    /// that enough exists to start watching, "starting" that it does not yet.
    /// </summary>
    public Status StatusFor(CatalogItem item)
    {
        if (File.Exists(Path.Combine(DirFor(item.ItemKey), ".done")))
            return new Status("ready", 100, true, null);

        if (_jobs.TryGetValue(item.ItemKey, out var job))
        {
            if (job.Error is not null) return new Status("failed", 0, false, job.Error);
            var ahead = job.OutSeconds >= StartAfterSeconds;
            var percent = item.DurationMs is > 0
                ? (int)Math.Clamp(job.OutSeconds * 100_000 / item.DurationMs.Value, 0, 99)
                : 0;
            return new Status(ahead ? "running" : "starting", percent, ahead, null);
        }
        return new Status("idle", 0, false, null);
    }

    /// <summary>Begin remuxing if nobody already is. Returns at once; the work is in the background.</summary>
    public void Start(CatalogItem item, string sourcePath)
    {
        if (File.Exists(Path.Combine(DirFor(item.ItemKey), ".done"))) return;
        if (!CanRemux(item)) return;

        _jobs.GetOrAdd(item.ItemKey, _ =>
        {
            var job = new Job();
            job.Task = Task.Run(() => RunAsync(item, sourcePath, job));
            return job;
        });
    }

    /// <summary>
    /// Wait for a segment to appear, for as long as is reasonable. A viewer who
    /// seeks slightly past the write head is better served by a pause than by a
    /// 404, which players tend to turn into a stall they never recover from.
    /// </summary>
    public async Task<bool> WaitForAsync(string itemKey, string file, CancellationToken ct)
    {
        var dir = DirFor(itemKey);
        var path = Path.Combine(dir, file);
        for (var i = 0; i < 60; i++)
        {
            if (File.Exists(path)) return true;
            // Nothing is coming: either the job died, or it finished without
            // ever writing this segment.
            if (!_jobs.TryGetValue(itemKey, out var job) || job.Error is not null) return false;
            if (File.Exists(Path.Combine(dir, ".done"))) return File.Exists(path);
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return false; }
        }
        return File.Exists(path);
    }

    /// <summary>Resolve a cached file, refusing anything that tries to leave its own folder.</summary>
    public static string? ResolveCached(string itemKey, string file)
    {
        // The item key comes from the database; the file name comes off the
        // wire, so it is the one that has to prove itself.
        if (file.Length is 0 or > 64) return null;
        foreach (var c in file)
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-') return null;
        if (file.Contains("..", StringComparison.Ordinal)) return null;

        var dir = Path.GetFullPath(DirFor(itemKey));
        var full = Path.GetFullPath(Path.Combine(dir, file));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Read the playlist back with the caller's token stitched onto every URI.
    ///
    /// ffmpeg writes bare segment names, and a player fetching them attaches
    /// nothing of its own — no header, no cookie, not even the query string of
    /// the playlist it just read. So the grant has to be carried by each line of
    /// the playlist itself, which is also what keeps a copied segment URL
    /// expiring on the same clock as everything else.
    /// </summary>
    public static async Task<string?> PlaylistWithTokenAsync(string itemKey, string token)
    {
        var path = ResolveCached(itemKey, PlaylistName);
        if (path is null) return null;

        var query = "?t=" + Uri.EscapeDataString(token);
        var lines = await File.ReadAllLinesAsync(path);
        var outp = new StringBuilder(lines.Length * 40);

        // ffmpeg writes EVENT because the file grows as it works. A player told
        // EVENT treats the film as open-ended and reports its duration as
        // Infinity — which reads as a live broadcast, disabling the scrub bar and
        // seeking. Once the remux is finished the film is plainly VOD, so say so.
        var finished = File.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".done"));

        foreach (var line in lines)
        {
            if (finished && line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                outp.Append("#EXT-X-PLAYLIST-TYPE:VOD");
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                // #EXT-X-MAP:URI="init.mp4" — the initialisation segment, named
                // inside an attribute rather than on a line of its own.
                outp.Append(line.Replace("\"init.mp4\"", $"\"init.mp4{query}\"", StringComparison.Ordinal));
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                outp.Append(line).Append(query);
            }
            else
            {
                outp.Append(line);
            }
            outp.Append('\n');
        }
        return outp.ToString();
    }

    /// <summary>
    /// Discard cache folders left half-written by a previous run.
    ///
    /// Only a folder carrying the .done marker is a whole film; anything else is
    /// the wreckage of a server that stopped mid-remux. Clearing it at startup —
    /// while it is certain nothing is writing, because nothing has started yet —
    /// keeps that wreckage from being discovered later by a viewer, and from
    /// counting against the cache limit forever.
    /// </summary>
    public void SweepPartials()
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(CacheRoot))
            {
                if (File.Exists(Path.Combine(dir, ".done"))) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    log.LogInformation("Discarded an unfinished web copy left by a previous run ({Dir}).",
                        Path.GetFileName(dir));
                }
                catch (Exception ex)
                {
                    log.LogWarning("Could not discard the unfinished web copy {Dir}: {Reason}",
                        Path.GetFileName(dir), ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not sweep the HLS cache at startup.");
        }
    }

    /// <summary>Note that a film was watched, so eviction drops the right one.</summary>
    public static void Touch(string itemKey)
    {
        try
        {
            var dir = DirFor(itemKey);
            if (Directory.Exists(dir)) Directory.SetLastAccessTimeUtc(dir, DateTime.UtcNow);
        }
        catch { }
    }

    private static string DirFor(string itemKey) => Path.Combine(CacheRoot, itemKey);

    // ---- The work ----------------------------------------------------------

    private sealed class Job
    {
        public Task? Task;
        public volatile string? Error;
        public long OutSeconds;
    }

    private async Task RunAsync(CatalogItem item, string sourcePath, Job job)
    {
        var dir = DirFor(item.ItemKey);
        try
        {
            // A previous attempt may have died halfway and left segments no
            // playlist refers to. Start from nothing rather than reason about it.
            await ClearAsync(dir);
            Directory.CreateDirectory(dir);
            // The remux is a shade smaller than the source — surplus audio tracks
            // and subtitles are dropped — so the file size is a fair upper bound.
            Evict(keep: item.ItemKey, incomingBytes: item.SizeBytes);

            var copyAudio = item.AudioCodec is not null && CopyableAudio.Contains(item.AudioCodec);
            var args = new List<string>
            {
                "-nostdin", "-loglevel", "error",
                "-i", sourcePath,
                // First real video and first audio track only. Capital V skips
                // attached cover art, which many rips carry as a second video
                // stream — and which, if it happened to come first, would remux
                // the film into a two-hour still photograph.
                //
                // Subtitles are dropped: burning them in is a full re-encode,
                // which is the cost this whole path exists to avoid.
                "-map", "0:V:0", "-map", "0:a:0?", "-sn", "-dn",
                "-c:v", "copy",
            };

            // HEVC in MP4 has two possible tags and Apple accepts exactly one of
            // them. Copying leaves ffmpeg's default of hev1, which every iPhone,
            // iPad and Apple TV refuses without saying why — an empty player
            // rather than an error. hvc1 describes the same bitstream and plays
            // everywhere, so it is always the right one to write.
            if (string.Equals(item.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["-tag:v", "hvc1"]);

            if (copyAudio)
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else
            {
                // Stereo AAC. Home-cinema layouts get folded down, which is what
                // a phone or a television speaker wants anyway.
                args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ac", "2"]);
            }

            args.AddRange([
                "-f", "hls",
                "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
                "-hls_playlist_type", "event",
                "-hls_list_size", "0",
                "-hls_flags", "independent_segments",
                "-hls_segment_type", "fmp4",
                "-hls_fmp4_init_filename", "init.mp4",
                // Bare names, not paths: ffmpeg copies the segment pattern into
                // the playlist verbatim, so an absolute path here would produce a
                // playlist full of this server's private filesystem. The process
                // is given the folder as its working directory instead.
                "-hls_segment_filename", "s%05d.m4s",
                "-progress", "pipe:1", "-nostats",
                PlaylistName,
            ]);

            log.LogInformation("Remuxing {Name} for the web ({Video}/{Audio}; audio {Mode}).",
                item.Name, item.VideoCodec, item.AudioCodec, copyAudio ? "copied" : "re-encoded");

            await FfmpegAsync(args, dir, job);

            // An event playlist carries no end marker, so a player keeps asking
            // for more forever. Close it, and only then mark the film done.
            var playlist = Path.Combine(dir, PlaylistName);
            if (!File.Exists(playlist)) throw new InvalidOperationException("ffmpeg wrote no playlist");
            var text = await File.ReadAllTextAsync(playlist);
            if (!text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal))
                await File.AppendAllTextAsync(playlist, "#EXT-X-ENDLIST\n");

            await File.WriteAllTextAsync(Path.Combine(dir, ".done"), "");
            log.LogInformation("Remuxed {Name}.", item.Name);
        }
        catch (Exception ex)
        {
            // Deliberately not ex.Message: these carry absolute server paths and
            // ffmpeg's internals, and this string is handed to any signed-in
            // viewer. The detail belongs in the log, where the owner can read it.
            job.Error = "The server couldn't prepare this film.";
            log.LogWarning(ex, "Could not remux {Name}.", item.Name);
            // Leave nothing half-written: a stale playlist would be served to the
            // next viewer as though it were whole.
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
        finally
        {
            // Keep a failure around briefly so the client can report why, then
            // let a later request try again.
            if (job.Error is not null) await Task.Delay(TimeSpan.FromMinutes(2));
            _jobs.TryRemove(item.ItemKey, out _);
        }
    }

    /// <summary>
    /// Remove a half-written cache folder, waiting out whoever still holds it.
    ///
    /// On Windows a file cannot be deleted while another process has it open, and
    /// the process in question is usually an ffmpeg from a previous run of this
    /// server that has not quite noticed it was killed. Giving up on the first
    /// refusal fails the film for whoever is waiting on it, when a few seconds
    /// would have been enough.
    /// </summary>
    private async Task ClearAsync(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                if (attempt == 0)
                    log.LogInformation("Waiting for a previous remux to release {Dir}.", dir);
                await Task.Delay(1000);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                await Task.Delay(1000);
            }
        }
    }

    private async Task FfmpegAsync(List<string> args, string workingDirectory, Job job)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        _running[proc.Id] = proc;
        try
        {
            await PumpAsync(proc, job);
        }
        finally
        {
            _running.TryRemove(proc.Id, out _);
        }
    }

    private static async Task PumpAsync(Process proc, Job job)
    {

        // -progress writes key=value lines. out_time_us is how far into the film
        // the writer has reached, which is what decides when a viewer may start.
        var progress = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                const string key = "out_time_us=";
                if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                if (long.TryParse(line.AsSpan(key.Length), out var us)) job.OutSeconds = us / 1_000_000;
            }
        });
        var stderr = proc.StandardError.ReadToEndAsync();

        await proc.WaitForExitAsync();
        await progress;
        var errors = await stderr;

        if (proc.ExitCode != 0)
        {
            // ffmpeg's first error line says what actually went wrong; the rest
            // is usually the same complaint repeated per frame.
            var first = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(first) ? $"ffmpeg exited {proc.ExitCode}" : first);
        }
    }

    // ---- Cache -------------------------------------------------------------

    /// <summary>
    /// Drop least-recently-watched films until the cache fits. Every one of them
    /// is only a copy of something still on the media disk, so losing one costs
    /// a remux and nothing else.
    /// </summary>
    /// <param name="keep">The film being written now, which must survive.</param>
    /// <param name="incomingBytes">
    /// Roughly what the new film will add. Counted against the limit before it
    /// is written rather than after: a 4K remux is tens of gigabytes, so tidying
    /// up only on the way in would let a single film overrun the disk and take
    /// the server down with it.
    /// </param>
    private void Evict(string keep, long incomingBytes)
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var limit = CacheLimitBytes - incomingBytes;
            if (limit < 0)
            {
                // One film alone exceeds the whole allowance. Clearing the cache
                // will not help, and the remux is about to overrun it — say so
                // once, plainly, rather than silently thrashing.
                log.LogWarning(
                    "VAULT_HLS_CACHE_GB is smaller than a single web copy of this film. "
                    + "Raise it, or the cache will hold nothing and every viewer will wait for a remux.");
                limit = 0;
            }

            var cached = Directory.EnumerateDirectories(CacheRoot)
                .Select(d => new DirectoryInfo(d))
                .Select(d => (Dir: d, Size: d.EnumerateFiles().Sum(f => f.Length), d.LastAccessTimeUtc))
                .OrderBy(x => x.LastAccessTimeUtc)
                .ToList();

            var total = cached.Sum(x => x.Size);
            foreach (var entry in cached)
            {
                if (total <= limit) break;
                if (entry.Dir.Name == keep) continue;
                // Never evict something being written right now.
                if (_jobs.ContainsKey(entry.Dir.Name)) continue;
                try
                {
                    entry.Dir.Delete(recursive: true);
                    total -= entry.Size;
                    log.LogInformation("Dropped the web copy of {Key} to stay inside the cache limit.",
                        entry.Dir.Name);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not tidy the HLS cache.");
        }
    }

    private static bool HaveFfmpeg()
    {
        if (_haveFfmpeg is not null) return _haveFfmpeg.Value;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            probe!.WaitForExit(5000);
            _haveFfmpeg = probe.ExitCode == 0;
        }
        catch
        {
            _haveFfmpeg = false;
        }
        return _haveFfmpeg.Value;
    }
}
