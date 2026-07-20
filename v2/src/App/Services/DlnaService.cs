using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace VideoPlayer.App.Services;

/// <summary>A DLNA MediaRenderer found on the LAN (LG TV, Xbox Media Player…).</summary>
public sealed record DlnaRenderer(string Name, Uri ControlUrl);

/// <summary>
/// DLNA/UPnP support: SSDP discovery of MediaRenderers and the AVTransport
/// SOAP verbs needed to push a URL to a TV and remote-control playback.
/// </summary>
public static class DlnaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // ---- Discovery (SSDP M-SEARCH) ----------------------------------------

    public static async Task<List<DlnaRenderer>> DiscoverAsync(int seconds = 4, CancellationToken ct = default)
    {
        var locations = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        // Multi-homed PCs (VPNs, VirtualBox/Hyper-V adapters) route multicast
        // unpredictably from a single any-bound socket — send the M-SEARCH out
        // of EVERY usable IPv4 interface and listen on each.
        var sockets = new List<UdpClient>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    var udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(addr.Address, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        addr.Address.GetAddressBytes());
                    sockets.Add(udp);
                }
                catch
                {
                    // Interface refused the bind — skip it.
                }
            }
        }
        if (sockets.Count == 0)
        {
            var fallback = new UdpClient(AddressFamily.InterNetwork);
            fallback.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            sockets.Add(fallback);
        }

        try
        {
            var search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n");
            var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            // Re-burst the search throughout the listen window — some TVs
            // (LG standby) miss or ignore the first packets.
            _ = Task.Run(async () =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    foreach (var udp in sockets)
                    {
                        try { await udp.SendAsync(search, search.Length, multicast); } catch { }
                    }
                    try { await Task.Delay(900, ct); } catch { break; }
                }
            }, ct);
            var listeners = sockets.Select(async udp =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(remaining);
                    try
                    {
                        var result = await udp.ReceiveAsync(timeout.Token);
                        var text = Encoding.ASCII.GetString(result.Buffer);
                        var m = Regex.Match(text, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase);
                        if (m.Success) locations.TryAdd(m.Groups[1].Value.Trim(), 0);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break; // socket torn down
                    }
                }
            }).ToArray();
            await Task.WhenAll(listeners);
        }
        finally
        {
            foreach (var udp in sockets) udp.Dispose();
        }

        // Resolve each device description into a friendly renderer entry.
        var renderers = new List<DlnaRenderer>();
        foreach (var location in locations.Keys)
        {
            try
            {
                var renderer = await DescribeAsync(location, ct);
                if (renderer is not null && renderers.All(r => r.ControlUrl != renderer.ControlUrl))
                    renderers.Add(renderer);
            }
            catch
            {
                // Unreachable/malformed device: skip.
            }
        }
        return renderers;
    }

    private static async Task<DlnaRenderer?> DescribeAsync(string location, CancellationToken ct)
    {
        var xml = await Http.GetStringAsync(location, ct);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";

        var name = doc.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? "DLNA device";
        var avTransport = doc.Descendants(ns + "service")
            .FirstOrDefault(s => (s.Element(ns + "serviceType")?.Value ?? "").StartsWith("urn:schemas-upnp-org:service:AVTransport"));
        var controlPath = avTransport?.Element(ns + "controlURL")?.Value;
        if (controlPath is null) return null;

        var baseUri = new Uri(location);
        var control = Uri.TryCreate(controlPath, UriKind.Absolute, out var abs) ? abs : new Uri(baseUri, controlPath);
        return new DlnaRenderer(name, control);
    }

    // ---- AVTransport control ----------------------------------------------

    public static Task SetUriAndPlayAsync(DlnaRenderer device, string mediaUrl, string title, CancellationToken ct = default)
        => SetUriThenPlay(device, mediaUrl, title, ct);

    private static async Task SetUriThenPlay(DlnaRenderer device, string mediaUrl, string title, CancellationToken ct)
    {
        var didl =
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
            $"<item id=\"0\" parentID=\"-1\" restricted=\"1\"><dc:title>{SecurityElement.Escape(title)}</dc:title>" +
            "<upnp:class>object.item.videoItem.movie</upnp:class>" +
            $"<res protocolInfo=\"http-get:*:video/mp4:DLNA.ORG_OP=01\">{SecurityElement.Escape(mediaUrl)}</res>" +
            "</item></DIDL-Lite>";

        await InvokeAsync(device, "SetAVTransportURI",
            $"<CurrentURI>{SecurityElement.Escape(mediaUrl)}</CurrentURI>" +
            $"<CurrentURIMetaData>{SecurityElement.Escape(didl)}</CurrentURIMetaData>", ct);
        await InvokeAsync(device, "Play", "<Speed>1</Speed>", ct);
    }

    public static Task PlayAsync(DlnaRenderer device, CancellationToken ct = default)
        => InvokeAsync(device, "Play", "<Speed>1</Speed>", ct);

    public static Task PauseAsync(DlnaRenderer device, CancellationToken ct = default)
        => InvokeAsync(device, "Pause", "", ct);

    public static Task StopAsync(DlnaRenderer device, CancellationToken ct = default)
        => InvokeAsync(device, "Stop", "", ct);

    public static Task SeekAsync(DlnaRenderer device, TimeSpan target, CancellationToken ct = default)
        => InvokeAsync(device, "Seek",
            $"<Unit>REL_TIME</Unit><Target>{target:h\\:mm\\:ss}</Target>", ct);

    /// <summary>Current position/duration on the renderer, or null if unavailable.</summary>
    public static async Task<(TimeSpan Position, TimeSpan Duration)?> GetPositionAsync(DlnaRenderer device, CancellationToken ct = default)
    {
        try
        {
            var body = await InvokeAsync(device, "GetPositionInfo", "", ct);
            var doc = XDocument.Parse(body);
            var rel = doc.Descendants("RelTime").FirstOrDefault()?.Value;
            var dur = doc.Descendants("TrackDuration").FirstOrDefault()?.Value;
            if (TimeSpan.TryParse(rel, out var position) && TimeSpan.TryParse(dur, out var duration))
                return (position, duration);
        }
        catch
        {
        }
        return null;
    }

    private static async Task<string> InvokeAsync(DlnaRenderer device, string action, string argsXml, CancellationToken ct)
    {
        const string serviceType = "urn:schemas-upnp-org:service:AVTransport:1";
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{serviceType}\"><InstanceID>0</InstanceID>{argsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, device.ControlUrl)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return body;
    }
}
