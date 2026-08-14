using Tap.Execution.Contracts;
using Tap.Execution.Http;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Asserts;

/// <summary>
/// The one place a response becomes an assertion verdict. Both execute endpoints and the
/// re-evaluate endpoint go through here, so a pass shown after a Send, a pass shown while
/// editing the assertion, and a pass computed by a future headless runner are the same
/// computation over the same snapshot.
/// </summary>
public static class AssertRunner
{
    private const string WebSocketUnsupported =
        "Assertions are not evaluated for WebSocket requests yet.";

    public static (IReadOnlyList<AssertResultDto> Results, AssertSummaryDto? Summary) Run(
        ResolvedRequest rendered,
        int status,
        IReadOnlyDictionary<string, string> responseHeaders,
        string? bodyText,
        long totalBytes,
        double durationMs)
        => Run(rendered.Assertions, rendered.Protocol, status, responseHeaders, bodyText, totalBytes, durationMs);

    /// <summary>
    /// Overload for callers that assembled the assertion list themselves. A test run evaluates
    /// the request file's own assertions together with the ones its step or test entry adds —
    /// one snapshot, one contiguous index space, so a result row always points at the row above
    /// it in the editor.
    /// </summary>
    public static (IReadOnlyList<AssertResultDto> Results, AssertSummaryDto? Summary) Run(
        IReadOnlyList<ResolvedAssert> assertions,
        RequestProtocol protocol,
        int status,
        IReadOnlyDictionary<string, string> responseHeaders,
        string? bodyText,
        long totalBytes,
        double durationMs)
    {
        if (assertions.Count == 0) return ([], null);

        var results = protocol == RequestProtocol.WebSocket
            ? AssertEvaluator.SkipAll(assertions, WebSocketUnsupported)
            : AssertEvaluator.Evaluate(assertions, new ResponseSnapshot
            {
                Status = status,
                Headers = responseHeaders.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToArray(),
                BodyText = bodyText,
                // TryDecodeBody appends a "…truncated" marker, but the assertion layer needs the
                // fact, not the marker — matching a prefix would report a pass the full response
                // might not have earned.
                BodyTruncated = totalBytes > HttpExecutionHelpers.BodyCap,
                DurationMs = durationMs,
            });

        return (AssertResultMapper.ToDto(results), AssertResultMapper.ToDto(AssertSummary.From(results)));
    }

    /// <summary>Result shape for a request that never reached the wire (render failure,
    /// connection refused). The assertions are neither passed nor failed — there was nothing
    /// to check them against.</summary>
    public static (IReadOnlyList<AssertResultDto> Results, AssertSummaryDto? Summary) NotRun(
        ResolvedRequest? rendered, string reason)
    {
        if (rendered is null || rendered.Assertions.Count == 0) return ([], null);
        var results = AssertEvaluator.SkipAll(rendered.Assertions, reason);
        return (AssertResultMapper.ToDto(results), AssertResultMapper.ToDto(AssertSummary.From(results)));
    }
}
