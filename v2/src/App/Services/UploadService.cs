using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.App.Models;

namespace VideoPlayer.App.Services;

/// <summary>
/// Sends films from the user's folder up to the Vault server, one at a time.
///
/// One at a time on purpose: uploads share the home connection with everything
/// else this machine is doing, and two parallel 20 GB transfers just make both
/// slower while saturating the link for hours. A queue with visible progress is
/// the honest shape of "this takes as long as your upload speed says it takes".
///
/// Each film is chunked, resumable, and checksummed: the connection dropping at
/// 90% costs the current chunk, not the transfer, and the server refuses to
/// index bytes that don't hash to what left this disk.
/// </summary>
public class UploadService(AccountService account)
{
    private const int ChunkBytes = 8 << 20;

    public sealed class Job : INotifyPropertyChanged
    {
        public required string FilePath { get; init; }
        public required string Name { get; init; }
        public long SizeBytes { get; init; }

        private string _state = "Queued";
        /// <summary>Queued | Hashing | Uploading | Done | Already on server | Failed: reason</summary>
        public string State
        {
            get => _state;
            set { _state = value; Notify(nameof(State), nameof(Detail)); }
        }

        private long _sent;
        public long SentBytes
        {
            get => _sent;
            set { _sent = value; Notify(nameof(SentBytes), nameof(Fraction), nameof(Detail)); }
        }

        public double Fraction => SizeBytes > 0 ? Math.Clamp(_sent / (double)SizeBytes, 0, 1) : 0;

        public string Detail => State switch
        {
            "Uploading" => $"{_sent / (double)(1L << 30):0.##} of {SizeBytes / (double)(1L << 30):0.##} GB",
            _ => State,
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(params string[] names)
        {
            foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    // No overall timeout: a chunk on a slow line can legitimately take minutes.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _worker = new(1, 1);

    /// <summary>Everything queued this session, newest last. UI binds to this.</summary>
    public List<Job> Jobs { get; } = new();

    /// <summary>Raised when a job's state changes or the queue gains a member.</summary>
    public event Action? Changed;

    public bool HasActiveJobs => Jobs.Any(j => j.State is "Queued" or "Hashing" or "Uploading");

    /// <summary>Queue a film. Returns false when it's already queued or not a real file.</summary>
    public bool Enqueue(MovieItem movie)
    {
        if (movie.Kind != MovieKind.Local || !File.Exists(movie.Path)) return false;
        if (Jobs.Any(j => j.FilePath == movie.Path && j.State is not ("Failed" or "Done"))) return false;

        var job = new Job { FilePath = movie.Path, Name = movie.Name, SizeBytes = movie.SizeBytes };
        Jobs.Add(job);
        Changed?.Invoke();
        _ = RunQueueAsync();
        return true;
    }

    private async Task RunQueueAsync()
    {
        if (!await _worker.WaitAsync(0)) return; // a worker is already draining
        try
        {
            while (Jobs.FirstOrDefault(j => j.State == "Queued") is { } job)
            {
                try
                {
                    await UploadAsync(job);
                }
                catch (Exception ex)
                {
                    job.State = $"Failed: {ex.Message}";
                }
                Changed?.Invoke();
            }
        }
        finally
        {
            _worker.Release();
        }
    }

    private async Task UploadAsync(Job job)
    {
        var token = await account.AccessTokenAsync()
            ?? throw new InvalidOperationException("Not signed in.");
        var baseUrl = account.BaseUrl;

        // Hash before sending a byte, so completion can prove integrity. On a
        // big remux this reads the whole file once — tell the user why.
        job.State = "Hashing";
        Changed?.Invoke();
        string sha;
        await using (var hashStream = File.OpenRead(job.FilePath))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream));

        var begin = await PostJsonAsync(token, $"{baseUrl}/api/v1/upload/begin",
            new { fileName = Path.GetFileName(job.FilePath), sizeBytes = job.SizeBytes });
        var status = begin.GetProperty("status").GetString();
        if (status == "exists")
        {
            job.State = "Already on server";
            return;
        }
        var uploadId = begin.GetProperty("uploadId").GetString()!;
        var offset = begin.GetProperty("receivedBytes").GetInt64();

        job.State = "Uploading";
        job.SentBytes = offset;
        Changed?.Invoke();

        await using (var file = File.OpenRead(job.FilePath))
        {
            file.Position = offset;
            var buffer = new byte[ChunkBytes];
            while (file.Position < file.Length)
            {
                var read = await file.ReadAtLeastAsync(buffer, ChunkBytes, throwOnEndOfStream: false);
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{baseUrl}/api/v1/upload/{uploadId}/chunk?offset={file.Position - read}")
                { Content = new ByteArrayContent(buffer, 0, read) };
                req.Headers.Authorization = new("Bearer", token);
                using var resp = await _http.SendAsync(req);

                if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // We and the server disagree on progress (a chunk landed but
                    // its response was lost). The server's answer is the truth.
                    var conflict = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    file.Position = conflict.RootElement.GetProperty("receivedBytes").GetInt64();
                    job.SentBytes = file.Position;
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(await ErrorOfAsync(resp));

                job.SentBytes = file.Position;

                // Tokens expire mid-transfer on anything longer than an hour.
                token = await account.AccessTokenAsync() ?? token;
            }
        }

        var final = await PostJsonAsync(token, $"{baseUrl}/api/v1/upload/{uploadId}/complete",
            new { sha256 = sha });
        _ = final; // non-2xx already threw
        job.State = "Done";
    }

    private async Task<JsonElement> PostJsonAsync(string token, string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorOf(text, resp));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage resp) =>
        ErrorOf(await resp.Content.ReadAsStringAsync(), resp);

    private static string ErrorOf(string body, HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? $"Server said {(int)resp.StatusCode}.";
        }
        catch { }
        return $"Server said {(int)resp.StatusCode}.";
    }
}
