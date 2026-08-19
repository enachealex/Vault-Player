using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VideoPlayer.Protocol;
using VideoPlayer.Rendezvous.Vault;

// Watch Party rendezvous. Three jobs:
//   1. Control relay — one host ↔ many guests over WebSockets (play/pause,
//      sync beacons, chat, roster, pause requests).
//   2. MEDIA relay — guests pull the movie from THIS server (GET /stream/{room});
//      the server asks the host for the needed byte range over the control
//      channel, the host pushes it back (POST /upload/{reqId}), and the server
//      pipes it straight to the guest. So guests need only the server address +
//      a room code — no reachable host, works across the internet through NAT.
//   3. LIBRARY — films kept on this machine's own disk, indexed by the catalogue
//      scanner and streamed to signed-in accounts (GET /media/{key}). Unlike the
//      relay, here the server is the origin: nobody has to be at their PC with
//      the file open for someone else to watch it.
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
        // Email is optional now (usernames are the credential), and Identity's
        // unique-email rule refuses an account with no email at all — so the
        // register endpoint enforces uniqueness itself, only when one is given.
        o.User.RequireUniqueEmail = false;
        // The register endpoint enforces the user-facing 3–20 letters/digits/._-
        // rule; this wider set additionally admits '@' because LAN Party shadow
        // accounts use a synthesised address as their username.
        o.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-@";
    })
    .AddEntityFrameworkStores<VaultDbContext>()
    .AddDefaultTokenProviders(); // needed for GeneratePasswordResetTokenAsync
builder.Services.AddSingleton<VaultTokens>();
// The server's own library: an index of what is on the media disk, plus the
// signer for the stream URLs players fetch it with. Singletons because the
// scanner holds the "am I already sweeping" gate.
builder.Services.AddSingleton<CatalogScanner>();
builder.Services.AddSingleton<MediaTokens>();
builder.Services.AddSingleton<HlsRemuxer>();
// Sign-up CAPTCHA challenges; in memory on purpose — a restart voiding open
// challenges costs a registrant one refresh.
builder.Services.AddSingleton<CaptchaStore>();
builder.Services.AddScoped<EmailSender>();
// Single sign-on from LAN Party. Verification happens here rather than in the
// desktop app, which cannot keep a shared secret.
builder.Services.AddHttpClient();
builder.Services.AddScoped<LanPartySso>();
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
// EnsureCreated only helps a brand-new file; an existing vault.db keeps its old
// schema, so tables added later are patched in with idempotent DDL below.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Friendships" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Friendships" PRIMARY KEY AUTOINCREMENT,
            "RequesterId" TEXT NOT NULL,
            "AddresseeId" TEXT NOT NULL,
            "Status" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "RespondedAt" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Friendships_RequesterId_AddresseeId"
            ON "Friendships" ("RequesterId", "AddresseeId");
        CREATE TABLE IF NOT EXISTS "PartyInvites" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PartyInvites" PRIMARY KEY AUTOINCREMENT,
            "FromUserId" TEXT NOT NULL,
            "ToUserId" TEXT NOT NULL,
            "FromName" TEXT NOT NULL,
            "RoomCode" TEXT NOT NULL,
            "Server" TEXT NOT NULL,
            "MovieTitle" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "Dismissed" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_PartyInvites_ToUserId" ON "PartyInvites" ("ToUserId");
        CREATE TABLE IF NOT EXISTS "LinkedIdentities" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_LinkedIdentities" PRIMARY KEY AUTOINCREMENT,
            "Provider" TEXT NOT NULL,
            "Subject" TEXT NOT NULL,
            "UserId" TEXT NOT NULL,
            "ProviderName" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "LastSeenAt" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LinkedIdentities_Provider_Subject"
            ON "LinkedIdentities" ("Provider", "Subject");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LinkedIdentities_Provider_UserId"
            ON "LinkedIdentities" ("Provider", "UserId");
        CREATE TABLE IF NOT EXISTS "ConsumedJtis" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ConsumedJtis" PRIMARY KEY AUTOINCREMENT,
            "Jti" TEXT NOT NULL,
            "ExpiresAt" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ConsumedJtis_Jti" ON "ConsumedJtis" ("Jti");
        """);

    // Columns added to an Identity table after first ship. SQLite has no
    // ADD COLUMN IF NOT EXISTS, so each one is attempted and allowed to fail
    // on databases that already have it.
    foreach (var ddl in new[]
    {
        """ALTER TABLE "AspNetUsers" ADD COLUMN "AccessStatus" TEXT NOT NULL DEFAULT 'pending'""",
        """ALTER TABLE "AspNetUsers" ADD COLUMN "IsOwner" INTEGER NOT NULL DEFAULT 0""",
        """ALTER TABLE "AspNetUsers" ADD COLUMN "CreatedAt" INTEGER NOT NULL DEFAULT 0""",
    })
    {
        try { db.Database.ExecuteSqlRaw(ddl); }
        catch { /* column already there */ }
    }

    db.Database.ExecuteSqlRaw("""
        SELECT 1;
        CREATE TABLE IF NOT EXISTS "CatalogItems" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CatalogItems" PRIMARY KEY AUTOINCREMENT,
            "ItemKey" TEXT NOT NULL,
            "RelativePath" TEXT NOT NULL,
            "Kind" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "SizeBytes" INTEGER NOT NULL,
            "DurationMs" INTEGER NULL,
            "ContentType" TEXT NOT NULL,
            "VideoCodec" TEXT NULL,
            "AudioCodec" TEXT NULL,
            "WebPlayable" INTEGER NOT NULL,
            "FilmKey" TEXT NOT NULL,
            "AddedAt" INTEGER NOT NULL,
            "LastSeenAt" INTEGER NOT NULL,
            "ArtFile" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_CatalogItems_ItemKey" ON "CatalogItems" ("ItemKey");
        CREATE INDEX IF NOT EXISTS "IX_CatalogItems_Kind" ON "CatalogItems" ("Kind");
        CREATE TABLE IF NOT EXISTS "InviteCodes" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InviteCodes" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "ExpiresAt" INTEGER NOT NULL,
            "Revoked" INTEGER NOT NULL,
            "UsedByUserId" TEXT NULL,
            "UsedByName" TEXT NOT NULL DEFAULT '',
            "UsedAt" INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_InviteCodes_Code" ON "InviteCodes" ("Code");
        """);
}

app.MapVaultApi();

app.MapCatalogApi();
app.MapUploadApi();
app.MapLinkApi();

// Throw away any web copy left half-written by a previous run, before anything
// can start writing a new one. Synchronous and first: it is the one moment when
// nothing in this process is touching the cache.
app.Services.GetRequiredService<HlsRemuxer>().SweepPartials();

// Index the media disk in the background. Not awaited: a first sweep of a large
// library probes and extracts a poster for every file, and the relay must be
// answering /health and hosting parties long before that finishes.
_ = Task.Run(() => app.Services.GetRequiredService<CatalogScanner>().ScanAsync());

var rooms = new ConcurrentDictionary<string, Room>();
var pulls = new ConcurrentDictionary<string, PendingPull>();

app.MapGet("/health", () => "ok");

// ---- Invite links -----------------------------------------------------------
// https://party.thejumpvault.com/join/MOVIE-XXXX — shared in any chat. Serves a
// tiny page that bounces into the installed app (vaultmovies://) and offers the
// installer to anyone who doesn't have it. ?server= carries a non-default relay
// (e.g. a LAN host) through to the app.

// ---- Television app ---------------------------------------------------------
// Served to the TV's own browser rather than shipped as a store package: the
// certification cost of webOS/Tizen/Android TV buys nothing a page cannot do.
app.MapGet("/tv", (HttpContext ctx) =>
    Results.Content(
        VideoPlayer.Rendezvous.TvPage.Render(
            $"{ctx.Request.Scheme}://{ctx.Request.Host}/link", $"{ctx.Request.Host}/link"),
        "text/html; charset=utf-8"));

app.MapGet("/join/{code}", (string code, HttpContext ctx) =>
{
    code = code.Trim().ToUpperInvariant();
    // Plain 6-character codes, plus the retired MOVIE-XXXX form so links
    // already sitting in somebody's chat history keep resolving.
    if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^([A-Z0-9]{6}|MOVIE-[A-Z0-9]{4})$"))
        return Results.NotFound();

    var server = ctx.Request.Query["server"].ToString();
    if (string.IsNullOrWhiteSpace(server)) server = ctx.Request.Host.Host;

    const string download =
        "https://github.com/enachealex/Vault-Player/releases/latest/download/VideoPlayer-win-Setup.exe";
    return Results.Content(VideoPlayer.Rendezvous.JoinPage.Render(code, Uri.EscapeDataString(server), download),
        "text/html; charset=utf-8");
});

// ---- Invite landing page ----------------------------------------------------
// What a phone sees after scanning the owner's invite QR. Deliberately dumb: it
// shows the code and the way in, and never checks the database — a page that
// answered "valid or not" would be an oracle for guessing codes. Registration
// is where a bad code gets refused.

app.MapGet("/invite/{code}", (string code) =>
{
    code = code.Trim().ToUpperInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^VAULT-[A-Z0-9]{8}$"))
        return Results.NotFound();

    const string download =
        "https://github.com/enachealex/Vault-Player/releases/latest/download/VideoPlayer-win-Setup.exe";

    var html = $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>You're invited — Vault Movies</title>
        <style>
          body { margin:0; min-height:100vh; display:grid; place-items:center;
                 background:#1A0D09; color:#F6ECE5; font-family:'Segoe UI',system-ui,sans-serif; }
          .card { text-align:center; padding:40px 48px; max-width:460px; }
          .dot { width:22px; height:22px; border-radius:7px; background:#C8442A; margin:0 auto 18px; }
          h1 { font-size:22px; margin:0 0 6px; }
          .code { font-family:Consolas,monospace; font-size:28px; font-weight:bold; color:#C8442A;
                  letter-spacing:2px; margin:18px 0 6px; user-select:all; }
          p { color:#D9C0B3; font-size:14px; line-height:1.55; margin:8px 0; }
          ol { text-align:left; color:#D9C0B3; font-size:14px; line-height:1.7; }
          .btn { display:inline-block; margin-top:14px; padding:12px 26px; border-radius:10px;
                 background:#C8442A; color:#fff; text-decoration:none; font-weight:600; font-size:15px; }
        </style>
        </head>
        <body>
        <div class="card">
          <div class="dot"></div>
          <h1>You're invited to a film library</h1>
          <p>Someone wants to share their Vault Movies collection with you.</p>
          <div class="code">{{code}}</div>
          <ol>
            <li>Install <b>Vault Movies</b> on your Windows PC.</li>
            <li>Open it, choose <b>Sign in &rarr; Create account</b>.</li>
            <li>Enter this invite code with your new username.</li>
          </ol>
          <p>The code works once and expires after a week.</p>
          <a class="btn" href="{{download}}">Download Vault Movies for Windows</a>
        </div>
        </body>
        </html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

// ---- Room info for web viewers ---------------------------------------------
// LAN Party's Movie Night polls this to decide whether it can play the film in
// a browser <video> and, if so, where the host currently is. Public by room
// code — the same trust level as /stream, which anyone with the code can read.
// CORS is needed because this is a cross-origin fetch from the LAN Party page
// (a <video> tag wouldn't need it, but fetch() does).

app.MapGet("/room/{code}", (string code, HttpContext ctx) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    code = code.ToUpperInvariant();
    if (!rooms.TryGetValue(code, out var r)) return Results.NotFound(new { error = "Room not found." });

    return Results.Ok(new
    {
        code = r.Code,
        host = r.HostName,
        title = r.MovieTitle,
        service = r.Service,               // non-null => streaming title, nothing to relay
        durationMs = r.DurationMs,
        started = r.Started,
        // Web playability is decided by the host, which can actually read the file.
        webPlayable = r.Service is null && r.FileSize > 0 && r.WebPlayable,
        webReason = r.WebReason,
        contentType = r.ContentType,
        // Latest sync beacon, so a browser can follow the host's clock.
        positionMs = r.LastState?.PositionMs,
        playing = r.LastState?.Playing,
        atUnixMs = r.LastState?.AtUnixMs,
        // The beacon is stamped with the host's clock; sending ours lets a
        // viewer cancel out the skew between the two machines instead of
        // trusting that everyone's system time agrees.
        serverNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
});

// Browsers preflight the poll above; answer it without touching room state.
app.MapMethods("/room/{code}", new[] { "OPTIONS" }, (HttpContext ctx) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    ctx.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
    return Results.Ok();
});

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

    // Optional account: `auth` carries an ordinary access token. A valid one
    // pins the person's roster name to their real username — a signed-in guest
    // cannot be impersonated by whoever types the same nickname — and it is
    // the admission ticket for private rooms.
    string? accountUser = null;
    var accountApproved = false;
    var authToken = context.Request.Query["auth"].ToString();
    if (!string.IsNullOrWhiteSpace(authToken))
    {
        try
        {
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler
            { MapInboundClaims = false };
            var validation = context.RequestServices.GetRequiredService<VaultTokens>().Validation();
            var principal = handler.ValidateToken(authToken, validation, out _);
            var uid = principal.FindFirst("sub")?.Value;
            if (uid is not null)
            {
                var vdb = context.RequestServices.GetRequiredService<VaultDbContext>();
                var account = await vdb.Users.FindAsync(uid);
                if (account is not null && account.AccessStatus != AccessStates.Blocked)
                {
                    accountUser = account.UserName;
                    accountApproved = account.AccessStatus == AccessStates.Approved;
                }
            }
        }
        catch
        {
            // Bad or expired token: treated as anonymous, and the private-room
            // gate below says so in words rather than dropping the socket.
        }
    }
    if (accountUser is not null) name = accountUser;

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    if (role == "host")
    {
        var room = new Room
        {
            Code = NewCode(rooms),
            HostName = name,
            Host = socket,
            Private = context.Request.Query["private"] == "1",
            HostUserName = accountUser,
        };
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
                    room.WebPlayable = msg.WebPlayable ?? false;
                    room.WebReason = msg.WebReason;
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
                    room.WebPlayable = msg.WebPlayable ?? false;
                    room.WebReason = msg.WebReason;
                    room.LastState = null;
                    await room.Broadcast(msg);
                    break;
                case MsgType.Countdown:
                    // Pure relay: guests count down locally from receipt, so a
                    // few tens of ms of network jitter is irrelevant here.
                    await room.Broadcast(msg);
                    break;
                case MsgType.Kick:
                    if (msg.GuestId is { Length: > 0 } kickId
                        && room.Guests.TryGetValue(kickId, out var kicked))
                    {
                        // Ban by account first, so a signed-in guest can't
                        // rejoin on a fresh connection. Anonymous guests have
                        // no durable identity to ban — closing the socket is
                        // all there is, and rejoining needs the code anyway.
                        if (room.GuestUserNames.TryGetValue(kickId, out var kickedUser)
                            && kickedUser is not null)
                            room.Banned[kickedUser] = true;
                        await SendTo(kicked, new PartyMessage
                        {
                            Type = MsgType.Error,
                            Text = "The host removed you from the party.",
                        });
                        // The guest's own connection handler does the roster
                        // cleanup when this abort lands.
                        TryClose(kicked);
                    }
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

    // A private room admits only signed-in, approved accounts. The refusals
    // are distinct on purpose: "sign in" and "wait for approval" are different
    // things to go and do.
    if (joined.Private && accountUser is null)
    {
        await SendTo(socket, new PartyMessage
        {
            Type = MsgType.Error,
            Text = "This party is private — sign in to your Vault account to join.",
        });
        return;
    }
    if (joined.Private && !accountApproved)
    {
        await SendTo(socket, new PartyMessage
        {
            Type = MsgType.Error,
            Text = "This party is private and your account hasn't been approved yet.",
        });
        return;
    }

    // Kicked is kicked — for the life of this room, not this connection.
    if (accountUser is not null && joined.Banned.ContainsKey(accountUser))
    {
        await SendTo(socket, new PartyMessage
        {
            Type = MsgType.Error,
            Text = "The host removed you from this party.",
        });
        return;
    }

    var guestId = Guid.NewGuid().ToString("N");
    joined.Guests[guestId] = socket;
    joined.GuestNames[guestId] = name;
    joined.GuestUserNames[guestId] = accountUser;
    await SendTo(socket, new PartyMessage
    {
        Type = MsgType.Welcome,
        Room = joined.Code,
        GuestId = guestId,
        Private = joined.Private,
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
    joined.GuestUserNames.TryRemove(guestId, out _);
    joined.Ready.TryRemove(guestId, out _);
    await joined.BroadcastRoster();
    await joined.SendReadyCount();
});

app.Run();

static string NewCode(ConcurrentDictionary<string, Room> rooms)
{
    // Six plain characters, no prefix — typed into TV remotes and read out
    // over voice chat, so every character costs something. The alphabet drops
    // lookalikes (no I/L/O/0/1).
    const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    var rng = Random.Shared;
    while (true)
    {
        var code = new string(Enumerable.Range(0, 6).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
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
    /// <summary>Host's verdict on whether a browser &lt;video&gt; can play this.</summary>
    public bool WebPlayable;
    public string? WebReason;
    public bool Started;
    public PartyMessage? LastState;

    /// <summary>Only signed-in, approved accounts may join.</summary>
    public bool Private;
    /// <summary>The host's account username, when they connected signed in.</summary>
    public string? HostUserName;
    /// <summary>Account usernames kicked from this room; refused on rejoin.</summary>
    public readonly ConcurrentDictionary<string, bool> Banned = new();

    public readonly ConcurrentDictionary<string, WebSocket> Guests = new();
    public readonly ConcurrentDictionary<string, string> GuestNames = new();
    /// <summary>Roster id → account username, or null for an anonymous guest.</summary>
    public readonly ConcurrentDictionary<string, string?> GuestUserNames = new();
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
        // Parallel arrays rather than a member-object list, so pre-management
        // clients (which only read Members) still render the roster.
        var ids = new List<string> { "" };
        var members = new List<string> { HostName + " (host)" };
        var verified = new List<bool> { HostUserName is not null };
        foreach (var (id, guestName) in GuestNames)
        {
            ids.Add(id);
            members.Add(guestName);
            verified.Add(GuestUserNames.TryGetValue(id, out var u) && u is not null);
        }
        var msg = new PartyMessage
        {
            Type = MsgType.Roster,
            Members = members.ToArray(),
            MemberIds = ids.ToArray(),
            Verified = verified.ToArray(),
        };
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
