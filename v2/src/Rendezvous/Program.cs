using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VideoPlayer.Protocol;
using VideoPlayer.Rendezvous.Vault;

// Watch Party rendezvous. Two jobs:
//   1. Control relay — one host ↔ many guests over WebSockets (play/pause,
//      sync beacons, chat, roster, pause requests).
//   2. MEDIA relay — guests pull the movie from THIS server (GET /stream/{room});
//      the server asks the host for the needed byte range over the control
//      channel, the host pushes it back (POST /upload/{reqId}), and the server
//      pipes it straight to the guest. So guests need only the server address +
//      a room code — no reachable host, works across the internet through NAT.
//
// Runs locally today (the host auto-spawns it); deploy this same binary to a
// small cloud box for room-code-only joins over the internet.

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "5555";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Kestrel's defaults assume ordinary web traffic and are all wrong here: this
// server exists to shuttle entire films between machines.
builder.WebHost.ConfigureKestrel(options =>
{
    // The default cap is 30 MB. A host pushing a 2.65 GB film hit it and had
    // the connection reset mid-upload, which surfaced as a guest staring at a
    // black screen with a perfectly healthy-looking party around it.
    options.Limits.MaxRequestBodySize = null;

    // Minimum data-rate enforcement (240 bytes/s) aborts transfers it judges
    // too slow. A party idles between byte-range pulls and guests can be on
    // poor connections, both of which look like a stall to Kestrel.
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

// ---- Accounts + library sync (Vault Movies) --------------------------------
// SQLite keeps this self-contained: one file in a volume, no dependency on the
// Supabase/jobs Postgres also on this box, and the same provider works locally
// for testing. Isolated from everything else the server runs.
var dbPath = Environment.GetEnvironmentVariable("VAULT_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "vault.db");
builder.Services.AddDbContext<VaultDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddIdentityCore<VaultUser>(o =>
    {
        // Length only. Character-class rules push people toward worse, harder
        // passwords; for a friends app a decent length is the sensible bar.
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<VaultDbContext>()
    .AddDefaultTokenProviders(); // needed for GeneratePasswordResetTokenAsync
builder.Services.AddSingleton<VaultTokens>();
builder.Services.AddScoped<EmailSender>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new VaultTokens(builder.Configuration).Validation();
        // Keep claims as they were issued. Otherwise the handler rewrites "sub"
        // to ClaimTypes.NameIdentifier and code reading "sub" finds nothing.
        o.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

// Create the schema on first run (no migration tooling needed at this scale).
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<VaultDbContext>().Database.EnsureCreated();

app.MapVaultApi();

var rooms = new ConcurrentDictionary<string, Room>();
var pulls = new ConcurrentDictionary<string, PendingPull>();

app.MapGet("/health", () => "ok");

// ---- Media relay: guest pulls, server fetches from host --------------------

app.MapMethods("/stream/{room}", new[] { "GET", "HEAD" }, async (string room, HttpContext ctx) =>
{
    room = room.ToUpperInvariant();
    if (!rooms.TryGetValue(room, out var r) || r.FileSize <= 0)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    long start = 0, end = r.FileSize - 1;
    var isRange = false;
    var rangeHeader = ctx.Request.Headers.Range.ToString();
    var m = System.Text.RegularExpressions.Regex.Match(rangeHeader, @"bytes=(\d*)-(\d*)");
    if (m.Success)
    {
        isRange = true;
        if (m.Groups[1].Value.Length > 0)
        {
            start = long.Parse(m.Groups[1].Value);
            if (m.Groups[2].Value.Length > 0) end = Math.Min(long.Parse(m.Groups[2].Value), r.FileSize - 1);
        }
        else if (m.Groups[2].Value.Length > 0)
        {
            start = Math.Max(0, r.FileSize - long.Parse(m.Groups[2].Value));
        }
    }
    if (start > end || start >= r.FileSize)
    {
        ctx.Response.StatusCode = 416;
        ctx.Response.Headers.ContentRange = $"bytes */{r.FileSize}";
        return;
    }

    ctx.Response.StatusCode = isRange ? 206 : 200;
    ctx.Response.ContentType = r.ContentType ?? "application/octet-stream";
    ctx.Response.Headers.AcceptRanges = "bytes";
    ctx.Response.ContentLength = end - start + 1;
    if (isRange) ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{r.FileSize}";
    if (HttpMethods.IsHead(ctx.Request.Method)) return;

    // Ask the host for this range.
    var reqId = Guid.NewGuid().ToString("N");
    var pull = new PendingPull();
    pulls[reqId] = pull;
    try
    {
        await SendTo(r.Host, new PartyMessage { Type = MsgType.Pull, ReqId = reqId, Start = start, End = end });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(20)); // host must begin pushing within 20s
        var body = await pull.Body.Task.WaitAsync(timeout.Token);

        await body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        pull.Done.TrySetResult();
    }
    catch (OperationCanceledException)
    {
        // The guest stopped reading — closed the app, seeked elsewhere, or the
        // host took too long. Routine, not a failure.
        pull.Done.TrySetCanceled();
        ctx.Abort();
    }
    catch (Exception ex)
    {
        pull.Done.TrySetException(ex);
        // Content-Length went out with the headers, so returning normally here
        // makes Kestrel throw "Response Content-Length mismatch" and log an
        // unhandled exception. Dropping the connection is the honest signal:
        // the guest sees a broken transfer and re-requests the range.
        ctx.Abort();
    }
    finally
    {
        pulls.TryRemove(reqId, out _);
    }
});

// Host pushes the requested bytes here; body is piped to the waiting guest.
app.MapPost("/upload/{reqId}", async (string reqId, HttpContext ctx) =>
{
    if (!pulls.TryGetValue(reqId, out var pull))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    pull.Body.TrySetResult(ctx.Request.Body);
    try
    {
        await pull.Done.Task; // don't return until the guest has consumed the stream
    }
    catch
    {
        // Guest went away — let the host's push unwind.
    }
});

// ---- Control channel -------------------------------------------------------

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var role = context.Request.Query["role"].ToString();
    var name = context.Request.Query["name"].ToString();
    var code = context.Request.Query["room"].ToString().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(name)) name = "Someone";

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    if (role == "host")
    {
        var room = new Room { Code = NewCode(rooms), HostName = name, Host = socket };
        rooms[room.Code] = room;
        await SendTo(socket, new PartyMessage { Type = MsgType.Welcome, Room = room.Code });
        await Pump(socket, async msg =>
        {
            switch (msg.Type)
            {
                case MsgType.Hello:
                    room.MovieTitle = msg.MovieTitle;
                    room.Service = msg.Service; // non-null => streaming title, no media relay
                    room.DurationMs = msg.DurationMs;
                    room.FileSize = msg.FileSize ?? 0;
                    room.ContentType = msg.ContentType;
                    break;
                case MsgType.Start:
                    room.Started = true;
                    await room.Broadcast(msg);
                    break;
                case MsgType.State:
                    room.LastState = msg;
                    await room.Broadcast(msg);
                    break;
                case MsgType.Chat:
                    msg.Name ??= room.HostName;
                    await room.Broadcast(msg);
                    break;
                case MsgType.Switch:
                    // Same room and code — only what's being watched changes.
                    room.MovieTitle = msg.MovieTitle;
                    room.Service = msg.Service;
                    room.DurationMs = msg.DurationMs;
                    room.FileSize = msg.FileSize ?? 0;
                    room.ContentType = msg.ContentType;
                    room.LastState = null;
                    await room.Broadcast(msg);
                    break;
                case MsgType.Countdown:
                    // Pure relay: guests count down locally from receipt, so a
                    // few tens of ms of network jitter is irrelevant here.
                    await room.Broadcast(msg);
                    break;
            }
        });
        rooms.TryRemove(room.Code, out _);
        await room.Broadcast(new PartyMessage { Type = MsgType.Bye });
        foreach (var (_, g) in room.Guests) TryClose(g);
        return;
    }

    // Guest.
    if (!rooms.TryGetValue(code, out var joined))
    {
        await SendTo(socket, new PartyMessage { Type = MsgType.Error, Text = "Room not found. Check the code." });
        return;
    }

    var guestId = Guid.NewGuid().ToString("N");
    joined.Guests[guestId] = socket;
    joined.GuestNames[guestId] = name;
    await SendTo(socket, new PartyMessage
    {
        Type = MsgType.Welcome,
        Room = joined.Code,
        MovieTitle = joined.MovieTitle,
        Service = joined.Service,
        Name = joined.HostName, // so guests can say whose screen they're watching
        DurationMs = joined.DurationMs,
        Started = joined.Started,
        PositionMs = joined.LastState?.PositionMs,
        Playing = joined.LastState?.Playing,
        AtUnixMs = joined.LastState?.AtUnixMs,
    });
    await SendTo(joined.Host, new PartyMessage { Type = MsgType.Join, Name = name });
    await joined.BroadcastRoster();

    await Pump(socket, async msg =>
    {
        switch (msg.Type)
        {
            case MsgType.PauseRequest:
                msg.Name = name;
                await SendTo(joined.Host, msg);
                break;
            case MsgType.Ready:
                joined.Ready[guestId] = true;
                await joined.SendReadyCount();
                break;
            case MsgType.Chat:
                msg.Name = name;
                await joined.Broadcast(msg);
                await SendTo(joined.Host, msg);
                break;
        }
    });

    joined.Guests.TryRemove(guestId, out _);
    joined.GuestNames.TryRemove(guestId, out _);
    joined.Ready.TryRemove(guestId, out _);
    await joined.BroadcastRoster();
    await joined.SendReadyCount();
});

app.Run();

static string NewCode(ConcurrentDictionary<string, Room> rooms)
{
    const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no ambiguous chars
    var rng = Random.Shared;
    while (true)
    {
        var code = "MOVIE-" + new string(Enumerable.Range(0, 4).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
        if (!rooms.ContainsKey(code)) return code;
    }
}

static async Task SendTo(WebSocket socket, PartyMessage msg)
{
    try
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
    catch
    {
    }
}

static async Task Pump(WebSocket socket, Func<PartyMessage, Task> onMessage)
{
    var buffer = new byte[64 * 1024];
    var sb = new StringBuilder();
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            var msg = PartyMessage.FromJson(sb.ToString());
            if (msg is not null) await onMessage(msg);
        }
    }
    catch
    {
    }
}

static void TryClose(WebSocket socket)
{
    try { socket.Abort(); } catch { }
}

class PendingPull
{
    public TaskCompletionSource<Stream> Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

class Room
{
    public string Code = "";
    public string HostName = "";
    public WebSocket Host = null!;
    public string? MovieTitle;
    /// <summary>Streaming service name; non-null means there's no media to relay.</summary>
    public string? Service;
    public long? DurationMs;
    public long FileSize;
    public string? ContentType;
    public bool Started;
    public PartyMessage? LastState;
    public readonly ConcurrentDictionary<string, WebSocket> Guests = new();
    public readonly ConcurrentDictionary<string, string> GuestNames = new();
    public readonly ConcurrentDictionary<string, bool> Ready = new();

    public async Task Broadcast(PartyMessage msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
        foreach (var (_, socket) in Guests)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    public async Task BroadcastRoster()
    {
        var members = new List<string> { HostName + " (host)" };
        members.AddRange(GuestNames.Values);
        var msg = new PartyMessage { Type = MsgType.Roster, Members = members.ToArray() };
        await Broadcast(msg);
        await SendOne(Host, msg);
    }

    public Task SendReadyCount() => SendOne(Host, new PartyMessage
    {
        Type = MsgType.Ready,
        ReadyCount = Ready.Count(kv => kv.Value),
        GuestCount = Guests.Count,
    });

    private static async Task SendOne(WebSocket socket, PartyMessage msg)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch
        {
        }
    }
}
