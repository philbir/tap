using Tap.Core.Capture;

namespace Tap.Inspector.Mcp;

/// <summary>
/// Where the inspector's MCP tools get captured traffic for one call — the only seam between
/// the shared tool layer and its two hosts, because it is the only thing they genuinely
/// disagree about.
///
/// <para><c>Tap.Server</c> implements it over the live in-memory ring: no hop, no copy, and
/// <see cref="WaitAsync"/> is a real subscription to the store's event stream. <c>Tap.Cli</c>
/// implements it as an HTTP client of the inspector's redacted <c>/api/agent/*</c> surface,
/// because a separate process cannot read another process's memory — and must not: what
/// crosses that boundary is already projected and redacted, so the bridge never handles a raw
/// record. Redaction happens at the source or it does not happen.</para>
///
/// <para>Everything returned here is already redacted. An implementation that hands back a raw
/// record has misunderstood the interface.</para>
/// </summary>
public interface IMcpCaptureProvider
{
    /// <summary>Newest first, capped by <see cref="CaptureQuery.Limit"/>.</summary>
    Task<CaptureListEnvelope> ListAsync(CaptureQuery query, CancellationToken cancellationToken);

    /// <summary>One exchange in full, or <c>null</c> if the id is unknown — the ring is 200
    /// records deep, so "unknown" usually means "evicted", not "never existed".</summary>
    Task<CapturedRequestDetail?> GetAsync(
        string id, CaptureDetailOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Block until traffic matching <paramref name="query"/> arrives, or the timeout expires.
    /// Only traffic captured <em>after</em> the call starts counts: the point is to watch a
    /// human tap a button, not to re-read history that <see cref="ListAsync"/> already covers.
    /// </summary>
    Task<CaptureWaitEnvelope> WaitAsync(
        CaptureQuery query, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Send a captured exchange again, through the inspector's own proxy so the replay is
    /// itself captured. The <em>captured</em> credential goes with it and is never handed to
    /// the caller — the same "fully-authenticated request, zero credentials in context"
    /// property Studio's agent surface has, arrived at from the other direction.
    ///
    /// <para>A write, so it is gated separately from the read tools and off unless someone
    /// turned it on.</para>
    /// </summary>
    Task<CaptureReplayEnvelope> ReplayAsync(
        CaptureReplayRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Free-text search over <b>redacted</b> text. Never over the raw record: a search that saw
    /// through the masks would let a caller binary-search a hidden value and read the answer off
    /// the result count.
    /// </summary>
    Task<IReadOnlyList<CaptureSearchHit>> SearchAsync(
        string term, CaptureQuery query, CancellationToken cancellationToken);
}
