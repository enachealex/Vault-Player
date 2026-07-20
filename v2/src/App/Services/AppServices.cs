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
}
