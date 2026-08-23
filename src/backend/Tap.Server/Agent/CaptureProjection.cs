using Tap.Core.Capture;
using Tap.Core.Redaction;

namespace Tap.Server.Agent;

/// <summary>
/// Turns a captured <see cref="RequestRecord"/> into the shapes an agent may read.
///
/// <para>Written as an explicit field-by-field map, and never as
/// <c>JsonSerializer.Serialize(record)</c>. That is the whole point: a record field that
/// nobody has thought about does not reach an agent, because there is no code here that
/// would carry it. <see cref="WithheldFields"/> records the ones deliberately left behind,
/// and a guardrail test fails when <see cref="RequestRecord"/> grows a property that is
/// neither mapped nor named there.</para>
///
/// <para>Redaction happens here, at read time, over a store that still holds the raw bytes.
/// The inspector UI keeps showing the real values — which is the only reason the agent
/// surface can refuse to reveal anything at all.</para>
/// </summary>
public static class CaptureProjection
{
    /// <summary>
    /// Record properties that are deliberately never projected, and why. A name here is a
    /// decision someone made, not an oversight — which is precisely what distinguishes it
    /// from a field that was simply forgotten.
    /// </summary>
    public static IReadOnlyDictionary<string, string> WithheldFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ResponseBodyBase64"] =
                "raw bytes of an image or other binary response. Never rendered to an agent — " +
                "it would be an enormous context cost and could carry anything. The body's " +
                "sha256 identifies it instead.",
        };

    /// <summary>Record properties that survive under a different name, so the guardrail can
    /// tell a rename from a disappearance.</summary>
    public static IReadOnlyDictionary<string, string> RenamedFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Sequence"] = nameof(CapturedRequestSummary.Seq),
            ["Timestamp"] = nameof(CapturedRequestSummary.At),
            ["StatusCode"] = nameof(CapturedRequestSummary.Status),
            ["RemoteIp"] = nameof(CapturedRequestSummary.Client),
            ["RequestBodyTruncated"] = nameof(RedactedBody.Truncated),
            ["ResponseBodyTruncated"] = nameof(RedactedBody.Truncated),
            ["RequestBodyOriginalSize"] = nameof(RedactedBody.OriginalSize),
            ["ResponseBodyOriginalSize"] = nameof(RedactedBody.OriginalSize),
            ["SseEvents"] = nameof(CapturedRequestDetail.Sse),
            ["SseEventsDropped"] = nameof(CapturedRequestDetail.SseDropped),
            ["WebSocketMessages"] = nameof(CapturedRequestDetail.WebSocket),
            ["WebSocketMessagesDropped"] = nameof(CapturedRequestDetail.WebSocketDropped),
        };

    /// <summary>As <see cref="WithheldFields"/>, for the frame types.</summary>
    public static IReadOnlyDictionary<string, string> WithheldFrameFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Base64"] =
                "raw bytes of a binary WebSocket frame. Size and direction are projected; the " +
                "payload is not.",
        };

    /// <summary>One row of a listing: no bodies, no headers, no frames. Cheap enough that an
    /// agent can read twenty without spending its context on them.</summary>
    public static CapturedRequestSummary Summarize(RequestRecord record, CaptureRedactor redactor)
    {
        var notes = new List<RedactionNote>();

        var target = redactor.Target(record.Path, "url");
        notes.AddRange(target.Notes);

        var error = redactor.Text(record.Error, "error");
        notes.AddRange(error.Notes);

        return new CapturedRequestSummary(
            Id: record.Id.ToString(),
            Seq: record.Sequence,
            At: record.Timestamp,
            Method: record.Method,
            Scheme: record.Scheme,
            Host: record.Host,
            Path: target.Text,
            Status: record.StatusCode,
            DurationMs: record.DurationMs,
            RequestContentType: record.RequestContentType,
            ResponseContentType: record.ResponseContentType,
            RequestBytes: record.RequestBodyOriginalSize,
            ResponseBytes: record.ResponseBodyOriginalSize,
            IsStream: record.IsStream,
            StreamCompleted: record.StreamCompleted,
            IsWebSocket: record.IsWebSocket,
            Client: redactor.Fingerprint(record.RemoteIp),
            Error: record.Error is null ? null : error.Text,
            Redactions: notes);
    }

    /// <summary>The readable detail: headers, bodies, frames — each already redacted, then
    /// trimmed to the caller's budget.</summary>
    public static CapturedRequestDetail Describe(
        RequestRecord record,
        CaptureRedactor redactor,
        CaptureDetailOptions? options = null)
    {
        options ??= CaptureDetailOptions.Default;

        var summary = Summarize(record, redactor);
        var notes = new List<RedactionNote>(summary.Redactions);

        var upstream = redactor.Text(record.Upstream, "upstream");
        notes.AddRange(upstream.Notes);

        IReadOnlyDictionary<string, string>? requestHeaders = null;
        IReadOnlyDictionary<string, string>? responseHeaders = null;
        if (options.IncludeHeaders)
        {
            var request = redactor.Headers(record.RequestHeaders, "request.header");
            var response = redactor.Headers(record.ResponseHeaders, "response.header");
            requestHeaders = request.Headers;
            responseHeaders = response.Headers;
            notes.AddRange(request.Notes);
            notes.AddRange(response.Notes);
        }

        RedactedBody? requestBody = null;
        RedactedBody? responseBody = null;
        if (options.IncludeBodies)
        {
            requestBody = Trim(
                redactor.Body(
                    record.RequestBody, record.RequestContentType,
                    record.RequestBodyOriginalSize, record.RequestBodyTruncated, "request.body"),
                options.MaxBodyChars);

            responseBody = Trim(
                redactor.Body(
                    record.ResponseBody, record.ResponseContentType,
                    record.ResponseBodyOriginalSize, record.ResponseBodyTruncated, "response.body"),
                options.MaxBodyChars);

            notes.AddRange(requestBody.Notes);
            notes.AddRange(responseBody.Notes);
        }

        IReadOnlyList<SseEventView>? sse = null;
        IReadOnlyList<WebSocketFrameView>? webSocket = null;
        if (options.IncludeFrames)
        {
            if (record.SseEvents.Count > 0) sse = ProjectSse(record, redactor, options, notes);
            if (record.WebSocketMessages.Count > 0) webSocket = ProjectWebSocket(record, redactor, options, notes);
        }

        return new CapturedRequestDetail(
            Summary: summary,
            Upstream: record.Upstream is null ? null : upstream.Text,
            RequestHeaders: requestHeaders,
            RequestBody: requestBody,
            ResponseHeaders: responseHeaders,
            ResponseBody: responseBody,
            Sse: sse,
            SseDropped: record.SseEventsDropped,
            WebSocket: webSocket,
            WebSocketDropped: record.WebSocketMessagesDropped,
            Redactions: notes);
    }

    private static List<SseEventView> ProjectSse(
        RequestRecord record, CaptureRedactor redactor, CaptureDetailOptions options, List<RedactionNote> notes)
    {
        var events = Tail(record.SseEvents, options.MaxFrames);
        var views = new List<SseEventView>(events.Count);

        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var data = redactor.Frame(ev.Data, null, $"sse[{i}]");
            var comment = redactor.Text(ev.Comment, $"sse[{i}].comment");
            notes.AddRange(data.Notes);
            notes.AddRange(comment.Notes);

            views.Add(new SseEventView(
                At: ev.Timestamp,
                Event: ev.EventName,
                Data: data.Text,
                Id: ev.Id,
                Retry: ev.Retry,
                Comment: ev.Comment is null ? null : comment.Text));
        }

        return views;
    }

    private static List<WebSocketFrameView> ProjectWebSocket(
        RequestRecord record, CaptureRedactor redactor, CaptureDetailOptions options, List<RedactionNote> notes)
    {
        var messages = Tail(record.WebSocketMessages, options.MaxFrames);
        var views = new List<WebSocketFrameView>(messages.Count);

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            string? text = null;
            string? closeReason = null;

            if (message.CloseDescription is not null)
            {
                var reason = redactor.Text(message.CloseDescription, $"ws[{i}].close");
                closeReason = reason.Text;
                notes.AddRange(reason.Notes);
            }

            if (message.Text is not null)
            {
                // A token refresh arriving over a WebSocket bypasses every header rule, so a
                // frame gets the same treatment a body does.
                var frame = redactor.Frame(message.Text, null, $"ws[{i}]");
                text = frame.Text;
                notes.AddRange(frame.Notes);
            }
            else if (message.Base64 is not null)
            {
                notes.Add(new RedactionNote($"ws[{i}]", RedactionReason.Binary, null));
            }

            views.Add(new WebSocketFrameView(
                At: message.Timestamp,
                Direction: message.Direction,
                Type: message.Type,
                Text: text,
                Size: message.Size,
                Truncated: message.Truncated,
                CloseStatus: message.CloseStatus,
                CloseDescription: closeReason));
        }

        return views;
    }

    private static List<T> Tail<T>(List<T> items, int max)
        => items.Count <= max ? items : items[^max..];

    /// <summary>
    /// Trims redacted text to the caller's budget, never through the middle of a mask. Half a
    /// mask — <c>[redacted:jwt #a91f</c> — reads to a human as a leak, and to an agent as a
    /// value it might be able to complete.
    /// </summary>
    private static RedactedBody Trim(RedactedBody body, int maxChars)
    {
        if (body.Text is null || body.Text.Length <= maxChars) return body;

        var cut = maxChars;
        var open = body.Text.LastIndexOf("[redacted:", cut - 1, StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = body.Text.IndexOf(']', open);
            if (close < 0 || close >= cut) cut = open;
        }

        return body with { Text = body.Text[..cut], Truncated = true };
    }
}
