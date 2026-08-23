using Tap.Core.Capture;
using Tap.Core.Redaction;
using Tap.Inspector.Mcp;

namespace Tap.Server.Agent;

/// <summary>
/// Serves the agent surface straight from the live in-memory ring — no hop, no copy, and a
/// real subscription for <see cref="WaitAsync"/>.
///
/// <para>This is where redaction happens, and it happens here so that it happens exactly once,
/// at the source. Everything that leaves this class is already projected; nothing downstream —
/// the REST endpoints, the stdio bridge, an MCP client — ever handles a raw record, so none of
/// them can leak one.</para>
///
/// <para>One redactor for the process lifetime, because fingerprints are only comparable
/// within a single salt. Two requests read minutes apart must produce the same
/// <c>#a91f3c2d</c> for the same token, or the correlation the whole design leans on stops
/// working.</para>
/// </summary>
public sealed class StoreCaptureProvider(
    IRequestStore store,
    InspectorAgentOptions options,
    AgentActivity activity,
    CaptureReplayer? replayer = null)
    : IMcpCaptureProvider
{
    private readonly CaptureRedactor _redactor = new(options.ToRedactionOptions());

    /// <summary>
    /// Under <c>Scope=since-attach</c>, the ring position an agent first looked at. Set on the
    /// first read rather than at construction, because this provider is a singleton created
    /// when the inspector starts — "attach" has to mean when an agent showed up, not when the
    /// process did, or the scope would mean nothing.
    /// </summary>
    private long _floor = -1;

    private bool SinceAttach => options.Scope.Equals("since-attach", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records the read and, on the first one under <c>since-attach</c>, pins the
    /// floor at whatever the ring already held.</summary>
    private long Attach()
    {
        activity.RecordRead();
        if (!SinceAttach) return -1;

        if (Interlocked.Read(ref _floor) < 0)
        {
            var highWater = store.GetAll().Select(r => r.Sequence).DefaultIfEmpty(0).Max();
            Interlocked.CompareExchange(ref _floor, highWater, -1);
        }

        return Interlocked.Read(ref _floor);
    }

    public Task<CaptureListEnvelope> ListAsync(CaptureQuery query, CancellationToken cancellationToken)
    {
        var floor = Attach();
        var matches = new List<CapturedRequestSummary>();
        var available = 0;

        // Newest first: a debugging session cares about the tail, and the caller's limit should
        // trim the far end of history rather than the request that just arrived.
        foreach (var record in store.GetAll().OrderByDescending(r => r.Sequence))
        {
            if (!options.AllowsHost(record.Host)) continue;
            if (record.Sequence <= floor) continue;

            var summary = CaptureProjection.Summarize(record, _redactor);
            if (!query.Matches(summary)) continue;

            available++;
            if (matches.Count < query.Limit) matches.Add(summary);
        }

        return Task.FromResult(CaptureListEnvelope.For(matches, available));
    }

    public Task<CapturedRequestDetail?> GetAsync(
        string id, CaptureDetailOptions detailOptions, CancellationToken cancellationToken)
    {
        var floor = Attach();
        if (!Guid.TryParse(id, out var recordId)) return Task.FromResult<CapturedRequestDetail?>(null);

        var record = store.GetAll().FirstOrDefault(r => r.Id == recordId);
        if (record is null || !options.AllowsHost(record.Host) || record.Sequence <= floor)
        {
            return Task.FromResult<CapturedRequestDetail?>(null);
        }

        return Task.FromResult<CapturedRequestDetail?>(
            CaptureProjection.Describe(record, _redactor, detailOptions));
    }

    public async Task<CaptureWaitEnvelope> WaitAsync(
        CaptureQuery query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Attach();

        // Surfaced separately from a plain read: an agent parked here means somebody is
        // expecting the person at the keyboard to go and make something happen.
        using var parked = activity.BeginWait();

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(timeout);

        // A record is added to the store when the inspector first knows about it. For an
        // ordinary request that is after the response completed, so it arrives settled; for a
        // stream or a WebSocket it is when the exchange opened, and the status fills in later.
        // Hold the early sighting and prefer the settled version, but never let the caller time
        // out empty-handed when the traffic it asked about demonstrably arrived.
        CapturedRequestSummary? unsettled = null;

        try
        {
            await foreach (var storeEvent in store.Stream(expiry.Token))
            {
                if (storeEvent is not RecordEvent(var record)) continue;
                if (!options.AllowsHost(record.Host)) continue;

                var summary = CaptureProjection.Summarize(record, _redactor);
                if (!query.Matches(summary)) continue;

                if (summary.Status != 0 || summary.Error is not null) return CaptureWaitEnvelope.Found(summary);
                unsettled ??= summary;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout, not the caller giving up.
        }

        return unsettled is not null
            ? CaptureWaitEnvelope.Found(unsettled)
            : CaptureWaitEnvelope.TimedOut(timeout);
    }

    public async Task<CaptureReplayEnvelope> ReplayAsync(
        CaptureReplayRequest request, CancellationToken cancellationToken)
    {
        Attach();

        if (!options.AllowReplay || replayer is null)
        {
            return CaptureReplayEnvelope.Refused(
                "Replay is off. Reading captured traffic and re-sending it are separate " +
                "decisions: set Inspector__Agent__AllowReplay=true, or call " +
                ".WithAgentAccess(allowReplay: true) on the tap in your AppHost.");
        }

        if (!Guid.TryParse(request.Id, out var recordId))
        {
            return CaptureReplayEnvelope.Refused($"'{request.Id}' is not a request id.");
        }

        var record = store.GetAll().FirstOrDefault(r => r.Id == recordId);
        if (record is null || !options.AllowsHost(record.Host))
        {
            return CaptureReplayEnvelope.Refused($"No captured request with id '{request.Id}'.");
        }

        return await replayer.ReplayAsync(record, request, cancellationToken);
    }

    public Task<IReadOnlyList<CaptureSearchHit>> SearchAsync(
        string term, CaptureQuery query, CancellationToken cancellationToken)
    {
        var floor = Attach();
        var hits = new List<CaptureSearchHit>();

        if (string.IsNullOrWhiteSpace(term)) return Task.FromResult<IReadOnlyList<CaptureSearchHit>>(hits);

        // Details, not summaries: the term is usually in a body. Every one of them is redacted
        // first, which is what keeps the search from being an oracle over hidden values.
        var detailOptions = new CaptureDetailOptions { IncludeFrames = false };

        foreach (var record in store.GetAll().OrderByDescending(r => r.Sequence))
        {
            if (!options.AllowsHost(record.Host)) continue;
            if (record.Sequence <= floor) continue;

            var detail = CaptureProjection.Describe(record, _redactor, detailOptions);
            if (!query.Matches(detail.Summary)) continue;

            if (CaptureSearch.Find(detail, term) is { } hit) hits.Add(hit);
            if (hits.Count >= query.Limit) break;
        }

        return Task.FromResult<IReadOnlyList<CaptureSearchHit>>(hits);
    }
}
