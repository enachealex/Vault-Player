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
    private record AuthResp(string AccessToken, string RefreshToken, long ExpiresAt, string DisplayName, string Email);
    private record LibDto(string FilmKey, string Name, long ResumeMs, int WatchCount,
        long LastWatchedAt, string? ChaptersJson, long UpdatedAt);

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

    /// <summary>Raised on sign-in, sign-out, or after a sync changes the library.</summary>
    public event Action? Changed;

    private string BaseUrl => (AppServices.Settings.AuthServer ?? DefaultServer).TrimEnd('/');

    // ---- Auth --------------------------------------------------------------

    public enum AuthStatus { Success, NeedsVerification, Error }
    public record AuthOutcome(AuthStatus Status, string? Message = null, string? Email = null);

    /// <summary>Create an account. Success means "check your email for a code".</summary>
    public async Task<AuthOutcome> RegisterAsync(string email, string password, string displayName)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/register",
                new { email, password, displayName }, Json);
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            // Backend replies { status: "verify", email } — no tokens yet.
            return new(AuthStatus.NeedsVerification, Email: email);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    public async Task<AuthOutcome> LoginAsync(string email, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/login", new { email, password }, Json);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new(AuthStatus.NeedsVerification, Email: email); // unverified account
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            return await FinishSignInAsync(resp);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    /// <summary>Confirm the emailed code; on success the user is signed in.</summary>
    public async Task<AuthOutcome> VerifyAsync(string email, string code)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/verify", new { email, code }, Json);
            if (!resp.IsSuccessStatusCode)
                return new(AuthStatus.Error, await ReadErrorAsync(resp));
            return await FinishSignInAsync(resp);
        }
        catch (Exception ex)
        {
            return new(AuthStatus.Error, $"Couldn't reach the server. {ex.Message}");
        }
    }

    public async Task ResendCodeAsync(string email)
    {
        try { await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/resend", new { email }, Json); }
        catch { /* best effort */ }
    }

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
        s.RefreshToken = s.AccountEmail = s.AccountName = null;
        _accessToken = null;
        s.Save();
        Changed?.Invoke();
        if (refresh is not null)
            _ = _http.PostAsJsonAsync($"{BaseUrl}/api/v1/logout", new { refreshToken = refresh }, Json);
    }

    private void StoreSession(AuthResp auth)
    {
        var s = AppServices.Settings;
        s.RefreshToken = auth.RefreshToken;
        s.AccountEmail = auth.Email;
        s.AccountName = auth.DisplayName;
        _accessToken = auth.AccessToken;
        _accessExpiresAt = auth.ExpiresAt;
        s.Save();
    }

    /// <summary>Valid access token, refreshing via the stored refresh token if needed.</summary>
    private async Task<string?> AccessTokenAsync()
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
