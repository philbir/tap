using Tap.Core.Capture;
using Tap.Inspector.Mcp;

namespace Tap.Server.Agent;

/// <summary>
/// The redacted read surface at <c>/api/agent/*</c>, bound to the UI port.
///
/// <para>Two readers, one contract. <c>tap mcp</c> is an HTTP client of these endpoints — that
/// is what keeps redaction at the source rather than in the bridge — and they are equally
/// usable on their own, so CI and shell-shaped agents get the same guarantees without MCP at
/// all. The raw <c>/api/requests</c> alongside them is unchanged and still unredacted: it
/// backs the inspector UI, where a human is meant to see real values.</para>
///
/// <para>Everything here is gated on <c>Inspector:Agent:Enabled</c>. When it is off the routes
/// still answer — with a 404 that says so and names the switch. Leaving them unmapped would be
/// worse than useless: the UI port ends in a SPA fallback, so an unmapped <c>/api/agent/*</c>
/// returns <c>index.html</c> with a 200, and a client trying to parse that as JSON gets a
/// syntax error instead of "the feature is off".</para>
/// </summary>
internal static class AgentEndpoints
{
    public static void Map(IEndpointRouteBuilder ep, InspectorAgentOptions options)
    {
        if (!options.Enabled)
        {
            ep.MapGet("/api/agent/{**rest}", () => Results.Json(
                new CaptureErrorEnvelope(
                    "Agent access is disabled on this inspector.",
                    "Set Inspector__Agent__Enabled=true on the inspector, or call .WithAgentAccess() " +
                    "on the tap in your AppHost."),
                CaptureJson.Options,
                statusCode: StatusCodes.Status404NotFound));
            return;
        }

        ep.MapGet("/api/agent/requests", async (
            IMcpCaptureProvider provider,
            CancellationToken ct,
            string? host = null,
            string? pathGlob = null,
            string? method = null,
            int? status = null,
            bool onlyErrors = false,
            DateTimeOffset? since = null,
            int limit = 20) =>
        {
            var envelope = await provider.ListAsync(
                new CaptureQuery
                {
                    Host = host,
                    PathGlob = pathGlob,
                    Method = method,
                    Status = status,
                    OnlyErrors = onlyErrors,
                    Since = since,
                    Limit = Math.Clamp(limit, 1, 200),
                },
                ct);

            return Results.Json(envelope, CaptureJson.Options);
        });

        ep.MapGet("/api/agent/requests/{id}", async (
            string id,
            IMcpCaptureProvider provider,
            CancellationToken ct,
            bool includeHeaders = true,
            bool includeBodies = true,
            bool includeFrames = true,
            int maxBodyChars = 16_384,
            int maxFrames = 50) =>
        {
            var detail = await provider.GetAsync(
                id,
                new CaptureDetailOptions
                {
                    IncludeHeaders = includeHeaders,
                    IncludeBodies = includeBodies,
                    IncludeFrames = includeFrames,
                    MaxBodyChars = Math.Clamp(maxBodyChars, 256, 262_144),
                    MaxFrames = Math.Clamp(maxFrames, 1, 500),
                },
                ct);

            return detail is null
                ? Results.Json(
                    new CaptureErrorEnvelope(
                        $"No captured request with id '{id}'.",
                        "The inspector keeps the most recent 200 exchanges, so it may have been evicted."),
                    CaptureJson.Options,
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Json(CaptureDetailEnvelope.For(detail), CaptureJson.Options);
        });

        ep.MapGet("/api/agent/diff", async (
            string left,
            string right,
            IMcpCaptureProvider provider,
            CancellationToken ct) =>
        {
            var options = new CaptureDetailOptions { IncludeFrames = false };
            var a = await provider.GetAsync(left, options, ct);
            var b = await provider.GetAsync(right, options, ct);

            var missing = a is null ? left : b is null ? right : null;
            return missing is not null
                ? Results.Json(
                    new CaptureErrorEnvelope($"No captured request with id '{missing}'."),
                    CaptureJson.Options,
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Json(CaptureDiff.Compare(a!, b!), CaptureJson.Options);
        });

        // Long-poll. Deliberately a GET with no side effects: it changes nothing, it just
        // declines to answer until something happens.
        ep.MapGet("/api/agent/wait", async (
            IMcpCaptureProvider provider,
            CancellationToken ct,
            string? host = null,
            string? pathGlob = null,
            string? method = null,
            bool onlyErrors = false,
            int timeoutSeconds = 60) =>
        {
            var result = await provider.WaitAsync(
                new CaptureQuery
                {
                    Host = host,
                    PathGlob = pathGlob,
                    Method = method,
                    OnlyErrors = onlyErrors,
                    Limit = 1,
                },
                TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)),
                ct);

            return Results.Json(result, CaptureJson.Options);
        });
    }
}
