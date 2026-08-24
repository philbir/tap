using System.Collections.Concurrent;

namespace Tap.Studio;

/// <summary>
/// One retained response body: a prefix of what the upstream sent, long enough to answer
/// "show me the rest" and "download the whole thing" without firing the request again.
/// </summary>
/// <param name="Id">Opaque handle the UI carries on the execution result.</param>
/// <param name="Path">Absolute path to the spool file.</param>
/// <param name="ContentType">The upstream's <c>Content-Type</c>, replayed on download.</param>
/// <param name="TotalBytes">What the upstream sent, including whatever we didn't keep.</param>
/// <param name="RetainedBytes">How much of it this file actually holds.</param>
public sealed record RetainedResponseBody(
    string Id,
    string Path,
    string? ContentType,
    long TotalBytes,
    long RetainedBytes)
{
    /// <summary>True when the file is the entire response, not a longer prefix of it.</summary>
    public bool IsComplete => RetainedBytes >= TotalBytes;
}

/// <summary>
/// Holds the last few oversized response bodies on disk so the Studio can offer more than
/// it put on screen.
///
/// <para>The body pane deliberately renders a small prefix — a 200 MB payload in a code
/// editor helps nobody. But "truncated" used to mean the rest was simply gone: the only way
/// to see it was to send the request again, which for anything non-idempotent is not an
/// option. So the executor now streams past the inline cap into a spool file, up to the
/// workspace's <c>response.maxRetainedBytes</c>, and this store hands it back.</para>
///
/// <para>Bounded on purpose: <see cref="MaxEntries"/> spools survive, oldest evicted first,
/// and the whole directory goes away when the process does. Nothing here is durable — a
/// retained body is a convenience for the response you are looking at right now, not
/// history.</para>
/// </summary>
public sealed class ResponseBodyStore : IDisposable
{
    /// <summary>How many retained bodies stay reachable. Past this the oldest is deleted —
    /// the user is looking at one response, not eight.</summary>
    private const int MaxEntries = 8;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tap-studio-responses-{Environment.ProcessId}");

    private readonly ConcurrentDictionary<string, RetainedResponseBody> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>
    /// Opens a sink for one execution. Nothing touches the disk until the body passes
    /// <paramref name="spillAfterBytes"/> — the inline cap — because the overwhelming majority
    /// of responses fit inline and writing every one of them to a temp file would be pure
    /// churn. The caller writes the whole body to it and then calls
    /// <see cref="Publish"/>; a spool that never spilled publishes as null.
    /// </summary>
    public ResponseSpool CreateSpool(long spillAfterBytes)
        => new(this, spillAfterBytes);

    /// <summary>
    /// Registers a completed spool and returns its handle, or null when the response fit
    /// inline and there is nothing extra to hand out.
    /// </summary>
    public RetainedResponseBody? Publish(ResponseSpool spool, string? contentType, long totalBytes)
    {
        var path = spool.SpilledPath;
        if (path is null) return null;

        var entry = new RetainedResponseBody(
            Id: Guid.NewGuid().ToString("n"),
            Path: path,
            ContentType: contentType,
            TotalBytes: totalBytes,
            RetainedBytes: spool.Length);

        _entries[entry.Id] = entry;

        string[] evicted;
        lock (_gate)
        {
            _order.Enqueue(entry.Id);
            var drop = new List<string>();
            while (_order.Count > MaxEntries) drop.Add(_order.Dequeue());
            evicted = [.. drop];
        }
        foreach (var id in evicted)
        {
            if (_entries.TryRemove(id, out var old)) TryDelete(old.Path);
        }

        return entry;
    }

    public RetainedResponseBody? Get(string id)
        => _entries.TryGetValue(id, out var entry) && File.Exists(entry.Path) ? entry : null;

    /// <summary>Where spool files live. Created lazily by the first spill.</summary>
    internal string EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        return _directory;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) TryDelete(entry.Path);
        _entries.Clear();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory we couldn't remove is the OS's problem now, not a reason to
            // fail shutdown.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Write-only sink that keeps the first <c>spillAfterBytes</c> in memory and moves to a temp
/// file the moment the body outgrows that. Handed to
/// <see cref="Tap.Execution.Http.ResponseCapture"/> as the retain stream, so the file it
/// leaves behind is the response from byte zero — a longer prefix, not the overflow on its
/// own, which is what makes it servable as a download.
/// </summary>
public sealed class ResponseSpool : Stream
{
    private readonly ResponseBodyStore _store;
    private readonly long _spillAfter;
    private MemoryStream? _buffer = new();
    private FileStream? _file;
    private long _length;

    internal ResponseSpool(ResponseBodyStore store, long spillAfter)
    {
        _store = store;
        _spillAfter = Math.Max(0, spillAfter);
    }

    /// <summary>Path of the spool file, or null while the body still fits in memory.</summary>
    public string? SpilledPath { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;
        _length += buffer.Length;

        if (_file is null && _length <= _spillAfter)
        {
            await _buffer!.WriteAsync(buffer, ct).ConfigureAwait(false);
            return;
        }

        if (_file is null) await SpillAsync(ct).ConfigureAwait(false);
        await _file!.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    private async Task SpillAsync(CancellationToken ct)
    {
        var path = Path.Combine(_store.EnsureDirectory(), $"{Guid.NewGuid():n}.body");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        SpilledPath = path;
        if (_buffer is { Length: > 0 })
        {
            _buffer.Position = 0;
            await _buffer.CopyToAsync(_file, ct).ConfigureAwait(false);
        }
        _buffer?.Dispose();
        _buffer = null;
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        if (_file is not null) await _file.FlushAsync(ct).ConfigureAwait(false);
    }

    public override void Flush() => _file?.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file?.Dispose();
            _buffer?.Dispose();
            _file = null;
            _buffer = null;
        }
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
