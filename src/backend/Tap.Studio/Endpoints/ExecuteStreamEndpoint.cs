using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Workspace.Model;

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
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        // See ExecuteEndpoint: hops are followed manually so each new host is re-validated.
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        // Effectively no timeout — SSE producers stream indefinitely. The client can abort.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/execute/stream", async (HttpContext ctx, ExecuteRequestDto body, WorkspaceService svc) =>
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

            try
            {
                var rendered = await svc.RenderAsync(body.Path, body.Env, body.Overrides, ct, body.Stage, body.Spec).ConfigureAwait(false);
                HttpExecutionHelpers.ValidateScheme(rendered);
                rendered = HttpExecutionHelpers.WithDefaultUserAgent(rendered);
                stage = rendered.Metadata.StageName;
                variables = rendered.Metadata.VariablesUsed
                    .Select(s => new VariableTraceDto(s.ProviderName, s.Name, s.Resolved, s.IsSecret, s.Duration.TotalMilliseconds))
                    .ToArray();
                // Snapshot auth state *after* RenderAsync so the cached-token freshness check sees
                // whatever the executor saw (the renderer doesn't mutate the store, so the order
                // is just for clarity).
                var authStatus = svc.BuildAuthStatus(body.Path, body.Spec, stage, body.Env);

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
                    await WriteEventAsync(resp, "done",
                        new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, null), ct);
                    return;
                }

                using var req = HttpExecutionHelpers.BuildRequest(rendered);

                using var httpResp = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                    HttpClient, req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var contentType = httpResp.Content.Headers.ContentType?.ToString();
                var responseHeaders = HttpExecutionHelpers.FlattenHeaders(httpResp);

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

                using var stream = await httpResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                if (isSse)
                {
                    totalBytes = await PumpSseFramesAsync(stream, resp, sw, ct).ConfigureAwait(false);
                }
                else
                {
                    using var ms = new MemoryStream();
                    var buf = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        totalBytes += read;
                        if (ms.Length < HttpExecutionHelpers.BodyCap)
                        {
                            var slack = HttpExecutionHelpers.BodyCap - (int)ms.Length;
                            ms.Write(buf, 0, Math.Min(read, slack));
                        }
                    }
                    var bytes = ms.ToArray();
                    var text = HttpExecutionHelpers.TryDecodeBody(bytes, contentType, totalBytes);
                    await WriteEventAsync(resp, "body", new ExecuteStreamBodyDto(text, totalBytes), ct);
                }

                sw.Stop();
                await WriteEventAsync(resp, "done",
                    new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, null), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected — just stop.
            }
            catch (Exception ex)
            {
                sw.Stop();
                await WriteEventAsync(resp, "error", new ExecuteStreamErrorDto(ex.Message), ct);
                await WriteEventAsync(resp, "done",
                    new ExecuteStreamDoneDto(sw.Elapsed.TotalMilliseconds, totalBytes, variables, stage, ex.Message), ct);
            }
        });
    }

    /// <summary>
    /// Pump SSE frames from the upstream response to our client. We forward each frame
    /// as a single <c>event: sse</c> entry containing the parsed event-name / data / id
    /// trio. Blank lines separate frames per the SSE spec.
    /// </summary>
    private static async Task<long> PumpSseFramesAsync(Stream upstream, HttpResponse downstream, Stopwatch sw, CancellationToken ct)
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
