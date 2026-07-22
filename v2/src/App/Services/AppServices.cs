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

    public static Settings Settings { get; } = Settings.Load();

    public static CastService Cast { get; } = new();

    /// <summary>Optional account + cross-machine library sync.</summary>
    public static AccountService Account { get; } = new();

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
