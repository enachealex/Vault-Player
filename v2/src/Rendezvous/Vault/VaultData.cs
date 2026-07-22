using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>A Vault Movies account. Identity supplies email, password hash, etc.</summary>
public class VaultUser : IdentityUser
{
    /// <summary>What friends see. Not unique; the email is the identity.</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// One remembered film for one user. Keyed by <see cref="FilmKey"/> —
/// "filename|size" — so the same rip on two machines matches even in different
/// folders. Stores metadata only; never the video.
/// </summary>
public class LibraryItem
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string FilmKey { get; set; } = "";

    public string Name { get; set; } = "";
    public long ResumeMs { get; set; }
    public int WatchCount { get; set; }
    public long LastWatchedAt { get; set; }
    /// <summary>User chapters, as the app's JSON. Opaque to the server.</summary>
    public string? ChaptersJson { get; set; }

    /// <summary>Last write, unix ms. Last-writer-wins across devices.</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>A long-lived refresh token, stored so it can be revoked.</summary>
public class RefreshToken
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Token { get; set; } = "";
    public long ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>A short-lived numeric code emailed for verification or password reset.</summary>
public class EmailCode
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>"verify" or "reset".</summary>
    public string Purpose { get; set; } = "";
    public long ExpiresAt { get; set; }
    public bool Used { get; set; }
}

/// <summary>
/// A friendship between two accounts. One row per pair: while
/// <see cref="Status"/> is "pending" only <see cref="RequesterId"/> has asked;
/// "accepted" means both are friends. Direction only matters for a pending
/// request (who still needs to answer).
/// </summary>
public class Friendship
{
    public long Id { get; set; }
    public string RequesterId { get; set; } = "";
    public string AddresseeId { get; set; } = "";
    /// <summary>"pending" or "accepted".</summary>
    public string Status { get; set; } = "pending";
    public long CreatedAt { get; set; }
    public long RespondedAt { get; set; }
}

/// <summary>
/// An invitation to join a live Watch Party, from one friend to another. Carries
/// everything the invitee needs to join: the room code and the party server to
/// reach it on. Consumed by the invitee's app when it polls for invites.
/// </summary>
public class PartyInvite
{
    public long Id { get; set; }
    public string FromUserId { get; set; } = "";
    public string ToUserId { get; set; } = "";
    /// <summary>Denormalised so the invitee sees who invited them without a join.</summary>
    public string FromName { get; set; } = "";
    public string RoomCode { get; set; } = "";
    /// <summary>What the invitee types as "party server" (domain, or the host's LAN IP).</summary>
    public string Server { get; set; } = "";
    public string MovieTitle { get; set; } = "";
    public long CreatedAt { get; set; }
    public bool Dismissed { get; set; }
}

public class VaultDbContext(DbContextOptions<VaultDbContext> options)
    : IdentityDbContext<VaultUser>(options)
{
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<PartyInvite> PartyInvites => Set<PartyInvite>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // One row per (user, film); sync upserts on this pair.
        b.Entity<LibraryItem>().HasIndex(x => new { x.UserId, x.FilmKey }).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.Token).IsUnique();
        // One row per pair, so a request can't be duplicated.
        b.Entity<Friendship>().HasIndex(x => new { x.RequesterId, x.AddresseeId }).IsUnique();
        b.Entity<PartyInvite>().HasIndex(x => x.ToUserId);
    }
}
