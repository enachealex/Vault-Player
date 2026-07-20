using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>Folder scanning and metadata probing for the library.</summary>
public static class MovieLibrary
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ogv", ".3gp", ".ts",
    };

    /// <summary>Top-level video files in a folder, sorted by name.</summary>
    public static List<MovieItem> Scan(string folder)
    {
        var items = new List<MovieItem>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file))) continue;
            var info = new FileInfo(file);
            items.Add(new MovieItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Path = file,
                SizeBytes = info.Length,
            });
        }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Fill in durations in the background. Deliberately SEQUENTIAL: creating,
    /// parsing, and disposing libVLC Media objects concurrently off the UI
    /// thread races in native code (observed 0xc0000005 heap crash). Each
    /// local parse is fast, so one at a time still fills a folder in seconds.
    /// </summary>
    public static async Task ProbeDurationsAsync(IEnumerable<MovieItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                using var media = new Media(AppServices.LibVlc, item.Path, FromType.FromPath);
                await media.Parse(MediaParseOptions.ParseLocal);
                if (media.Duration > 0) item.DurationMs = media.Duration;
            }
            catch
            {
                // Unprobeable file: leave duration blank, still playable-attemptable.
            }
        }
    }
}
