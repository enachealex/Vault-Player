using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// Streaming shortcuts: library cards for titles the user owns on a service.
///
/// These never play in-app. Prime Video and friends are DRM-protected, there is
/// no consumer API for a user's purchased library, and circumventing the DRM
/// isn't something this app does. So a shortcut is exactly one thing — a link
/// that opens the service. No accounts, no credentials, no scraping.
/// </summary>
public static class StreamingServices
{
    /// <summary>Search URL template per service; {0} is the URL-encoded title.</summary>
    private static readonly (string Name, string SearchUrl)[] Catalog =
    {
        ("Prime Video", "https://www.primevideo.com/search/ref=atv_nb_sr?phrase={0}"),
        ("Netflix",     "https://www.netflix.com/search?q={0}"),
        ("Disney+",     "https://www.disneyplus.com/search?q={0}"),
        ("Max",         "https://play.max.com/search?q={0}"),
        ("Apple TV",    "https://tv.apple.com/search?term={0}"),
        ("Hulu",        "https://www.hulu.com/search?q={0}"),
        ("Paramount+",  "https://www.paramountplus.com/search/{0}/"),
        ("YouTube",     "https://www.youtube.com/results?search_query={0}"),
        ("Other",       "https://duckduckgo.com/?q={0}+watch+online"),
    };

    public static IReadOnlyList<string> ServiceNames =>
        Catalog.Select(c => c.Name).ToList();

    /// <summary>
    /// Where a shortcut should point. A saved direct link wins; otherwise we
    /// deep-link into the service's search for the title.
    /// </summary>
    public static string ResolveUrl(StreamingShortcut shortcut)
    {
        if (!string.IsNullOrWhiteSpace(shortcut.Url) && IsSafeUrl(shortcut.Url))
            return shortcut.Url!;

        var template = Catalog.FirstOrDefault(c => c.Name == shortcut.Service).SearchUrl
                       ?? Catalog[^1].SearchUrl;
        return string.Format(template, Uri.EscapeDataString(shortcut.Title));
    }

    /// <summary>
    /// Only ever hand http/https to the shell. The settings file is editable
    /// (and syncs between machines), so a card must not be able to launch
    /// arbitrary schemes like file: or a custom protocol handler.
    /// </summary>
    public static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Opens the shortcut in the user's default browser/app.</summary>
    public static bool Launch(string url)
    {
        if (!IsSafeUrl(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Saved shortcuts as library items, so they sort and search like films.</summary>
    public static List<MovieItem> AsMovieItems(IEnumerable<StreamingShortcut> shortcuts) =>
        shortcuts.Select(s => new MovieItem
        {
            Name = s.Title,
            Path = ResolveUrl(s),
            Kind = MovieKind.Shortcut,
            Service = s.Service,
        }).ToList();
}
