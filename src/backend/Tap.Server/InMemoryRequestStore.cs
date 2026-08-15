using System.Threading.Channels;

namespace Tap.Server;

public sealed class InMemoryRequestStore : IRequestStore
{
    private const int Capacity = 200;

    // The record ring is bounded, but each streaming record used to grow forever: a
    // long-lived SSE response or WebSocket is an unbounded allocation for as long as it
    // stays open. Frames are therefore ringed per record and charged against a global
    // budget so 200 records can never sum past a fixed ceiling.
    private const int MaxFramesPerRecord = 500;
    private const long MaxFrameBytesPerRecord = 2_000_000;
    private const long MaxTotalFrameBytes = 32_000_000;

    // Trim in batches: RemoveRange from the head of a List is O(n), so dropping one frame
    // per append would make a saturated stream quadratic.
    private const int TrimBatchSize = 64;

    // Rough UTF-16 accounting plus a fixed per-frame overhead for the object headers and
    // timestamps. Exact byte counts are not worth the bookkeeping; the point is a ceiling.
    private const int FrameOverheadBytes = 96;

    private static readonly Func<SseEvent, int> SseFrameSize = EstimateSize;
    private static readonly Func<WebSocketMessage, int> WebSocketFrameSize = EstimateSize;

    private readonly LinkedList<RequestRecord> _records = new();
    private readonly Lock _sync = new();
    private readonly List<Channel<StoreEvent>> _subscribers = new();
    private long _sequence;
    private long _frameBytes;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Add(RequestRecord record)
    {
        Channel<StoreEvent>[] subs;
        RequestRecord snapshot;

        lock (_sync)
        {
            // Avoid duplicate insertion when streaming records were already added at start.
            if (!_records.Any(r => r.Id == record.Id))
            {
                _records.AddFirst(record);
                while (_records.Count > Capacity)
                {
                    var evicted = _records.Last!.Value;
                    _records.RemoveLast();
                    Detach(evicted);
                }
            }

            snapshot = record.Snapshot();
            subs = _subscribers.ToArray();
        }

        Broadcast(subs, new RecordEvent(snapshot));
    }

    public void Update(RequestRecord record)
    {
        Channel<StoreEvent>[] subs;
        RequestRecord snapshot;
        lock (_sync)
        {
            snapshot = record.Snapshot();
            subs = _subscribers.ToArray();
        }
        Broadcast(subs, new RecordEvent(snapshot));
    }

    public void AppendSseEvent(RequestRecord record, SseEvent ev)
    {
        int ordinal;
        Channel<StoreEvent>[] subs;
        lock (_sync)
        {
            if (record.CaptureDetached)
            {
                return;
            }

            var size = EstimateSize(ev);
            record.SseEventsDropped += TrimFrames(record, record.SseEvents, size, SseFrameSize);

            if (_frameBytes + size > MaxTotalFrameBytes)
            {
                record.SseEventsDropped++;
                return;
            }

            record.SseEvents.Add(ev);
            record.CapturedFrameBytes += size;
            _frameBytes += size;

            // Absolute ordinal, not the list index: the list is a tail once trimming
            // starts, and subscribers dedupe on a monotonically increasing sequence.
            ordinal = record.SseEventsDropped + record.SseEvents.Count - 1;
            subs = _subscribers.ToArray();
        }
        Broadcast(subs, new SseStreamEvent(record.Id, ordinal, ev));
    }

    public void AppendWebSocketMessage(RequestRecord record, WebSocketMessage message)
    {
        int ordinal;
        Channel<StoreEvent>[] subs;
        lock (_sync)
        {
            if (record.CaptureDetached)
            {
                return;
            }

            var size = EstimateSize(message);
            record.WebSocketMessagesDropped +=
                TrimFrames(record, record.WebSocketMessages, size, WebSocketFrameSize);

            if (_frameBytes + size > MaxTotalFrameBytes)
            {
                record.WebSocketMessagesDropped++;
                return;
            }

            record.WebSocketMessages.Add(message);
            record.CapturedFrameBytes += size;
            _frameBytes += size;

            ordinal = record.WebSocketMessagesDropped + record.WebSocketMessages.Count - 1;
            subs = _subscribers.ToArray();
        }
        Broadcast(subs, new WebSocketStreamEvent(record.Id, ordinal, message));
    }

    /// <summary>
    /// Drops the oldest frames until <paramref name="incoming"/> fits the per-record caps
    /// and returns how many were dropped. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private int TrimFrames<T>(RequestRecord record, List<T> frames, int incoming, Func<T, int> sizeOf)
    {
        var dropped = 0;
        while (frames.Count > 0 &&
               (frames.Count >= MaxFramesPerRecord ||
                record.CapturedFrameBytes + incoming > MaxFrameBytesPerRecord))
        {
            var drop = Math.Min(TrimBatchSize, frames.Count);
            long freed = 0;
            for (var i = 0; i < drop; i++)
            {
                freed += sizeOf(frames[i]);
            }

            frames.RemoveRange(0, drop);
            record.CapturedFrameBytes -= freed;
            _frameBytes -= freed;
            dropped += drop;
        }

        return dropped;
    }

    /// <summary>
    /// Releases a record's frames back to the global budget and stops any pump still
    /// running against it from capturing more. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private void Detach(RequestRecord record)
    {
        _frameBytes -= record.CapturedFrameBytes;
        record.CapturedFrameBytes = 0;
        record.SseEvents.Clear();
        record.WebSocketMessages.Clear();
        record.CaptureDetached = true;
    }

    private static int EstimateSize(SseEvent ev) =>
        FrameOverheadBytes +
        2 * (ev.Data.Length + ev.EventName.Length + (ev.Id?.Length ?? 0) + (ev.Comment?.Length ?? 0));

    private static int EstimateSize(WebSocketMessage message) =>
        FrameOverheadBytes +
        2 * ((message.Text?.Length ?? 0) + (message.Base64?.Length ?? 0) +
             (message.CloseDescription?.Length ?? 0));

    private static void Broadcast(Channel<StoreEvent>[] subs, StoreEvent ev)
    {
        foreach (var sub in subs)
        {
            sub.Writer.TryWrite(ev);
        }
    }

    public IReadOnlyList<RequestRecord> GetAll()
    {
        lock (_sync)
        {
            // Snapshot under the lock: callers serialize the result outside it, and a
            // concurrent frame append into a live record would otherwise throw mid-response.
            return _records.Select(r => r.Snapshot()).ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var record in _records)
            {
                Detach(record);
            }

            _records.Clear();
            _frameBytes = 0;
        }
    }

    public async IAsyncEnumerable<StoreEvent> Stream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<StoreEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_sync)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return ev;
            }
        }
        finally
        {
            lock (_sync)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        }
    }
}
