namespace Tap.Studio.History;

/// <summary>
/// One recorded exchange, as it sits on disk under <c>.tap-history/&lt;request-id&gt;/</c>.
///
/// <para>Self-contained on purpose. <see cref="RequestPath"/> and <see cref="RequestName"/> are
/// snapshots of what the request was called <i>at the time</i>, not links — so an entry whose
/// request has since been deleted still reads as something a human recognizes instead of a bare
/// GUID. The live request, when there still is one, is found by id through the workspace index.</para>
///
/// <para>Whether this holds real credentials or masked ones is <see cref="Redacted"/>, and it is
/// recorded rather than inferred: the policy can change between one entry and the next, and a
/// reader must be able to tell which kind it is holding without consulting configuration that
/// has moved on.</para>
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>Schema version of this document. Bumped when the shape changes in a way a
    /// reader has to know about; an entry from a future version is skipped, not guessed at.</summary>
    public int V { get; init; } = 1;

    /// <summary>Entry id — the filename stem, timestamp-prefixed so a directory listing is
    /// already in chronological order.</summary>
    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>Stable id of the request that produced this. The folder name.</summary>
    public required string RequestId { get; init; }

    /// <summary>Where the request lived when this was recorded. Informational — the id is the link.</summary>
    public string? RequestPath { get; init; }

    /// <summary>What the request was called when this was recorded.</summary>
    public string? RequestName { get; init; }

    /// <summary>Slug of the owning collection, for filtering the timeline.</summary>
    public string? Collection { get; init; }

    public string? Env { get; init; }

    /// <summary>How the exchange was run. <c>studio</c> today; the field exists so a later CLI
    /// or CI recorder doesn't have to change the format to say who it was.</summary>
    public string Source { get; init; } = "studio";

    /// <summary>True when secrets were masked before writing. False means this file holds real
    /// credentials and is encrypted at rest — the two always travel together.</summary>
    public required bool Redacted { get; init; }

    public required HistoryRequest Request { get; init; }
    public HistoryResponse? Response { get; init; }

    public double DurationMs { get; init; }

    /// <summary>Providers consulted during the render — name and secret flag only, never a
    /// value. Lets someone answer "which vault did this read from" months later.</summary>
    public IReadOnlyList<HistoryVariable> VariablesUsed { get; init; } = [];

    public IReadOnlyList<HistoryAssert> Assertions { get; init; } = [];
    public HistoryAssertSummary? AssertSummary { get; init; }

    /// <summary>Set when the exchange never completed — a refused connection, a timeout. An
    /// entry with an error and no response is still worth keeping; it is often the one you
    /// come back for.</summary>
    public string? Error { get; init; }
}

public sealed record HistoryRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string Protocol);

public sealed record HistoryResponse(
    int Status,
    string? StatusText,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    string? Body,
    long BodyBytes,
    /// <summary>True when <see cref="Body"/> is a prefix — the response outgrew
    /// <c>history.maxBodyBytes</c>. Says so on screen rather than letting someone read a
    /// truncated payload as the whole thing.</summary>
    bool BodyTruncated);

public sealed record HistoryVariable(string Provider, string Name, bool Secret);

public sealed record HistoryAssert(
    int Index, string Name, bool Ok, bool Skipped, string? Actual, string? Expected, string? Message);

public sealed record HistoryAssertSummary(bool Ok, int Passed, int Failed, int Skipped);

/// <summary>
/// The listing shape — everything a timeline row needs, and no bodies. Read from the entry file
/// itself (there is no index to drift out of step), which is affordable because the timeline
/// only ever parses the newest N files, chosen by filename.
/// </summary>
public sealed record HistorySummary(
    string Id,
    string RequestId,
    DateTimeOffset At,
    string? RequestPath,
    string? RequestName,
    string? Collection,
    string? Env,
    string Method,
    string Url,
    int? Status,
    string? StatusText,
    double DurationMs,
    long BodyBytes,
    bool Ok,
    HistoryAssertSummary? AssertSummary,
    string? Error,
    bool Encrypted,
    /// <summary>True when the entry is encrypted and this machine has no key for it. The row
    /// still renders — from the little that can be known without decrypting — rather than
    /// vanishing.</summary>
    bool Locked,
    /// <summary>True when no request with <see cref="RequestId"/> exists in the workspace any
    /// more. Not an error: a deleted request's history is still history, and it re-links by
    /// itself if the file comes back.</summary>
    bool Orphaned);
