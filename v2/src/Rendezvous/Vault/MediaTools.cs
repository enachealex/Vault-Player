using System.Diagnostics;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Finds ffmpeg and ffprobe.
///
/// These used to be launched by bare name, which is correct on the deployed
/// server — its container image installs them — and wrong everywhere else. The
/// desktop app can now run this server on somebody's PC, and a PC has no ffmpeg
/// on its PATH. The failure was quiet and total: no durations, no posters, and
/// every MKV answering "this film can't be repackaged for a browser", which is
/// most of a real library and all of what a television needs.
///
/// So: beside the executable first, since that is where a shipped copy puts
/// them, then an explicit override, then PATH for the container.
/// </summary>
public static class MediaTools
{
    private static readonly Lazy<string?> _ffmpeg = new(() => Resolve("ffmpeg", "VAULT_FFMPEG"));
    private static readonly Lazy<string?> _ffprobe = new(() => Resolve("ffprobe", "VAULT_FFPROBE"));

    /// <summary>Full path or bare name, or null when it is not installed at all.</summary>
    public static string? Ffmpeg => _ffmpeg.Value;

    /// <summary>Full path or bare name, or null when it is not installed at all.</summary>
    public static string? Ffprobe => _ffprobe.Value;

    public static bool HasFfmpeg => Ffmpeg is not null;
    public static bool HasFfprobe => Ffprobe is not null;

    private static string? Resolve(string tool, string overrideVar)
    {
        // An explicit path wins, and is reported as missing if it is wrong
        // rather than silently falling through to a different copy — somebody
        // who set the variable wants that copy used.
        if (Environment.GetEnvironmentVariable(overrideVar) is { Length: > 0 } custom)
            return File.Exists(custom) && Works(custom) ? custom : null;

        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var beside = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(beside) && Works(beside)) return beside;

        return Works(tool) ? tool : null;
    }

    /// <summary>Run it once to confirm it is really there and really runs.</summary>
    private static bool Works(string path)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(path, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (probe is null) return false;
            if (!probe.WaitForExit(5000)) { try { probe.Kill(true); } catch { } return false; }
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// One line for the startup log, so a library that can never serve a
    /// television says so at boot rather than one film at a time, hours later.
    ///
    /// ffprobe is not the optional half it sounds like. It is what fills in each
    /// film's video codec, and a film with no known codec fails
    /// <c>HlsRemuxer.CanRemux</c> — so without ffprobe every single film answers
    /// "can't be repackaged for a browser" on a phone or a television, however
    /// ordinary it is.
    /// </summary>
    public static string Describe() =>
        (HasFfmpeg, HasFfprobe) switch
        {
            (true, true) => $"ffmpeg and ffprobe found ({Ffmpeg}).",
            (true, false) => "ffprobe is MISSING: films will have no runtimes, and NOTHING will play on a phone or a television — every film reports that it cannot be repackaged. Put ffprobe beside the executable or set VAULT_FFPROBE.",
            (false, true) => "ffmpeg is MISSING: no posters, and no film can be repackaged for a phone or a television.",
            (false, false) => "ffmpeg and ffprobe are both MISSING: no posters, no runtimes, and nothing will play on a phone or a television.",
        };
}
