using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace VideoPlayer.Rendezvous.Vault;

// ---- DTOs (what the app sends/receives) ------------------------------------
public record RegisterReq(string Username, string Password, string? Email,
    string CaptchaId, string CaptchaAnswer, string? InviteCode = null);
public record LoginReq(string Username, string Password);
public record RefreshReq(string RefreshToken);
public record EmailReq(string Email);
public record ResetReq(string Email, string Code, string NewPassword);
public record AuthResp(string AccessToken, string RefreshToken, long ExpiresAt, string DisplayName,
    string Email, string Username, string Status, bool IsOwner);

// Owner-only user management.
public record AdminUserDto(string UserId, string Username, string DisplayName, string Email,
    string Status, bool IsOwner, long CreatedAt);
public record SetStatusReq(string UserId, string Status);
public record AdminRemoveReq(string UserId);

// Invites: the owner's approval handed out in advance, usually as a QR code.
// (Distinct from InviteDto above, which is a party invite between friends.)
public record LibraryInviteDto(string Code, string Url, long CreatedAt, long ExpiresAt,
    bool Revoked, string UsedByName, long UsedAt);
public record RevokeInviteReq(string Code);
public record LibraryItemDto(string FilmKey, string Name, long ResumeMs, int WatchCount,
    long LastWatchedAt, string? ChaptersJson, long UpdatedAt);
public record LibrarySyncReq(LibraryItemDto[] Items);

// Single sign-on from LAN Party.
public record LanPartyAuthReq(string Assertion);
public record LanPartyPerson(string Username, string DisplayName, string? AvatarUrl,
    string? AvatarColor, string? Status);
public record LanPartyProfile(LanPartyPerson User, LanPartyPerson[] Friends);

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

        // A fresh challenge for the sign-up form. The image is base64 in JSON
        // because the WPF client is going to decode it into a bitmap anyway,
        // and one response shape is simpler than an image URL plus a cookie.
        g.MapGet("/captcha", (CaptchaStore captcha) =>
        {
            var issued = captcha.Issue();
            if (issued is null)
                return Results.Json(new { error = "Too many open challenges — try again in a minute." },
                    statusCode: 429);

            // Test hook, never set in production: lets an automated check walk
            // the register flow without OCR. The answer is only otherwise
            // available by reading the image.
            if (Environment.GetEnvironmentVariable("VAULT_CAPTCHA_REVEAL") == "1")
                return Results.Ok(new
                {
                    id = issued.Value.Id,
                    image = Convert.ToBase64String(issued.Value.Png),
                    answer = captcha.Peek(issued.Value.Id),
                });
            return Results.Ok(new { id = issued.Value.Id, image = Convert.ToBase64String(issued.Value.Png) });
        });

        g.MapPost("/register", async (RegisterReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens, CaptchaStore captcha, EmailSender email) =>
        {
            // Captcha first, before any validation an enumeration script could
            // learn from. It is single-use: failing anything below costs the
            // caller a fresh image.
            if (!captcha.Consume(r.CaptchaId, r.CaptchaAnswer))
                return Results.BadRequest(new { error = "The characters didn't match the image. Try the new one." });

            var username = (r.Username ?? "").Trim();
            if (username.Length is < 3 or > 20 || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return Results.BadRequest(new
                    { error = "Username must be 3–20 characters: letters, digits, dots, dashes or underscores." });
            if (r.Password.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });

            // Email is optional and exists only for password reset. Uniqueness
            // is enforced here rather than by Identity, because Identity's
            // unique-email rule refuses accounts with no email at all.
            var mail = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
            if (mail is not null)
            {
                if (!mail.Contains('@') || mail.Length > 254)
                    return Results.BadRequest(new { error = "That email address doesn't look right." });
                if (await users.FindByEmailAsync(mail) is not null)
                    return Results.BadRequest(new { error = "That email is already on an account." });
            }

            // An invite is the owner's approval handed out in advance: register
            // with a live one and the pending queue is skipped entirely. A code
            // that was typed but is wrong gets a loud error rather than a
            // silently-pending account — the person believes they were invited,
            // and quietly ignoring that belief produces a confusing empty
            // library and a "why can't my friend see anything" conversation.
            InviteCode? invite = null;
            if (!string.IsNullOrWhiteSpace(r.InviteCode))
            {
                var code = r.InviteCode.Trim().ToUpperInvariant();
                invite = await db.InviteCodes.FirstOrDefaultAsync(i => i.Code == code);
                if (invite is null || invite.Revoked || invite.UsedByUserId is not null
                    || invite.ExpiresAt < Now())
                    return Results.BadRequest(new
                    {
                        error = "That invite code isn't valid any more — it may have expired or "
                              + "already been used. Ask for a fresh one, or register without it "
                              + "and wait for approval.",
                    });
            }

            var user = new VaultUser
            {
                UserName = username,
                Email = mail,
                EmailConfirmed = false,
                DisplayName = username,
                CreatedAt = Now(),
                AccessStatus = invite is null ? AccessStates.Pending : AccessStates.Approved,
            };
            ApplyFirstAccountRule(user, users);

            var created = await users.CreateAsync(user, r.Password);
            if (!created.Succeeded)
                return Results.BadRequest(new { error = string.Join(" ", created.Errors.Select(e => e.Description)) });

            // Burn the invite only after the account actually exists, so a
            // failed registration doesn't waste it.
            if (invite is not null)
            {
                invite.UsedByUserId = user.Id;
                invite.UsedByName = username;
                invite.UsedAt = Now();
                await db.SaveChangesAsync();
            }

            if (mail is not null)
                try { await email.SendWelcomeAsync(mail, username); } catch { /* optional nicety */ }

            // Signed in at once — approval gates the library, not the account.
            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        // Pre-username clients (≤2.8.0) posted here to finish registration.
        // There is nothing to finish any more; tell them what to do instead.
        g.MapPost("/verify", () => Results.Json(
            new { error = "This server now uses username sign-in. Please update Vault Movies." },
            statusCode: 400));
        g.MapPost("/resend", () => Results.Json(
            new { error = "This server now uses username sign-in. Please update Vault Movies." },
            statusCode: 400));

        g.MapPost("/login", async (LoginReq r, UserManager<VaultUser> users,
            VaultDbContext db, VaultTokens tokens) =>
        {
            var id = (r.Username ?? "").Trim();
            // Username is the credential; accepting an email too costs one
            // lookup and saves everyone who forgets which one they typed.
            var user = await users.FindByNameAsync(id) ?? await users.FindByEmailAsync(id);
            if (user is null || !await users.CheckPasswordAsync(user, r.Password))
                return Results.Json(new { error = "Wrong username or password." }, statusCode: 401);
            if (user.AccessStatus == AccessStates.Blocked)
                return Results.Json(new { error = "This account has been blocked by the server owner." },
                    statusCode: 403);
            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        g.MapPost("/forgot", async (EmailReq r, UserManager<VaultUser> users,
            VaultDbContext db, EmailSender email) =>
        {
            var user = await users.FindByEmailAsync(r.Email.Trim());
            // A LAN Party-linked account has no password and a non-routable
            // address; there is nothing to reset and nowhere to send it.
            if (user is not null && await users.HasPasswordAsync(user))
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

            // The owner deleting themselves would strand the server: the
            // first-account rule only fires on an EMPTY user table, so with other
            // accounts present nothing could ever grant that role again.
            if (user.IsOwner)
                return Results.BadRequest(new
                {
                    error = "This is the server's owner account, so it can't be deleted. "
                          + "Hand ownership to someone else first.",
                });

            db.LibraryItems.RemoveRange(db.LibraryItems.Where(x => x.UserId == uid));
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(x => x.UserId == uid));
            db.Friendships.RemoveRange(db.Friendships.Where(x => x.RequesterId == uid || x.AddresseeId == uid));
            db.PartyInvites.RemoveRange(db.PartyInvites.Where(x => x.FromUserId == uid || x.ToUserId == uid));
            db.LinkedIdentities.RemoveRange(db.LinkedIdentities.Where(x => x.UserId == uid));
            await db.SaveChangesAsync();
            var (mail, name) = (user.Email!, user.DisplayName);
            await users.DeleteAsync(user);
            await email.SendDeletedAsync(mail, name);
            return Results.Ok();
        }).RequireAuthorization();

        MapLanPartyApi(g);
        MapFriendsApi(g);
        MapAdminApi(g);
    }

    // ---- Owner-only user management ----------------------------------------

    /// <summary>Resolve the caller iff they are the owner; null otherwise.</summary>
    private static async Task<VaultUser?> OwnerAsync(ClaimsPrincipal me, UserManager<VaultUser> users)
    {
        var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (uid is null) return null;
        var user = await users.FindByIdAsync(uid);
        return user is { IsOwner: true } ? user : null;
    }

    private static void MapAdminApi(RouteGroupBuilder g)
    {
        g.MapGet("/admin/users", async (ClaimsPrincipal me, UserManager<VaultUser> users) =>
        {
            if (await OwnerAsync(me, users) is null) return Results.StatusCode(403);
            var all = users.Users
                .OrderBy(u => u.CreatedAt)
                .Select(u => new AdminUserDto(u.Id, u.UserName ?? "", u.DisplayName,
                    u.Email ?? "", u.AccessStatus, u.IsOwner, u.CreatedAt))
                .ToList();
            return Results.Ok(all);
        }).RequireAuthorization();

        g.MapPost("/admin/users/status", async (SetStatusReq r, ClaimsPrincipal me,
            UserManager<VaultUser> users, VaultDbContext db) =>
        {
            var owner = await OwnerAsync(me, users);
            if (owner is null) return Results.StatusCode(403);
            if (r.Status is not (AccessStates.Pending or AccessStates.Approved or AccessStates.Blocked))
                return Results.BadRequest(new { error = "Unknown status." });

            var target = await users.FindByIdAsync(r.UserId);
            if (target is null) return Results.NotFound(new { error = "No such user." });
            // The owner demoting themself would leave a server nobody administers.
            if (target.IsOwner) return Results.BadRequest(new { error = "The owner's access can't be changed." });

            target.AccessStatus = r.Status;
            await users.UpdateAsync(target);

            // Blocking must end their sessions, not just their next sign-in:
            // access tokens live an hour, refresh tokens two months.
            if (r.Status == AccessStates.Blocked)
            {
                foreach (var t in db.RefreshTokens.Where(t => t.UserId == target.Id && !t.Revoked))
                    t.Revoked = true;
                await db.SaveChangesAsync();
            }
            return Results.Ok(new AdminUserDto(target.Id, target.UserName ?? "", target.DisplayName,
                target.Email ?? "", target.AccessStatus, target.IsOwner, target.CreatedAt));
        }).RequireAuthorization();

        // ---- Invites -------------------------------------------------------

        g.MapPost("/admin/invites", async (ClaimsPrincipal me, UserManager<VaultUser> users,
            VaultDbContext db, HttpContext ctx) =>
        {
            if (await OwnerAsync(me, users) is null) return Results.StatusCode(403);

            // Same unambiguous alphabet as room codes. Eight characters is
            // ~10^12 possibilities against a rate-limited HTTPS endpoint that
            // also demands a captcha — brute force is not the weak point here.
            const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var code = "VAULT-" + new string(Enumerable.Range(0, 8)
                .Select(_ => alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)])
                .ToArray());

            var invite = new InviteCode
            {
                Code = code,
                CreatedAt = Now(),
                // A week: long enough to hand out at your leisure, short enough
                // that a code lost in some chat log dies on its own.
                ExpiresAt = Now() + 7L * 24 * 60 * 60 * 1000,
            };
            db.InviteCodes.Add(invite);
            await db.SaveChangesAsync();

            // The QR points here; the page tells the phone-holder what to do.
            var url = $"https://{ctx.Request.Host.Host}/invite/{code}";
            return Results.Ok(new LibraryInviteDto(code, url, invite.CreatedAt, invite.ExpiresAt,
                false, "", 0));
        }).RequireAuthorization();

        g.MapGet("/admin/invites", async (ClaimsPrincipal me, UserManager<VaultUser> users,
            VaultDbContext db, HttpContext ctx) =>
        {
            if (await OwnerAsync(me, users) is null) return Results.StatusCode(403);
            var host = ctx.Request.Host.Host;
            var invites = await db.InviteCodes.OrderByDescending(i => i.CreatedAt)
                .Take(50).ToListAsync();
            return Results.Ok(invites.Select(i => new LibraryInviteDto(
                i.Code, $"https://{host}/invite/{i.Code}", i.CreatedAt, i.ExpiresAt,
                i.Revoked, i.UsedByName, i.UsedAt)));
        }).RequireAuthorization();

        g.MapPost("/admin/invites/revoke", async (RevokeInviteReq r, ClaimsPrincipal me,
            UserManager<VaultUser> users, VaultDbContext db) =>
        {
            if (await OwnerAsync(me, users) is null) return Results.StatusCode(403);
            var invite = await db.InviteCodes.FirstOrDefaultAsync(i => i.Code == r.Code.Trim().ToUpperInvariant());
            if (invite is null) return Results.NotFound(new { error = "No such invite." });
            invite.Revoked = true;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        g.MapPost("/admin/users/remove", async (AdminRemoveReq r, ClaimsPrincipal me,
            UserManager<VaultUser> users, VaultDbContext db) =>
        {
            var owner = await OwnerAsync(me, users);
            if (owner is null) return Results.StatusCode(403);
            var target = await users.FindByIdAsync(r.UserId);
            if (target is null) return Results.NotFound(new { error = "No such user." });
            if (target.IsOwner) return Results.BadRequest(new { error = "The owner can't be removed." });

            // Same cleanup the self-service delete does.
            var uid = target.Id;
            db.LibraryItems.RemoveRange(db.LibraryItems.Where(x => x.UserId == uid));
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(x => x.UserId == uid));
            db.Friendships.RemoveRange(db.Friendships.Where(x => x.RequesterId == uid || x.AddresseeId == uid));
            db.PartyInvites.RemoveRange(db.PartyInvites.Where(x => x.FromUserId == uid || x.ToUserId == uid));
            db.LinkedIdentities.RemoveRange(db.LinkedIdentities.Where(x => x.UserId == uid));
            await db.SaveChangesAsync();
            await users.DeleteAsync(target);
            return Results.Ok();
        }).RequireAuthorization();
    }

    // ---- Single sign-on from LAN Party -------------------------------------

    private static void MapLanPartyApi(RouteGroupBuilder g)
    {
        // The desktop app forwards the assertion it received on the deep link.
        // Everything sensitive happens here: the app never holds the secret and
        // never inspects the token.
        g.MapPost("/auth/lanparty", async (LanPartyAuthReq r, LanPartySso sso,
            UserManager<VaultUser> users, VaultDbContext db, VaultTokens tokens) =>
        {
            var (principal, error) = sso.Verify(r.Assertion ?? "");
            if (principal is null) return Results.Json(new { error }, statusCode: 401);

            var subject = principal.FindFirst("sub")?.Value?.Trim();
            var jti = principal.FindFirst("jti")?.Value;
            var name = principal.FindFirst("name")?.Value;
            var email = principal.FindFirst("email")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(subject))
                return Results.Json(new { error = "That sign-in link is missing its user." }, statusCode: 401);

            // Burn the assertion. Unique index on Jti makes a concurrent replay
            // lose the race rather than sneak through behind the check.
            if (!string.IsNullOrWhiteSpace(jti))
            {
                var now = Now();
                db.ConsumedJtis.RemoveRange(db.ConsumedJtis.Where(c => c.ExpiresAt < now));
                db.ConsumedJtis.Add(new ConsumedJti { Jti = jti!, ExpiresAt = now + JtiRetentionMs });
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    return Results.Json(new { error = "That sign-in link has already been used." },
                        statusCode: 401);
                }
            }

            var link = await db.LinkedIdentities.FirstOrDefaultAsync(
                l => l.Provider == LanPartySso.Provider && l.Subject == subject);

            VaultUser? user = null;
            if (link is not null)
            {
                user = await users.FindByIdAsync(link.UserId);
                if (user is null)
                {
                    // Account deleted out from under the link; start clean.
                    db.LinkedIdentities.Remove(link);
                    await db.SaveChangesAsync();
                    link = null;
                }
                else if (user.AccessStatus == AccessStates.Blocked)
                {
                    // Blocked is blocked, whichever door they come in through.
                    return Results.Json(
                        new { error = "This account has been blocked by the server owner." },
                        statusCode: 403);
                }
            }

            if (link is null)
            {
                // NEVER auto-link by email. LAN Party does not verify addresses,
                // so anyone can register with someone else's — matching on one
                // would hand over that person's library. Ask instead.
                if (!string.IsNullOrWhiteSpace(email) && await users.FindByEmailAsync(email!) is not null)
                    return Results.Json(new { status = "confirm-merge", email }, statusCode: 409);

                // A shadow account with no password and a non-routable address:
                // it exists to own a library, and is only ever reachable through
                // LAN Party until the person deliberately adds a password.
                // UserName mirrors Email, as the ordinary register path does —
                // Identity's default allowed-character set rejects a colon, and
                // a second naming scheme would only be a second thing to get wrong.
                var shadowEmail = $"{Sanitise(subject!)}@{LanPartySso.Provider}.invalid";
                user = new VaultUser
                {
                    UserName = shadowEmail,
                    Email = shadowEmail,
                    EmailConfirmed = true,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? subject! : name!,
                    CreatedAt = Now(),
                };
                // Deliberately NOT ApplyFirstAccountRule. On an empty server that
                // would hand ownership to whoever presents the first assertion —
                // and to a passwordless account that /forgot refuses to reset, so
                // it could never be recovered. Ownership is claimed by
                // registering, which proves possession of a password.
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                    return Results.Json(new { error = string.Join(" ", created.Errors.Select(e => e.Description)) },
                        statusCode: 400);

                link = new LinkedIdentity
                {
                    Provider = LanPartySso.Provider,
                    Subject = subject!,
                    UserId = user.Id,
                    ProviderName = user.DisplayName,
                    CreatedAt = Now(),
                };
                db.LinkedIdentities.Add(link);
            }

            // One snapshot per sign-in — never polled.
            var profile = await sso.FetchProfileAsync(r.Assertion!);

            var display = profile?.User.DisplayName ?? name ?? subject!;
            if (user!.DisplayName != display)
            {
                user.DisplayName = display;
                await users.UpdateAsync(user);
            }
            link.ProviderName = display;
            link.LastSeenAt = Now();
            await db.SaveChangesAsync();

            var auth = await IssueAsync(user, db, tokens);
            return Results.Ok(new
            {
                auth.AccessToken,
                auth.RefreshToken,
                auth.ExpiresAt,
                auth.DisplayName,
                auth.Email,
                auth.Username,
                auth.Status,
                auth.IsOwner,
                lanParty = new
                {
                    username = subject,
                    displayName = display,
                    avatarUrl = profile?.User.AvatarUrl,
                    avatarColor = profile?.User.AvatarColor,
                    status = profile?.User.Status,
                    friends = profile?.Friends ?? Array.Empty<LanPartyPerson>(),
                },
            });
        });
    }

    /// <summary>Kept longer than the assertion itself, so a replay can never outlive its own record.</summary>
    private const long JtiRetentionMs = 10 * 60 * 1000;

    /// <summary>Keep a generated address to characters an email may contain.</summary>
    private static string Sanitise(string subject)
    {
        var clean = new string(subject.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length > 0 ? clean : Guid.NewGuid().ToString("N");
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

    /// <summary>
    /// The very first account on a fresh server is the owner, approved by
    /// definition — otherwise nobody exists who could approve anyone.
    /// </summary>
    private static void ApplyFirstAccountRule(VaultUser user, UserManager<VaultUser> users)
    {
        if (users.Users.Any()) return;
        user.IsOwner = true;
        user.AccessStatus = AccessStates.Approved;
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
        return new AuthResp(access, refresh, expiresAt, user.DisplayName, user.Email ?? "",
            user.UserName ?? "", user.AccessStatus, user.IsOwner);
    }

    private static LibraryItemDto ToDto(LibraryItem x) =>
        new(x.FilmKey, x.Name, x.ResumeMs, x.WatchCount, x.LastWatchedAt, x.ChaptersJson, x.UpdatedAt);
}
