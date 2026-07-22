using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoPlayer.App.Services;

/// <summary>
/// Account-to-account friends and Watch Party invitations. Layers on top of the
/// optional account: signed out there are no friends, so everything here quietly
/// no-ops. Reuses <see cref="AccountService"/> for tokens and the server URL.
/// </summary>
public class FriendsService
{
    // Wire shapes, matching the backend's /api/v1 friend DTOs.
    public record Person(string UserId, string Name, string Email, long RequestId = 0);
    public record Invite(long Id, string FromName, string RoomCode, string Server, string MovieTitle, long CreatedAt);
    private record FriendsResp(Person[] Friends, Person[] Incoming, Person[] Outgoing);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly AccountService _account;

    public FriendsService(AccountService account) => _account = account;

    public IReadOnlyList<Person> Friends { get; private set; } = Array.Empty<Person>();
    public IReadOnlyList<Person> Incoming { get; private set; } = Array.Empty<Person>();
    public IReadOnlyList<Person> Outgoing { get; private set; } = Array.Empty<Person>();
    public IReadOnlyList<Invite> Invites { get; private set; } = Array.Empty<Invite>();

    /// <summary>Raised whenever the friends list, requests, or invites change.</summary>
    public event Action? Changed;

    // ---- Friends -----------------------------------------------------------

    /// <summary>Refresh friends + pending requests + party invites in two calls.</summary>
    public async Task RefreshAsync()
    {
        if (!_account.IsSignedIn)
        {
            Friends = Incoming = Outgoing = Array.Empty<Person>();
            Invites = Array.Empty<Invite>();
            Changed?.Invoke();
            return;
        }

        var friends = await GetAsync<FriendsResp>("/api/v1/friends");
        if (friends is not null)
        {
            Friends = friends.Friends ?? Array.Empty<Person>();
            Incoming = friends.Incoming ?? Array.Empty<Person>();
            Outgoing = friends.Outgoing ?? Array.Empty<Person>();
        }
        var invites = await GetAsync<Invite[]>("/api/v1/party/invites");
        if (invites is not null) Invites = invites;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ask someone to be your friend by email. Returns null on success, or a
    /// human-readable reason it couldn't be sent.
    /// </summary>
    public async Task<string?> AddByEmailAsync(string email)
    {
        var (ok, error) = await PostAsync("/api/v1/friends/request", new { email });
        if (ok) await RefreshAsync();
        return ok ? null : error;
    }

    public async Task RespondAsync(long requestId, bool accept)
    {
        await PostAsync("/api/v1/friends/respond", new { requestId, accept });
        await RefreshAsync();
    }

    public async Task RemoveAsync(string userId)
    {
        await PostAsync("/api/v1/friends/remove", new { userId });
        await RefreshAsync();
    }

    // ---- Party invites -----------------------------------------------------

    /// <summary>Invite a friend into the room being hosted right now.</summary>
    public async Task<string?> InviteAsync(string toUserId, string roomCode, string server, string movieTitle)
    {
        var (ok, error) = await PostAsync("/api/v1/party/invite",
            new { toUserId, roomCode, server, movieTitle });
        return ok ? null : error;
    }

    /// <summary>Just pull invites (cheap poll while sitting on the party screen).</summary>
    public async Task RefreshInvitesAsync()
    {
        if (!_account.IsSignedIn) return;
        var invites = await GetAsync<Invite[]>("/api/v1/party/invites");
        if (invites is not null) { Invites = invites; Changed?.Invoke(); }
    }

    public async Task DismissInviteAsync(long id)
    {
        await PostAsync("/api/v1/party/invites/dismiss", new { id });
        Invites = Invites.Where(i => i.Id != id).ToArray();
        Changed?.Invoke();
    }

    // ---- HTTP plumbing -----------------------------------------------------

    private async Task<T?> GetAsync<T>(string path) where T : class
    {
        var token = await _account.AccessTokenAsync();
        if (token is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_account.BaseUrl}{path}");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<T>(Json);
        }
        catch { return null; }   // offline: keep the last known state
    }

    private async Task<(bool ok, string? error)> PostAsync(string path, object body)
    {
        var token = await _account.AccessTokenAsync();
        if (token is null) return (false, "You need to be signed in.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_account.BaseUrl}{path}")
            { Content = JsonContent.Create(body, options: Json) };
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't reach the server. {ex.Message}");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? "That didn't work.";
        }
        catch { }
        return "That didn't work.";
    }
}
