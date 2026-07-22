using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace VideoPlayer.Rendezvous.Vault;

// ---- DTOs (what the app sends/receives) ------------------------------------
public record RegisterReq(string Email, string Password, string DisplayName);
public record LoginReq(string Email, string Password);
public record RefreshReq(string RefreshToken);
public record EmailReq(string Email);
public record VerifyReq(string Email, string Code);
public record ResetReq(string Email, string Code, string NewPassword);
public record AuthResp(string AccessToken, string RefreshToken, long ExpiresAt, string DisplayName, string Email);
public record LibraryItemDto(string FilmKey, string Name, long ResumeMs, int WatchCount,
    long LastWatchedAt, string? ChaptersJson, long UpdatedAt);
public record LibrarySyncReq(LibraryItemDto[] Items);

// Friends + party invites.
public record FriendReq(string Email);
public record RespondReq(long RequestId, bool Accept);
public record RemoveFriendReq(string UserId);
public record PersonDto(string UserId, string Name, string Email, long RequestId = 0);
public record FriendsDto(PersonDto[] Friends, PersonDto[] Incoming, PersonDto[] Outgoing);
public record InviteReq(string ToUserId, string RoomCode, string Server, string MovieTitle);
public record InviteDto(long Id, string FromName, string RoomCode, string Server, string MovieTitle, long CreatedAt);
public record DismissInviteReq(long Id);

/// <summary>Issues access tokens and mints/rotates refresh tokens.</summary>
public class VaultTokens(IConfiguration cfg)
{
    // A dev fallback keeps local runs working; production MUST set VAULT_JWT_SECRET.
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        cfg["VAULT_JWT_SECRET"] ?? "dev-only-insecure-secret-change-me-in-production!");

    public const int AccessTokenMinutes = 60;
    public const int RefreshTokenDays = 60;

    public (string token, long expiresAt) AccessToken(VaultUser user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(AccessTokenMinutes);
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
                new Claim("name", user.DisplayName),
            },
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires.ToUnixTimeMilliseconds());
    }

    public static string NewRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public TokenValidationParameters Validation() => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        IssuerSigningKey = new SymmetricSecurityKey(_key),
    };
}

public static class VaultApi
{
    public static void MapVaultApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapPost("/register", async (RegisterReq r, UserManager<VaultUser> users,
            VaultDbContext db, EmailSender email) =>
        {
            if (string.IsNullOrWhiteSpace(r.Email) || r.Password.Length < 8)
                return Results.BadRequest(new { error = "Email required and password must be at least 8 characters." });

            var user = new VaultUser
            {
                UserName = r.Email.Trim(),
                Email = r.Email.Trim(),
                EmailConfirmed = false, // confirmed by the emailed code
                DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email.Split('@')[0] : r.DisplayName.Trim(),
            };
            var created = await users.CreateAsync(user, r.Password);
            if (!created.Succeeded)
                return Results.BadRequest(new { error = string.Join(" ", created.Errors.Select(e => e.Description)) });

            await email.SendVerifyCodeAsync(user.Email!, await NewCodeAsync(db, user.Email!, "verify"));
            // No tokens yet — the client goes to the "enter your code" step.
            return Results.Ok(new { status = "verify", email = user.Email });
        });

        g.MapPost("/verify", async (VerifyReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens, EmailSender email) =>
        {
            if (!await ConsumeCodeAsync(db, r.Email, r.Code, "verify"))
                return Results.Json(new { error = "That code is wrong or has expired." }, statusCode: 400);

            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is null) return Results.Json(new { error = "Account not found." }, statusCode: 400);
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await email.SendWelcomeAsync(user.Email!, user.DisplayName);
            return Results.Ok(await IssueAsync(user, db, tokens)); // verified = signed in
        });

        g.MapPost("/resend", async (EmailReq r, UserManager<VaultUser> users,
            VaultDbContext db, EmailSender email) =>
        {
            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is not null && !user.EmailConfirmed)
                await email.SendVerifyCodeAsync(user.Email!, await NewCodeAsync(db, user.Email!, "verify"));
            return Results.Ok(); // never reveal whether the address exists
        });

        g.MapPost("/login", async (LoginReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens) =>
        {
            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is null || !await users.CheckPasswordAsync(user, r.Password))
                return Results.Json(new { error = "Wrong email or password." }, statusCode: 401);
            if (!user.EmailConfirmed)
                return Results.Json(new { error = "verify", email = user.Email }, statusCode: 403);
            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        g.MapPost("/forgot", async (EmailReq r, UserManager<VaultUser> users,
            VaultDbContext db, EmailSender email) =>
        {
            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is not null)
                await email.SendResetCodeAsync(user.Email!, await NewCodeAsync(db, user.Email!, "reset"));
            return Results.Ok(); // always OK, so we don't leak which emails exist
        });

        g.MapPost("/reset", async (ResetReq r, UserManager<VaultUser> users, VaultDbContext db) =>
        {
            if (r.NewPassword.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });
            if (!await ConsumeCodeAsync(db, r.Email, r.Code, "reset"))
                return Results.Json(new { error = "That code is wrong or has expired." }, statusCode: 400);

            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is null) return Results.Json(new { error = "Account not found." }, statusCode: 400);
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var res = await users.ResetPasswordAsync(user, token, r.NewPassword);
            if (!res.Succeeded)
                return Results.BadRequest(new { error = string.Join(" ", res.Errors.Select(e => e.Description)) });

            // A password change kills every existing session.
            foreach (var t in db.RefreshTokens.Where(t => t.UserId == user.Id && !t.Revoked)) t.Revoked = true;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/refresh", async (RefreshReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rt = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == r.RefreshToken);
            if (rt is null || rt.Revoked || rt.ExpiresAt < now)
                return Results.Json(new { error = "Session expired. Please sign in again." }, statusCode: 401);

            rt.Revoked = true; // rotate: a refresh token is single-use
            var user = await users.FindByIdAsync(rt.UserId);
            if (user is null) return Results.Json(new { error = "Account not found." }, statusCode: 401);
            await db.SaveChangesAsync();
            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        g.MapPost("/logout", async (RefreshReq r, VaultDbContext db) =>
        {
            var rt = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == r.RefreshToken);
            if (rt is not null) { rt.Revoked = true; await db.SaveChangesAsync(); }
            return Results.Ok();
        });

        // ---- Library sync (requires a valid access token) ------------------

        g.MapGet("/library", async (ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            var items = await db.LibraryItems.Where(x => x.UserId == uid).ToListAsync();
            return Results.Ok(items.Select(ToDto));
        }).RequireAuthorization();

        // Push local changes; server merges last-writer-wins and returns the
        // authoritative set so the client ends up in sync in one round trip.
        g.MapPut("/library", async (LibrarySyncReq req, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var existing = await db.LibraryItems.Where(x => x.UserId == uid)
                .ToDictionaryAsync(x => x.FilmKey);
            foreach (var dto in req.Items)
            {
                if (existing.TryGetValue(dto.FilmKey, out var row))
                {
                    if (dto.UpdatedAt <= row.UpdatedAt) continue; // server copy is newer
                    row.Name = dto.Name; row.ResumeMs = dto.ResumeMs; row.WatchCount = dto.WatchCount;
                    row.LastWatchedAt = dto.LastWatchedAt; row.ChaptersJson = dto.ChaptersJson;
                    row.UpdatedAt = dto.UpdatedAt;
                }
                else
                {
                    db.LibraryItems.Add(new LibraryItem
                    {
                        UserId = uid, FilmKey = dto.FilmKey, Name = dto.Name, ResumeMs = dto.ResumeMs,
                        WatchCount = dto.WatchCount, LastWatchedAt = dto.LastWatchedAt,
                        ChaptersJson = dto.ChaptersJson, UpdatedAt = dto.UpdatedAt,
                    });
                }
            }
            await db.SaveChangesAsync();
            var merged = await db.LibraryItems.Where(x => x.UserId == uid).ToListAsync();
            return Results.Ok(merged.Select(ToDto));
        }).RequireAuthorization();

        g.MapPost("/delete", async (ClaimsPrincipal me, UserManager<VaultUser> users,
            VaultDbContext db, EmailSender email) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            var user = await users.FindByIdAsync(uid);
            if (user is null) return Results.Unauthorized();

            db.LibraryItems.RemoveRange(db.LibraryItems.Where(x => x.UserId == uid));
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(x => x.UserId == uid));
            db.Friendships.RemoveRange(db.Friendships.Where(x => x.RequesterId == uid || x.AddresseeId == uid));
            db.PartyInvites.RemoveRange(db.PartyInvites.Where(x => x.FromUserId == uid || x.ToUserId == uid));
            await db.SaveChangesAsync();
            var (mail, name) = (user.Email!, user.DisplayName);
            await users.DeleteAsync(user);
            await email.SendDeletedAsync(mail, name);
            return Results.Ok();
        }).RequireAuthorization();

        MapFriendsApi(g);
    }

    // ---- Friends + party invites (all require a valid access token) ---------

    private static void MapFriendsApi(RouteGroupBuilder g)
    {
        // Ask to be someone's friend, by email. If they already asked you, this
        // accepts instead — two people reaching for each other just become friends.
        g.MapPost("/friends/request", async (FriendReq r, ClaimsPrincipal me,
            UserManager<VaultUser> users, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var target = await users.FindByEmailAsync((r.Email ?? "").Trim());
            if (target is null)
                return Results.Json(new { error = "No Vault Movies account uses that email yet." }, statusCode: 404);
            if (target.Id == uid)
                return Results.Json(new { error = "That's your own email." }, statusCode: 400);

            var existing = await db.Friendships.FirstOrDefaultAsync(f =>
                (f.RequesterId == uid && f.AddresseeId == target.Id) ||
                (f.RequesterId == target.Id && f.AddresseeId == uid));
            if (existing is not null)
            {
                if (existing.Status == "accepted")
                    return Results.Json(new { error = "You're already friends." }, statusCode: 400);
                if (existing.RequesterId == uid)
                    return Results.Json(new { error = "You've already sent them a request." }, statusCode: 400);
                // They already invited you — accept it now.
                existing.Status = "accepted";
                existing.RespondedAt = Now();
                await db.SaveChangesAsync();
                return Results.Ok(new { status = "accepted" });
            }

            db.Friendships.Add(new Friendship
            {
                RequesterId = uid, AddresseeId = target.Id, Status = "pending", CreatedAt = Now(),
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { status = "sent" });
        }).RequireAuthorization();

        // Accept or decline a pending request addressed to me.
        g.MapPost("/friends/respond", async (RespondReq r, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var req = await db.Friendships.FirstOrDefaultAsync(f =>
                f.Id == r.RequestId && f.AddresseeId == uid && f.Status == "pending");
            if (req is null) return Results.Json(new { error = "That request is no longer available." }, statusCode: 404);

            if (r.Accept) { req.Status = "accepted"; req.RespondedAt = Now(); }
            else db.Friendships.Remove(req);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        // My friends, plus requests waiting on me and requests I'm still waiting on.
        g.MapGet("/friends", async (ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var rows = await db.Friendships
                .Where(f => f.RequesterId == uid || f.AddresseeId == uid).ToListAsync();

            // Resolve the other party's display info in one lookup.
            var ids = rows.Select(f => f.RequesterId == uid ? f.AddresseeId : f.RequesterId).Distinct().ToList();
            var people = await db.Users.Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u);
            PersonDto Person(string otherId, long requestId = 0) =>
                people.TryGetValue(otherId, out var u)
                    ? new PersonDto(u.Id, u.DisplayName, u.Email ?? "", requestId)
                    : new PersonDto(otherId, "Unknown", "", requestId);

            var friends = rows.Where(f => f.Status == "accepted")
                .Select(f => Person(f.RequesterId == uid ? f.AddresseeId : f.RequesterId)).ToArray();
            var incoming = rows.Where(f => f.Status == "pending" && f.AddresseeId == uid)
                .Select(f => Person(f.RequesterId, f.Id)).ToArray();
            var outgoing = rows.Where(f => f.Status == "pending" && f.RequesterId == uid)
                .Select(f => Person(f.AddresseeId)).ToArray();
            return Results.Ok(new FriendsDto(friends, incoming, outgoing));
        }).RequireAuthorization();

        // Unfriend, or withdraw/decline — removes the pair's row either way.
        g.MapPost("/friends/remove", async (RemoveFriendReq r, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var row = await db.Friendships.FirstOrDefaultAsync(f =>
                (f.RequesterId == uid && f.AddresseeId == r.UserId) ||
                (f.RequesterId == r.UserId && f.AddresseeId == uid));
            if (row is not null) { db.Friendships.Remove(row); await db.SaveChangesAsync(); }
            return Results.Ok();
        }).RequireAuthorization();

        // Invite a friend into the room I'm hosting right now.
        g.MapPost("/party/invite", async (InviteReq r, ClaimsPrincipal me,
            UserManager<VaultUser> users, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var friends = await db.Friendships.AnyAsync(f => f.Status == "accepted" &&
                ((f.RequesterId == uid && f.AddresseeId == r.ToUserId) ||
                 (f.RequesterId == r.ToUserId && f.AddresseeId == uid)));
            if (!friends) return Results.Json(new { error = "You can only invite friends." }, statusCode: 403);

            var mine = await users.FindByIdAsync(uid);
            db.PartyInvites.Add(new PartyInvite
            {
                FromUserId = uid,
                ToUserId = r.ToUserId,
                FromName = mine?.DisplayName ?? "A friend",
                RoomCode = r.RoomCode,
                Server = r.Server,
                MovieTitle = r.MovieTitle,
                CreatedAt = Now(),
            });
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        // Pending invites for me. Invites go stale fast — a room that ended is
        // useless — so anything older than the window is quietly retired.
        g.MapGet("/party/invites", async (ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var cutoff = Now() - InviteTtlMs;
            var stale = db.PartyInvites.Where(i => i.ToUserId == uid && !i.Dismissed && i.CreatedAt < cutoff);
            foreach (var s in stale) s.Dismissed = true;
            await db.SaveChangesAsync();

            var live = await db.PartyInvites
                .Where(i => i.ToUserId == uid && !i.Dismissed && i.CreatedAt >= cutoff)
                .OrderByDescending(i => i.CreatedAt).ToListAsync();
            return Results.Ok(live.Select(i =>
                new InviteDto(i.Id, i.FromName, i.RoomCode, i.Server, i.MovieTitle, i.CreatedAt)));
        }).RequireAuthorization();

        g.MapPost("/party/invites/dismiss", async (DismissInviteReq r, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();

            var inv = await db.PartyInvites.FirstOrDefaultAsync(i => i.Id == r.Id && i.ToUserId == uid);
            if (inv is not null) { inv.Dismissed = true; await db.SaveChangesAsync(); }
            return Results.Ok();
        }).RequireAuthorization();
    }

    /// <summary>How long a party invite stays live. A room rarely outlasts this.</summary>
    private const long InviteTtlMs = 3 * 60 * 60 * 1000; // 3 hours

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- Email codes -------------------------------------------------------

    private const int CodeMinutes = 20;

    /// <summary>Mint a fresh 6-digit code, retiring any earlier one for this purpose.</summary>
    private static async Task<string> NewCodeAsync(VaultDbContext db, string email, string purpose)
    {
        email = email.Trim().ToLowerInvariant();
        foreach (var old in db.EmailCodes.Where(c => c.Email == email && c.Purpose == purpose && !c.Used))
            old.Used = true;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailCodes.Add(new EmailCode
        {
            Email = email,
            Code = code,
            Purpose = purpose,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(CodeMinutes).ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();
        return code;
    }

    /// <summary>Validate and burn a code. True only if it matched and was live.</summary>
    private static async Task<bool> ConsumeCodeAsync(VaultDbContext db, string email, string code, string purpose)
    {
        email = email.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = await db.EmailCodes.FirstOrDefaultAsync(c =>
            c.Email == email && c.Purpose == purpose && c.Code == code && !c.Used && c.ExpiresAt > now);
        if (row is null) return false;
        row.Used = true;
        await db.SaveChangesAsync();
        return true;
    }

    private static async Task<AuthResp> IssueAsync(VaultUser user, VaultDbContext db, VaultTokens tokens)
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
        return new AuthResp(access, refresh, expiresAt, user.DisplayName, user.Email ?? "");
    }

    private static LibraryItemDto ToDto(LibraryItem x) =>
        new(x.FilmKey, x.Name, x.ResumeMs, x.WatchCount, x.LastWatchedAt, x.ChaptersJson, x.UpdatedAt);
}
