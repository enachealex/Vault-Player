using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer.App.Services;

/// <summary>
/// Runs the film server on this machine, so somebody with a folder of films and
/// no appetite for Docker can still watch them on their television.
///
/// It is the same server that runs on a proper box — the one that ships in this
/// installation, given a films folder and somewhere to keep its database. It
/// indexes, it makes posters, it repackages the awkward half of a library for
/// browsers, and a television on the same network reaches it exactly as it
/// would reach a server in a cupboard.
///
/// Two things it cannot do, both said plainly in the setup screen rather than
/// discovered later: it is not reachable from outside the house, which needs a
/// way in through the router; and it is only up while this PC is awake.
/// </summary>
public sealed class LibraryServer
{
    /// <summary>
    /// The port the app already expects a relay on, so hosting a party and
    /// hosting a library are one process rather than two.
    /// </summary>
    public const int Port = PartySession.RendezvousPort;

    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>True while a server this app started is running.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Set when the last start failed, in words somebody can act on.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when the server starts, stops, or fails.</summary>
    public event Action? Changed;

    /// <summary>Where the server keeps its database, posters and repackaged films.</summary>
    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoPlayerV2", "server");

    /// <summary>The address this app should talk to when hosting here.</summary>
    public static string LocalAddress => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// The address to type into a television on the same network, or null when
    /// this machine has no usable network address — which is the honest answer
    /// on an offline PC, rather than a URL that resolves for nobody.
    /// </summary>
    public static string? LanAddress()
    {
        var ip = LocalIPv4();
        return ip is null ? null : $"http://{ip}:{Port}";
    }

    private static string? LocalIPv4()
    {
        try
        {
            // The address on whichever interface actually carries traffic, found
            // by asking the routing table rather than by taking the first
            // adapter — a machine with a VPN, a virtual switch, or a phone
            // tethered to it has several, and most of them are wrong.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);   // no packet is sent; this only picks a route
            var ip = (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
            return ip == "0.0.0.0" ? null : ip;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A signing key for this installation, made once and kept.
    ///
    /// The server carries a development fallback key and says in its own source
    /// that production must replace it. Generating one here means nobody has to
    /// be told to, and means two people running this app never share a key that
    /// would let either mint tokens against the other's library.
    /// </summary>
    private static string SigningKey()
    {
        var settings = AppServices.Settings;
        if (string.IsNullOrWhiteSpace(settings.ServerSigningKey))
        {
            settings.ServerSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            settings.Save();
        }
        return settings.ServerSigningKey!;
    }

    /// <summary>
    /// Start the library the settings describe, if they describe one. Called at
    /// app startup; does nothing at all unless the user chose to host here.
    /// </summary>
    public Task<bool> StartFromSettingsAsync(CancellationToken ct = default)
    {
        var settings = AppServices.Settings;
        if (settings.LibraryMode != "this-pc" || !settings.HostAutoStart) return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(settings.HostFilmsFolder)) return Task.FromResult(false);
        return StartAsync(settings.HostFilmsFolder!, settings.HostShareOnNetwork, ct);
    }

    /// <summary>
    /// Start a server here for a Watch Party, with no library behind it.
    ///
    /// Hosting a party and hosting a library are the same executable on the same
    /// port, so this exists to make sure a party never starts a *second*,
    /// bare-configured one: that server would have no films, would fall back to
    /// the published development signing key, and would then be adopted by the
    /// library code as healthy — reporting success while serving nothing.
    /// </summary>
    public Task<bool> StartForPartyAsync(CancellationToken ct = default)
    {
        var settings = AppServices.Settings;
        if (settings.LibraryMode == "this-pc" && !string.IsNullOrWhiteSpace(settings.HostFilmsFolder))
            return StartAsync(settings.HostFilmsFolder!, settings.HostShareOnNetwork, ct);

        // A party has to be reachable by its guests, so this one always binds
        // wide regardless of the library sharing preference — which is about a
        // library, and there isn't one here.
        var empty = Path.Combine(DataFolder, "no-library");
        Directory.CreateDirectory(empty);
        return StartAsync(empty, shareOnNetwork: true, ct);
    }

    /// <summary>
    /// Start serving <paramref name="filmsFolder"/>, or attach to a server that
    /// is already answering on this machine.
    /// </summary>
    /// <param name="shareOnNetwork">
    /// False binds loopback only, so nothing but this PC can reach the library.
    /// </param>
    public async Task<bool> StartAsync(string filmsFolder, bool shareOnNetwork, CancellationToken ct = default)
    {
        LastError = null;

        if (!Directory.Exists(filmsFolder))
        {
            LastError = "That folder no longer exists. Choose the folder your films are in.";
            Changed?.Invoke();
            return false;
        }

        // Anything already listening is our own server from a previous run or a
        // party relay. Either way a second one would only lose the race for the
        // port, so adopt what is there.
        if (await HealthyAsync(ct)) { Changed?.Invoke(); return true; }

        var exe = PartySession.FindRendezvousExe();
        if (exe is null)
        {
            LastError = "The library server is missing from this installation. Reinstalling should restore it.";
            Changed?.Invoke();
            return false;
        }

        Directory.CreateDirectory(DataFolder);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PORT"] = Port.ToString();
        psi.Environment["VAULT_BIND"] = shareOnNetwork ? "0.0.0.0" : "127.0.0.1";
        psi.Environment["VAULT_MEDIA"] = filmsFolder;
        psi.Environment["VAULT_DB"] = Path.Combine(DataFolder, "vault.db");
        // Films sent up from a phone or another PC land in the same folder the
        // library is read from, which is the only place somebody would look for
        // them afterwards.
        psi.Environment["VAULT_UPLOADS"] = filmsFolder;
        // Repackaged films are the bulky part and are pure cache, so they sit
        // beside the database rather than in the films folder — which the server
        // treats as read-only and which may be on a disk that is full, a NAS
        // share, or genuinely not writable.
        psi.Environment["VAULT_HLS_CACHE"] = Path.Combine(DataFolder, "hls");
        psi.Environment["VAULT_JWT_SECRET"] = SigningKey();

        // The server looks for ffmpeg and ffprobe beside itself and then on
        // PATH, and a PC has neither. This app ships its own ffmpeg for
        // thumbnails, so hand that copy over: without it there are no posters
        // and, worse, no MKV plays on a television at all.
        foreach (var (variable, tool) in new[] { ("VAULT_FFMPEG", "ffmpeg.exe"), ("VAULT_FFPROBE", "ffprobe.exe") })
        {
            var path = Path.Combine(AppContext.BaseDirectory, tool);
            if (File.Exists(path)) psi.Environment[variable] = path;
        }

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            LastError = "Could not start the library server: " + ex.Message;
            Changed?.Invoke();
            return false;
        }

        // Wait for it to open its database and bind. A first run over a large
        // folder also starts indexing, but that happens after it is answering,
        // so there is nothing to wait for here.
        for (var i = 0; i < 30; i++)
        {
            if (await HealthyAsync(ct)) { Changed?.Invoke(); return true; }
            if (_process is { HasExited: true })
            {
                LastError = $"The library server stopped straight away (code {_process.ExitCode}). "
                          + $"Another program may already be using port {Port}.";
                Changed?.Invoke();
                return false;
            }
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { return false; }
        }

        LastError = "The library server did not start in time.";
        Changed?.Invoke();
        return false;
    }

    /// <summary>
    /// Tools the hosted library needs and cannot find, or an empty array.
    ///
    /// Worth reporting on screen rather than in a log, because the consequence
    /// is severe and completely unlike the cause: without ffprobe the server
    /// never learns any film's video codec, and a film with an unknown codec is
    /// refused for phones and televisions — so the library looks perfect on the
    /// PC and every single film fails on the TV.
    /// </summary>
    public static string[] MissingTools()
    {
        var exe = PartySession.FindRendezvousExe();
        var serverDir = exe is null ? null : Path.GetDirectoryName(exe);
        var appDir = AppContext.BaseDirectory;

        return new[] { "ffmpeg.exe", "ffprobe.exe" }
            .Where(tool =>
                !(serverDir is not null && File.Exists(Path.Combine(serverDir, tool)))
                && !File.Exists(Path.Combine(appDir, tool)))
            .ToArray();
    }

    /// <summary>Whether something is answering as a Vault server on this machine.</summary>
    public async Task<bool> HealthyAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetStringAsync($"{LocalAddress}/health", ct)).Trim() == "ok"; }
        catch { return false; }
    }

    /// <summary>
    /// Reach a server at <paramref name="address"/> and say whether it is one of
    /// ours. Used by the setup screen so "connect to my own server" fails at the
    /// moment the address is typed rather than at the moment a film is opened.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ProbeAsync(string address, CancellationToken ct = default)
    {
        address = address.Trim().TrimEnd('/');
        if (address.Length == 0) return (false, "Enter your server's address.");
        if (!address.Contains("://")) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return (false, "That does not look like an address. Try something like http://192.168.1.10:5555");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            var health = (await http.GetStringAsync($"{address}/health", ct)).Trim();
            if (health != "ok")
                return (false, "Something answered there, but it is not a Vault server.");
            return (true, $"Connected to {uri.Host}.");
        }
        catch (TaskCanceledException)
        {
            return (false, "No answer from that address. Check the server is running and that you typed the port.");
        }
        catch (HttpRequestException ex)
        {
            return (false, "Could not reach that address. " + Simplify(ex));
        }
    }

    private static string Simplify(HttpRequestException ex) =>
        ex.InnerException is SocketException socket
            ? socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "Nothing is listening on that port.",
                SocketError.HostNotFound => "That name did not resolve.",
                SocketError.TimedOut => "The connection timed out.",
                _ => socket.Message,
            }
            : ex.Message;

    /// <summary>
    /// Stop the server this app started. A server started by anything else — a
    /// party relay, or one left over from a previous run — is left alone,
    /// because killing somebody else's process to tidy up is how a party ends
    /// mid-film.
    /// </summary>
    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or not ours to kill.
        }
        _process = null;
        Changed?.Invoke();
    }
}
