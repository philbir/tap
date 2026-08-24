using Microsoft.Extensions.Logging;
using Tap.Execution.Contracts;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.History;

/// <summary>
/// Turns a completed exchange into a <see cref="HistoryEntry"/> and hands it to the
/// <see cref="HistoryStore"/> — the one place that decides what a recorded request looks like.
///
/// <para>Both of the Studio's send paths call this: the one-shot <c>ExecuteEndpoint</c> and the
/// streaming <c>ExecuteStreamEndpoint</c>. They differ in how the body arrives, not in what is
/// worth keeping, so they hand over the same five things and the shape stays identical between
/// a plain GET and an SSE stream.</para>
///
/// <para>Nothing here throws at the caller. A send that reached the upstream and came back is a
/// success; failing it afterwards because a disk was full would be the recorder deciding it
/// matters more than the thing it is recording.</para>
/// </summary>
public sealed class HistoryRecorder(IHistoryStores stores, ILogger<HistoryRecorder> logger)
{
    /// <summary>
    /// Records one exchange, if the request asked to be recorded and can be identified.
    ///
    /// <para>Two conditions turn this into a no-op, both deliberate. History off is the default.
    /// And a request with no <c>id:</c> has no durable identity to file under — recording it
    /// against its path would produce a folder that a rename silently orphans, which is the
    /// failure the id exists to prevent. The Studio assigns an id on save, so in practice this
    /// only skips a request that has never been saved through it.</para>
    /// </summary>
    public void TryRecord(
        LoadedWorkspace workspace,
        ResolvedRequest rendered,
        HistoryResponse? response,
        double durationMs,
        IReadOnlyList<AssertResultDto> assertions,
        AssertSummaryDto? assertSummary,
        string? error)
    {
        var options = rendered.History;
        if (!options.EffectiveEnabled) return;

        var requestPath = rendered.Metadata.SourceRequestPath;
        var file = workspace.FindByPath(requestPath);
        var requestId = file?.Id;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            logger.LogDebug(
                "History is enabled for {Path} but it has no id, so there is nothing stable to file the "
                + "exchange under. Saving the request once assigns one.", requestPath);
            return;
        }

        var at = DateTimeOffset.UtcNow;
        var entry = Build(at, requestId, file, rendered, response, durationMs, assertions, assertSummary, error, options);

        var written = stores.Current.TryWrite(entry, options, out var problem);
        if (written is null && problem is not null)
            logger.LogWarning("Could not record history for {Path}: {Problem}", requestPath, problem);
    }

    private static HistoryEntry Build(
        DateTimeOffset at,
        string requestId,
        WorkspaceFile? file,
        ResolvedRequest rendered,
        HistoryResponse? response,
        double durationMs,
        IReadOnlyList<AssertResultDto> assertions,
        AssertSummaryDto? assertSummary,
        string? error,
        HistoryOptions options)
    {
        // Encryption is what licenses keeping the real values: the file is unreadable without
        // this machine's key. Without it the entry is plaintext on someone's disk and inside
        // whatever backs it up, so the redactor runs — the same one the CLI's --json and the MCP
        // results go through, masking credential headers by name and every resolved secret by
        // value wherever it landed.
        var redact = !options.EffectiveEncrypt;
        var redactor = rendered.Redactor;

        var requestHeaders = redact ? redactor.RedactHeaders(rendered.Headers) : rendered.Headers;
        var requestBody = redact ? redactor.Redact(rendered.Body) : rendered.Body;
        // The URL too: a token in a query string is exactly the case value-replacement exists
        // for, and it is the surface people forget because it doesn't look like a credential
        // field.
        var requestUrl = (redact ? redactor.Redact(rendered.Url) : rendered.Url) ?? rendered.Url;

        if (response is not null && redact)
        {
            response = response with
            {
                Headers = redactor.RedactHeaders(response.Headers),
                Body = redactor.Redact(response.Body),
            };
        }

        response = Cap(response, options.EffectiveMaxBodyBytes);

        return new HistoryEntry
        {
            Id = HistoryStore.NewEntryId(at),
            At = at,
            RequestId = requestId,
            RequestPath = rendered.Metadata.SourceRequestPath,
            RequestName = file?.Name,
            Collection = CollectionSlug(rendered.Metadata.SourceRequestPath),
            Env = rendered.Metadata.EnvPath,
            Redacted = redact,
            Request = new HistoryRequest(
                rendered.Method, requestUrl, requestHeaders, requestBody, rendered.Protocol.ToString().ToLowerInvariant()),
            Response = response,
            DurationMs = durationMs,
            VariablesUsed = [.. rendered.Metadata.VariablesUsed
                .Select(v => new HistoryVariable(v.ProviderName, v.Name, v.IsSecret))],
            // An assertion's `actual` is a slice of the response and its `message` quotes both
            // sides, so a failing check against a secret-bearing field would otherwise smuggle
            // the value past everything above.
            Assertions = [.. assertions.Select(a => new HistoryAssert(
                a.Index, a.Name, a.Ok, a.Skipped,
                redact ? redactor.Redact(a.Actual) : a.Actual,
                redact ? redactor.Redact(a.Expected) : a.Expected,
                redact ? redactor.Redact(a.Message) : a.Message))],
            AssertSummary = assertSummary is null
                ? null
                : new HistoryAssertSummary(assertSummary.Ok, assertSummary.Passed, assertSummary.Failed, assertSummary.Skipped),
            Error = error,
        };
    }

    /// <summary>
    /// Trims a stored body to the configured cap, marking it truncated when it trims. The cap is
    /// deliberately far below what the response panel will show you live: history is a folder
    /// that grows unattended, and a megabyte per entry times twenty-five entries times every
    /// request in a workspace is a number nobody signed up for.
    ///
    /// <para><c>BodyBytes</c> keeps reporting what the upstream actually sent — the size of the
    /// response, not the size of what we chose to keep.</para>
    /// </summary>
    private static HistoryResponse? Cap(HistoryResponse? response, long maxBodyBytes)
    {
        if (response?.Body is not { Length: > 0 } body) return response;
        if (maxBodyBytes <= 0) return response with { Body = null, BodyTruncated = true };
        // Character count is a fair proxy: the cap exists to bound the file, and every extra
        // char is at most four bytes of UTF-8.
        if (body.Length <= maxBodyBytes) return response;
        return response with { Body = body[..(int)maxBodyBytes], BodyTruncated = true };
    }

    /// <summary>The owning collection's slug, read off the path. Positional by design — every
    /// request lives under <c>collections/&lt;slug&gt;/</c> — and used only to filter the
    /// timeline, so an unfiled request simply has none.</summary>
    private static string? CollectionSlug(string requestPath)
    {
        var parts = requestPath.Split('/');
        return parts.Length >= 3 && parts[0] == "collections" ? parts[1] : null;
    }
}
