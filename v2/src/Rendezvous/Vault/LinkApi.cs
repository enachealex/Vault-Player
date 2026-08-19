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
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private const int TtlMinutes = 10;

    private sealed record PendingLink(string UserId, long ExpiresAt);

    // In memory, like captcha challenges: a restart voids open codes and the
    // person scans again. Codes are single-claim and die in minutes.
    private static readonly ConcurrentDictionary<string, PendingLink> Open = new();

    // A 6-character code is guessable in principle, so failed claims are
    // metered globally: past this rate the endpoint answers 429 for everyone
    // for the remainder of the minute. Legitimate traffic is a handful of
    // claims an evening; a brute-force run is thousands a second.
    private static int _failedClaims;
    private static long _failedWindowStart;
    private const int MaxFailedPerMinute = 20;

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
              <div class="code">{code}</div>
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
              h1 { font-size:22px; margin:0 0 6px; }
              p { color:#D9C0B3; font-size:14px; line-height:1.55; margin:8px 0; }
              .err { color:#F0A070; }
              label { display:block; font-size:10.5px; font-weight:bold; color:#D9C0B3;
                      letter-spacing:1px; margin:14px 0 6px; }
              input { width:100%; box-sizing:border-box; padding:12px; font-size:16px; border-radius:10px;
                      border:1px solid #43281F; background:#2A160F; color:#F6ECE5; }
              button { margin-top:18px; width:100%; padding:13px; border-radius:10px; border:none;
                       background:#C8442A; color:#fff; font-weight:600; font-size:15px; cursor:pointer; }
              .code { font-family:Consolas,monospace; font-size:44px; font-weight:bold; color:#C8442A;
                      letter-spacing:6px; text-align:center; margin:18px 0; user-select:all; }
            </style>
            </head>
            <body><div class="card"><div class="dot"></div>{{body}}</div></body>
            </html>
            """;
    }
}
