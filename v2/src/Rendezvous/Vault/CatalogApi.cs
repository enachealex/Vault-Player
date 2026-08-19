using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace VideoPlayer.Rendezvous.Vault;

// ---- DTOs -------------------------------------------------------------------

/// <summary>
/// One film in the server's own library. <c>StreamUrl</c> and <c>ArtUrl</c> are
/// relative on purpose: only the client knows which address it reached us on
/// (a LAN IP, the public domain), and guessing wrong hands out a URL that
/// resolves for nobody.
/// </summary>
public record CatalogItemDto(string ItemKey, string Name, long SizeBytes, long? DurationMs,
    string ContentType, string FilmKey, bool WebPlayable, string? VideoCodec, string? AudioCodec,
    string StreamUrl, string? ArtUrl, long AddedAt);

public record CatalogDto(CatalogItemDto[] Items, bool Scanning, long LastScanAt, bool MediaRootPresent);

/// <summary>
/// Signs the short-lived URLs that let a player fetch a film.
///
/// The access token cannot be used here: libVLC, a browser &lt;video&gt; tag and
/// a TV's DLNA client all fetch by URL alone and none of them can be persuaded
/// to attach an Authorization header. So the grant is moved into the URL, kept
/// short-lived, and bound to the account it was issued to.
/// </summary>
public class MediaTokens(IConfiguration cfg)
{
    // Same secret as the access tokens: one thing to configure, one thing to
    // rotate. Rotating it invalidates in-flight stream URLs, which costs a
    // viewer a reload at worst.
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        cfg["VAULT_JWT_SECRET"] ?? "dev-only-insecure-secret-change-me-in-production!");

    /// <summary>
    /// Long enough to watch a film without the URL dying mid-playback, short
    /// enough that a link pasted somewhere it shouldn't be stops working the
    /// same day.
    /// </summary>
    public const int LifetimeHours = 12;

    public string Mint(string itemKey, string userId)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(LifetimeHours).ToUnixTimeSeconds();
        var user = Base64Url(Encoding.UTF8.GetBytes(userId));
        return $"{expires}.{user}.{Sign(itemKey, userId, expires)}";
    }

    /// <summary>Check a token really was issued by us, for this item, and has not expired.</summary>
    public bool TryVerify(string? token, string itemKey, out string userId)
    {
        userId = "";
        if (string.IsNullOrEmpty(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        if (!long.TryParse(parts[0], out var expires)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) return false;

        string candidate;
        try
        {
            candidate = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
        }
        catch
        {
            return false;
        }

        // Fixed-time comparison: a signature check that returns early leaks how
        // much of a forged signature was right.
        var expected = Sign(itemKey, candidate, expires);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
            return false;

        userId = candidate;
        return true;
    }

    private string Sign(string itemKey, string userId, long expires)
    {
        // The separator keeps the fields from running together — without it,
        // ("ab", "c") and ("a", "bc") would sign identically.
        var payload = Encoding.UTF8.GetBytes($"{itemKey}\n{userId}\n{expires}");
        return Base64Url(HMACSHA256.HashData(_key, payload));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

public static class CatalogApi
{
    /// <summary>
    /// The film library is gated on the owner's approval, not on merely having
    /// an account — an account exists the moment someone passes the sign-up
    /// captcha, and that must not be worth anything by itself.
    /// </summary>
    private static async Task<bool> ApprovedAsync(VaultDbContext db, string? uid)
    {
        if (uid is null) return false;
        var user = await db.Users.FindAsync(uid);
        return user is { AccessStatus: AccessStates.Approved };
    }

    public static void MapCatalogApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1");

        // ---- The library ---------------------------------------------------

        g.MapGet("/catalog", async (ClaimsPrincipal me, VaultDbContext db,
            CatalogScanner scanner, MediaTokens media) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            if (!await ApprovedAsync(db, uid))
                return Results.Json(
                    new { error = "Your account is waiting for the owner's approval." },
                    statusCode: 403);

            var items = await db.CatalogItems
                .Where(x => x.Kind == "film")
                .OrderBy(x => x.Name)
                .ToListAsync();

            // One token per item rather than one for the session: each is bound
            // to the film it opens, so a URL shared on to someone else grants
            // that film and nothing more.
            var dtos = items.Select(x =>
            {
                var token = media.Mint(x.ItemKey, uid);
                return new CatalogItemDto(x.ItemKey, x.Name, x.SizeBytes, x.DurationMs, x.ContentType,
                    x.FilmKey, x.WebPlayable, x.VideoCodec, x.AudioCodec,
                    $"/media/{x.ItemKey}?t={Uri.EscapeDataString(token)}",
                    x.ArtFile is null ? null : $"/api/v1/catalog/{x.ItemKey}/art",
                    x.AddedAt);
            }).ToArray();

            return Results.Ok(new CatalogDto(dtos, scanner.IsScanning, scanner.LastScanAt,
                Directory.Exists(CatalogScanner.MediaRoot)));
        }).RequireAuthorization();

        // Posters go over the ordinary authorised API, because the app fetches
        // them with an HttpClient that can carry the bearer token. Only playback
        // needs the signed-URL detour.
        g.MapGet("/catalog/{key}/art", async (string key, ClaimsPrincipal me, VaultDbContext db) =>
        {
            if (!await ApprovedAsync(db, me.FindFirstValue(JwtRegisteredClaimNames.Sub)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var item = await db.CatalogItems.FirstOrDefaultAsync(x => x.ItemKey == key);
            if (item?.ArtFile is null) return Results.NotFound();

            var path = Path.Combine(CatalogScanner.ArtRoot, item.ArtFile);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "image/jpeg");
        }).RequireAuthorization();

        // ---- Rescan --------------------------------------------------------

        // Fire-and-forget: a full sweep of a large library takes minutes on
        // first run (every file gets probed and a poster extracted), and no
        // client should be made to hold a request open for it.
        g.MapPost("/catalog/rescan", async (ClaimsPrincipal me, VaultDbContext db, CatalogScanner scanner) =>
        {
            if (!await ApprovedAsync(db, me.FindFirstValue(JwtRegisteredClaimNames.Sub)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (scanner.IsScanning) return Results.Accepted(value: new { scanning = true });
            _ = Task.Run(() => scanner.ScanAsync());
            return Results.Accepted(value: new { scanning = true });
        }).RequireAuthorization();

        // ---- Playback ------------------------------------------------------

        // Deliberately outside /api/v1 and outside the JWT pipeline: the caller
        // is a media player following a URL, and its ticket is the signature in
        // the query string.
        app.MapMethods("/media/{key}", new[] { "GET", "HEAD" },
            async (string key, HttpContext ctx, VaultDbContext db, MediaTokens media) =>
        {
            if (!media.TryVerify(ctx.Request.Query["t"], key, out var userId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // The signature proves the token was minted for an approved account;
            // this re-check makes a block take effect on the next request rather
            // than whenever the last 12-hour token dies.
            if (!await ApprovedAsync(db, userId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var item = await db.CatalogItems.FirstOrDefaultAsync(x => x.ItemKey == key);
            if (item is null) return Results.NotFound();

            var path = CatalogScanner.ResolveFile(item);
            if (path is null) return Results.NotFound();

            // enableRangeProcessing is the whole point: seeking in a two-hour
            // film is a byte-range request, and without this the player can only
            // ever start from the beginning.
            return Results.File(path, item.ContentType,
                lastModified: File.GetLastWriteTimeUtc(path),
                entityTag: null,
                enableRangeProcessing: true);
        });
    }
}
