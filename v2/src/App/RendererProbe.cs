using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using LibVLCSharp.Shared;

namespace VideoPlayer.App;

/// <summary>
/// Headless cast-device scan: `--list-renderers [out.json] [seconds]`.
/// Runs libVLC renderer discovery and reports what it finds on the LAN.
/// </summary>
public static class RendererProbe
{
    public static int Run(string outPath, int seconds)
    {
        var report = new Dictionary<string, object?> { ["startedAt"] = DateTime.Now.ToString("o") };
        try
        {
            Core.Initialize();
            using var libvlc = new LibVLC();
            var services = libvlc.RendererList.Select(d => new { d.Name, d.LongName }).ToArray();
            report["discoverers"] = services;

            var devices = new List<object>();
            var gate = new object();
            if (services.Length > 0)
            {
                using var rd = new RendererDiscoverer(libvlc, services[0].Name);
                rd.ItemAdded += (_, e) =>
                {
                    lock (gate)
                    {
                        devices.Add(new
                        {
                            name = e.RendererItem.Name,
                            type = e.RendererItem.Type,
                            video = e.RendererItem.CanRenderVideo,
                            audio = e.RendererItem.CanRenderAudio,
                        });
                    }
                };
                report["discoveryStarted"] = rd.Start();
                Thread.Sleep(seconds * 1000);
                rd.Stop();
            }
            report["devices"] = devices;
            report["pass"] = true;
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            report["pass"] = false;
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
