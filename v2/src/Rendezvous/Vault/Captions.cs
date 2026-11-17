using System.Text.Json;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// English text captions, pulled out of each film once and kept as WebVTT.
///
/// The web copy of a film carries no subtitles (see HlsRemuxer), and the
/// players that use it cannot take them from the source: a phone's expo-video
/// only shows subtitle tracks inside the stream, and a television's video
/// element wants a WebVTT file. So the captions are extracted ahead of time —
/// during the catalogue scan, because it means reading the whole film, which
/// is too slow to do while somebody waits for playback to start.
///
/// Only text tracks qualify. A DVD's own subtitles are pictures and would need
/// OCR; the captions MakeMKV lifts off a DVD's closed-caption stream are text,
/// and so is almost every subtitle track in a downloaded MKV.
/// </summary>
public static class Captions
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text",
    };

    public static string Root =>
        Path.Combine(Path.GetDirectoryName(CatalogScanner.ArtRoot) ?? AppContext.BaseDirectory, "captions");

    private static string VttPath(string itemKey) => Path.Combine(Root, itemKey + ".en.vtt");

    /// <summary>Written when a film has been checked and has no English text track, so it is not re-read every sweep.</summary>
    private static string NonePath(string itemKey) => Path.Combine(Root, itemKey + ".none");

    /// <summary>The film's English captions, or null if it has none (or they are not extracted yet).</summary>
    public static string? FileFor(string itemKey)
    {
        var path = VttPath(itemKey);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public static bool Has(string itemKey) => FileFor(itemKey) is not null;

    /// <summary>Forget a film's captions — it changed or went away.</summary>
    public static void Delete(string itemKey)
    {
        try { File.Delete(VttPath(itemKey)); } catch { }
        try { File.Delete(NonePath(itemKey)); } catch { }
    }

    /// <summary>Whether this film still needs looking at.</summary>
    public static bool Pending(string itemKey) =>
        !File.Exists(VttPath(itemKey)) && !File.Exists(NonePath(itemKey));

    /// <summary>
    /// Find the film's English text track and write it out as WebVTT.
    /// Returns true when captions now exist.
    /// </summary>
    public static async Task<bool> ExtractAsync(string itemKey, string file,
        Func<string, string[], TimeSpan, CancellationToken, Task<string?>> run, ILogger log, CancellationToken ct)
    {
        if (MediaTools.Ffprobe is not { } ffprobe || MediaTools.Ffmpeg is not { } ffmpeg) return false;
        Directory.CreateDirectory(Root);

        var json = await run(ffprobe, new[]
        {
            "-v", "quiet", "-print_format", "json", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name:stream_tags=language,title:stream_disposition=forced",
            file,
        }, TimeSpan.FromSeconds(60), ct);
        if (json is null) return false;   // unreadable just now; try again next sweep

        int? pick = null;
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                // Relative position among the subtitle streams (0:s:N), not the
                // absolute index, is what -map wants.
                var n = 0;
                int? undetermined = null;
                foreach (var s in streams.EnumerateArray())
                {
                    var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    var lang = s.TryGetProperty("tags", out var t) && t.TryGetProperty("language", out var l)
                        ? l.GetString() : null;
                    var forced = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("forced", out var f)
                        && f.GetInt32() == 1;
                    // A forced track only translates the odd foreign line; it
                    // is not captions for the film.
                    if (codec is not null && TextCodecs.Contains(codec) && !forced)
                    {
                        if (lang is "eng" or "en") { pick = n; break; }
                        if (lang is null or "und") undetermined ??= n;
                    }
                    n++;
                }
                pick ??= undetermined;
            }
        }

        if (pick is null)
        {
            await File.WriteAllTextAsync(NonePath(itemKey), "", ct);
            return false;
        }

        // Write beside the final name and rename, so a half-written file is
        // never served as the film's captions.
        var temp = VttPath(itemKey) + ".part";
        var ok = await run(ffmpeg, new[]
        {
            "-nostdin", "-v", "error", "-y", "-i", file,
            "-map", $"0:s:{pick}", "-c:s", "webvtt", "-f", "webvtt", temp,
        }, TimeSpan.FromMinutes(10), ct) is not null;

        if (!ok || !File.Exists(temp) || new FileInfo(temp).Length < 16)
        {
            try { File.Delete(temp); } catch { }
            log.LogWarning("Could not extract captions from {File}.", Path.GetFileName(file));
            return false;
        }
        File.Move(temp, VttPath(itemKey), overwrite: true);
        log.LogInformation("Extracted English captions from {File}.", Path.GetFileName(file));
        return true;
    }

    /// <summary>
    /// A one-segment subtitle playlist covering the whole film, pointing at the
    /// VTT file. Players accept one long segment, and it saves cutting the
    /// captions into pieces that line up with the video's.
    /// </summary>
    public static string SubtitlePlaylist(long durationMs, string query)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(durationMs / 1000.0));
        return "#EXTM3U\n#EXT-X-VERSION:3\n" +
               $"#EXT-X-TARGETDURATION:{seconds}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
               $"#EXTINF:{seconds}.000,\nen.vtt{query}\n#EXT-X-ENDLIST\n";
    }

    /// <summary>
    /// The master playlist: the film's own playlist, plus English captions as a
    /// subtitle rendition — the form phones' players pick up as a selectable track.
    /// </summary>
    public static string MasterPlaylist(long bandwidth, string query) =>
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n" +
        "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\"," +
        $"DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"subs_en.m3u8{query}\"\n" +
        $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},SUBTITLES=\"subs\"\n" +
        $"{HlsRemuxer.PlaylistName}{query}\n";
}
