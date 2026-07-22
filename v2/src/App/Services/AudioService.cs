using System;
using System.Collections.Generic;
using System.Linq;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoPlayer.App.Services;

/// <summary>
/// Chooses which audio output device films play through. libVLC otherwise picks
/// one for you, which can be the wrong one (sound out of the speakers while
/// you're on headphones). The choice is remembered and applied to every film.
/// </summary>
public class AudioService
{
    /// <summary>One selectable output. Id null = let the system decide.</summary>
    public record Device(string? Id, string Name);

    /// <summary>The film currently open, so a change can take effect immediately.</summary>
    private MediaPlayer? _current;

    public string? SelectedId => AppServices.Settings.AudioOutputDevice;

    /// <summary>
    /// The output devices libVLC can send audio to, always led by "System
    /// default". Enumerated through a throwaway player so it works with no film
    /// open. Descriptions are what Windows calls the device.
    /// </summary>
    public IReadOnlyList<Device> Devices()
    {
        var list = new List<Device> { new(null, "System default") };
        try
        {
            using var probe = new MediaPlayer(AppServices.LibVlc);
            foreach (var d in probe.AudioOutputDeviceEnum)
            {
                if (string.IsNullOrEmpty(d.DeviceIdentifier)) continue;
                var name = string.IsNullOrWhiteSpace(d.Description) ? d.DeviceIdentifier : d.Description;
                list.Add(new Device(d.DeviceIdentifier, name));
            }
        }
        catch
        {
            // No enumeration available (rare) — the default still works.
        }
        return list;
    }

    /// <summary>Bind the live player so device changes apply to it at once.</summary>
    public void Attach(MediaPlayer player)
    {
        _current = player;
        ApplyTo(player);
    }

    public void Detach(MediaPlayer player)
    {
        if (ReferenceEquals(_current, player)) _current = null;
    }

    /// <summary>Remember the chosen device and route the current film to it.</summary>
    public void Select(string? deviceId)
    {
        AppServices.Settings.AudioOutputDevice = deviceId;
        AppServices.Settings.Save();
        if (_current is not null) ApplyTo(_current);
    }

    /// <summary>Point a player at the saved device. No-op for "system default".</summary>
    public void ApplyTo(MediaPlayer player)
    {
        var id = AppServices.Settings.AudioOutputDevice;
        if (string.IsNullOrEmpty(id)) return;
        try { player.SetOutputDevice(id, null); }
        catch { /* device unplugged since it was chosen — falls back to default */ }
    }
}
