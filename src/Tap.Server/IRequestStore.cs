namespace Tap.Server;

public abstract record StoreEvent;

public sealed record RecordEvent(RequestRecord Record) : StoreEvent;

public sealed record SseStreamEvent(Guid RecordId, int Sequence, SseEvent Event) : StoreEvent;

public interface IRequestStore
{
    void Add(RequestRecord record);

    /// <summary>Re-broadcasts an existing record (e.g. when streaming finishes).</summary>
    void Update(RequestRecord record);

    /// <summary>Append a SSE event to a record and notify subscribers.</summary>
    void AppendSseEvent(RequestRecord record, SseEvent ev);

    IReadOnlyList<RequestRecord> GetAll();

    void Clear();

    IAsyncEnumerable<StoreEvent> Stream(CancellationToken cancellationToken);
}
