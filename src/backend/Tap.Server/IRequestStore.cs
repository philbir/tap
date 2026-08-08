namespace Tap.Server;

public abstract record StoreEvent;

public sealed record RecordEvent(RequestRecord Record) : StoreEvent;

/// <summary><paramref name="Sequence"/> is the frame's absolute ordinal in the stream, not
/// its index in <see cref="RequestRecord.SseEvents"/> — the list is a trimmed tail once the
/// capture caps kick in, and subscribers dedupe on a monotonically increasing value.</summary>
public sealed record SseStreamEvent(Guid RecordId, int Sequence, SseEvent Event) : StoreEvent;

/// <summary><paramref name="Sequence"/> is the frame's absolute ordinal, as for
/// <see cref="SseStreamEvent"/>.</summary>
public sealed record WebSocketStreamEvent(Guid RecordId, int Sequence, WebSocketMessage Message) : StoreEvent;

public interface IRequestStore
{
    void Add(RequestRecord record);

    /// <summary>Re-broadcasts an existing record (e.g. when streaming finishes).</summary>
    void Update(RequestRecord record);

    /// <summary>
    /// Append a SSE event to a record and notify subscribers. Capture is best-effort:
    /// once the record's frame caps or the store's byte budget are reached the event is
    /// counted in <see cref="RequestRecord.SseEventsDropped"/> instead of stored.
    /// </summary>
    void AppendSseEvent(RequestRecord record, SseEvent ev);

    /// <summary>
    /// Append a WebSocket message to a record and notify subscribers. Best-effort in the
    /// same way as <see cref="AppendSseEvent"/>, counting into
    /// <see cref="RequestRecord.WebSocketMessagesDropped"/>.
    /// </summary>
    void AppendWebSocketMessage(RequestRecord record, WebSocketMessage message);

    /// <summary>
    /// Point-in-time copies of the stored records. Callers serialize outside the store
    /// lock, so they must never be handed the live, still-mutating instances.
    /// </summary>
    IReadOnlyList<RequestRecord> GetAll();

    /// <summary>Drops all records and the frames they hold, including for still-open streams.</summary>
    void Clear();

    IAsyncEnumerable<StoreEvent> Stream(CancellationToken cancellationToken);
}
