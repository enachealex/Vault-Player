using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoPlayer.Protocol;

namespace VideoPlayer.App.Services;

/// <summary>
/// One live Watch Party connection. Control (play/pause/sync/chat) always flows
/// through the rendezvous WebSocket. Media flows through it too: the host, on a
/// "pull" request, POSTs the requested byte range back — so guests only ever
/// contact the rendezvous (works across the internet, no reachable host).
///
/// The rendezvous is local by default (the host spawns the bundled server);
/// point it at a deployed URL for room-code-only joins over the internet.
/// </summary>
public sealed class PartySession : IDisposable
{
    public const int RendezvousPort = 5555;

    private ClientWebSocket _socket = null!;
    private readonly HttpClient _pushClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string _pushBase = "";      // http base of the rendezvous (host pushes here)
    private string? _hostFilePath;      // host: the movie file to serve
    private long _lastBeaconAt;
    private bool _saidGoodbye;          // a real reason reached Closed already

    public bool IsHost { get; private set; }
    public string RoomCode { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    /// <summary>Whose screen the guest is watching. Equals DisplayName on the host.</summary>
    public string HostName { get; private set; } = "";

    /// <summary>Address guests should enter to reach the party server.</summary>
    public string ShareAddress { get; private set; } = "";

    // Guest-side session info.
    public string? MediaUrl { get; private set; }
    public string? MovieTitle { get; private set; }

    /// <summary>True when the room is built around a streaming title, not a local file.</summary>
    public bool IsExternal { get; private set; }
    public string? ExternalService { get; private set; }
    public string? ExternalUrl { get; private set; }
    public long DurationMs { get; private set; }
    public bool AlreadyStarted { get; private set; }

    // Latest sync beacon (guests correct against this).
    public long BeaconPositionMs { get; private set; }
    public bool BeaconPlaying { get; private set; } = true;
    public long BeaconAtUnixMs { get; private set; }
    public bool HasBeacon { get; private set; }

    /// <summary>One roster slot: id for kicks, name for display, verified = signed-in account.</summary>
    public record RosterEntry(string Id, string Name, bool Verified);

    /// <summary>This room only admits signed-in, approved accounts.</summary>
    public bool IsPrivate { get; private set; }

    /// <summary>Latest roster with identities; parallel to what RosterChanged reports.</summary>
    public RosterEntry[] Roster { get; private set; } = Array.Empty<RosterEntry>();

    public event Action<string>? GuestJoined;
    public event Action<string[]>? RosterChanged;
    /// <summary>(guestName, kind) — kind is "pause" or "resume".</summary>
    public event Action<string, string>? PauseRequested;
    public event Action<int, int>? ReadyChanged; // ready, total
    public event Action<string, string>? ChatReceived; // name, text
    /// <summary>seconds, title, service, url — an external "press play together" cue.</summary>
    public event Action<int, string, string, string>? CountdownStarted;
    /// <summary>title, service (null = local file) — the host swapped the film.</summary>
    public event Action<string, string?>? SwitchedFilm;
    public event Action? Started;
    public event Action? BeaconUpdated;
    public event Action<string>? Closed;

    private PartySession()
    {
    }

    // ---- Host --------------------------------------------------------------

    /// <param name="serverAddress">Rendezvous to use; null = spawn a local one.</param>
    /// <param name="isPrivate">Only signed-in, approved Vault accounts may join.</param>
    public static async Task<PartySession> HostAsync(Models.MovieItem movie, string displayName,
        string? serverAddress = null, bool isPrivate = false)
    {
        var session = await ConnectHostAsync(displayName, serverAddress, isPrivate);
        session._hostFilePath = movie.Path;

        var info = new FileInfo(movie.Path);
        // Tells web viewers (LAN Party's Movie Night) whether they can watch in
        // the browser or need the app. Checked here so the answer travels with
        // the room rather than every viewer guessing.
        var (webOk, webReason) = await WebPlayback.CheckAsync(movie);
        await session.SendAsync(new PartyMessage
        {
            Type = MsgType.Hello,
            MovieTitle = movie.Name,
            DurationMs = movie.DurationMs,
            FileSize = info.Length,
            ContentType = ContentTypeFor(movie.Path),
            WebPlayable = webOk,
            WebReason = webReason,
        });

        _ = session.PumpAsync();
        return session;
    }

    /// <summary>
    /// Host a party around a title on a streaming service. Nothing is relayed —
    /// the content is DRM-protected and stays in the service's own app — so the
    /// room exists purely for the roster, chat, and the synchronised start cue.
    /// </summary>
    public static async Task<PartySession> HostExternalAsync(
        string title, string service, string url, string displayName, string? serverAddress = null,
        bool isPrivate = false)
    {
        var session = await ConnectHostAsync(displayName, serverAddress, isPrivate);
        session.IsExternal = true;
        session.ExternalService = service;
        session.ExternalUrl = url;
        session.MovieTitle = title;

        await session.SendAsync(new PartyMessage
        {
            Type = MsgType.Hello,
            MovieTitle = title,
            Service = service,
            FileSize = 0, // no media relay for this room
        });

        _ = session.PumpAsync();
        return session;
    }

    /// <summary>
    /// Turns whatever the user typed into a base URL.
    ///
    ///     192.168.1.2                -> http://192.168.1.2:5555
    ///     party.example.com          -> http://party.example.com:5555
    ///     https://party.example.com  -> https://party.example.com
    ///
    /// A bare host keeps the original plain-HTTP behaviour so existing setups
    /// and LAN parties are unaffected. Writing a scheme is how TLS gets turned
    /// on, and it carries its own default port — which is why the port is no
    /// longer glued on unconditionally.
    /// </summary>
    public static Uri ResolveBase(string address)
    {
        var text = address.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            var explicitUri = new Uri(text.TrimEnd('/'), UriKind.Absolute);
            if (explicitUri.Scheme != Uri.UriSchemeHttp && explicitUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"Party server must be http or https, not '{explicitUri.Scheme}'.");
            return explicitUri;
        }
        return new Uri($"http://{text}:{RendezvousPort}");
    }

    /// <summary>WebSocket URL for a path, matching the base's security: https implies wss.</summary>
    private static Uri SocketUri(Uri baseUri, string pathAndQuery)
    {
        var builder = new UriBuilder(new Uri(baseUri, pathAndQuery))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return builder.Uri;
    }

    private static async Task<PartySession> ConnectHostAsync(string displayName, string? serverAddress,
        bool isPrivate = false)
    {
        var session = new PartySession { IsHost = true, DisplayName = displayName, IsPrivate = isPrivate };

        Uri baseUri;
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            await EnsureLocalRendezvousAsync();
            baseUri = ResolveBase("127.0.0.1");
            session.ShareAddress = MediaHttpServer.GetLanAddress().ToString();
        }
        else
        {
            baseUri = ResolveBase(serverAddress);
            session.ShareAddress = serverAddress.Trim();
        }
        session._pushBase = baseUri.ToString().TrimEnd('/');

        session._socket = new ClientWebSocket();
        await session._socket.ConnectAsync(
            SocketUri(baseUri, $"/ws?role=host&name={Uri.EscapeDataString(displayName)}"
                + (isPrivate ? "&private=1" : "")
                + await AuthQueryAsync()),
            Cts(8));

        var welcome = await session.ReceiveAsync() ?? throw new InvalidOperationException("Rendezvous did not answer.");
        session.RoomCode = welcome.Room ?? "";
        return session;
    }

    /// <summary>
    /// "&auth=..." when signed in, else "". Sent by hosts and guests alike so
    /// the roster shows real usernames; required by the server only for
    /// private rooms. Signed-out stays fully anonymous, exactly as before.
    /// </summary>
    private static async Task<string> AuthQueryAsync()
    {
        if (!AppServices.Account.IsSignedIn) return "";
        var token = await AppServices.Account.AccessTokenAsync();
        return token is null ? "" : $"&auth={Uri.EscapeDataString(token)}";
    }

    public void PushState(long positionMs, bool playing, bool force = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && now - _lastBeaconAt < 2000) return;
        _lastBeaconAt = now;
        _ = SendAsync(new PartyMessage { Type = MsgType.State, PositionMs = positionMs, Playing = playing, AtUnixMs = now });
    }

    public Task SendStartAsync() => SendAsync(new PartyMessage { Type = MsgType.Start });

    /// <summary>
    /// Swap what the room is watching without tearing it down — the code stays
    /// valid and guests stay connected. Lobby only; once playback has started
    /// the host ends the session instead.
    /// </summary>
    public async Task SwitchToLocalAsync(Models.MovieItem movie)
    {
        _hostFilePath = movie.Path;
        IsExternal = false;
        ExternalService = null;
        ExternalUrl = null;
        MovieTitle = movie.Name;
        var info = new FileInfo(movie.Path);
        var (webOk, webReason) = await WebPlayback.CheckAsync(movie);
        await SendAsync(new PartyMessage
        {
            Type = MsgType.Switch,
            MovieTitle = movie.Name,
            DurationMs = movie.DurationMs,
            FileSize = info.Length,
            ContentType = ContentTypeFor(movie.Path),
            WebPlayable = webOk,
            WebReason = webReason,
        });
    }

    public async Task SwitchToExternalAsync(string title, string service, string url)
    {
        _hostFilePath = null;
        IsExternal = true;
        ExternalService = service;
        ExternalUrl = url;
        MovieTitle = title;
        await SendAsync(new PartyMessage
        {
            Type = MsgType.Switch,
            MovieTitle = title,
            Service = service,
            FileSize = 0,
        });
    }

    /// <summary>
    /// Host → everyone: synchronised "press play in N seconds" for a title on a
    /// streaming service. Nothing is relayed but the cue — each member plays it
    /// in the service's own app, because the content is DRM-protected.
    /// </summary>
    public Task SendCountdownAsync(int seconds, string title, string service, string url) =>
        SendAsync(new PartyMessage
        {
            Type = MsgType.Countdown,
            Seconds = seconds,
            MovieTitle = title,
            Service = service,
            MediaUrl = url,
        });

    /// <summary>
    /// Append a line to %APPDATA%\VideoPlayerV2\party.log. The media relay
    /// spans three machines, so when it breaks there is nothing on screen to
    /// explain it — the guest just sees black. Best-effort and never throws.
    /// </summary>
    internal static void Log(string line)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoPlayerV2", "party.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break playback.
        }
    }

    /// <summary>Serve a requested byte range to the rendezvous (for a guest).</summary>
    private async Task PushRangeAsync(string reqId, long start, long end)
    {
        if (_hostFilePath is null) { Log($"pull {reqId}: ignored, no host file"); return; }
        var url = $"{_pushBase}/upload/{reqId}";
        using var content = new FileRangeContent(_hostFilePath, start, end);
        try
        {
            using var resp = await _pushClient.PostAsync(url, content);
            Log($"pull {reqId}: sent {content.BytesSent:N0} of {end - start + 1:N0} bytes, HTTP {(int)resp.StatusCode}");
        }
        catch when (content.BytesSent > 0)
        {
            // libVLC asks for a range running to the end of the file, takes the
            // few megabytes it wants, then hangs up and seeks elsewhere. A push
            // dying part-way is the normal shape of playback, not a fault --
            // provided bytes actually moved.
            Log($"pull {reqId}: guest stopped reading after {content.BytesSent:N0} bytes (routine)");
        }
        catch (Exception ex)
        {
            // Nothing transferred at all. This is the case worth alarming
            // about: the relay is broken and the guest is watching black.
            var detail = ex.Message;
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                detail += $" <- {inner.GetType().Name}: {inner.Message}";
            Log($"pull {reqId}: FAILED with 0 bytes sent to {url} -- {ex.GetType().Name}: {detail}");
        }
    }

    // ---- Guest -------------------------------------------------------------

    public static async Task<PartySession> JoinAsync(string serverAddress, string code, string displayName)
    {
        var address = serverAddress.Trim();
        var session = new PartySession
        {
            IsHost = false,
            DisplayName = displayName,
            RoomCode = code.Trim().ToUpperInvariant(),
            ShareAddress = address,
        };
        var baseUri = ResolveBase(address);
        session._socket = new ClientWebSocket();
        await session._socket.ConnectAsync(
            SocketUri(baseUri, $"/ws?role=guest&room={Uri.EscapeDataString(session.RoomCode)}&name={Uri.EscapeDataString(displayName)}"
                + await AuthQueryAsync()),
            Cts(8));

        var welcome = await session.ReceiveAsync() ?? throw new InvalidOperationException("No answer from the party.");
        if (welcome.Type == MsgType.Error) throw new InvalidOperationException(welcome.Text ?? "Join failed.");

        // Guests pull the movie from the rendezvous itself — one address, any NAT.
        session.MediaUrl = new Uri(baseUri, $"/stream/{session.RoomCode}").ToString();
        session.MovieTitle = welcome.MovieTitle;
        session.HostName = welcome.Name ?? "the host";
        // A service name means the host is watching something DRM-protected:
        // there's no stream to pull, only the synchronised start cue.
        session.IsExternal = welcome.Service is not null;
        session.ExternalService = welcome.Service;
        session.IsPrivate = welcome.Private == true;
        session.DurationMs = welcome.DurationMs ?? 0;
        session.AlreadyStarted = welcome.Started == true;
        if (welcome.PositionMs is { } p)
        {
            session.BeaconPositionMs = p;
            session.BeaconPlaying = welcome.Playing ?? true;
            session.BeaconAtUnixMs = welcome.AtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            session.HasBeacon = true;
        }

        _ = session.PumpAsync();
        return session;
    }

    public Task RequestPauseAsync(bool wantPause) =>
        SendAsync(new PartyMessage { Type = MsgType.PauseRequest, Text = wantPause ? "pause" : "resume" });

    public Task SendReadyAsync() => SendAsync(new PartyMessage { Type = MsgType.Ready });

    // ---- Shared ------------------------------------------------------------

    public Task SendChatAsync(string text) => SendAsync(new PartyMessage { Type = MsgType.Chat, Text = text });

    /// <summary>Host: remove a guest by roster id. They can't rejoin this room on the same account.</summary>
    public Task KickAsync(string guestId) =>
        SendAsync(new PartyMessage { Type = MsgType.Kick, GuestId = guestId });

    private async Task PumpAsync()
    {
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var msg = await ReceiveAsync();
                if (msg is null) break;

                // Host media pulls run off the UI thread (file read + HTTP push).
                if (msg.Type == MsgType.Pull && IsHost && msg.ReqId is not null)
                {
                    _ = PushRangeAsync(msg.ReqId, msg.Start ?? 0, msg.End ?? 0);
                    continue;
                }
                OnUi(() => Handle(msg));
            }
        }
        catch
        {
        }
        // A kick or "host ended" already told the user why; don't follow the
        // real reason with a generic one.
        OnUi(() => { if (!_saidGoodbye) Closed?.Invoke("Connection lost."); });
    }

    private void Handle(PartyMessage msg)
    {
        switch (msg.Type)
        {
            case MsgType.Join: GuestJoined?.Invoke(msg.Name ?? "Someone"); break;
            case MsgType.Roster:
                var names = msg.Members ?? Array.Empty<string>();
                // Ids and verified flags ride alongside the names; an older
                // server sends names alone, and the roster still works minus
                // the kick menu and the verified marks.
                Roster = names.Select((memberName, i) => new RosterEntry(
                    msg.MemberIds is { } ids && i < ids.Length ? ids[i] : "",
                    memberName,
                    msg.Verified is { } v && i < v.Length && v[i])).ToArray();
                RosterChanged?.Invoke(names);
                break;
            case MsgType.Error:
                // Mid-session error = the server showed us the door (kicked,
                // room closed under us). The socket is about to die; say why.
                Closed?.Invoke(msg.Text ?? "Disconnected by the server.");
                _saidGoodbye = true;
                break;
            case MsgType.PauseRequest: PauseRequested?.Invoke(msg.Name ?? "Someone", msg.Text ?? "pause"); break;
            case MsgType.Ready: ReadyChanged?.Invoke(msg.ReadyCount ?? 0, msg.GuestCount ?? 0); break;
            case MsgType.Chat: ChatReceived?.Invoke(msg.Name ?? "?", msg.Text ?? ""); break;
            case MsgType.Switch:
                MovieTitle = msg.MovieTitle;
                IsExternal = msg.Service is not null;
                ExternalService = msg.Service;
                DurationMs = msg.DurationMs ?? 0;
                SwitchedFilm?.Invoke(msg.MovieTitle ?? "a different film", msg.Service);
                break;
            case MsgType.Countdown:
                CountdownStarted?.Invoke(msg.Seconds ?? 5, msg.MovieTitle ?? "the film",
                    msg.Service ?? "your streaming service", msg.MediaUrl ?? "");
                break;
            case MsgType.Start: Started?.Invoke(); break;
            case MsgType.State:
                BeaconPositionMs = msg.PositionMs ?? 0;
                BeaconPlaying = msg.Playing ?? true;
                BeaconAtUnixMs = msg.AtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                HasBeacon = true;
                BeaconUpdated?.Invoke();
                break;
            case MsgType.Bye:
                Closed?.Invoke("The host ended the session.");
                _saidGoodbye = true;
                break;
        }
    }

    private async Task SendAsync(PartyMessage msg)
    {
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task<PartyMessage?> ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return PartyMessage.FromJson(sb.ToString());
    }

    private static CancellationToken Cts(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Make sure a server is running here for the party to go through.
    ///
    /// This used to start the executable itself, with no environment at all.
    /// That was survivable while a party was the only thing the server did, and
    /// stopped being so once the same server, on the same port, could also be
    /// somebody's film library: whichever path started first won, and if this
    /// one did, the user's library was quietly replaced by a server pointed at
    /// no films and signing tokens with the published development key — while
    /// the library screen, seeing a healthy port, reported success.
    ///
    /// So there is one way to start it now, and this is not it.
    /// </summary>
    private static async Task EnsureLocalRendezvousAsync()
    {
        if (await AppServices.Library.StartForPartyAsync()) return;
        throw new InvalidOperationException(
            AppServices.Library.LastError ?? "The Watch Party server would not start.");
    }

    internal static string? FindRendezvousExe()
    {
        // Where a shipped copy lives: its own folder beside the app, so the two
        // applications' dependencies never share a directory.
        var packaged = Path.Combine(AppContext.BaseDirectory, "server", "VideoPlayer.Rendezvous.exe");
        if (File.Exists(packaged)) return packaged;

        var beside = Path.Combine(AppContext.BaseDirectory, "VideoPlayer.Rendezvous.exe");
        if (File.Exists(beside)) return beside;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var dev = Path.Combine(dir.FullName, "src", "Rendezvous", "bin", "Release", "net10.0", "VideoPlayer.Rendezvous.exe");
            if (File.Exists(dev)) return dev;
            dir = dir.Parent!;
        }
        return null;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        try { _socket?.Abort(); } catch { }
        _pushClient.Dispose();
    }

    /// <summary>HttpContent that streams a byte range of a file into the request body.</summary>
    private sealed class FileRangeContent : HttpContent
    {
        private readonly string _path;
        private readonly long _start, _end;

        public FileRangeContent(string path, long start, long end)
        {
            _path = path;
            _start = start;
            _end = end;
        }

        /// <summary>Bytes handed to the network, so callers can tell a stalled
        /// transfer from one the guest simply stopped reading.</summary>
        public long BytesSent { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            fs.Seek(_start, SeekOrigin.Begin);
            var remaining = _end - _start + 1;
            var buffer = new byte[1 << 16];
            while (remaining > 0)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                BytesSent += read;
                remaining -= read;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _end - _start + 1;
            return true;
        }
    }
}
