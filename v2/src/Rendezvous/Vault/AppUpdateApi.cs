using System.Security.Cryptography;
using System.Text.Json;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// The Android app is sideloaded, so the store that would normally update it
/// is this server. A release is two files dropped into the data volume's
/// <c>app/</c> folder: <c>vault-movies.apk</c> and <c>android.json</c>
/// (<c>{"versionCode":3,"versionName":"1.0.2","notes":"..."}</c>). The app asks
/// at launch, offers the update, and installs it itself.
/// </summary>
/// <remarks>
/// Signed-in people only, approved or not: someone waiting for approval still
/// runs the app and should get its fixes. The size and checksum are worked out
/// here from the file itself, so a half-copied APK can never be offered with
/// the checksum of the whole one.
/// </remarks>
public static class AppUpdateApi
{
    public static string Root =>
        Path.Combine(Path.GetDirectoryName(CatalogScanner.ArtRoot) ?? AppContext.BaseDirectory, "app");

    static string ApkPath => Path.Combine(Root, "vault-movies.apk");
    static string ManifestPath => Path.Combine(Root, "android.json");

    record Release(long VersionCode, string VersionName, string? Notes, long SizeBytes, string Sha256);

    static readonly object Gate = new();
    static (DateTime apkTime, long apkSize, DateTime manifestTime, Release release)? _cached;

    static Release? Current()
    {
        if (!File.Exists(ApkPath) || !File.Exists(ManifestPath)) return null;
        var apk = new FileInfo(ApkPath);
        var manifestTime = File.GetLastWriteTimeUtc(ManifestPath);
        lock (Gate)
        {
            if (_cached is { } c && c.apkTime == apk.LastWriteTimeUtc && c.apkSize == apk.Length
                && c.manifestTime == manifestTime)
                return c.release;

            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            var root = doc.RootElement;
            var code = root.GetProperty("versionCode").GetInt64();
            var name = root.TryGetProperty("versionName", out var n) ? n.GetString() ?? "" : "";
            var notes = root.TryGetProperty("notes", out var t) ? t.GetString() : null;

            string sha;
            using (var s = File.OpenRead(ApkPath)) sha = Convert.ToHexStringLower(SHA256.HashData(s));

            var release = new Release(code, name, notes, apk.Length, sha);
            _cached = (apk.LastWriteTimeUtc, apk.Length, manifestTime, release);
            return release;
        }
    }

    public static void MapAppUpdateApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/app");

        g.MapGet("/android", () =>
        {
            Release? r;
            try { r = Current(); }
            catch (Exception) { return Results.NotFound(new { error = "No app release is published." }); }
            return r is null
                ? Results.NotFound(new { error = "No app release is published." })
                : Results.Json(new
                {
                    r.VersionCode, r.VersionName, r.Notes, r.SizeBytes, r.Sha256,
                    url = "/api/v1/app/android/apk",
                });
        }).RequireAuthorization();

        g.MapGet("/android/apk", () =>
            File.Exists(ApkPath)
                ? Results.File(ApkPath, "application/vnd.android.package-archive",
                               fileDownloadName: "vault-movies.apk", enableRangeProcessing: true)
                : Results.NotFound()
        ).RequireAuthorization();
    }
}
