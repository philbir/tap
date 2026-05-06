using System.Threading.Channels;

namespace Tap.Server;

public sealed class InMemoryRequestStore : IRequestStore
{
    private const int Capacity = 200;

    private readonly LinkedList<RequestRecord> _records = new();
    private readonly Lock _sync = new();
    private readonly List<Channel<StoreEvent>> _subscribers = new();
    private long _sequence;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Add(RequestRecord record)
    {
        Channel<StoreEvent>[] subs;

        lock (_sync)
        {
            // Avoid duplicate insertion when streaming records were already added at start.
            if (!_records.Any(r => r.Id == record.Id))
            {
                _records.AddFirst(record);
                while (_records.Count > Capacity)
                {
                    _records.RemoveLast();
                }
            }

            subs = _subscribers.ToArray();
        }

        Broadcast(subs, new RecordEvent(record));
    }

    public void Update(RequestRecord record)
    {
        Channel<StoreEvent>[] subs;
        lock (_sync)
        {
            subs = _subscribers.ToArray();
        }
        Broadcast(subs, new RecordEvent(record));
    }

    public void AppendSseEvent(RequestRecord record, SseEvent ev)
    {
        int index;
        Channel<StoreEvent>[] subs;
        lock (_sync)
        {
            record.SseEvents.Add(ev);
            index = record.SseEvents.Count - 1;
            subs = _subscribers.ToArray();
        }
        Broadcast(subs, new SseStreamEvent(record.Id, index, ev));
    }

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
            return _records.ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
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
