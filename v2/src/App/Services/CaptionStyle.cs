using System.Collections.Generic;
using LibVLCSharp.Shared;

namespace VideoPlayer.App.Services;

/// <summary>
/// Turns the user's caption preferences into libVLC options.
///
/// Only affects TEXT subtitles (SRT/subrip/ASS/VTT) — libVLC's freetype text
/// renderer draws those and honours these settings. Bitmap subtitles (PGS from
/// Blu-ray, VOBSUB from DVD) are pre-rendered images with the font baked in;
/// nothing here changes them.
///
/// Applied as per-media options rather than at engine construction, so a change
/// takes effect on the next film — or immediately, by reloading the current one
/// at its current position — without rebuilding the libVLC engine.
/// </summary>
public static class CaptionStyle
{
    public static void Apply(Media media, Settings s)
    {
        // Size. sub-text-scale is a straightforward percentage the freetype
        // renderer multiplies its computed size by.
        media.AddOption($":sub-text-scale={Clamp(s.SubtitleScalePct, 50, 250)}");

        // Weight and the outline that keeps text legible over bright scenes —
        // libVLC's default outline is a hairline, which is most of why the
        // stock captions are hard to read.
        media.AddOption(s.SubtitleBold ? ":freetype-bold" : ":no-freetype-bold");
        media.AddOption($":freetype-outline-thickness={Clamp(s.SubtitleOutline, 0, 8)}");

        // Background box. Opacity 0 means no box at all (outline-only look).
        var bg = Clamp(s.SubtitleBackgroundOpacity, 0, 255);
        media.AddOption($":freetype-background-opacity={bg}");
        if (bg > 0) media.AddOption(":freetype-background-color=0"); // black box
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
