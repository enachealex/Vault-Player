using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// Streaming shortcuts: library cards for titles the user owns on a service.
///
/// Browsing a service (signing in, finding what you own) happens in the in-app
/// browser; PLAYING is handed to the real browser. Prime Video and friends are
/// DRM-protected and only permit playback in certified browsers, so the app
/// never tries to decode their video — and never touches credentials, which
/// stay between the user and the service's own sign-in page.
/// </summary>
public static class StreamingServices
{
    /// <summary>
    /// Per service: the search template ({0} = URL-encoded title) and the page
    /// to open when browsing — the user's own library where the service has
    /// one, otherwise the home page.
    /// </summary>
    private static readonly (string Name, string SearchUrl, string BrowseUrl)[] Catalog =
    {
        ("Prime Video", "https://www.primevideo.com/search/ref=atv_nb_sr?phrase={0}", "https://www.primevideo.com/storefront/library"),
        ("Netflix",     "https://www.netflix.com/search?q={0}",                        "https://www.netflix.com/browse"),
        ("Disney+",     "https://www.disneyplus.com/search?q={0}",                     "https://www.disneyplus.com/home"),
        ("Max",         "https://play.max.com/search?q={0}",                           "https://play.max.com"),
        ("Apple TV",    "https://tv.apple.com/search?term={0}",                        "https://tv.apple.com/library"),
        ("Hulu",        "https://www.hulu.com/search?q={0}",                           "https://www.hulu.com/hub/home"),
        ("Paramount+",  "https://www.paramountplus.com/search/{0}/",                   "https://www.paramountplus.com/"),
        ("YouTube",     "https://www.youtube.com/results?search_query={0}",            "https://www.youtube.com/feed/library"),
        ("Other",       "https://duckduckgo.com/?q={0}+watch+online",                  "https://duckduckgo.com/"),
    };

    public static IReadOnlyList<string> ServiceNames =>
        Catalog.Select(c => c.Name).ToList();

    /// <summary>Services worth offering a "browse in-app" entry for.</summary>
    public static IReadOnlyList<string> BrowsableServices =>
        Catalog.Where(c => c.Name != "Other").Select(c => c.Name).ToList();

    /// <summary>Where "browse this service" starts — their library page where one exists.</summary>
    public static string BrowseUrlFor(string service) =>
        Catalog.FirstOrDefault(c => c.Name == service).BrowseUrl ?? Catalog[^1].BrowseUrl;

    /// <summary>
    /// True when a URL is a DRM-protected player page. Those can't decode in an
    /// embedded browser (no Widevine, and the services only allow certified
    /// ones), so the app offers to hand the page to the real browser instead of
    /// letting someone stare at a black rectangle.
    /// </summary>
    public static bool IsProtectedPlayer(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        // YouTube is not DRM-gated and plays fine embedded, so it's excluded.
        return url.Contains("netflix.com/watch", StringComparison.OrdinalIgnoreCase)
            || url.Contains("hulu.com/watch", StringComparison.OrdinalIgnoreCase)
            || url.Contains("play.max.com/video", StringComparison.OrdinalIgnoreCase)
            || url.Contains("disneyplus.com/video", StringComparison.OrdinalIgnoreCase)
            || url.Contains("disneyplus.com/play", StringComparison.OrdinalIgnoreCase)
            || url.Contains("paramountplus.com/shows/video", StringComparison.OrdinalIgnoreCase)
            || url.Contains("paramountplus.com/movies/video", StringComparison.OrdinalIgnoreCase)
            || url.Contains("primevideo.com/detail", StringComparison.OrdinalIgnoreCase)
            || url.Contains("amazon.com/gp/video/detail", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tv.apple.com/episode", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tv.apple.com/movie", StringComparison.OrdinalIgnoreCase);
    }

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
