using System;
using LibVLCSharp.Shared;

namespace VideoPlayer.App.Services;

/// <summary>Process-wide singletons: one libVLC engine, one settings store.</summary>
public static class AppServices
{
    private static readonly Lazy<LibVLC> _libVlc = new(() =>
    {
        Core.Initialize();
        return new LibVLC("--no-video-title-show");
    });

    public static LibVLC LibVlc => _libVlc.Value;

    /// <summary>
    /// Build libVLC's plugin cache (plugins.dat) once, at pack time, then exit.
    /// Without the cache, libVLC scans all ~320 native plugin DLLs on every cold
    /// start — ~12 seconds. With it, startup reads one file. `--reset-plugins-cache`
    /// forces a fresh scan and writes the cache into the bundled plugins folder.
    /// </summary>
    public static void GeneratePluginCache()
    {
        Core.Initialize();
        using var _ = new LibVLC("--reset-plugins-cache");
    }

    public static Settings Settings { get; } = Settings.Load();

    public static CastService Cast { get; } = new();

    /// <summary>Audio output device selection, applied to every film.</summary>
    public static AudioService Audio { get; } = new();

    /// <summary>Optional account + cross-machine library sync.</summary>
    public static AccountService Account { get; } = new();

    /// <summary>Films held on the Vault server and streamed from it.</summary>
    public static CatalogService Catalog { get; } = new(Account);

    /// <summary>
    /// A film library hosted on this PC. Idle unless the user chose to host
    /// here, which is the point: this exists so a folder of films can become
    /// something a television can watch without anybody installing a server.
    /// </summary>
    public static LibraryServer Library { get; } = new();

    /// <summary>Owner-only queue pushing films from the folder up to the server.</summary>
    public static UploadService Uploads { get; } = new(Account);

    /// <summary>Account-to-account friends and Watch Party invites.</summary>
    public static FriendsService Friends { get; } = new(Account);

    /// <summary>
    /// The Watch Party the user is currently in, if any.
    ///
    /// This lives here rather than on a view because navigation disposes the
    /// outgoing view: while PlayerView owned the session, going back to the
    /// library tore the room down and dropped every guest. A party outlives any
    /// screen you happen to be looking at, so nothing but <see cref="LeaveParty"/>
    /// may dispose it.
    /// </summary>
    public static PartySession? CurrentParty { get; set; }

    /// <summary>What the current party is watching, so it can be reopened.</summary>
    public static Models.MovieItem? CurrentPartyMovie { get; set; }

    /// <summary>Ends the party and releases it. Safe to call when there isn't one.</summary>
    public static void LeaveParty()
    {
        var party = CurrentParty;
        CurrentParty = null;
        CurrentPartyMovie = null;
        party?.Dispose();
    }
}
