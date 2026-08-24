using System.Text.Json.Serialization;

namespace Tap.Server.Agent;

/// <summary>
/// What an agent has been doing, so the person at the inspector can see it.
///
/// <para>This is the consent story. With <c>Scope=all</c> an enabled agent reads the whole
/// ring, and the honest way to handle that is not a dialog nobody reads before the fact — it is
/// making the activity visible while it happens. A counter that ticks up while you watch tells
/// you more than a checkbox you clicked last week.</para>
///
/// <para>Counts only. Nothing here records <em>which</em> requests were read: that would be a
/// second copy of the same data with none of the redaction, and the point is visibility of the
/// access, not a transcript of it.</para>
/// </summary>
public sealed class AgentActivity(bool enabled)
{
    /// <summary>How long after its last read an agent still counts as attached. Long enough to
    /// span a human turn in a conversation, short enough that a chip does not lie for an hour.</summary>
    private static readonly TimeSpan AttachedWindow = TimeSpan.FromMinutes(2);

    private long _reads;
    private long _lastReadTicks;
    private int _waiting;

    public bool Enabled { get; } = enabled;

    /// <summary>One tool call or REST read served.</summary>
    public void RecordRead()
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Exchange(ref _lastReadTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>An agent is parked on <c>wait_for_request</c>. Worth surfacing separately: it
    /// means somebody is expecting the user to go and make something happen.</summary>
    public IDisposable BeginWait()
    {
        Interlocked.Increment(ref _waiting);
        return new WaitScope(this);
    }

    public AgentActivitySnapshot Snapshot()
    {
        var ticks = Interlocked.Read(ref _lastReadTicks);
        var lastRead = ticks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(ticks, TimeSpan.Zero);
        var waiting = Volatile.Read(ref _waiting);

        return new AgentActivitySnapshot(
            Enabled: Enabled,
            Attached: waiting > 0 || (lastRead is not null && DateTimeOffset.UtcNow - lastRead < AttachedWindow),
            Reads: Interlocked.Read(ref _reads),
            Waiting: waiting,
            LastReadAt: lastRead);
    }

    private sealed class WaitScope(AgentActivity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Idempotent: a cancelled long-poll can unwind more than once.
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._waiting);
        }
    }
}

/// <param name="Attached">An agent read recently, or is parked on a wait right now.</param>
public sealed record AgentActivitySnapshot(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("attached")] bool Attached,
    [property: JsonPropertyName("reads")] long Reads,
    [property: JsonPropertyName("waiting")] int Waiting,
    [property: JsonPropertyName("lastReadAt")] DateTimeOffset? LastReadAt);

[JsonSerializable(typeof(AgentActivitySnapshot))]
public sealed partial class AgentActivityJson : JsonSerializerContext;
