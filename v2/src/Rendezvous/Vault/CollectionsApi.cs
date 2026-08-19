using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace VideoPlayer.Rendezvous.Vault;

// ---- DTOs -------------------------------------------------------------------

/// <summary>
/// One film on a shelf. The name travels with it so an entry whose film has
/// left the server still renders as something a person can recognise and
/// remove, instead of disappearing without explanation.
/// </summary>
public record CollectionEntryDto(string FilmKey, string Name);

/// <summary>
/// A shelf and what is on it.
///
/// Carries FilmKeys rather than whole catalogue rows on purpose: GET /catalog is
/// unpaginated and mints a fresh signed media token for every film on every
/// call, so a client that re-fetched the catalogue once per shelf would re-mint
/// the entire library's tokens each time. The client joins these against the one
/// catalogue response it already has.
/// </summary>
public record CollectionDto(long Id, string Name, string? ArtFilmKey, int SortOrder,
    CollectionEntryDto[] Items, long UpdatedAt);

public record CreateCollectionReq(string Name);
public record UpdateCollectionReq(string? Name, string? ArtFilmKey, int? SortOrder);
public record SetMembersReq(string[] FilmKeys);
public record MembershipReq(string FilmKey);

/// <summary>
/// Personal shelves over the server's film library.
///
/// Every shelf belongs to an ACCOUNT. Anyone signed in with that account sees
/// them — the desktop app, a phone, a television that took a six-character link
/// code — and no other account sees them at all. There is no owner privilege
/// here: an approved account curates its own view and nobody else's, so unlike
/// the admin routes these need no owner gate, only the same approval gate the
/// catalogue itself uses.
///
/// Membership is by FilmKey. See <see cref="CollectionItem"/> for why that
/// rather than ItemKey — briefly, ItemKey is a hash of the file's path and does
/// not survive a rename.
/// </summary>
public static class CollectionsApi
{
    /// <summary>
    /// Enough that nobody sensible hits it, low enough that a runaway client
    /// cannot fill the database. Both are per account.
    /// </summary>
    private const int MaxCollections = 200;
    private const int MaxItemsPerCollection = 2000;
    private const int MaxNameLength = 60;

    public static void MapCollectionsApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1");

        // ---- Read ----------------------------------------------------------

        g.MapGet("/collections", async (ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid))
                return Results.Json(
                    new { error = "Your account is waiting for the owner's approval." },
                    statusCode: 403);

            return Results.Ok(await LoadAllAsync(db, uid!));
        }).RequireAuthorization();

        // ---- Create --------------------------------------------------------

        g.MapPost("/collections", async (CreateCollectionReq r, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var name = (r.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "Give the collection a name." });
            if (name.Length > MaxNameLength)
                return Results.BadRequest(new { error = $"Keep the name under {MaxNameLength} characters." });

            var mine = await db.Collections.Where(x => x.UserId == uid).ToListAsync();
            if (mine.Count >= MaxCollections)
                return Results.BadRequest(new { error = "You already have as many collections as we allow." });
            if (mine.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = "You already have a collection with that name." });

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var row = new Collection
            {
                UserId = uid!,
                Name = name,
                // New shelves land at the end of the person's own ordering.
                SortOrder = mine.Count == 0 ? 0 : mine.Max(x => x.SortOrder) + 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Collections.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(new CollectionDto(row.Id, row.Name, row.ArtFilmKey, row.SortOrder,
                Array.Empty<CollectionEntryDto>(), row.UpdatedAt));
        }).RequireAuthorization();

        // ---- Rename / re-order / choose art --------------------------------

        g.MapPatch("/collections/{id:long}", async (long id, UpdateCollectionReq r,
            ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var row = await MineAsync(db, uid!, id);
            if (row is null) return Results.NotFound();

            if (r.Name is not null)
            {
                var name = r.Name.Trim();
                if (name.Length == 0) return Results.BadRequest(new { error = "Give the collection a name." });
                if (name.Length > MaxNameLength)
                    return Results.BadRequest(new { error = $"Keep the name under {MaxNameLength} characters." });
                var clash = await db.Collections.AnyAsync(x =>
                    x.UserId == uid && x.Id != id && x.Name.ToLower() == name.ToLower());
                if (clash) return Results.Conflict(new { error = "You already have a collection with that name." });
                row.Name = name;
            }
            if (r.ArtFilmKey is not null) row.ArtFilmKey = r.ArtFilmKey.Length == 0 ? null : r.ArtFilmKey;
            if (r.SortOrder is not null) row.SortOrder = r.SortOrder.Value;

            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
            return Results.Ok(await LoadOneAsync(db, row));
        }).RequireAuthorization();

        // ---- Delete --------------------------------------------------------

        g.MapDelete("/collections/{id:long}", async (long id, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var row = await MineAsync(db, uid!, id);
            if (row is null) return Results.NotFound();

            // Nothing in this schema has a foreign key, so the members go by
            // hand. Missing this would leave rows pointing at an id that a later
            // AUTOINCREMENT could hand to somebody else's collection.
            db.CollectionItems.RemoveRange(db.CollectionItems.Where(x => x.CollectionId == id));
            db.Collections.Remove(row);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization();

        // ---- Membership ----------------------------------------------------

        // Whole-set replace. The per-user library sync merges additively and so
        // cannot express a removal without tombstones; a shelf is small and has
        // exactly one writer, so sending the whole set is both simpler and the
        // only way "I took that one off" survives a round trip.
        g.MapPut("/collections/{id:long}/items", async (long id, SetMembersReq r,
            ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var row = await MineAsync(db, uid!, id);
            if (row is null) return Results.NotFound();

            var wanted = (r.FilmKeys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxItemsPerCollection)
                .ToList();

            var names = await NamesForAsync(db, wanted);

            // What is on the shelf already, and under what name. Two things
            // depend on this. A film that has left the server must keep the name
            // it was saved under — looking names up in the catalogue alone would
            // replace it with the raw key, destroying the very thing the
            // denormalised name exists to preserve. And a key that is neither in
            // the catalogue nor already here is not something this account can
            // have meant, so it is refused, exactly as adding one would be.
            var existing = await db.CollectionItems
                .Where(x => x.CollectionId == id)
                .ToDictionaryAsync(x => x.FilmKey, x => x.Name, StringComparer.Ordinal);

            var unknown = wanted.Where(k => !names.ContainsKey(k) && !existing.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
                return Results.BadRequest(new
                {
                    error = unknown.Count == 1
                        ? "That film isn't in the server's library."
                        : $"{unknown.Count} of those films aren't in the server's library.",
                });

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.CollectionItems.RemoveRange(db.CollectionItems.Where(x => x.CollectionId == id));
            for (var i = 0; i < wanted.Count; i++)
            {
                db.CollectionItems.Add(new CollectionItem
                {
                    CollectionId = id,
                    FilmKey = wanted[i],
                    // Catalogue name first so a rename on disk is picked up;
                    // the saved name second so an orphan keeps its identity.
                    Name = names.GetValueOrDefault(wanted[i])
                           ?? existing.GetValueOrDefault(wanted[i], wanted[i]),
                    SortOrder = i,
                    AddedAt = now,
                });
            }
            row.UpdatedAt = now;
            await db.SaveChangesAsync();
            return Results.Ok(await LoadOneAsync(db, row));
        }).RequireAuthorization();

        // Add one film — the "right-click, add to collection" path, so a client
        // never has to hold the whole set just to append to it.
        g.MapPost("/collections/{id:long}/items/add", async (long id, MembershipReq r,
            ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var row = await MineAsync(db, uid!, id);
            if (row is null) return Results.NotFound();

            var key = (r.FilmKey ?? "").Trim();
            if (key.Length == 0) return Results.BadRequest(new { error = "Which film?" });

            // The name is taken from the catalogue rather than from the caller:
            // the server knows what it is holding, and this is the one place a
            // client could otherwise write arbitrary text into everyone's view
            // of a film.
            var names = await NamesForAsync(db, new[] { key });
            if (!names.TryGetValue(key, out var name))
                return Results.BadRequest(new { error = "That film isn't in the server's library." });

            var already = await db.CollectionItems.AnyAsync(x => x.CollectionId == id && x.FilmKey == key);
            if (!already)
            {
                var count = await db.CollectionItems.CountAsync(x => x.CollectionId == id);
                if (count >= MaxItemsPerCollection)
                    return Results.BadRequest(new { error = "That collection is full." });

                var last = await db.CollectionItems.Where(x => x.CollectionId == id)
                    .MaxAsync(x => (int?)x.SortOrder) ?? -1;
                db.CollectionItems.Add(new CollectionItem
                {
                    CollectionId = id,
                    FilmKey = key,
                    Name = name,
                    SortOrder = last + 1,
                    AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
            return Results.Ok(await LoadOneAsync(db, row));
        }).RequireAuthorization();

        g.MapPost("/collections/{id:long}/items/remove", async (long id, MembershipReq r,
            ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Forbidden();

            var row = await MineAsync(db, uid!, id);
            if (row is null) return Results.NotFound();

            var key = (r.FilmKey ?? "").Trim();
            db.CollectionItems.RemoveRange(
                db.CollectionItems.Where(x => x.CollectionId == id && x.FilmKey == key));
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
            return Results.Ok(await LoadOneAsync(db, row));
        }).RequireAuthorization();
    }

    // ---- Helpers -----------------------------------------------------------

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>
    /// Fetch a shelf only if it belongs to this account.
    ///
    /// Every route goes through here. Collection ids are sequential and
    /// therefore guessable, so ownership is the whole of the access control —
    /// a route that looked one up by id alone would hand somebody else's shelf
    /// to any signed-in account that counted upwards.
    /// </summary>
    private static Task<Collection?> MineAsync(VaultDbContext db, string uid, long id) =>
        db.Collections.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);

    /// <summary>Current catalogue names for a set of FilmKeys, for the ones still present.</summary>
    private static async Task<Dictionary<string, string>> NamesForAsync(
        VaultDbContext db, IReadOnlyCollection<string> filmKeys)
    {
        if (filmKeys.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = await db.CatalogItems
            .Where(x => filmKeys.Contains(x.FilmKey))
            .Select(x => new { x.FilmKey, x.Name })
            .ToListAsync();

        // Two files can share a FilmKey (same name, same size, different
        // folders). Either name will do; the point is not to throw.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows) map[r.FilmKey] = r.Name;
        return map;
    }

    internal static async Task<CollectionDto[]> LoadAllAsync(VaultDbContext db, string uid)
    {
        var shelves = await db.Collections
            .Where(x => x.UserId == uid)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync();
        if (shelves.Count == 0) return Array.Empty<CollectionDto>();

        var ids = shelves.Select(x => x.Id).ToList();
        var members = await db.CollectionItems
            .Where(x => ids.Contains(x.CollectionId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync();

        return shelves.Select(s => new CollectionDto(
            s.Id, s.Name, s.ArtFilmKey, s.SortOrder,
            members.Where(m => m.CollectionId == s.Id)
                   .Select(m => new CollectionEntryDto(m.FilmKey, m.Name))
                   .ToArray(),
            s.UpdatedAt)).ToArray();
    }

    private static async Task<CollectionDto> LoadOneAsync(VaultDbContext db, Collection row)
    {
        var members = await db.CollectionItems
            .Where(x => x.CollectionId == row.Id)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new CollectionEntryDto(x.FilmKey, x.Name))
            .ToArrayAsync();
        return new CollectionDto(row.Id, row.Name, row.ArtFilmKey, row.SortOrder, members, row.UpdatedAt);
    }
}
