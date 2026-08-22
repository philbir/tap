using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Studio.History;
using Tap.Workspace.Model;
using Tap.Execution.Asserts;
using Tap.Execution.Http;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>POST /api/execute/stream</c> — same input as <c>/api/execute</c>, but the response
/// is an SSE stream so the UI can show progressive output. Three event types:
/// <list type="bullet">
///   <item><c>event: meta</c> — request + response headers, status, content-type. Fires once,
///   immediately after the upstream's headers arrive (<c>HttpCompletionOption.ResponseHeadersRead</c>).</item>
///   <item><c>event: sse</c> — fired for each upstream SSE frame when the upstream's
///   Content-Type is <c>text/event-stream</c>. Carries the parsed <c>event/data/id</c>
///   triple plus a server-relative timestamp.</item>
///   <item><c>event: body</c> — fired once with the full (decoded) body when the upstream
///   is NOT an SSE producer. Mirrors what <c>/api/execute</c> returns in one shot.</item>
///   <item><c>event: done</c> — final event, includes duration / total bytes / secret traces / stage.</item>
///   <item><c>event: error</c> — emitted on transport failure; <c>done</c> still follows.</item>
/// </list>
/// This keeps the client implementation unified: one streaming call handles both
/// normal and SSE responses, and the user gets live updates for the latter.
/// </summary>
public static class ExecuteStreamEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/execute/stream", async (
            HttpContext ctx, ExecuteRequestDto body, WorkspaceService svc, ResponseBodyStore bodies,
            HistoryRecorder history) =>
        {
            var resp = ctx.Response;
            resp.Headers.ContentType = "text/event-stream";
            resp.Headers.CacheControl = "no-cache";
            resp.Headers["X-Accel-Buffering"] = "no";

            var sw = Stopwatch.StartNew();
            long totalBytes = 0;
            string? stage = null;
            IReadOnlyList<VariableTraceDto> variables = Array.Empty<VariableTraceDto>();
            var ct = ctx.RequestAborted;

            // Captured as the exchange progresses so the `done` event can hand the whole
            // response to the assertion evaluator in one piece.
            Tap.Workspace.Rendering.ResolvedRequest? rendered = null;
            var assertStatus = 0;
            IReadOnlyDictionary<string, string> assertHeaders = new Dictionary<string, string>();
            string? assertBody = null;
            // Kept alongside the assertion inputs because request history wants the same
            // snapshot the evaluator got, plus the two fields a verdict doesn't care about.
            string? assertStatusText = null;
            string? assertContentType = null;
            var limits = svc.Current.ResponseLimits;

            try
            {
                rendered = await svc.RenderAsync(
                    body.Path, body.Env, body.Overrides, ct, body.Stage, body.Spec, draftSource: body.Source).ConfigureAwait(false);
                HttpExecutionHelpers.ValidateScheme(rendered);
                rendered = HttpExecutionHelpers.WithDefaultUserAgent(rendered);
                stage = rendered.Metadata.StageName;
                variables = rendered.Metadata.VariablesUsed
                    .Select(s => new VariableTraceDto(s.ProviderName, s.Name, s.Resolved, s.IsSecret, s.Duration.TotalMilliseconds))
                    .ToArray();
                // Snapshot auth state *after* RenderAsync so the cached-token freshness check sees
                // whatever the executor saw (the renderer doesn't mutate the store, so the order
                // is just for clarity).
                var authStatus = svc.BuildAuthStatus(body.Path, body.Spec, stage, body.Env, body.Source);

                // WebSocket requests skip the HttpClient path entirely — see WebSocketExecutor.
                // Same event stream shape, just `ws` frames in place of `body`/`sse`.
                if (rendered.Protocol == Tap.Workspace.Model.RequestProtocol.WebSocket)
                {
                    totalBytes = await WebSocketExecutor.RunAsync(
                        rendered,
                        emitMeta: async (status, statusText, headers, contentType, ictt) =>
                        {
                            var meta = new ExecuteStreamMetaDto(
                                Method: rendered.Method,
                                Url: rendered.Url,
                                Status: status,
                                StatusText: statusText,
                                RequestHeaders: rendered.Headers,
                                RequestBody: rendered.Body,
                                ResponseHeaders: headers,
                                ContentType: contentType,
                                Protocol: rendered.Protocol.ToWire(),
                                AuthStatus: authStatus);
                            await WriteEventAsync(resp, "meta", meta, ictt).ConfigureAwait(false);
                        },
                        emitFrame: async (frame, ictt) =>
                            await WriteEventAsync(resp, "ws", frame, ictt).ConfigureAwait(false),
                        sw,
                        ct).ConfigureAwait(false);

                    sw.Stop();
                    var (wsAsserts, wsSummary) = AssertRunner.Run(
                        rendered, 0, assertHeaders, null, totalBytes, sw.Elapsed.TotalMilliseconds);
                    history.TryRecord(
                        svc.Current, rendered,
                        new HistoryResponse(0, null, assertHeaders, null, null, totalBytes, BodyTruncated: false),
                        sw.Elapsed.TotalMilliseconds, wsAsserts, wsSummary, error: null);
                    await WriteEventAsync(resp, "done",
                        new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, null, wsAsserts, wsSummary), ct);
                    return;
                }

                using var req = HttpExecutionHelpers.BuildRequest(rendered);
                using var timeout = HttpTransport.CreateTimeout(rendered, ct);
                using var httpClient = HttpTransport.CreateClient(rendered);

                using var httpResp = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                    httpClient, req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var contentType = httpResp.Content.Headers.ContentType?.ToString();
                var responseHeaders = HttpExecutionHelpers.FlattenHeaders(httpResp);
                assertStatus = (int)httpResp.StatusCode;
                assertHeaders = responseHeaders;
                assertStatusText = httpResp.ReasonPhrase;
                assertContentType = contentType;

                var meta = new ExecuteStreamMetaDto(
                    Method: rendered.Method,
                    Url: rendered.Url,
                    Status: (int)httpResp.StatusCode,
                    StatusText: httpResp.ReasonPhrase,
                    RequestHeaders: rendered.Headers,
                    RequestBody: rendered.Body,
                    ResponseHeaders: responseHeaders,
                    ContentType: contentType,
                    Protocol: rendered.Protocol.ToWire(),
                    AuthStatus: authStatus);
                await WriteEventAsync(resp, "meta", meta, ct);

                var isSse = contentType?.Split(';')[0].Trim()
                    .Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) ?? false;

                using var stream = await httpResp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);

                if (isSse)
                {
                    // A stream has no "final body", but its assertions still need something to
                    // read. The captured frame text — what the SSE tab shows — is that something.
                    var captured = new StringBuilder();
                    totalBytes = await PumpSseFramesAsync(
                        stream, resp, sw, captured, limits.EffectiveMaxBytes, timeout.Token).ConfigureAwait(false);
                    assertBody = captured.ToString();
                }
                else
                {
                    // Everything past the inline cap streams into a spool file so the panel can
                    // offer "show all" and a complete download without re-sending the request.
                    await using var spool = bodies.CreateSpool(limits.EffectiveMaxBytes);
                    var captured = await ResponseCapture.ReadAsync(
                        stream, limits.EffectiveMaxBytes, spool, limits.EffectiveMaxRetainedBytes, timeout.Token)
                        .ConfigureAwait(false);
                    await spool.FlushAsync(timeout.Token).ConfigureAwait(false);

                    totalBytes = captured.TotalBytes;
                    var retained = bodies.Publish(spool, contentType, totalBytes);
                    var text = HttpExecutionHelpers.TryDecodeBody(captured.Inline, contentType, totalBytes);
                    assertBody = text;
                    await WriteEventAsync(resp, "body", new ExecuteStreamBodyDto(
                        text,
                        totalBytes,
                        captured.Inline.LongLength,
                        retained?.Id,
                        retained?.RetainedBytes ?? captured.Inline.LongLength), ct);
                }

                sw.Stop();
                var (assertions, assertSummary) = AssertRunner.Run(
                    rendered, assertStatus, assertHeaders, assertBody, totalBytes, sw.Elapsed.TotalMilliseconds,
                    limits.EffectiveMaxBytes);
                // For an SSE response `assertBody` is the frame transcript rather than a body —
                // which is the right thing to keep, since it is what the exchange actually was.
                history.TryRecord(
                    svc.Current, rendered,
                    new HistoryResponse(
                        assertStatus, assertStatusText, assertHeaders, assertContentType,
                        assertBody, totalBytes, BodyTruncated: totalBytes > (assertBody?.Length ?? 0)),
                    sw.Elapsed.TotalMilliseconds, assertions, assertSummary, error: null);
                await WriteEventAsync(resp, "done",
                    new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, null, assertions, assertSummary), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — just stop.
            }
            catch (Exception ex)
            {
                sw.Stop();
                var message = HttpTransport.DescribeException(ex);
                var (assertions, assertSummary) = AssertRunner.NotRun(
                    rendered, "The request did not complete, so this assertion was not evaluated.");
                if (rendered is not null)
                {
                    history.TryRecord(
                        svc.Current, rendered, response: null,
                        sw.Elapsed.TotalMilliseconds, assertions, assertSummary, message);
                }
                await WriteEventAsync(resp, "error", new ExecuteStreamErrorDto(message), ct);
                await WriteEventAsync(resp, "done",
                    new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, message, assertions, assertSummary), ct);
            }
        });
    }

    /// <summary>
    /// Pump SSE frames from the upstream response to our client. We forward each frame
    /// as a single <c>event: sse</c> entry containing the parsed event-name / data / id
    /// trio. Blank lines separate frames per the SSE spec.
    /// </summary>
    /// <param name="captured">Accumulates the raw stream text (capped at the same
    /// <c>response.maxBytes</c> the one-shot body path uses) so assertions have a body to read
    /// once the stream ends.</param>
    private static async Task<long> PumpSseFramesAsync(
        Stream upstream, HttpResponse downstream, Stopwatch sw, StringBuilder captured, long captureCap, CancellationToken ct)
    {
        using var reader = new StreamReader(upstream, Encoding.UTF8);
        long bytes = 0;
        var eventName = "message";
        var dataLines = new List<string>();
        string? id = null;
        var seq = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            bytes += line.Length + 1; // +1 for newline
            if (captured.Length < captureCap) captured.Append(line).Append('\n');

            if (string.IsNullOrEmpty(line))
            {
                if (dataLines.Count > 0 || id is not null)
                {
                    var frame = new ExecuteStreamSseDto(
                        Seq: seq++,
                        Event: eventName,
                        Data: string.Join('\n', dataLines),
                        Id: id,
                        TimestampMs: sw.Elapsed.TotalMilliseconds);
                    await WriteEventAsync(downstream, "sse", frame, ct).ConfigureAwait(false);
                    eventName = "message";
                    dataLines.Clear();
                    id = null;
                }
                continue;
            }
            if (line.StartsWith(":", StringComparison.Ordinal)) continue; // SSE comment
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event": eventName = value; break;
                case "data": dataLines.Add(value); break;
                case "id": id = value; break;
                // We don't forward `retry:` — that's a reconnect hint for browsers and
                // doesn't affect the captured stream.
            }
        }
        // Flush trailing frame if no terminating blank line.
        if (dataLines.Count > 0 || id is not null)
        {
            var frame = new ExecuteStreamSseDto(
                Seq: seq,
                Event: eventName,
                Data: string.Join('\n', dataLines),
                Id: id,
                TimestampMs: sw.Elapsed.TotalMilliseconds);
            await WriteEventAsync(downstream, "sse", frame, ct).ConfigureAwait(false);
        }
        return bytes;
    }

    private static async Task WriteEventAsync<T>(HttpResponse resp, string name, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, typeof(T), StudioJson.Default);
        var sb = new StringBuilder(json.Length + 32);
        sb.Append("event: ").Append(name).Append('\n');
        sb.Append("data: ").Append(json).Append("\n\n");
        await resp.WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
        await resp.Body.FlushAsync(ct).ConfigureAwait(false);
    }

}
