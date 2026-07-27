using System;
using System.Linq;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// Can a plain browser &lt;video&gt; play this film straight off the relay?
///
/// When it can, web viewers (LAN Party's Movie Night) watch in the browser with
/// nothing installed. When it can't, they're told to open Vault Movies, which
/// decodes anything. Deliberately conservative: a wrong "yes" is a black
/// rectangle for every guest, whereas a wrong "no" just sends them to the app
/// that definitely works.
///
/// Transcoding to widen this is possible but costs the host real CPU (HEVC
/// especially), so the app doesn't — it hands those films to the player built
/// to decode them.
/// </summary>
public static class WebPlayback
{
    // Containers browsers actually demux. MKV is the big absentee — very common
    // for rips, and no browser plays it however friendly the codecs inside are.
    private static readonly string[] Containers = { ".mp4", ".m4v", ".mov", ".webm" };

    // libVLC reports codecs as fourccs.
    private static readonly string[] VideoOk = { "h264", "avc1", "vp08", "vp8 ", "vp09", "vp90", "av01" };
    private static readonly string[] AudioOk = { "mp4a", "aac ", "mpga", "mp3 ", "opus", "vorb" };

    /// <summary>
    /// Null reason = playable. Must be called on the UI thread: it parses a
    /// libVLC Media, and that lifecycle is dispatcher-affine (see AppServices).
    /// </summary>
    public static async Task<(bool Ok, string? Reason)> CheckAsync(MovieItem movie)
    {
        if (movie.IsShortcut) return (false, "Streaming titles open in their own service.");

        var ext = System.IO.Path.GetExtension(movie.Path);
        if (!Containers.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return (false, $"{ext.TrimStart('.').ToUpperInvariant()} files don't play in browsers.");

        try
        {
            using var media = new Media(AppServices.LibVlc, movie.Path, FromType.FromPath);
            await media.Parse(MediaParseOptions.ParseLocal);

            var video = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            var audio = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);

            // No video track at all is odd enough to hand to the real player.
            if (video.TrackType != TrackType.Video) return (false, "Couldn't read this file's video track.");

            var v = FourCc(video.Codec);
            if (!VideoOk.Contains(v, StringComparer.OrdinalIgnoreCase))
                return (false, $"{Pretty(v)} video needs the app.");

            // A file with no audio still plays; only reject audio we know breaks.
            if (audio.TrackType == TrackType.Audio)
            {
                var a = FourCc(audio.Codec);
                if (!AudioOk.Contains(a, StringComparer.OrdinalIgnoreCase))
                    return (false, $"{Pretty(a)} audio needs the app.");
            }
            return (true, null);
        }
        catch
        {
            return (false, "Couldn't read this file's format.");
        }
    }

    /// <summary>libVLC packs codecs as a four-character code in a uint.</summary>
    private static string FourCc(uint codec)
    {
        var chars = new[]
        {
            (char)(codec & 0xFF), (char)((codec >> 8) & 0xFF),
            (char)((codec >> 16) & 0xFF), (char)((codec >> 24) & 0xFF),
        };
        return chars.All(c => c is >= (char)32 and < (char)127)
            ? new string(chars)
            : codec.ToString("X8");
    }

    /// <summary>Codec names a viewer might recognise, rather than a fourcc.</summary>
    private static string Pretty(string fourCc) => fourCc.Trim().ToLowerInvariant() switch
    {
        "hevc" or "hev1" or "hvc1" => "HEVC (H.265)",
        "dts" or "dtsc" or "dtsh" => "DTS",
        "a52" or "ac-3" or "ac3" => "Dolby Digital",
        "eac3" or "ec-3" => "Dolby Digital Plus",
        "trhd" or "mlp" => "Dolby TrueHD",
        "flac" => "FLAC",
        "av01" => "AV1",
        "mpgv" or "mp2v" => "MPEG-2",
        "wmv3" or "wmv2" => "Windows Media",
        _ => fourCc.Trim().ToUpperInvariant(),
    };
}
