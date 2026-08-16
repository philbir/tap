using System.Diagnostics;
using System.Text;
using Tap.Studio.Contracts;
using Tap.Workspace.Model;
using Tap.Execution.Asserts;
using Tap.Execution.Http;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>POST /api/execute</c> — render a request file and actually fire it against the
/// upstream. Returns the response (status, headers, body, timing) plus the secret trace
/// from the resolution stage.
///
/// Body bytes are returned as a string (UTF-8 attempt, falling back to a length-only
/// summary if we can't decode). Response size is capped at 2 MiB to keep the studio UI
/// responsive — beyond that we send a truncated tail with a marker.
/// </summary>
public static class ExecuteEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/execute", async (ExecuteRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            try
            {
                var rendered = await svc.RenderAsync(
                    body.Path, body.Env, body.Overrides, ct, body.Stage, body.Spec, draftSource: body.Source).ConfigureAwait(false);
                HttpExecutionHelpers.ValidateScheme(rendered);
                rendered = HttpExecutionHelpers.WithDefaultUserAgent(rendered);

                // WebSocket path: open the connection, optionally send the body as the first
                // frame, then close. The synchronous /api/execute shape isn't well suited to
                // long-lived frame capture — clients that want a live feed should call
                // /api/execute/stream instead, which surfaces each frame as it arrives.
                if (rendered.Protocol == Tap.Workspace.Model.RequestProtocol.WebSocket)
                {
                    var swWs = Stopwatch.StartNew();
                    var capturedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int captureStatus = 0;
                    string? captureStatusText = null;
                    var frames = new List<ExecuteStreamWsDto>();

                    var bytes = await WebSocketExecutor.RunAsync(
                        rendered,
                        emitMeta: (status, statusText, headers, _, _) =>
                        {
                            captureStatus = status;
                            captureStatusText = statusText;
                            foreach (var (k, v) in headers) capturedHeaders[k] = v;
                            return ValueTask.CompletedTask;
                        },
                        emitFrame: (frame, _) => { frames.Add(frame); return ValueTask.CompletedTask; },
                        swWs,
                        ct).ConfigureAwait(false);
                    swWs.Stop();

                    // We summarize the captured frames as a text body so the existing
                    // ExecutionResultDto layout still makes sense for ad-hoc one-shot calls.
                    // Streaming clients use /api/execute/stream and get each frame individually.
                    var summary = new StringBuilder();
                    summary.AppendLine($"WebSocket capture: {frames.Count} frame(s), {bytes:N0} bytes");
                    foreach (var f in frames)
                    {
                        summary.Append($"[{f.TimestampMs:F1}ms] ").Append(f.Direction).Append(' ').Append(f.Type);
                        if (f.Text is not null) summary.Append(": ").Append(f.Text.Length > 200 ? f.Text[..200] + "…" : f.Text);
                        else if (f.Base64 is not null) summary.Append($": ({f.Size} bytes binary)");
                        summary.AppendLine();
                    }

                    var (wsAsserts, wsAssertSummary) = AssertRunner.Run(
                        rendered, captureStatus, capturedHeaders, summary.ToString(), bytes, swWs.Elapsed.TotalMilliseconds);

                    return Results.Ok(new ExecutionResultDto(
                        Status: captureStatus,
                        StatusText: captureStatusText,
                        Url: rendered.Url,
                        Method: rendered.Method,
                        RequestHeaders: rendered.Headers,
                        RequestBody: rendered.Body,
                        ResponseHeaders: capturedHeaders,
                        ResponseBody: summary.ToString(),
                        ContentType: "text/plain",
                        ResponseBodyBytes: bytes,
                        DurationMs: swWs.Elapsed.TotalMilliseconds,
                        VariablesUsed: rendered.Metadata.VariablesUsed
                            .Select(s => new VariableTraceDto(s.ProviderName, s.Name, s.Resolved, s.IsSecret, s.Duration.TotalMilliseconds))
                            .ToArray(),
                        Stage: rendered.Metadata.StageName,
                        Error: null,
                        Protocol: rendered.Protocol.ToWire(),
                        Assertions: wsAsserts,
                        AssertSummary: wsAssertSummary));
                }

                using var req = HttpExecutionHelpers.BuildRequest(rendered);
                using var timeout = HttpTransport.CreateTimeout(rendered, ct);
                using var httpClient = HttpTransport.CreateClient(rendered);

                var sw = Stopwatch.StartNew();
                using var resp = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                    httpClient, req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var stream = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);

                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (ms.Length < HttpExecutionHelpers.BodyCap)
                    {
                        var slack = HttpExecutionHelpers.BodyCap - (int)ms.Length;
                        ms.Write(buf, 0, Math.Min(read, slack));
                    }
                }
                sw.Stop();

                var responseBytes = ms.ToArray();
                var contentType = resp.Content.Headers.ContentType?.ToString();
                var bodyText = HttpExecutionHelpers.TryDecodeBody(responseBytes, contentType, total);
                var responseHeaders = HttpExecutionHelpers.FlattenHeaders(resp);

                var (assertions, assertSummary) = AssertRunner.Run(
                    rendered, (int)resp.StatusCode, responseHeaders, bodyText, total, sw.Elapsed.TotalMilliseconds);

                return Results.Ok(new ExecutionResultDto(
                    Status: (int)resp.StatusCode,
                    StatusText: resp.ReasonPhrase,
                    Url: rendered.Url,
                    Method: rendered.Method,
                    RequestHeaders: rendered.Headers,
                    RequestBody: rendered.Body,
                    ResponseHeaders: responseHeaders,
                    ResponseBody: bodyText,
                    ContentType: contentType,
                    ResponseBodyBytes: total,
                    DurationMs: sw.Elapsed.TotalMilliseconds,
                    VariablesUsed: rendered.Metadata.VariablesUsed
                        .Select(s => new VariableTraceDto(s.ProviderName, s.Name, s.Resolved, s.IsSecret, s.Duration.TotalMilliseconds))
                        .ToArray(),
                    Stage: rendered.Metadata.StageName,
                    Error: null,
                    Protocol: rendered.Protocol.ToWire(),
                    Assertions: assertions,
                    AssertSummary: assertSummary));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Network failure / timeout — surface as a non-200 result so the UI can render it
                // in the same response panel rather than as an error toast.
                return Results.Ok(new ExecutionResultDto(
                    Status: 0,
                    StatusText: ex.GetType().Name,
                    Url: body.Path,
                    Method: "—",
                    RequestHeaders: new Dictionary<string, string>(),
                    RequestBody: null,
                    ResponseHeaders: new Dictionary<string, string>(),
                    ResponseBody: null,
                    ContentType: null,
                    ResponseBodyBytes: 0,
                    DurationMs: 0,
                    VariablesUsed: [],
                    Stage: null,
                    Error: HttpTransport.DescribeException(ex),
                    Protocol: "http",
                    Assertions: [],
                    AssertSummary: null));
            }
        });

        app.MapPost("/api/execute/tls-diagnose", async (ExecuteRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            var rendered = await svc.RenderAsync(
                body.Path, body.Env, body.Overrides, ct, body.Stage, body.Spec, draftSource: body.Source).ConfigureAwait(false);
            return Results.Ok(await HttpTransport.DiagnoseAsync(new Uri(rendered.Url), ct).ConfigureAwait(false));
        });
    }

}
