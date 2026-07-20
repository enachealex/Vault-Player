using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VideoPlayer.App.Services;

/// <summary>
/// Makes the app show up under Windows' "Open with" for video files.
///
/// Deliberately conservative: this registers the app as an *available* choice
/// and nothing more. It does not make itself the default handler for any
/// extension, because silently taking over someone's file types is hostile —
/// if they want that, Windows' own "Always use this app" checkbox does it.
///
/// Everything is written under HKEY_CURRENT_USER, so it needs no elevation and
/// affects only the person who ran the app.
/// </summary>
public static class FileAssociations
{
    /// <summary>What we claim to open. Matches MovieLibrary's scan list.</summary>
    private static readonly string[] Extensions =
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ogv", ".3gp", ".ts",
    };

    /// <summary>
    /// Idempotent, so it can run on every launch — the executable path changes
    /// with each Velopack update, and a stale command line would open the old
    /// version (or nothing at all).
    /// </summary>
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            var appKeyName = Path.GetFileName(exe); // e.g. VideoPlayer.App.exe
            using var app = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\Applications\{appKeyName}");
            app.SetValue("FriendlyAppName", "Vault Movies");

            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue("", $"\"{exe}\" \"%1\"");

            // Without SupportedTypes, Windows offers the app for everything,
            // which is noise in menus where it makes no sense.
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions) types.SetValue(ext, "");

            // Listing the app under each extension's OpenWithList is what puts
            // it in the "Open with" flyout rather than behind "Choose another
            // app". Still not the default -- that stays the user's choice.
            foreach (var ext in Extensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{ext}\OpenWithList\{appKeyName}");
            }
        }
        catch (Exception ex)
        {
            // A player that cannot write the registry should still play films.
            Debug.WriteLine($"File association registration skipped: {ex.Message}");
        }
    }

    /// <summary>True if the path looks like something this app can play.</summary>
    public static bool IsPlayableFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return Array.Exists(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}
