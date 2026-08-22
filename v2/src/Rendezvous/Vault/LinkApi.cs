using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace VideoPlayer.Rendezvous.Vault;

public record LinkClaimReq(string Code);

/// <summary>
/// Device-link sign-in, for screens where typing a password is misery — TVs
/// above all.
///
/// The device shows a QR pointing at <c>/link</c>. The person scans it with
/// their phone, signs in there (a phone keyboard, their password manager), and
/// the page answers with a short code. They type that code into the device,
/// the device calls <c>/api/v1/link/claim</c>, and it holds an ordinary session
/// for that account — the same tokens a password sign-in would have produced.
/// The password itself never goes anywhere near the TV.
/// </summary>
public static class LinkApi
{
    // Digits only, because the thing typing this is a television remote. A
    // keypad needs no hunting across a grid of letters, and most remotes have
    // number buttons that work directly.
    //
    // Six of them: a million combinations. That is a hundredth of what eight
    // gave, so the guess limiter below is tightened to match — the keyspace and
    // the rate limit are one decision, not two, and shrinking one without the
    // other is how a short code becomes a guessable one.
    //
    // The pool recycles rather than being consumed. A code is removed the
    // instant it is claimed, and expired ones are swept whenever another is
    // minted, so at any moment only the handful of codes actually waiting to be
    // typed are spoken for.
    private const string Alphabet = "0123456789";
    private const int CodeLength = 6;
    private const int TtlMinutes = 10;

    private sealed record PendingLink(string UserId, long ExpiresAt);

    // In memory, like captcha challenges: a restart voids open codes and the
    // person scans again. Codes are single-claim and die in minutes.
    private static readonly ConcurrentDictionary<string, PendingLink> Open = new();

    // A short code is guessable in principle, so failed claims are metered
    // globally: past this rate the endpoint answers 429 for everyone for the
    // remainder of the minute.
    //
    // Five a minute, not twenty. Against a million combinations, twenty would
    // let a patient attacker put two hundred guesses at every code during the
    // ten minutes it lives. Five costs a legitimate device nothing — it types
    // one code, once, correctly — and a brute-force run is thousands a second,
    // so it trips this instantly either way.
    private static int _failedClaims;
    private static long _failedWindowStart;
    private const int MaxFailedPerMinute = 5;

    public static void MapLinkApi(this WebApplication app)
    {
        // The page the QR opens. Sign-in form; on success it shows the code.
        app.MapGet("/link", () => Results.Content(Page(null, null), "text/html; charset=utf-8"));

        app.MapPost("/link", async (HttpContext ctx, UserManager<VaultUser> users) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var username = (form["username"].ToString() ?? "").Trim();
            var password = form["password"].ToString() ?? "";

            var user = await users.FindByNameAsync(username) ?? await users.FindByEmailAsync(username);
            if (user is null || !await users.CheckPasswordAsync(user, password))
                return Results.Content(Page("Wrong username or password.", null), "text/html; charset=utf-8");
            if (user.AccessStatus == AccessStates.Blocked)
                return Results.Content(Page("This account has been blocked by the server owner.", null),
                    "text/html; charset=utf-8");

            Sweep();
            string code;
            do
            {
                code = new string(Enumerable.Range(0, CodeLength)
                    .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            } while (!Open.TryAdd(code, new PendingLink(user.Id,
                DateTimeOffset.UtcNow.AddMinutes(TtlMinutes).ToUnixTimeMilliseconds())));

            return Results.Content(Page(null, code), "text/html; charset=utf-8");
        });

        // The device trades the typed code for a session. Single use.
        app.MapPost("/api/v1/link/claim", async (LinkClaimReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens) =>
        {
            if (RateLimited())
                return Results.Json(new { error = "Too many attempts — wait a minute and try again." },
                    statusCode: 429);

            var code = (r.Code ?? "").Trim().ToUpperInvariant();
            if (!Open.TryRemove(code, out var pending)
                || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > pending.ExpiresAt)
            {
                CountFailure();
                return Results.Json(
                    new { error = "That code isn't valid — it may have expired. Sign in on your phone again." },
                    statusCode: 400);
            }

            var user = await users.FindByIdAsync(pending.UserId);
            if (user is null || user.AccessStatus == AccessStates.Blocked)
                return Results.Json(new { error = "This account can't sign in." }, statusCode: 403);

            // From here it is an ordinary session, identical to a password
            // sign-in: same tokens, same refresh rotation, same gates.
            return Results.Ok(await IssueForLink(user, db, tokens));
        });
    }

    /// <summary>VaultApi.IssueAsync is private to it; this is the same shape.</summary>
    private static async Task<AuthResp> IssueForLink(VaultUser user, VaultDbContext db, VaultTokens tokens)
    {
        var (access, expiresAt) = tokens.AccessToken(user);
        var refresh = VaultTokens.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            Token = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(VaultTokens.RefreshTokenDays).ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();
        return new AuthResp(access, refresh, expiresAt, user.DisplayName, user.Email ?? "",
            user.UserName ?? "", user.AccessStatus, user.IsOwner);
    }

    private static void Sweep()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (key, pending) in Open)
            if (pending.ExpiresAt < now) Open.TryRemove(key, out _);
    }

    private static bool RateLimited()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _failedWindowStart) > 60_000)
        {
            Interlocked.Exchange(ref _failedWindowStart, now);
            Interlocked.Exchange(ref _failedClaims, 0);
        }
        return Volatile.Read(ref _failedClaims) >= MaxFailedPerMinute;
    }

    private static void CountFailure() => Interlocked.Increment(ref _failedClaims);

    /// <summary>One page, two states: the sign-in form, or the code to type in.</summary>
    private static string Page(string? error, string? code)
    {
        var body = code is null
            ? $"""
              <h1>Link your TV or device</h1>
              <p>Sign in with your Vault Movies account. You'll get a short code
                 to type into the screen that sent you here.</p>
              {(error is null ? "" : $"<p class=\"err\">{System.Net.WebUtility.HtmlEncode(error)}</p>")}
              <form method="post" action="/link">
                <label>USERNAME</label>
                <input name="username" autocomplete="username" autocapitalize="none" required>
                <label>PASSWORD</label>
                <input name="password" type="password" autocomplete="current-password" required>
                <button type="submit">Get my code</button>
              </form>
              """
            : $"""
              <h1>You're verified</h1>
              <p>Type this code into your TV or device:</p>
              <div class="codebox"><div class="code">{code}</div></div>
              <p>It works once and expires in 10 minutes.</p>
              """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Link a device — Vault Movies</title>
            <style>
              body { margin:0; min-height:100vh; display:grid; place-items:center;
                     background:#1A0D09; color:#F6ECE5; font-family:'Segoe UI',system-ui,sans-serif; }
              .card { padding:40px 44px; max-width:420px; width:100%; box-sizing:border-box; }
              .dot { width:22px; height:22px; border-radius:7px; background:#C8442A; margin-bottom:18px; }
              /* Every size here is 6px larger than it was. This is read at arm's
                 length on a phone, often by somebody already squinting at a
                 television across the room. */
              h1 { font-size:28px; margin:0 0 6px; }
              p { color:#D9C0B3; font-size:20px; line-height:1.55; margin:8px 0; }
              .err { color:#F0A070; }
              label { display:block; font-size:16.5px; font-weight:bold; color:#D9C0B3;
                      letter-spacing:1px; margin:14px 0 6px; }
              input { width:100%; box-sizing:border-box; padding:12px; font-size:22px; border-radius:10px;
                      border:1px solid #43281F; background:#2A160F; color:#F6ECE5; }
              button { margin-top:18px; width:100%; padding:13px; border-radius:10px; border:none;
                       background:#C8442A; color:#fff; font-weight:600; font-size:21px; cursor:pointer; }
              /* The code gets a panel of its own. It is the one thing on this
                 page that has to be read and copied by hand, so it is separated
                 from the prose around it rather than being merely larger text. */
              .codebox { background:#3A3A3A; border:2px solid #5A5A5A; border-radius:14px;
                         padding:20px 16px; margin:22px 0; }
              .code { font-family:Consolas,monospace; font-size:50px; font-weight:bold; color:#F6ECE5;
                      letter-spacing:8px; text-align:center; user-select:all; }
            </style>
            </head>
            <body><div class="card"><div class="dot"></div>{{body}}</div></body>
            </html>
            """;
    }
}
