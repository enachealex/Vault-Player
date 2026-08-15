using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Consumes LAN Party's single sign-on assertion (see LAN Party's
/// docs/VAULT-PLAYER-INTEGRATION.md, and our docs/LANPARTY-INTEGRATION.md).
///
/// Verification lives HERE, on the relay, and never in the desktop app: the
/// shared secret cannot be shipped inside a binary that anyone can read. The
/// app treats the assertion as opaque and forwards it.
///
/// The assertion is deliberately not LAN Party's session token — it only states
/// who someone is, is signed with a different secret, and lives five minutes.
/// </summary>
public class LanPartySso(IConfiguration cfg, IHttpClientFactory http, ILogger<LanPartySso> log)
{
    public const string Provider = "lanparty";
    private const string Issuer = "lanparty";
    private const string Audience = "vault-player";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string? Secret => cfg["VAULT_SSO_SECRET"];

    /// <summary>
    /// Where profile lookups go. Compiled-in default; overridable for testing.
    /// Never taken from the token — a claim must not be able to redirect us.
    /// </summary>
    public string Origin =>
        (cfg["LANPARTY_ORIGIN"] ?? "https://lanparty.thejumpvault.com").TrimEnd('/');

    /// <summary>False when no secret is configured; the whole feature then no-ops.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Secret);

    /// <summary>
    /// Validate the assertion. Returns null claims plus a reason when it fails.
    /// Algorithm is pinned — the token header's own "alg" is never trusted.
    /// </summary>
    public (ClaimsPrincipal? Claims, string? Error) Verify(string assertion)
    {
        if (!Enabled) return (null, "LAN Party sign-in isn't configured on this server.");
        if (string.IsNullOrWhiteSpace(assertion)) return (null, "Missing assertion.");

        var handler = new JwtSecurityTokenHandler
        {
            // Keep claim names as issued, so "sub"/"jti" read as themselves
            // rather than being rewritten to the long ClaimTypes URIs.
            MapInboundClaims = false,
        };
        try
        {
            var principal = handler.ValidateToken(assertion, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret!)),
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            }, out _);
            return (principal, null);
        }
        catch (SecurityTokenExpiredException)
        {
            return (null, "That sign-in link has expired. Open it again from LAN Party.");
        }
        catch (Exception ex)
        {
            log.LogWarning("LAN Party assertion rejected: {Message}", ex.Message);
            return (null, "That sign-in link isn't valid.");
        }
    }

    /// <summary>
    /// Profile + friends for the asserted user, server-to-server, using the same
    /// assertion as the bearer. Called once per sign-in — never polled; LAN Party
    /// rate-limits this route and the data is explicitly a snapshot.
    /// </summary>
    public async Task<LanPartyProfile?> FetchProfileAsync(string assertion, CancellationToken ct = default)
    {
        try
        {
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Origin}/integrations/vault/userinfo");
            req.Headers.Authorization = new("Bearer", assertion);

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("LAN Party userinfo returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<UserInfoDto>(Json, ct);
            if (dto?.User is null) return null;

            return new LanPartyProfile(
                ToPerson(dto.User),
                (dto.Friends ?? Array.Empty<PersonDto>()).Select(ToPerson).ToArray());
        }
        catch (Exception ex)
        {
            // Sign-in still succeeds without the profile; the app just shows the
            // username from the token and an empty LAN Party friends group.
            log.LogWarning("LAN Party userinfo failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Avatars come back as site-relative paths; make them usable anywhere.</summary>
    private LanPartyPerson ToPerson(PersonDto p) => new(
        p.Username ?? "",
        string.IsNullOrWhiteSpace(p.DisplayName) ? p.Username ?? "" : p.DisplayName,
        string.IsNullOrWhiteSpace(p.AvatarUrl) ? null
            : p.AvatarUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? p.AvatarUrl
                : Origin + (p.AvatarUrl!.StartsWith('/') ? p.AvatarUrl : "/" + p.AvatarUrl),
        p.AvatarColor,
        p.Status);

    private record PersonDto(string? Username, string? DisplayName, string? AvatarUrl,
        string? AvatarColor, string? Status, string? Email);
    private record UserInfoDto(string? Issuer, PersonDto? User, PersonDto[]? Friends);
}
