using System;
using System.Text.RegularExpressions;

namespace VideoPlayer.App.Services;

/// <summary>
/// Watch Party invite links. Two shapes:
///   share:  https://party.thejumpvault.com/join/MOVIE-XXXX[?server=…]
///           — clickable anywhere (LAN Party, Discord, a text); the server
///           serves a page that bounces into the app, with a download
///           fallback for people who don't have it yet.
///   launch: vaultmovies://join?code=MOVIE-XXXX&server=…
///           — what that page (or the OS) hands to the app itself.
/// </summary>
public static class InviteLink
{
    /// <summary>Where the /join bounce page lives (the deployed rendezvous).</summary>
    public const string PublicHost = "party.thejumpvault.com";

    /// <summary>
    /// The https link a host shares for a room. The stored server address may
    /// carry a scheme ("https://party.thejumpvault.com") — compare without it so
    /// the default relay produces the short link with no redundant ?server=.
    /// </summary>
    public static string For(string code, string server)
    {
        var bare = server
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        return string.Equals(bare, PublicHost, StringComparison.OrdinalIgnoreCase)
            ? $"https://{PublicHost}/join/{code}"
            : $"https://{PublicHost}/join/{code}?server={Uri.EscapeDataString(server)}";
    }

    /// <summary>
    /// Parse a vaultmovies:// launch argument into (server, code). False for
    /// anything that isn't a well-formed join link with a plausible room code.
    /// </summary>
    public static bool TryParse(string arg, out string server, out string code)
    {
        server = PublicHost;
        code = "";
        if (string.IsNullOrWhiteSpace(arg)) return false;
        if (!arg.StartsWith("vaultmovies:", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(arg, UriKind.Absolute, out var uri)) return false;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i <= 0) continue;
            var key = pair[..i];
            var value = Uri.UnescapeDataString(pair[(i + 1)..]);
            if (key.Equals("server", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                server = value;
            else if (key.Equals("code", StringComparison.OrdinalIgnoreCase))
                code = value.Trim().ToUpperInvariant();
        }
        return Regex.IsMatch(code, "^MOVIE-[A-Z0-9]{4}$");
    }
}
