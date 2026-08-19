using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace VideoPlayer.Rendezvous.Vault;

// begin → n×append → complete. Resume is just calling begin again: the reply
// says how many bytes are already here, and the client continues from there.
public record UploadBeginReq(string FileName, long SizeBytes);
public record UploadBeginResp(string UploadId, long ReceivedBytes, string Status); // "new" | "resume" | "exists"
public record UploadCompleteReq(string Sha256);

/// <summary>
/// Owner-only film upload into the media disk.
///
/// The media mount stays read-only; uploads go through a second, deliberately
/// narrow writable mount (VAULT_UPLOADS → the films directory alone, so music
/// and everything else on the disk stays structurally untouchable). Partial
/// transfers live in a dot-directory the scanner's Hidden filter already
/// ignores, and only a completed, checksum-verified file is moved into place —
/// the scanner can never index half a film.
///
/// Chunked rather than one giant PUT because a film is hours of home upload:
/// the connection WILL drop mid-transfer at least once, and re-sending 20 GB
/// from byte zero is the difference between an upload feature and a trap.
/// </summary>
public static class UploadApi
{
    /// <summary>Writable landing area; the films directory on the media disk.</summary>
    public static string UploadRoot =>
        Environment.GetEnvironmentVariable("VAULT_UPLOADS")
        ?? Path.Combine(CatalogScanner.MediaRoot, "films");

    private static string IncomingDir => Path.Combine(UploadRoot, ".incoming");

    /// <summary>
    /// Refuse an upload that would leave less than this free. A full media disk
    /// fails every later poster extraction and, worse, the next database write
    /// if anything shares the volume.
    /// </summary>
    private const long FreeSpaceMarginBytes = 20L << 30;

    private const long MaxChunkBytes = 64L << 20;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ogv", ".3gp", ".ts",
    };

    public static void MapUploadApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/upload");

        g.MapPost("/begin", async (UploadBeginReq r, ClaimsPrincipal me,
            VaultDbContext db, HttpContext ctx) =>
        {
            if (!await IsOwnerAsync(db, me)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // The filename becomes a path on the media disk, so it is reduced
            // to its base name before anything else looks at it — "..\\" and
            // friends die here.
            var name = Path.GetFileName((r.FileName ?? "").Trim());
            if (name.Length is 0 or > 200 || name.StartsWith('.'))
                return Results.BadRequest(new { error = "That file name can't be used." });
            if (!VideoExtensions.Contains(Path.GetExtension(name)))
                return Results.BadRequest(new { error = "Only video files can be uploaded." });
            if (r.SizeBytes <= 0)
                return Results.BadRequest(new { error = "File size must be known up front." });

            // Same identity the whole app uses. Already in the catalogue —
            // whether it arrived by upload or by copying to the disk — means
            // there is nothing to transfer.
            var filmKey = $"{name}|{r.SizeBytes}";
            if (await db.CatalogItems.AnyAsync(x => x.FilmKey == filmKey))
                return Results.Ok(new UploadBeginResp("", r.SizeBytes, "exists"));

            Directory.CreateDirectory(IncomingDir);
            PruneStaleParts();

            var free = new DriveInfo(Path.GetFullPath(UploadRoot)).AvailableFreeSpace;
            if (free - r.SizeBytes < FreeSpaceMarginBytes)
                return Results.Json(new
                {
                    error = $"Not enough space on the media disk: {free >> 30} GB free, "
                          + $"{r.SizeBytes >> 30} GB needed plus a {FreeSpaceMarginBytes >> 30} GB margin.",
                }, statusCode: 507);

            if (File.Exists(Path.Combine(UploadRoot, name)))
                return Results.BadRequest(new
                    { error = "A different file with that name is already in the library." });

            // Deterministic id — an interrupted upload of the same file finds
            // its own partial. Hashed so the id is path-safe regardless of what
            // the filename contains.
            var uploadId = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filmKey)), 0, 12).ToLowerInvariant();
            var meta = MetaPath(uploadId);
            var part = PartPath(uploadId);

            // Metadata survives restarts beside the partial, so resume works
            // even after the server has been redeployed mid-upload.
            await File.WriteAllTextAsync(meta, $"{name}\n{r.SizeBytes}");
            var received = File.Exists(part) ? new FileInfo(part).Length : 0;
            return Results.Ok(new UploadBeginResp(uploadId, received, received > 0 ? "resume" : "new"));
        }).RequireAuthorization();

        // Raw bytes, appended at the declared offset. The offset must match
        // what is already here — if the client and server disagree (a chunk
        // landed but the response was lost), the 409 carries the truth and the
        // client re-aligns instead of corrupting the file.
        g.MapPost("/{id}/chunk", async (string id, long offset, ClaimsPrincipal me,
            VaultDbContext db, HttpContext ctx) =>
        {
            if (!await IsOwnerAsync(db, me)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!ValidId(id) || !File.Exists(MetaPath(id)))
                return Results.NotFound(new { error = "No such upload. Call begin again." });

            var declared = long.Parse((await File.ReadAllTextAsync(MetaPath(id))).Split('\n')[1]);
            var part = PartPath(id);
            var current = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (offset != current)
                return Results.Json(new { error = "Offset mismatch.", receivedBytes = current },
                    statusCode: 409);
            if (ctx.Request.ContentLength is > MaxChunkBytes)
                return Results.BadRequest(new { error = "Chunk too large." });

            await using (var file = new FileStream(part, FileMode.Append, FileAccess.Write,
                FileShare.None, 1 << 16, useAsync: true))
            {
                await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
                if (file.Length > declared)
                    return Results.BadRequest(new { error = "More bytes than the declared size." });
            }
            return Results.Ok(new { receivedBytes = new FileInfo(part).Length });
        }).RequireAuthorization();

        g.MapGet("/{id}", async (string id, ClaimsPrincipal me, VaultDbContext db) =>
        {
            if (!await IsOwnerAsync(db, me)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!ValidId(id) || !File.Exists(MetaPath(id))) return Results.NotFound();
            var part = PartPath(id);
            return Results.Ok(new { receivedBytes = File.Exists(part) ? new FileInfo(part).Length : 0 });
        }).RequireAuthorization();

        g.MapPost("/{id}/complete", async (string id, UploadCompleteReq r, ClaimsPrincipal me,
            VaultDbContext db, CatalogScanner scanner) =>
        {
            if (!await IsOwnerAsync(db, me)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!ValidId(id) || !File.Exists(MetaPath(id)))
                return Results.NotFound(new { error = "No such upload." });

            var metaLines = (await File.ReadAllTextAsync(MetaPath(id))).Split('\n');
            var (name, declared) = (metaLines[0], long.Parse(metaLines[1]));
            var part = PartPath(id);
            if (!File.Exists(part) || new FileInfo(part).Length != declared)
                return Results.BadRequest(new
                {
                    error = "The upload is incomplete.",
                    receivedBytes = File.Exists(part) ? new FileInfo(part).Length : 0,
                });

            // The whole file is re-read and hashed before it becomes a library
            // row. Hours of chunked transfer over a home connection is exactly
            // where silent corruption lives, and a film that stutters at minute
            // 90 because of one bad chunk is worse than a failed upload.
            if (string.IsNullOrWhiteSpace(r.Sha256))
                return Results.BadRequest(new { error = "A sha256 of the source file is required." });
            string actual;
            await using (var stream = File.OpenRead(part))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(r.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                File.Delete(MetaPath(id));
                return Results.BadRequest(new
                    { error = "Checksum mismatch — the transfer was corrupted. Upload it again." });
            }

            var final = Path.Combine(UploadRoot, name);
            if (File.Exists(final))
            {
                // Same name arrived by other means mid-upload. The catalogue
                // dedupe at begin() makes this a same-file race, not data loss.
                File.Delete(part);
            }
            else
            {
                File.Move(part, final);
            }
            File.Delete(MetaPath(id));

            // Index it now, in the background; the client polls the catalogue.
            _ = Task.Run(() => scanner.ScanAsync());
            return Results.Ok(new { status = "ok", fileName = name });
        }).RequireAuthorization();
    }

    private static async Task<bool> IsOwnerAsync(VaultDbContext db, ClaimsPrincipal me)
    {
        var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (uid is null) return false;
        var user = await db.Users.FindAsync(uid);
        return user is { IsOwner: true };
    }

    private static bool ValidId(string id) =>
        id.Length == 24 && id.All(char.IsAsciiHexDigitLower);

    private static string PartPath(string id) => Path.Combine(IncomingDir, id + ".part");
    private static string MetaPath(string id) => Path.Combine(IncomingDir, id + ".meta");

    /// <summary>Abandoned partials are reclaimed after a week — disk, not tombstones.</summary>
    private static void PruneStaleParts()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(IncomingDir))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
        }
        catch
        {
            // Housekeeping; never the reason an upload fails.
        }
    }
}
