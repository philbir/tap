using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Tap.Server;

public sealed class CaptureMiddleware
{
    private const long MaxCaptureBytes = 1_000_000;

    private readonly RequestDelegate _next;
    private readonly InMemoryRequestStore _store;
    private readonly ILogger<CaptureMiddleware> _logger;
    private readonly InspectorIngressEntry[] _ingress;

    public CaptureMiddleware(
        RequestDelegate next,
        InMemoryRequestStore store,
        ILogger<CaptureMiddleware> logger,
        InspectorIngressEntry[] ingress)
    {
        _next = next;
        _store = store;
        _logger = logger;
        _ingress = ingress;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();

        var record = new RequestRecord
        {
            Sequence = _store.NextSequence(),
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Method = ctx.Request.Method,
            Host = ctx.Request.Host.ToString(),
            Scheme = ctx.Request.Scheme,
            Path = ctx.Request.Path + ctx.Request.QueryString,
            Upstream = null,
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            RequestHeaders = SnapshotHeaders(ctx.Request.Headers),
            RequestContentType = ctx.Request.ContentType,
        };

        if (ctx.WebSockets.IsWebSocketRequest)
        {
            try
            {
                await WebSocketProxy.ProxyAsync(ctx, record, ResolveIngress(ctx.Request.Host.Host), _store, _logger);
            }
            catch (Exception ex)
            {
                record.Error = ex.Message;
                _logger.LogError(ex, "WebSocket proxy error for {Host}{Path}", record.Host, record.Path);
            }
            finally
            {
                sw.Stop();
                record.DurationMs = sw.ElapsedMilliseconds;
                record.StreamCompleted = true;
                _store.Update(record);
            }
            return;
        }

        await CaptureRequestBodyAsync(ctx, record);

        var originalBody = ctx.Response.Body;
        var capture = new CapturingResponseStream(ctx, originalBody, record, _store);
        ctx.Response.Body = capture;

        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
            _logger.LogError(ex, "Error proxying {Method} {Host}{Path}", record.Method, record.Host, record.Path);
            throw;
        }
        finally
        {
            ctx.Response.Body = originalBody;
            sw.Stop();
            record.DurationMs = sw.ElapsedMilliseconds;

            if (capture.IsStreaming)
            {
                // Streaming response: bytes already flushed to client; no buffer copy.
                record.StreamCompleted = true;
                record.StatusCode = ctx.Response.StatusCode;
                record.ResponseHeaders = SnapshotHeaders(ctx.Response.Headers);
                record.ResponseBodyOriginalSize = capture.BytesWritten;
                _store.Update(record);
            }
            else if (capture.IsPassthrough)
            {
                // Large response: already passed through after the capture cap was reached.
                record.StatusCode = ctx.Response.StatusCode;
                record.ResponseContentType = ctx.Response.ContentType;
                record.ResponseHeaders = SnapshotHeaders(ctx.Response.Headers);
                record.ResponseBodyOriginalSize = capture.BytesWritten;
                record.ResponseBodyTruncated = true;
                _store.Add(record);
            }
            else
            {
                var captureStream = capture.Buffer;
                captureStream.Position = 0;
                record.ResponseContentType = ctx.Response.ContentType;
                record.ResponseBodyOriginalSize = captureStream.Length;
                var contentEncoding = ctx.Response.Headers.ContentEncoding.ToString();
                CaptureResponseBody(captureStream, contentEncoding, record);
                captureStream.Position = 0;

                try
                {
                    await captureStream.CopyToAsync(originalBody);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy captured response to downstream body.");
                }

                record.StatusCode = ctx.Response.StatusCode;
                record.ResponseHeaders = SnapshotHeaders(ctx.Response.Headers);
                _store.Add(record);
            }
        }
    }

    private InspectorIngressEntry? ResolveIngress(string requestHost)
    {
        InspectorIngressEntry? fallback = null;
        foreach (var entry in _ingress)
        {
            if (string.IsNullOrWhiteSpace(entry.Hostname))
            {
                fallback ??= entry;
                continue;
            }
            if (string.Equals(entry.Hostname, requestHost, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return fallback;
    }

    private static async Task CaptureRequestBodyAsync(HttpContext ctx, RequestRecord record)
    {
        var length = ctx.Request.ContentLength ?? 0;
        record.RequestBodyOriginalSize = length;

        if (length == 0)
        {
            return;
        }

        ctx.Request.EnableBuffering();

        if (!IsTextContentType(ctx.Request.ContentType) || length > MaxCaptureBytes)
        {
            record.RequestBodyTruncated = true;
            return;
        }

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        record.RequestBody = Encoding.UTF8.GetString(ms.ToArray());
        ctx.Request.Body.Position = 0;
    }

    private static void CaptureResponseBody(MemoryStream captureStream, string? contentEncoding, RequestRecord record)
    {
        if (captureStream.Length == 0)
        {
            return;
        }

        if (captureStream.Length > MaxCaptureBytes)
        {
            record.ResponseBodyTruncated = true;
            return;
        }

        byte[] bytes;
        try
        {
            bytes = DecodeBody(captureStream.ToArray(), contentEncoding);
        }
        catch
        {
            record.ResponseBodyTruncated = true;
            return;
        }

        if (bytes.Length > MaxCaptureBytes)
        {
            record.ResponseBodyTruncated = true;
            return;
        }

        if (IsSvgContentType(record.ResponseContentType))
        {
            record.ResponseBody = Encoding.UTF8.GetString(bytes);
            return;
        }

        if (IsImageContentType(record.ResponseContentType))
        {
            record.ResponseBodyBase64 = Convert.ToBase64String(bytes);
            return;
        }

        if (IsTextContentType(record.ResponseContentType))
        {
            record.ResponseBody = Encoding.UTF8.GetString(bytes);
            return;
        }

        record.ResponseBodyTruncated = true;
    }

    private static byte[] DecodeBody(byte[] bytes, string? contentEncoding)
    {
        if (string.IsNullOrEmpty(contentEncoding))
        {
            return bytes;
        }

        var encoding = contentEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrEmpty(encoding) || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return bytes;
        }

        using var input = new MemoryStream(bytes);
        Stream decompressor = encoding.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true),
            "br" => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true),
            _ => null!,
        };
        if (decompressor is null)
        {
            return bytes;
        }
        using (decompressor)
        {
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }
    }

    private static bool IsSvgContentType(string? contentType) =>
        contentType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsImageContentType(string? contentType) =>
        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
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
}
