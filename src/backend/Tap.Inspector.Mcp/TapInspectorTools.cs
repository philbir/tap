using System.ComponentModel;
using ModelContextProtocol.Server;
using Tap.Core.Capture;

namespace Tap.Inspector.Mcp;

/// <summary>
/// The MCP face of the inspector: what an agent can learn about traffic that already arrived,
/// and how it waits for traffic that has not.
///
/// <para>Served twice from one implementation — <c>tap mcp</c> over stdio and
/// <c>Tap.Server</c>'s in-process <c>/mcp</c> endpoint — with
/// <see cref="IMcpCaptureProvider"/> carrying the whole difference between them. Tool
/// contracts must not fork: change them here, never per host.</para>
///
/// <para>No tool here reads a raw record, because the provider cannot hand one over. Every
/// result is already projected and redacted, and every result is wrapped in an envelope that
/// says so — captured bodies come from the public internet and are data, never instructions.</para>
/// </summary>
[McpServerToolType]
public sealed class TapInspectorTools(IMcpCaptureProvider provider)
{
    /// <summary>Cap on how long a caller may park a connection waiting for traffic. Long enough
    /// to walk to a phone and tap a button; short enough that a forgotten call ends.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = "list_requests")]
    [Description(
        "Recent HTTP exchanges the inspector captured, newest first — method, host, path, " +
        "status, duration, sizes. No headers or bodies: call describe_request for those. " +
        "Credentials are already redacted and cannot be revealed by any tool. Start here to " +
        "find out what a mobile app, webhook provider, or device actually sent.")]
    public async Task<string> ListRequests(
        [Description("Only this host, e.g. api.example.com.")] string? host = null,
        [Description("Glob over the path, e.g. /webhooks/* — matched against the path, not the query string.")] string? pathGlob = null,
        [Description("Only this HTTP method.")] string? method = null,
        [Description("Only this exact status code.")] int? status = null,
        [Description("Only exchanges that failed: 4xx, 5xx, or a proxy-level error.")] bool onlyErrors = false,
        [Description("How many to return, newest first. Default 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var envelope = await provider.ListAsync(
            new CaptureQuery
            {
                Host = host,
                PathGlob = pathGlob,
                Method = method,
                Status = status,
                OnlyErrors = onlyErrors,
                Limit = Math.Clamp(limit, 1, 200),
            },
            cancellationToken);

        return CaptureJson.List(envelope.Requests, envelope.Available);
    }

    [McpServerTool(Name = "describe_request")]
    [Description(
        "One captured exchange in full: redacted request and response headers, bodies, and any " +
        "SSE or WebSocket frames. Every hidden value is listed under 'redactions' with where it " +
        "was and why — a value shown as [redacted:…] cannot be retrieved, so ask the user to " +
        "read it from the inspector UI if you genuinely need it. Matching fingerprints (#a91f3c2d) " +
        "mean identical values, which is how you compare two requests' credentials without seeing them.")]
    public async Task<string> DescribeRequest(
        [Description("The request id from list_requests.")] string id,
        [Description("Include request and response headers. Default true.")] bool includeHeaders = true,
        [Description("Include request and response bodies. Default true.")] bool includeBodies = true,
        [Description("Include SSE and WebSocket frames. Default true.")] bool includeFrames = true,
        [Description("Characters of body text to keep per direction. Default 16384.")] int maxBodyChars = 16_384,
        CancellationToken cancellationToken = default)
    {
        var detail = await provider.GetAsync(
            id,
            new CaptureDetailOptions
            {
                IncludeHeaders = includeHeaders,
                IncludeBodies = includeBodies,
                IncludeFrames = includeFrames,
                MaxBodyChars = Math.Clamp(maxBodyChars, 256, 262_144),
            },
            cancellationToken);

        return detail is null
            ? CaptureJson.Error(
                $"No captured request with id '{id}'.",
                "The inspector keeps the most recent 200 exchanges, so it may have been evicted. " +
                "Call list_requests for what is still held.")
            : CaptureJson.Detail(detail);
    }

    [McpServerTool(Name = "diff_requests")]
    [Description(
        "Compare two captured exchanges and report only what differs — method, path, query " +
        "parameters, headers, body. Answers 'why does this one work and that one not' without " +
        "reading two walls of headers. Redacted values still compare: matching fingerprints mean " +
        "the same credential, so 'Authorization differs (#a91f3c2d vs #4b2ec7d1)' is a real " +
        "finding even though neither token is visible. Volatile headers (Date, Content-Length, " +
        "request ids) are ignored.")]
    public async Task<string> DiffRequests(
        [Description("Id of the first request, from list_requests.")] string leftId,
        [Description("Id of the second request.")] string rightId,
        CancellationToken cancellationToken = default)
    {
        var options = new CaptureDetailOptions { IncludeFrames = false };
        var left = await provider.GetAsync(leftId, options, cancellationToken);
        var right = await provider.GetAsync(rightId, options, cancellationToken);

        var missing = left is null ? leftId : right is null ? rightId : null;
        return missing is not null
            ? CaptureJson.Error(
                $"No captured request with id '{missing}'.",
                "The inspector keeps the most recent 200 exchanges, so it may have been evicted.")
            : CaptureJson.Diff(CaptureDiff.Compare(left!, right!));
    }

    [McpServerTool(Name = "search_requests")]
    [Description(
        "Find captured exchanges containing a piece of text — an order id, an error string, a " +
        "correlation id — across paths, headers, and bodies. Returns each match with where it " +
        "was and a short excerpt. IMPORTANT: this searches the REDACTED text, so a term aimed " +
        "at a masked credential finds nothing by design; use the fingerprints in " +
        "describe_request to compare hidden values instead.")]
    public async Task<string> SearchRequests(
        [Description("Text to look for. Case-insensitive.")] string term,
        [Description("Only this host.")] string? host = null,
        [Description("Glob over the path, e.g. /api/*.")] string? pathGlob = null,
        [Description("How many matches to return, newest first. Default 10.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var hits = await provider.SearchAsync(
            term,
            new CaptureQuery { Host = host, PathGlob = pathGlob, Limit = Math.Clamp(limit, 1, 50) },
            cancellationToken);

        return CaptureJson.Search(hits);
    }

    [McpServerTool(Name = "export_request")]
    [Description(
        "Turn a captured exchange into a request file you can keep — a Tap '.req.tap' or a " +
        "portable '.http'. Returns the document text plus a suggested filename; write it " +
        "wherever you want it. Redacted values become {{placeholders}} rather than masks, and " +
        "are listed so you know what to supply — a file containing [redacted:…] would be a " +
        "request that cannot work.")]
    public async Task<string> ExportRequest(
        [Description("Id of the request to export, from list_requests.")] string id,
        [Description("'tap' for a .req.tap (default), or 'http' for a portable .http file.")] string format = "tap",
        CancellationToken cancellationToken = default)
    {
        var detail = await provider.GetAsync(
            id, new CaptureDetailOptions { IncludeFrames = false }, cancellationToken);

        return detail is null
            ? CaptureJson.Error($"No captured request with id '{id}'.")
            : CaptureJson.Export(CaptureExport.Export(detail, format));
    }

    [McpServerTool(Name = "replay_request")]
    [Description(
        "Send a captured request again, optionally with an edited path, body, or headers. The " +
        "captured credential goes with it and is never shown to you, so this reproduces an " +
        "authenticated call you could not otherwise make. The destination is fixed to the host " +
        "the request came from — a relative path only, and Host cannot be edited. The replay is " +
        "itself captured, so the returned capturedId can be read straight back with " +
        "describe_request. Off unless the inspector enables it; a refusal explains why.")]
    public async Task<string> ReplayRequest(
        [Description("Id of the request to replay, from list_requests.")] string id,
        [Description("Optional replacement path, relative and starting with '/'.")] string? path = null,
        [Description("Optional replacement body.")] string? body = null,
        [Description("Optional Content-Type for a replacement body.")] string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        var result = await provider.ReplayAsync(
            new CaptureReplayRequest(id, path, body, contentType), cancellationToken);

        return CaptureJson.Replay(result);
    }

    [McpServerTool(Name = "wait_for_request")]
    [Description(
        "Block until a matching request arrives, then return its summary. Use this to watch " +
        "something happen: ask the user to tap the button, fire the webhook, or retry the " +
        "failing call, and this returns the moment it lands. Only traffic captured after the " +
        "call starts counts — for history, use list_requests. Returns matched=false on timeout.")]
    public async Task<string> WaitForRequest(
        [Description("Glob over the path, e.g. /webhooks/stripe or /api/*.")] string? pathGlob = null,
        [Description("Only this HTTP method.")] string? method = null,
        [Description("Only this host.")] string? host = null,
        [Description("Only exchanges that failed: 4xx, 5xx, or a proxy-level error.")] bool onlyErrors = false,
        [Description("How long to wait, in seconds. Default 60, maximum 300.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, (int)MaxWait.TotalSeconds));

        var result = await provider.WaitAsync(
            new CaptureQuery
            {
                Host = host,
                PathGlob = pathGlob,
                Method = method,
                OnlyErrors = onlyErrors,
                Limit = 1,
            },
            timeout,
            cancellationToken);

        return CaptureJson.Wait(result);
    }
}
