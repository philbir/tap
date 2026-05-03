using System.Threading.Channels;

namespace Tap.Server;

public sealed class InMemoryRequestStore : IRequestStore
{
    private const int Capacity = 200;

    private readonly LinkedList<RequestRecord> _records = new();
    private readonly Lock _sync = new();
    private readonly List<Channel<RequestRecord>> _subscribers = new();
    private long _sequence;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Add(RequestRecord record)
    {
        Channel<RequestRecord>[] subs;

        lock (_sync)
        {
            _records.AddFirst(record);
            while (_records.Count > Capacity)
            {
                _records.RemoveLast();
            }

            subs = _subscribers.ToArray();
        }

        foreach (var sub in subs)
        {
            sub.Writer.TryWrite(record);
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

    public async IAsyncEnumerable<RequestRecord> Stream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RequestRecord>(new UnboundedChannelOptions
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
            await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return record;
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
