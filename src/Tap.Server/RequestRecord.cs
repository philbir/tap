using System.Text.Json.Serialization;

namespace Tap.Server;

public sealed class RequestRecord
{
    public required long Sequence { get; init; }

    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Method { get; init; }

    public required string Host { get; init; }

    public required string Path { get; init; }

    public required string Scheme { get; init; }

    public string? Upstream { get; set; }

    public required string? RemoteIp { get; init; }

    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }

    public string? RequestBody { get; set; }

    public bool RequestBodyTruncated { get; set; }

    public long RequestBodyOriginalSize { get; set; }

    public string? RequestContentType { get; set; }

    public int StatusCode { get; set; }

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; set; } =
        new Dictionary<string, string>();

    public string? ResponseBody { get; set; }

    public string? ResponseBodyBase64 { get; set; }

    public bool ResponseBodyTruncated { get; set; }

    public long ResponseBodyOriginalSize { get; set; }

    public string? ResponseContentType { get; set; }

    public long DurationMs { get; set; }

    public string? Error { get; set; }

    /// <summary>True when the response is being streamed (e.g. text/event-stream).</summary>
    public bool IsStream { get; set; }

    /// <summary>True after the upstream response has finished.</summary>
    public bool StreamCompleted { get; set; }

    /// <summary>SSE events captured from a text/event-stream response.</summary>
    public List<SseEvent> SseEvents { get; set; } = new();

    /// <summary>True when this record represents a WebSocket connection.</summary>
    public bool IsWebSocket { get; set; }

    /// <summary>WebSocket frames captured for this connection (both directions).</summary>
    public List<WebSocketMessage> WebSocketMessages { get; set; } = new();
}

public sealed record SseEvent(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("event")] string EventName,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("retry")] int? Retry,
    [property: JsonPropertyName("comment")] string? Comment);

public sealed record SseEventEnvelope(
    [property: JsonPropertyName("recordId")] Guid RecordId,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("event")] SseEvent Event);

/// <summary>A single WebSocket frame/message captured by the inspector.
/// <paramref name="Direction"/> is "client" (browser → upstream) or "server" (upstream → browser).
/// <paramref name="Type"/> is "text" | "binary" | "close".</summary>
public sealed record WebSocketMessage(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("base64")] string? Base64,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("closeStatus")] int? CloseStatus,
    [property: JsonPropertyName("closeDescription")] string? CloseDescription);

public sealed record WebSocketMessageEnvelope(
    [property: JsonPropertyName("recordId")] Guid RecordId,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("message")] WebSocketMessage Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RequestRecord))]
[JsonSerializable(typeof(List<RequestRecord>))]
[JsonSerializable(typeof(SseEvent))]
[JsonSerializable(typeof(SseEventEnvelope))]
[JsonSerializable(typeof(WebSocketMessage))]
[JsonSerializable(typeof(WebSocketMessageEnvelope))]
internal sealed partial class RequestRecordJsonContext : JsonSerializerContext;
