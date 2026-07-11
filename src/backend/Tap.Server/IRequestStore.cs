namespace Tap.Server;

public abstract record StoreEvent;

public sealed record RecordEvent(RequestRecord Record) : StoreEvent;

public sealed record SseStreamEvent(Guid RecordId, int Sequence, SseEvent Event) : StoreEvent;

public sealed record WebSocketStreamEvent(Guid RecordId, int Sequence, WebSocketMessage Message) : StoreEvent;

public interface IRequestStore
{
    void Add(RequestRecord record);

    /// <summary>Re-broadcasts an existing record (e.g. when streaming finishes).</summary>
    void Update(RequestRecord record);

    /// <summary>Append a SSE event to a record and notify subscribers.</summary>
    void AppendSseEvent(RequestRecord record, SseEvent ev);

    /// <summary>Append a WebSocket message to a record and notify subscribers.</summary>
    void AppendWebSocketMessage(RequestRecord record, WebSocketMessage message);

    IReadOnlyList<RequestRecord> GetAll();

    void Clear();

    IAsyncEnumerable<StoreEvent> Stream(CancellationToken cancellationToken);
}
