using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Tap.Server;

/// <summary>
/// Wraps the original response body. Buffers writes into a MemoryStream by default,
/// but switches to streaming pass-through (with per-chunk flush + SSE parsing) the
/// first time a write reveals a <c>text/event-stream</c> content type.
/// </summary>
public sealed class CapturingResponseStream : Stream
{
    private const long MaxCaptureBytes = 1_000_000;

    private readonly HttpContext _ctx;
    private readonly Stream _origin;
    private readonly RequestRecord _record;
    private readonly IRequestStore _store;

    private readonly MemoryStream _buffer = new();
    private bool _decided;
    private bool _streaming;
    private bool _passthrough;
    private long _bytesWritten;

    private readonly StringBuilder _data = new();
    private string _eventName = "message";
    private string? _eventId;
    private int? _retry;
    private readonly StringBuilder _line = new();
    private bool _carryCr;

    public CapturingResponseStream(HttpContext ctx, Stream origin, RequestRecord record, IRequestStore store)
    {
        _ctx = ctx;
        _origin = origin;
        _record = record;
        _store = store;
    }

    public MemoryStream Buffer => _buffer;
    public bool IsStreaming => _streaming;
    public bool IsPassthrough => _passthrough;
    public long BytesWritten => _bytesWritten;

    public bool TryResetBufferedResponse()
    {
        if (_streaming || _passthrough)
        {
            return false;
        }

        _buffer.SetLength(0);
        _buffer.Position = 0;
        _bytesWritten = 0;
        return true;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _origin.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _origin.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await WriteCoreAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => new(WriteCoreAsync(buffer, cancellationToken));

    private async Task WriteCoreAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        EnsureDecision();
        _bytesWritten += chunk.Length;

        if (_streaming || _passthrough)
        {
            await _origin.WriteAsync(chunk, ct);
            if (_streaming)
            {
                await _origin.FlushAsync(ct);
                ProcessSseChunk(chunk.Span);
            }
            return;
        }

        if (_buffer.Length + chunk.Length > MaxCaptureBytes)
        {
            _record.ResponseBodyTruncated = true;
            _passthrough = true;
            _buffer.Position = 0;
            await _buffer.CopyToAsync(_origin, ct);
            await _origin.WriteAsync(chunk, ct);
            return;
        }

        await _buffer.WriteAsync(chunk, ct);
    }

    private void EnsureDecision()
    {
        if (_decided)
        {
            return;
        }
        _decided = true;

        var contentType = _ctx.Response.ContentType;
        if (string.IsNullOrEmpty(contentType) || !contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _streaming = true;

        // Make the wire flow real-time: stop Kestrel from buffering writes.
        _ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        _record.IsStream = true;
        _record.StatusCode = _ctx.Response.StatusCode;
        _record.ResponseContentType = contentType;
        _record.ResponseHeaders = SnapshotHeaders(_ctx.Response.Headers);
        _store.Add(_record);
    }

    private static Dictionary<string, string> SnapshotHeaders(IHeaderDictionary headers)
    {
        var snapshot = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            snapshot[h.Key] = h.Value.ToString();
        }
        return snapshot;
    }

    // SSE parser: spec-lite. Lines separated by \n, \r\n, or \r. An empty line
    // dispatches the buffered event. Lines beginning with ":" are comments.
    private void ProcessSseChunk(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (_carryCr)
            {
                _carryCr = false;
                if (c == '\n') continue; // already dispatched on \r
            }
            if (c == '\n')
            {
                DispatchLine(_line.ToString());
                _line.Clear();
                continue;
            }
            if (c == '\r')
            {
                DispatchLine(_line.ToString());
                _line.Clear();
                _carryCr = true;
                continue;
            }
            _line.Append(c);
        }
    }

    private void DispatchLine(string line)
    {
        if (line.Length == 0)
        {
            // Blank line: dispatch event if any data accumulated.
            if (_data.Length > 0 || _eventId != null || _retry != null)
            {
                var dataStr = _data.ToString();
                if (dataStr.EndsWith('\n'))
                {
                    dataStr = dataStr[..^1];
                }
                var ev = new SseEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventName: _eventName,
                    Data: dataStr,
                    Id: _eventId,
                    Retry: _retry,
                    Comment: null);
                _store.AppendSseEvent(_record, ev);
            }
            _data.Clear();
            _eventName = "message";
            _retry = null;
            // _eventId carries forward across events per spec
            return;
        }

        if (line[0] == ':')
        {
            var comment = line.Length > 1 ? line[1..].TrimStart(' ') : string.Empty;
            var ev = new SseEvent(
                Timestamp: DateTimeOffset.UtcNow,
                EventName: "comment",
                Data: comment,
                Id: null,
                Retry: null,
                Comment: comment);
            _store.AppendSseEvent(_record, ev);
            return;
        }

        var colon = line.IndexOf(':');
        string field, value;
        if (colon < 0)
        {
            field = line;
            value = string.Empty;
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }
        }

        switch (field)
        {
            case "event":
                _eventName = value;
                break;
            case "data":
                _data.Append(value).Append('\n');
                break;
            case "id":
                _eventId = value;
                break;
            case "retry":
                if (int.TryParse(value, out var ms)) _retry = ms;
                break;
        }
    }
}
