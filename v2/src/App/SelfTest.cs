using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using LibVLCSharp.Shared;

namespace VideoPlayer.App;

/// <summary>
/// Headless playback verification: opens a video with libVLC (no window, no
/// sound output), confirms tracks parse, playback advances, decode counters
/// move for both audio and video, and seeking lands. Writes a JSON report.
/// </summary>
public static class SelfTest
{
    public static int Run(string file, string outPath)
    {
        var report = new Dictionary<string, object?>
        {
            ["file"] = file,
            ["startedAt"] = DateTime.Now.ToString("o"),
        };

        try
        {
            Core.Initialize();
            // Discard video/audio output — decoding still runs, nothing pops up.
            using var libvlc = new LibVLC("--vout=dummy", "--aout=dummy", "--no-video-title-show");
            report["vlcVersion"] = libvlc.Version;

            using var media = new Media(libvlc, file, FromType.FromPath);
            media.Parse(MediaParseOptions.ParseLocal).GetAwaiter().GetResult();

            report["durationMs"] = media.Duration;
            report["tracks"] = media.Tracks.Select(t => new
            {
                type = t.TrackType.ToString(),
                codec = FourCc(t.Codec),
                language = t.Language,
                description = t.Description,
                channels = t.TrackType == TrackType.Audio ? (int?)t.Data.Audio.Channels : null,
                rate = t.TrackType == TrackType.Audio ? (int?)t.Data.Audio.Rate : null,
                width = t.TrackType == TrackType.Video ? (int?)t.Data.Video.Width : null,
                height = t.TrackType == TrackType.Video ? (int?)t.Data.Video.Height : null,
            }).ToArray();

            using var mp = new MediaPlayer(media);
            mp.Play();

            // Wait for playback to actually start.
            var started = WaitFor(() => mp.State == VLCState.Playing && mp.Time > 0, 15000);
            report["started"] = started;
            if (!started)
            {
                report["state"] = mp.State.ToString();
                return Fail(report, outPath);
            }

            // Playback clock must advance.
            var t1 = mp.Time;
            Thread.Sleep(2000);
            var t2 = mp.Time;
            report["advancing"] = t2 > t1;
            report["advancedMs"] = t2 - t1;

            // Both decoders must be doing real work (this is the DTS proof).
            var stats = media.Statistics;
            report["decodedVideoBlocks"] = stats.DecodedVideo;
            report["decodedAudioBlocks"] = stats.DecodedAudio;

            // Seek deep into the file and confirm we land and keep playing.
            var target = Math.Min((long)(media.Duration * 0.5), 30 * 60 * 1000L);
            mp.Time = target;
            var seeked = WaitFor(() => Math.Abs(mp.Time - target) < 6000 && mp.State == VLCState.Playing, 8000);
            Thread.Sleep(1500);
            report["seekTargetMs"] = target;
            report["seekLandedMs"] = mp.Time;
            report["seekOk"] = seeked && mp.Time > target - 6000;

            mp.Stop();

            var pass = started
                && (bool)report["advancing"]!
                && stats.DecodedVideo > 0
                && stats.DecodedAudio > 0
                && (bool)report["seekOk"]!;
            report["pass"] = pass;
            Write(report, outPath);
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            return Fail(report, outPath);
        }
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }

    private static string FourCc(uint codec)
    {
        var bytes = BitConverter.GetBytes(codec);
        var s = Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
        return s.All(c => c >= 32 && c < 127) ? s : $"0x{codec:X8}";
    }

    private static int Fail(Dictionary<string, object?> report, string outPath)
    {
        report["pass"] = false;
        Write(report, outPath);
        return 1;
    }

    private static void Write(Dictionary<string, object?> report, string outPath)
    {
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
