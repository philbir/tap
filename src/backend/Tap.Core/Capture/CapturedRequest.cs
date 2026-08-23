using Tap.Core.Redaction;

namespace Tap.Core.Capture;

/// <summary>
/// One captured exchange, reduced to what an agent may see. Deliberately <b>not</b> a redacted
/// <c>RequestRecord</c>: that type carries a base64 response body and raw header dictionaries,
/// and the next field added to it would reach an agent silently. A separate type turns that
/// leak into a compile error — the same reasoning that keeps <c>Tap.Execution/Contracts</c>
/// apart from <c>Tap.Studio/Contracts/Dtos.cs</c>.
///
/// <para>These live in <c>Tap.Core</c> rather than beside the record they come from, because
/// <c>Tap.Inspector.Mcp</c> has to see them and must not reference <c>Tap.Server</c> back. The
/// projection into them stays in <c>Tap.Server</c>, next to the record.</para>
///
/// <para>No bodies here at all. A listing of twenty of these is something an agent can afford
/// to read; twenty bodies is not, and the token budget is a security control as much as an
/// ergonomic one.</para>
/// </summary>
/// <param name="Client">Salted fingerprint of the caller's address, not the address. Answers
/// "same device?" without putting an IP in a transcript.</param>
/// <param name="Redactions">What was hidden from <paramref name="Path"/> and
/// <paramref name="Error"/> — usually empty, but a query-string credential shows up here.</param>
public sealed record CapturedRequestSummary(
    string Id,
    long Seq,
    DateTimeOffset At,
    string Method,
    string Scheme,
    string Host,
    string Path,
    int Status,
    long DurationMs,
    string? RequestContentType,
    string? ResponseContentType,
    long RequestBytes,
    long ResponseBytes,
    bool IsStream,
    bool StreamCompleted,
    bool IsWebSocket,
    string? Client,
    string? Error,
    IReadOnlyList<RedactionNote> Redactions);

/// <summary>
/// The full readable surface of one exchange: headers, bodies, and any stream frames, each
/// already through <see cref="CaptureRedactor"/>.
/// </summary>
/// <param name="Redactions">Everything hidden anywhere in this record, including
/// <see cref="CapturedRequestSummary.Redactions"/>. One flat list so a reader can see the
/// whole picture without walking the tree — and so "what am I not being shown?" is one
/// question with one answer.</param>
/// <param name="SseDropped">Frames the capture caps discarded. Non-zero means
/// <paramref name="Sse"/> is a tail, not the conversation.</param>
public sealed record CapturedRequestDetail(
    CapturedRequestSummary Summary,
    string? Upstream,
    IReadOnlyDictionary<string, string>? RequestHeaders,
    RedactedBody? RequestBody,
    IReadOnlyDictionary<string, string>? ResponseHeaders,
    RedactedBody? ResponseBody,
    IReadOnlyList<SseEventView>? Sse,
    int SseDropped,
    IReadOnlyList<WebSocketFrameView>? WebSocket,
    int WebSocketDropped,
    IReadOnlyList<RedactionNote> Redactions);

/// <summary>One server-sent event. <paramref name="Data"/> is redacted like a body — a token
/// refresh arriving over SSE bypasses every header rule.</summary>
public sealed record SseEventView(
    DateTimeOffset At,
    string Event,
    string? Data,
    string? Id,
    int? Retry,
    string? Comment);

/// <summary>
/// One WebSocket frame. <paramref name="Text"/> is redacted; a binary frame's bytes are never
/// rendered, so <paramref name="Text"/> is null and <paramref name="Size"/> is all a reader
/// gets.
/// </summary>
/// <param name="Direction">"client" (caller → upstream) or "server" (upstream → caller).</param>
/// <param name="Type">"text", "binary", or "close".</param>
public sealed record WebSocketFrameView(
    DateTimeOffset At,
    string Direction,
    string Type,
    string? Text,
    int Size,
    bool Truncated,
    int? CloseStatus,
    string? CloseDescription);

/// <summary>
/// How much of an exchange to project. Both budgets are applied <b>after</b> redaction, never
/// before: trimming first and redacting second would scan less than was shown, and a mask that
/// gets cut in half reads as a leak.
/// </summary>
public sealed record CaptureDetailOptions
{
    public bool IncludeHeaders { get; init; } = true;

    public bool IncludeBodies { get; init; } = true;

    public bool IncludeFrames { get; init; } = true;

    /// <summary>Characters of redacted body text to keep per direction.</summary>
    public int MaxBodyChars { get; init; } = 16_384;

    /// <summary>Most recent frames to keep per stream. The tail is what a debugging session
    /// wants; the head is what the capture caps already dropped.</summary>
    public int MaxFrames { get; init; } = 50;

    public static CaptureDetailOptions Default { get; } = new();
}
