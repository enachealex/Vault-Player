using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace VideoPlayer.App.Services;

/// <summary>A device playback can be sent to.</summary>
public abstract record CastTarget(string Name);

/// <summary>Chromecast/Google TV class device, driven by libVLC's renderer system.</summary>
public sealed record VlcTarget(string Name, RendererItem Item) : CastTarget(Name);

/// <summary>DLNA MediaRenderer (LG webOS TVs, Xbox Media Player, AirScreen…).</summary>
public sealed record DlnaTarget(string Name, DlnaRenderer Renderer) : CastTarget(Name);

/// <summary>
/// Cast-device discovery across two protocols: libVLC renderers (Chromecast
/// family, mDNS) and DLNA MediaRenderers (SSDP — the protocol LG smart TVs and
/// Xbox speak out of the box).
/// </summary>
public class CastService : IDisposable
{
    private RendererDiscoverer? _discoverer;
    private bool _dlnaScanRunning;

    /// <summary>Devices found on the network (UI-thread collection).</summary>
    public ObservableCollection<CastTarget> Devices { get; } = new();

    /// <summary>The target playback should use; null = this computer.</summary>
    public CastTarget? Active { get; set; }

    public bool Started { get; private set; }

    public void EnsureStarted()
    {
        if (!Started)
        {
            Started = true;
            StartVlcDiscovery();
        }
        // Re-scan DLNA every time a player opens — TVs come and go.
        _ = RefreshDlnaAsync();
    }

    private void StartVlcDiscovery()
    {
        var service = AppServices.LibVlc.RendererList.FirstOrDefault().Name;
        if (string.IsNullOrEmpty(service)) return;

        _discoverer = new RendererDiscoverer(AppServices.LibVlc, service);
        _discoverer.ItemAdded += (_, e) => OnUi(() =>
        {
            if (Devices.All(d => d.Name != e.RendererItem.Name))
                Devices.Add(new VlcTarget(e.RendererItem.Name, e.RendererItem));
        });
        _discoverer.ItemDeleted += (_, e) => OnUi(() =>
        {
            var existing = Devices.OfType<VlcTarget>().FirstOrDefault(d => d.Name == e.RendererItem.Name);
            if (existing is not null) Devices.Remove(existing);
            if (Active == existing) Active = null;
        });
        _discoverer.Start();
    }

    /// <summary>
    /// Search again on demand. A TV that was asleep a moment ago answers as
    /// soon as it wakes, and making the user reopen the player to trigger a
    /// fresh scan is a poor way to discover that.
    /// </summary>
    public void Rescan() => _ = RefreshDlnaAsync();

    /// <summary>SSDP scan for DLNA renderers; safe to call repeatedly (e.g. on menu open).</summary>
    public async Task RefreshDlnaAsync()
    {
        if (_dlnaScanRunning) return;
        _dlnaScanRunning = true;
        try
        {
            var found = await Task.Run(() => DlnaService.DiscoverAsync(4));
            OnUi(() =>
            {
                foreach (var renderer in found)
                {
                    // Dedupe by name AND by control URL *value* (Uri != is a
                    // reference comparison — it re-added the same TV each scan).
                    var duplicate = Devices.OfType<DlnaTarget>().Any(d =>
                        d.Name == renderer.Name ||
                        d.Renderer.ControlUrl.AbsoluteUri == renderer.ControlUrl.AbsoluteUri);
                    if (!duplicate) Devices.Add(new DlnaTarget(renderer.Name, renderer));
                }
            });
        }
        catch
        {
            // Network hiccup during scan — devices list simply stays as-is.
        }
        finally
        {
            _dlnaScanRunning = false;
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _discoverer?.Stop();
        _discoverer?.Dispose();
        _discoverer = null;
        Started = false;
    }
}
