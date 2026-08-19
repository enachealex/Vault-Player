using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// The Vault library: films that live on the server rather than on this machine.
///
/// Everything else in the app plays files you already have — your folder, a
/// party host's copy pushed to you. This is the one source where the server
/// itself holds the film and streams it on demand, so a signed-in machine has a
/// library without a single byte of it stored locally.
///
/// Requires an account, and degrades to nothing without one: signed out, or
/// offline, the library is simply the films in your folder.
/// </summary>
public class CatalogService(AccountService account)
{
    // Wire shapes, matching the backend's /api/v1/catalog DTOs.
    private record CatalogItemDto(string ItemKey, string Name, long SizeBytes, long? DurationMs,
        string ContentType, string FilmKey, bool WebPlayable, string? VideoCodec, string? AudioCodec,
        string StreamUrl, string? ArtUrl, long AddedAt);
    private record CatalogDto(CatalogItemDto[] Items, bool Scanning, long LastScanAt, bool MediaRootPresent);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Films are large and posters are small; the long timeout is for the fetch
    // of the catalogue itself, which waits on a scan only on a cold server.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Last fetched library, so navigating back doesn't refetch.</summary>
    public IReadOnlyList<MovieItem> Items { get; private set; } = Array.Empty<MovieItem>();

    /// <summary>Whether the server is mid-scan; the UI says so rather than claiming an empty library.</summary>
    public bool ServerScanning { get; private set; }

    /// <summary>Set when the last fetch failed, for a message the user can act on.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when <see cref="Items"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Fetch the server library. Returns an empty list when signed out — the
    /// catalogue is an account feature, and this is not an error worth showing.
    /// </summary>
    public async Task<IReadOnlyList<MovieItem>> RefreshAsync(CancellationToken ct = default)
    {
        LastError = null;
        if (!account.IsSignedIn)
        {
            Items = Array.Empty<MovieItem>();
            return Items;
        }

        var token = await account.AccessTokenAsync();
        if (token is null)
        {
            // Offline, or the session expired. The folder library still works,
            // so this stays quiet rather than throwing a dialog at someone.
            Items = Array.Empty<MovieItem>();
            return Items;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{account.BaseUrl}/api/v1/catalog");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req, ct);

            // An older relay simply has no catalogue. Report nothing rather than
            // an error: the app must keep working against a server that predates
            // this feature.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Items = Array.Empty<MovieItem>();
                return Items;
            }
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"The server couldn't list its library ({(int)resp.StatusCode}).";
                Items = Array.Empty<MovieItem>();
                return Items;
            }

            var dto = await resp.Content.ReadFromJsonAsync<CatalogDto>(Json, ct);
            if (dto is null)
            {
                LastError = "The server returned an unexpected response.";
                Items = Array.Empty<MovieItem>();
                return Items;
            }

            ServerScanning = dto.Scanning;
            Items = dto.Items.Select(x => new MovieItem
            {
                Name = x.Name,
                // Absolute here, relative on the wire: only this end knows which
                // address it reached the server on.
                Path = account.BaseUrl + x.StreamUrl,
                SizeBytes = x.SizeBytes,
                Kind = MovieKind.Server,
                ItemKey = x.ItemKey,
                ServerFilmKey = x.FilmKey,
                ArtUrl = x.ArtUrl is null ? null : account.BaseUrl + x.ArtUrl,
                DurationMs = x.DurationMs,
            }).ToList();

            Changed?.Invoke();
            return Items;
        }
        catch (OperationCanceledException)
        {
            Items = Array.Empty<MovieItem>();
            return Items;
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't reach the server. {ex.Message}";
            Items = Array.Empty<MovieItem>();
            return Items;
        }
    }

    /// <summary>Ask the server to re-index its media disk. Fire and forget.</summary>
    public async Task RequestRescanAsync()
    {
        var token = await account.AccessTokenAsync();
        if (token is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{account.BaseUrl}/api/v1/catalog/rescan");
            req.Headers.Authorization = new("Bearer", token);
            await _http.SendAsync(req);
        }
        catch
        {
            // Best effort; the next refresh shows whatever actually changed.
        }
    }

    /// <summary>
    /// Fill in posters. Sequential and cached on disk, mirroring how local
    /// thumbnails behave — a library of a few hundred films should not open a
    /// few hundred simultaneous connections to a home server.
    /// </summary>
    public async Task LoadArtAsync(IEnumerable<MovieItem> items, CancellationToken ct)
    {
        var token = await account.AccessTokenAsync();
        if (token is null) return;

        Directory.CreateDirectory(CacheDir);
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            if (item.ArtUrl is null || item.Thumbnail is not null || item.ItemKey is null) continue;

            var cached = System.IO.Path.Combine(CacheDir, item.ItemKey + ".jpg");
            try
            {
                if (!File.Exists(cached))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, item.ArtUrl);
                    req.Headers.Authorization = new("Bearer", token);
                    var resp = await _http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length == 0) continue;
                    await File.WriteAllBytesAsync(cached, bytes, ct);
                }
                item.Thumbnail = LoadFrozen(cached);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A missing poster is a blank card, not a broken library.
            }
        }
    }

    private static string CacheDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayerV2", "vault-art");

    /// <summary>
    /// Decode to a frozen bitmap so it can cross to the UI thread, with OnLoad
    /// so the cache file is not left open by the image.
    /// </summary>
    private static ImageSource LoadFrozen(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
