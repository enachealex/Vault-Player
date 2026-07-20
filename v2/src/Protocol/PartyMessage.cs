using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPlayer.Protocol;

/// <summary>Message types on the party control channel.</summary>
public static class MsgType
{
    public const string Hello = "hello";           // host → server: movie meta + file size
    public const string Welcome = "welcome";       // server → client: room code (+ media meta for guests)
    public const string Join = "join";             // server → host: a guest arrived
    public const string Roster = "roster";         // server → all: member list
    public const string Start = "start";           // host → all: open the player
    public const string State = "state";           // host → all: sync beacon
    public const string PauseRequest = "pauseRequest"; // guest → host
    public const string Ready = "ready";           // guest → host: buffered / ready to play
    public const string Chat = "chat";             // anyone → all
    /// <summary>
    /// Host → all: "everyone press play in N seconds". Used for DRM titles on
    /// streaming services, which this app can't relay — each member plays the
    /// title in the service's own app and we only synchronise the start.
    /// </summary>
    public const string Countdown = "countdown";
    /// <summary>Host → all: the room is now about a different film (lobby only).</summary>
    public const string Switch = "switch";
    public const string Pull = "pull";             // server → host: fetch a byte range for a guest
    public const string Bye = "bye";               // server → guests: host left / room closed
    public const string Error = "error";
}

/// <summary>
/// One flexible message envelope for the whole party protocol (JSON over
/// WebSocket). Unused fields are omitted on the wire.
/// </summary>
public class PartyMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("room")] public string? Room { get; set; }
    [JsonPropertyName("mediaUrl")] public string? MediaUrl { get; set; }
    [JsonPropertyName("movieTitle")] public string? MovieTitle { get; set; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("positionMs")] public long? PositionMs { get; set; }
    [JsonPropertyName("playing")] public bool? Playing { get; set; }
    [JsonPropertyName("atUnixMs")] public long? AtUnixMs { get; set; }
    [JsonPropertyName("started")] public bool? Started { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("members")] public string[]? Members { get; set; }
    [JsonPropertyName("readyCount")] public int? ReadyCount { get; set; }
    [JsonPropertyName("guestCount")] public int? GuestCount { get; set; }

    // External (streaming service) countdown.
    [JsonPropertyName("seconds")] public int? Seconds { get; set; }
    [JsonPropertyName("service")] public string? Service { get; set; }

    // Media pull relay (server ↔ host).
    [JsonPropertyName("reqId")] public string? ReqId { get; set; }
    [JsonPropertyName("start")] public long? Start { get; set; }
    [JsonPropertyName("end")] public long? End { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static PartyMessage? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PartyMessage>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}
