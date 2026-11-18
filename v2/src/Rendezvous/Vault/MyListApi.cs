using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// "My List": films someone saved with the + button, the same on every device
/// they sign in on. The catalogue response carries the list too (newest first),
/// so a client needs these routes only to change it.
/// </summary>
public static class MyListApi
{
    public static async Task<string[]> LoadAsync(VaultDbContext db, string uid) =>
        await db.MyListItems.Where(x => x.UserId == uid)
            .OrderByDescending(x => x.AddedAt)
            .Select(x => x.FilmKey)
            .ToArrayAsync();

    public static void MapMyListApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/mylist");

        g.MapGet("", async (ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            return Results.Ok(await LoadAsync(db, uid));
        }).RequireAuthorization();

        // Idempotent both ways: adding a saved film or removing an unsaved one
        // succeeds and changes nothing, so a retried tap is harmless.
        g.MapPut("/{filmKey}", async (string filmKey, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            if (!await CatalogApi.ApprovedAsync(db, uid)) return Results.Forbid();
            if (!await db.CatalogItems.AnyAsync(x => x.FilmKey == filmKey))
                return Results.NotFound(new { error = "That film is not on this server." });
            if (!await db.MyListItems.AnyAsync(x => x.UserId == uid && x.FilmKey == filmKey))
            {
                db.MyListItems.Add(new MyListItem
                {
                    UserId = uid, FilmKey = filmKey,
                    AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException) { /* a racing duplicate; the unique index kept one */ }
            }
            return Results.Ok(await LoadAsync(db, uid));
        }).RequireAuthorization();

        g.MapDelete("/{filmKey}", async (string filmKey, ClaimsPrincipal me, VaultDbContext db) =>
        {
            var uid = me.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (uid is null) return Results.Unauthorized();
            await db.MyListItems.Where(x => x.UserId == uid && x.FilmKey == filmKey).ExecuteDeleteAsync();
            return Results.Ok(await LoadAsync(db, uid));
        }).RequireAuthorization();
    }
}
