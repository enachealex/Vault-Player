using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>A Vault Movies account. Identity supplies username, password hash, etc.</summary>
public class VaultUser : IdentityUser
{
    /// <summary>What friends see. Defaults to the username; not unique.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// "pending" | "approved" | "blocked". The server's film library is the
    /// owner's own collection, so an account existing is not the same as an
    /// account being allowed in: everyone starts pending and the owner decides.
    /// Blocked also refuses login outright.
    /// </summary>
    public string AccessStatus { get; set; } = AccessStates.Pending;

    /// <summary>
    /// The person whose server this is. Exactly one in practice — the first
    /// account registered — and the only one who sees the admin endpoints and
    /// may upload films.
    /// </summary>
    public bool IsOwner { get; set; }

    public long CreatedAt { get; set; }
}

public static class AccessStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Blocked = "blocked";
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

/// <summary>
/// One of a person's own shelves — "Pixar", "Christmas", "Watch with Dad".
///
/// Deliberately NOT the word "library": in this codebase a library is already a
/// person's resume and watch state (<see cref="LibraryItem"/>, /api/v1/library,
/// LibraryView), which is close enough to be confusing and means the opposite
/// thing. These are Plex's Collections, minus Plex's rule that only the server
/// owner may make them.
///
/// Belongs to the ACCOUNT, not to a person or a device. Everyone signed in with
/// the same account — a desktop, a phone, a television that took a six-character
/// code — sees the same shelves, and nobody else's.
/// </summary>
public class Collection
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Which member film's poster stands for the shelf. Held as a FilmKey for
    /// the same reason membership is; null means "use the first member".
    /// </summary>
    public string? ArtFilmKey { get; set; }

    /// <summary>Where this shelf sits in the person's own ordering.</summary>
    public int SortOrder { get; set; }

    public long CreatedAt { get; set; }

    /// <summary>Last write, unix ms. Last-writer-wins across devices.</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>
/// One film on one shelf.
///
/// Keyed by FilmKey — "name|size" — and never by CatalogItem.ItemKey. ItemKey is
/// a hash of the file's path, and the scanner deletes rows whose ItemKey it did
/// not see this sweep, so renaming a film or moving it between folders is a
/// delete and an insert. Membership keyed on ItemKey would quietly empty itself
/// every time somebody tidied their folders. FilmKey survives that, and is
/// already what resume follows for exactly the same reason.
/// </summary>
public class CollectionItem
{
    public long Id { get; set; }
    public long CollectionId { get; set; }
    public string FilmKey { get; set; } = "";

    /// <summary>
    /// Denormalised so a film that has left the server still shows as a named,
    /// greyed-out entry the owner can remove, rather than silently vanishing.
    /// </summary>
    public string Name { get; set; } = "";

    public int SortOrder { get; set; }
    public long AddedAt { get; set; }
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

/// <summary>
/// An account on another service that maps to a Vault account — today only LAN
/// Party. <see cref="Subject"/> is that service's stable id for the person
/// (LAN Party's username), stored verbatim: it is the ONLY join key. Email is
/// deliberately never used to match, because LAN Party does not verify
/// addresses, so matching on one would let anyone claim someone else's library.
/// </summary>
public class LinkedIdentity
{
    public long Id { get; set; }
    /// <summary>Issuer, e.g. "lanparty".</summary>
    public string Provider { get; set; } = "";
    /// <summary>The provider's stable user id — never case-folded, never an email.</summary>
    public string Subject { get; set; } = "";
    public string UserId { get; set; } = "";
    /// <summary>Display name as the provider last reported it.</summary>
    public string ProviderName { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
}

/// <summary>
/// A single-use ticket into the library, minted by the owner and handed to one
/// person — usually as a QR code. Registering with a live one skips the pending
/// queue entirely: the invite is the owner's approval, given in advance. This
/// is what makes membership personal rather than "whoever found the domain".
/// </summary>
public class InviteCode
{
    public long Id { get; set; }
    /// <summary>What the friend types, e.g. "VAULT-7KM2Q4XB". Unique.</summary>
    public string Code { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    /// <summary>Set when spent; a used invite admits nobody else.</summary>
    public string? UsedByUserId { get; set; }
    /// <summary>Denormalised so the owner's invite list reads without a join.</summary>
    public string UsedByName { get; set; } = "";
    public long UsedAt { get; set; }
}

/// <summary>
/// A single-sign-on assertion that has already been spent. The assertion itself
/// is short-lived, so rows only need to outlive it; the sweep keeps the table
/// from growing without bound.
/// </summary>
public class ConsumedJti
{
    public long Id { get; set; }
    public string Jti { get; set; } = "";
    public long ExpiresAt { get; set; }
}

/// <summary>
/// One media file that lives on the SERVER rather than on somebody's PC — the
/// shared library everyone with an account can stream from.
///
/// This is the one thing here the relay does not otherwise do: everything else
/// treats media as somebody else's bytes passing through. Rows are built by
/// <see cref="CatalogScanner"/> from the media root and are disposable — delete
/// the table and a rescan rebuilds it. The files themselves are never written.
/// </summary>
public class CatalogItem
{
    public long Id { get; set; }

    /// <summary>
    /// Opaque, stable id used in stream URLs. Derived from the relative path, so
    /// it survives rescans and reboots but changes if the file is renamed —
    /// which is fine, because resume follows <see cref="FilmKey"/>, not this.
    /// </summary>
    public string ItemKey { get; set; } = "";

    /// <summary>
    /// Path beneath the media root, '/'-separated. Stored relative so the disk
    /// can be remounted somewhere else without invalidating the catalogue.
    /// </summary>
    public string RelativePath { get; set; } = "";

    /// <summary>"film" or "track". Only films are scanned today; music is next.</summary>
    public string Kind { get; set; } = "film";

    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public long? DurationMs { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }

    /// <summary>Whether a plain browser &lt;video&gt; can play this. See CatalogScanner.</summary>
    public bool WebPlayable { get; set; }

    /// <summary>
    /// "filename|size" — the same film identity the app uses locally. Sharing it
    /// means a film watched from the server and the same rip watched off a USB
    /// drive are one film as far as resume points and watch counts go.
    /// </summary>
    public string FilmKey { get; set; } = "";

    public long AddedAt { get; set; }
    /// <summary>Unix ms the scanner last saw the file on disk.</summary>
    public long LastSeenAt { get; set; }

    /// <summary>Cached poster filename under the art directory; null until extracted.</summary>
    public string? ArtFile { get; set; }
}

public class VaultDbContext(DbContextOptions<VaultDbContext> options)
    : IdentityDbContext<VaultUser>(options)
{
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<PartyInvite> PartyInvites => Set<PartyInvite>();
    public DbSet<LinkedIdentity> LinkedIdentities => Set<LinkedIdentity>();
    public DbSet<ConsumedJti> ConsumedJtis => Set<ConsumedJti>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // One row per (user, film); sync upserts on this pair.
        b.Entity<LibraryItem>().HasIndex(x => new { x.UserId, x.FilmKey }).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.Token).IsUnique();
        // One row per pair, so a request can't be duplicated.
        b.Entity<Friendship>().HasIndex(x => new { x.RequesterId, x.AddresseeId }).IsUnique();
        b.Entity<PartyInvite>().HasIndex(x => x.ToUserId);
        // One Vault account per provider identity, and vice versa — neither a
        // person nor an account may be linked twice.
        b.Entity<LinkedIdentity>().HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
        b.Entity<LinkedIdentity>().HasIndex(x => new { x.Provider, x.UserId }).IsUnique();
        b.Entity<ConsumedJti>().HasIndex(x => x.Jti).IsUnique();
        // One row per file; the scanner upserts on this key every sweep.
        b.Entity<CatalogItem>().HasIndex(x => x.ItemKey).IsUnique();
        b.Entity<CatalogItem>().HasIndex(x => x.Kind);
        b.Entity<InviteCode>().HasIndex(x => x.Code).IsUnique();
        // Every read is "this account's shelves", so that is the index.
        b.Entity<Collection>().HasIndex(x => x.UserId);
        // A film appears on a shelf once. The unique pair also makes "add"
        // idempotent, so a double-click cannot produce two identical entries.
        b.Entity<CollectionItem>().HasIndex(x => new { x.CollectionId, x.FilmKey }).IsUnique();
    }
}
