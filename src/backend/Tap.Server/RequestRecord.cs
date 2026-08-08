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

    /// <summary>
    /// Number of SSE events discarded because the capture caps were hit. Non-zero means
    /// <see cref="SseEvents"/> is a tail, not the whole stream.
    /// </summary>
    public int SseEventsDropped { get; set; }

    /// <summary>True when this record represents a WebSocket connection.</summary>
    public bool IsWebSocket { get; set; }

    /// <summary>WebSocket frames captured for this connection (both directions).</summary>
    public List<WebSocketMessage> WebSocketMessages { get; set; } = new();

    /// <summary>
    /// Number of WebSocket frames discarded because the capture caps were hit. Non-zero
    /// means <see cref="WebSocketMessages"/> is a tail, not the whole conversation.
    /// </summary>
    public int WebSocketMessagesDropped { get; set; }

    /// <summary>
    /// Approximate bytes this record's frame lists hold, charged against the store's
    /// global capture budget. Internal, so it never reaches the wire.
    /// </summary>
    internal long CapturedFrameBytes { get; set; }

    /// <summary>
    /// Set once the record leaves the store (evicted or cleared). A pump may still be
    /// running against it, and without this flag it would keep growing frames that
    /// nobody can ever read.
    /// </summary>
    internal bool CaptureDetached { get; set; }

    /// <summary>
    /// Point-in-time copy for serialization. Readers serialize outside the store lock
    /// while pumps append frames under it — handing out the live lists lets a concurrent
    /// append throw mid-response or emit a half-written frame.
    /// </summary>
    internal RequestRecord Snapshot() => new()
    {
        Sequence = Sequence,
        Id = Id,
        Timestamp = Timestamp,
        Method = Method,
        Host = Host,
        Path = Path,
        Scheme = Scheme,
        Upstream = Upstream,
        RemoteIp = RemoteIp,
        RequestHeaders = RequestHeaders,
        RequestBody = RequestBody,
        RequestBodyTruncated = RequestBodyTruncated,
        RequestBodyOriginalSize = RequestBodyOriginalSize,
        RequestContentType = RequestContentType,
        StatusCode = StatusCode,
        ResponseHeaders = ResponseHeaders,
        ResponseBody = ResponseBody,
        ResponseBodyBase64 = ResponseBodyBase64,
        ResponseBodyTruncated = ResponseBodyTruncated,
        ResponseBodyOriginalSize = ResponseBodyOriginalSize,
        ResponseContentType = ResponseContentType,
        DurationMs = DurationMs,
        Error = Error,
        IsStream = IsStream,
        StreamCompleted = StreamCompleted,
        SseEvents = [.. SseEvents],
        SseEventsDropped = SseEventsDropped,
        IsWebSocket = IsWebSocket,
        WebSocketMessages = [.. WebSocketMessages],
        WebSocketMessagesDropped = WebSocketMessagesDropped,
    };
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

/// <summary>
/// Edit-and-replay payload posted to <c>POST /api/replay</c>. Either <see cref="Path"/>
/// or <see cref="Url"/> must be set; <see cref="Path"/> sends through the local proxy
/// (so the replay is itself captured), <see cref="Url"/> sends to an absolute upstream
/// (off-proxy — useful for replaying against staging/prod).
/// </summary>
public sealed record ReplayRequest(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("contentType")] string? ContentType);

public sealed record ReplayResponse(
    [property: JsonPropertyName("replayed")] bool Replayed,
    [property: JsonPropertyName("status")] int? Status = null,
    [property: JsonPropertyName("error")] string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RequestRecord))]
[JsonSerializable(typeof(List<RequestRecord>))]
[JsonSerializable(typeof(SseEvent))]
[JsonSerializable(typeof(SseEventEnvelope))]
[JsonSerializable(typeof(WebSocketMessage))]
[JsonSerializable(typeof(WebSocketMessageEnvelope))]
[JsonSerializable(typeof(ReplayRequest))]
[JsonSerializable(typeof(ReplayResponse))]
internal sealed partial class RequestRecordJsonContext : JsonSerializerContext;
