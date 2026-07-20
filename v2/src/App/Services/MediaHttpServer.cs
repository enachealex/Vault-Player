using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer.App.Services;

/// <summary>
/// Minimal LAN HTTP server that serves exactly one video file per session so a
/// DLNA renderer (TV/Xbox) can pull it. Supports HEAD and byte ranges (TVs
/// seek via ranges) plus the DLNA headers picky renderers ask for. Built on
/// TcpListener: no admin URL-ACLs, no framework dependencies.
/// </summary>
public sealed class MediaHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _filePath;
    private string _token = "";

    public string? Url { get; private set; }

    /// <summary>Start serving <paramref name="filePath"/>; returns the LAN URL for the renderer.</summary>
    public string Start(string filePath)
    {
        Stop();
        _filePath = filePath;
        _token = Guid.NewGuid().ToString("N");
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_listener, _cts.Token);

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        Url = $"http://{GetLanAddress()}:{port}/{_token}/video.{ext}";
        return Url;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        Url = null;
    }

    public void Dispose() => Stop();

    /// <summary>The LAN IPv4 the TV can reach us on (route-table trick, no traffic sent).</summary>
    public static IPAddress GetLanAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch
        {
            // Listener stopped.
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();

            // Read request head.
            var head = await ReadHeadAsync(stream, ct);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');
            if (request.Length < 2) return;
            var method = request[0];
            var target = request[1];

            var file = _filePath;
            if (file is null || !target.Contains(_token, StringComparison.Ordinal))
            {
                await WriteSimpleAsync(stream, "404 Not Found", ct);
                return;
            }

            var info = new FileInfo(file);
            long start = 0, end = info.Length - 1;
            var isRange = false;
            foreach (var line in lines)
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"bytes=(\d*)-(\d*)");
                if (!m.Success) continue;
                isRange = true;
                if (m.Groups[1].Value.Length > 0)
                {
                    start = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value.Length > 0) end = Math.Min(long.Parse(m.Groups[2].Value), info.Length - 1);
                }
                else if (m.Groups[2].Value.Length > 0)
                {
                    start = Math.Max(0, info.Length - long.Parse(m.Groups[2].Value));
                }
            }
            if (start > end || start >= info.Length)
            {
                await WriteSimpleAsync(stream, "416 Range Not Satisfiable", ct);
                return;
            }

            var mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".ts" => "video/mp2t",
                _ => "application/octet-stream",
            };

            var sb = new StringBuilder();
            sb.Append(isRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append($"Content-Type: {mime}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append($"Content-Length: {end - start + 1}\r\n");
            if (isRange) sb.Append($"Content-Range: bytes {start}-{end}/{info.Length}\r\n");
            // DLNA niceties: declare range-seek support to picky renderers.
            sb.Append("contentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0\r\n");
            sb.Append("transferMode.dlna.org: Streaming\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var header = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(header, ct);

            if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            fs.Seek(start, SeekOrigin.Begin);
            var remaining = end - start + 1;
            var buffer = new byte[1 << 16];
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        catch
        {
            // Client hung up (normal on seeks) or we're shutting down.
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var total = 0;
        while (total < buf.Length)
        {
            var read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (read <= 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buf, 0, total);
            var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0) return text[..idx];
        }
        return null;
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, string status, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, ct);
    }
}
