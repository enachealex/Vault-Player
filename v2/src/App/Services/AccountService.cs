using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// The optional account: sign in, stay signed in, and sync library activity
/// (resume points, watch counts, chapters) across machines. Everything the app
/// does works signed out; this only adds the cross-device layer.
/// </summary>
public class AccountService
{
    // Wire shapes, matching the backend's /api/v1 DTOs.
    private record AuthResp(string AccessToken, string RefreshToken, long ExpiresAt, string DisplayName,
        string Email, string Username, string Status, bool IsOwner);
    private record LibDto(string FilmKey, string Name, long ResumeMs, int WatchCount,
        long LastWatchedAt, string? ChaptersJson, long UpdatedAt);
    public record CaptchaChallenge(string Id, byte[] Png);
    public record ManagedUser(string UserId, string Username, string DisplayName, string Email,
        string Status, bool IsOwner, long CreatedAt);
    public record LibraryInvite(string Code, string Url, long CreatedAt, long ExpiresAt,
        bool Revoked, string UsedByName, long UsedAt);

    // Points at the deployed relay host once the API route is live. Overridable
    // via Settings.AuthServer (used to test against a local backend).
    private const string DefaultServer = "https://party.thejumpvault.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string? _accessToken;
    private long _accessExpiresAt;
    private System.Threading.Timer? _syncDebounce;

    public bool IsSignedIn => AppServices.Settings.RefreshToken is not null;
    public string? Name => AppServices.Settings.AccountName;
    public string? Email => AppServices.Settings.AccountEmail;
    public string? Username => AppServices.Settings.AccountUsername;
    /// <summary>"pending" | "approved" | "blocked", as of the last sign-in or refresh.</summary>
    public string? Status => AppServices.Settings.AccountStatus;
    /// <summary>Whether this is the server owner's account — gates upload and user management.</summary>
    public bool IsOwner => AppServices.Settings.AccountIsOwner;

    /// <summary>Raised on sign-in, sign-out, or after a sync changes the library.</summary>
    public event Action? Changed;

    // A blank AuthServer (not just null) must fall back to the default — an
    // empty string here would make every request URI relative and fail with
    // "an invalid request URI was provided".
    internal string BaseUrl
    {
        get
        {
            var server = AppServices.Settings.AuthServer;
            return (string.IsNullOrWhiteSpace(server) ? DefaultServer : server).TrimEnd('/');
        }
    }

    // ---- Auth --------------------------------------------------------------

    public enum AuthStatus { Success, Error }
    public record AuthOutcome(AuthStatus Status, string? Message = null);

    /// <summary>A fresh sign-up challenge image, or null when the server is unreachable.</summary>
    public async Task<CaptchaChallenge?> FetchCaptchaAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/api/v1/captcha");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return new CaptchaChallenge(
                doc.RootElement.GetProperty("id").GetString() ?? "",
                Convert.FromBase64String(doc.RootElement.GetProperty("image").GetString() ?? ""));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Create an account. Success means signed in — approval gates the library,
    /// not the account. A valid invite code IS the approval, granted up front.
    /// </summary>
    public async Task<AuthOutcome> RegisterAsync(string username, string password, string? email,
        string captchaId, string captchaAnswer, string? inviteCode = null)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/register",
                new { username, password, email, captchaId, captchaAnswer, inviteCode }, Json);
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            return await FinishSignInAsync(resp);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    /// <summary>
    /// Trade a device-link code (typed off the /link page on a phone) for a
    /// session. What TVs use; on the desktop it means never typing the
    /// password on a shared machine.
    /// </summary>
    public async Task<AuthOutcome> LoginWithLinkCodeAsync(string code)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/link/claim", new { code }, Json);
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            return await FinishSignInAsync(resp);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    public async Task<AuthOutcome> LoginAsync(string username, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/login", new { username, password }, Json);
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            return await FinishSignInAsync(resp);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    /// <summary>
    /// Sign in as a LAN Party user, using the assertion that arrived on the
    /// launch link. The assertion is opaque to us: the relay holds the shared
    /// secret, verifies it, and answers with an ordinary Vault session — so this
    /// is just a different way of arriving at the same signed-in state.
    /// </summary>
    public async Task<AuthOutcome> SignInWithLanPartyAsync(string assertion)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/auth/lanparty",
                new { assertion }, Json);

            // 409: an existing Vault account already uses that email address. We
            // deliberately do NOT merge — LAN Party doesn't verify addresses, so
            // linking on one would hand over somebody else's library.
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
                return new(AuthStatus.Error,
                    "A Vault Movies account already uses that email. Sign in to it here first, "
                    + "then link LAN Party from your account.");

            if (!resp.IsSuccessStatusCode) return new(AuthStatus.Error, await ReadErrorAsync(resp));

            var lp = await resp.Content.ReadFromJsonAsync<LanPartyAuthResp>(Json);
            if (lp is null) return new(AuthStatus.Error, "The server returned an unexpected response.");

            StoreSession(new AuthResp(lp.AccessToken, lp.RefreshToken, lp.ExpiresAt, lp.DisplayName,
                lp.Email, lp.Username, lp.Status, lp.IsOwner));
            LanParty = lp.LanParty;
            AppServices.Settings.LanPartyUsername = lp.LanParty?.Username;
            AppServices.Settings.Save();
            Changed?.Invoke();
            _ = SyncAsync();
            return new(AuthStatus.Success);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    /// <summary>
    /// Who LAN Party says we are, plus their friends — a snapshot taken at
    /// sign-in, never polled. Null when we didn't arrive via LAN Party.
    /// </summary>
    public LanPartyIdentity? LanParty { get; private set; }

    public record LanPartyPerson(string Username, string DisplayName, string? AvatarUrl,
        string? AvatarColor, string? Status);
    public record LanPartyIdentity(string Username, string DisplayName, string? AvatarUrl,
        string? AvatarColor, string? Status, LanPartyPerson[] Friends);
    private record LanPartyAuthResp(string AccessToken, string RefreshToken, long ExpiresAt,
        string DisplayName, string Email, string Username, string Status, bool IsOwner,
        LanPartyIdentity? LanParty);

    /// <summary>Request a reset code. Always reports success so it can't probe emails.</summary>
    public async Task ForgotAsync(string email)
    {
        try { await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/forgot", new { email }, Json); }
        catch { /* best effort */ }
    }

    /// <summary>Set a new password with the emailed code. Null on success, else an error.</summary>
    public async Task<string?> ResetAsync(string email, string code, string newPassword)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/reset",
                new { email, code, newPassword }, Json);
            return resp.IsSuccessStatusCode ? null : await ReadErrorAsync(resp);
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server. {ex.Message}";
        }
    }

    /// <summary>Delete the account and its synced library, then sign out. Null on success.</summary>
    public async Task<string?> DeleteAccountAsync()
    {
        var token = await AccessTokenAsync();
        if (token is null) return "You need to be signed in.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/delete");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return await ReadErrorAsync(resp);
            AppServices.Settings.SyncedLibrary.Clear();
            SignOut();
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server. {ex.Message}";
        }
    }

    private async Task<AuthOutcome> FinishSignInAsync(HttpResponseMessage resp)
    {
        var auth = await resp.Content.ReadFromJsonAsync<AuthResp>(Json);
        if (auth is null) return new(AuthStatus.Error, "The server returned an unexpected response.");
        StoreSession(auth);
        Changed?.Invoke();
        _ = SyncAsync();      // reconcile immediately after signing in
        return new(AuthStatus.Success);
    }

    public void SignOut()
    {
        var s = AppServices.Settings;
        var refresh = s.RefreshToken;
        s.RefreshToken = s.AccountEmail = s.AccountName = s.LanPartyUsername = null;
        s.AccountUsername = s.AccountStatus = null;
        s.AccountIsOwner = false;
        _accessToken = null;
        LanParty = null;
        s.Save();
        Changed?.Invoke();
        if (refresh is not null)
            _ = _http.PostAsJsonAsync($"{BaseUrl}/api/v1/logout", new { refreshToken = refresh }, Json);
    }

    // ---- Owner-only user management ----------------------------------------

    /// <summary>Everyone with an account, for the owner's manage-users screen. Null = failed.</summary>
    public async Task<List<ManagedUser>?> ListUsersAsync()
    {
        var token = await AccessTokenAsync();
        if (token is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/admin/users");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<ManagedUser>>(Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Approve, block or re-pend an account. Null on success, else an error.</summary>
    public async Task<string?> SetUserStatusAsync(string userId, string status)
    {
        var token = await AccessTokenAsync();
        if (token is null) return "You need to be signed in.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/users/status")
            { Content = JsonContent.Create(new { userId, status }, options: Json) };
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? null : await ReadErrorAsync(resp);
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server. {ex.Message}";
        }
    }

    /// <summary>Mint a fresh single-use invite. Null when it failed.</summary>
    public async Task<LibraryInvite?> CreateInviteAsync()
    {
        var token = await AccessTokenAsync();
        if (token is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/invites");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<LibraryInvite>(Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Recent invites, newest first: open, used-by-whom, revoked. Null = failed.</summary>
    public async Task<List<LibraryInvite>?> ListInvitesAsync()
    {
        var token = await AccessTokenAsync();
        if (token is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/admin/invites");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<LibraryInvite>>(Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Kill an unspent invite. Null on success, else an error.</summary>
    public async Task<string?> RevokeInviteAsync(string code)
    {
        var token = await AccessTokenAsync();
        if (token is null) return "You need to be signed in.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/invites/revoke")
            { Content = JsonContent.Create(new { code }, options: Json) };
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? null : await ReadErrorAsync(resp);
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server. {ex.Message}";
        }
    }

    /// <summary>Delete someone's account and synced data entirely. Null on success.</summary>
    public async Task<string?> RemoveUserAsync(string userId)
    {
        var token = await AccessTokenAsync();
        if (token is null) return "You need to be signed in.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/users/remove")
            { Content = JsonContent.Create(new { userId }, options: Json) };
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? null : await ReadErrorAsync(resp);
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server. {ex.Message}";
        }
    }

    private void StoreSession(AuthResp auth)
    {
        var s = AppServices.Settings;
        s.RefreshToken = auth.RefreshToken;
        s.AccountEmail = auth.Email;
        s.AccountName = auth.DisplayName;
        s.AccountUsername = auth.Username;
        s.AccountStatus = auth.Status;
        s.AccountIsOwner = auth.IsOwner;
        _accessToken = auth.AccessToken;
        _accessExpiresAt = auth.ExpiresAt;
        s.Save();
    }

    /// <summary>Valid access token, refreshing via the stored refresh token if needed.</summary>
    internal async Task<string?> AccessTokenAsync()
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _accessExpiresAt - 60_000)
            return _accessToken;

        var refresh = AppServices.Settings.RefreshToken;
        if (refresh is null) return null;
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/refresh", new { refreshToken = refresh }, Json);
            if (!resp.IsSuccessStatusCode)
            {
                // Refresh token dead/expired: sign out quietly rather than nag.
                if ((int)resp.StatusCode == 401) SignOut();
                return null;
            }
            var auth = await resp.Content.ReadFromJsonAsync<AuthResp>(Json);
            if (auth is null) return null;
            StoreSession(auth);
            return _accessToken;
        }
        catch
        {
            return null;   // offline: keep working locally
        }
    }

    // ---- Library sync ------------------------------------------------------

    /// <summary>Record a local change to a film and schedule a background sync.</summary>
    public void NoteLocalChange(MovieItem movie, long resumeMs, int watchCount,
        long lastWatchedAt, string? chaptersJson)
    {
        if (movie.IsShortcut || movie.SizeBytes <= 0) return;
        var s = AppServices.Settings;
        s.SyncedLibrary[movie.FilmKey] = new SyncedItem
        {
            Name = movie.Name,
            ResumeMs = resumeMs,
            WatchCount = watchCount,
            LastWatchedAt = lastWatchedAt,
            ChaptersJson = chaptersJson,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (IsSignedIn) ScheduleSync();
    }

    /// <summary>Debounce bursts of activity into a single push a few seconds later.</summary>
    private void ScheduleSync()
    {
        _syncDebounce?.Dispose();
        _syncDebounce = new System.Threading.Timer(_ => _ = SyncAsync(), null, 4000, Timeout.Infinite);
    }

    /// <summary>Push local activity, merge the server's reply, apply back locally.</summary>
    public async Task SyncAsync()
    {
        if (!IsSignedIn) return;
        var token = await AccessTokenAsync();
        if (token is null) return;

        var s = AppServices.Settings;
        var payload = new
        {
            items = s.SyncedLibrary.Select(kv => new LibDto(kv.Key, kv.Value.Name, kv.Value.ResumeMs,
                kv.Value.WatchCount, kv.Value.LastWatchedAt, kv.Value.ChaptersJson, kv.Value.UpdatedAt)).ToArray()
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/api/v1/library")
            { Content = JsonContent.Create(payload, options: Json) };
            req.Headers.Authorization = new("Bearer", token);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            var merged = await resp.Content.ReadFromJsonAsync<LibDto[]>(Json);
            if (merged is null) return;

            s.SyncedLibrary = merged.ToDictionary(m => m.FilmKey, m => new SyncedItem
            {
                Name = m.Name, ResumeMs = m.ResumeMs, WatchCount = m.WatchCount,
                LastWatchedAt = m.LastWatchedAt, ChaptersJson = m.ChaptersJson, UpdatedAt = m.UpdatedAt,
            });
            s.Save();
            Changed?.Invoke();
        }
        catch
        {
            // Offline or transient: the local copy is intact, retry next change.
        }
    }

    /// <summary>Overlay synced progress/counts onto scanned films by film-key.</summary>
    public void ApplyTo(IEnumerable<MovieItem> movies)
    {
        var synced = AppServices.Settings.SyncedLibrary;
        foreach (var movie in movies)
        {
            if (movie.IsShortcut) continue;
            if (synced.TryGetValue(movie.FilmKey, out var item))
            {
                if (item.ResumeMs > 0) movie.ResumeMs = item.ResumeMs;
                if (item.WatchCount > movie.WatchCount) movie.WatchCount = item.WatchCount;
            }
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var err)) return err.GetString() ?? "Sign-in failed.";
        }
        catch { }
        return (int)resp.StatusCode == 401 ? "Wrong email or password." : "Sign-in failed.";
    }
}
