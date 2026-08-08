using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Tap.Server;

/// <summary>
/// Proxies WebSocket connections from the inspector's proxy port to the configured upstream
/// while recording every text/binary frame in both directions. YARP forwards regular HTTP
/// traffic; WebSockets are intercepted before they hit YARP because we need to terminate
/// and re-originate the upgrade to read frame contents.
/// </summary>
public static class WebSocketProxy
{
    private const int MaxCaptureBytes = 1_000_000;
    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// Absolute ceiling on a single reassembled message. A peer that never sets
    /// EndOfMessage would otherwise stream frames at us for the life of the connection;
    /// past this point the socket is torn down rather than trusted.
    /// </summary>
    private const long MaxMessageBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Ceiling on simultaneously proxied sockets. Each one costs two pumps, a rented
    /// buffer and a reassembly stream, and this proxy is reachable from the public
    /// internet through the tunnel.
    /// </summary>
    private const int MaxConcurrentConnections = 64;

    private static int _activeConnections;

    public static async Task ProxyAsync(
        HttpContext ctx,
        RequestRecord record,
        InspectorIngressEntry? ingress,
        IRequestStore store,
        ILogger logger)
    {
        if (ingress is null || string.IsNullOrEmpty(ingress.Upstream) ||
            !Uri.TryCreate(ingress.Upstream, UriKind.Absolute, out var upstreamUri))
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            record.StatusCode = ctx.Response.StatusCode;
            record.Error = "WebSocket upstream not resolvable";
            store.Add(record);
            return;
        }

        var wsScheme = upstreamUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var wsBuilder = new UriBuilder(upstreamUri)
        {
            Scheme = wsScheme,
            Path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : "/",
            Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value!.TrimStart('?') : string.Empty,
        };

        if (Interlocked.Increment(ref _activeConnections) > MaxConcurrentConnections)
        {
            Interlocked.Decrement(ref _activeConnections);
            logger.LogWarning("Refusing WebSocket upgrade: {Limit} connections already proxied", MaxConcurrentConnections);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            record.IsWebSocket = true;
            record.StatusCode = ctx.Response.StatusCode;
            record.Error = "WebSocket connection limit reached";
            record.StreamCompleted = true;
            store.Add(record);
            return;
        }

        try
        {
            await ProxyCoreAsync(ctx, record, wsBuilder.Uri, store, logger);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private static async Task ProxyCoreAsync(
        HttpContext ctx,
        RequestRecord record,
        Uri upstream,
        IRequestStore store,
        ILogger logger)
    {
        record.IsWebSocket = true;
        record.IsStream = true;
        record.Upstream = upstream.ToString();
        record.ResponseContentType = "websocket";
        store.Add(record);

        using var clientWs = new ClientWebSocket();
        // Forward the requested subprotocols so the upstream can pick one.
        foreach (var sp in ctx.WebSockets.WebSocketRequestedProtocols)
        {
            clientWs.Options.AddSubProtocol(sp);
        }

        // Forward common bearer/cookie headers so authenticated WebSocket APIs work end-to-end.
        foreach (var header in ctx.Request.Headers)
        {
            if (IsHopByHopOrUpgradeHeader(header.Key)) continue;
            try { clientWs.Options.SetRequestHeader(header.Key, header.Value.ToString()); }
            catch { /* some headers are restricted; ignore */ }
        }

        try
        {
            await clientWs.ConnectAsync(upstream, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebSocket upstream connect failed: {Uri}", upstream);
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            record.StatusCode = ctx.Response.StatusCode;
            record.Error = $"upstream connect failed: {ex.Message}";
            record.StreamCompleted = true;
            store.Update(record);
            return;
        }

        using var browserWs = await ctx.WebSockets.AcceptWebSocketAsync(
            clientWs.SubProtocol);

        record.StatusCode = StatusCodes.Status101SwitchingProtocols;
        store.Update(record);

        var ct = ctx.RequestAborted;
        var clientToUpstream = PumpAsync(browserWs, clientWs, "client", record, store, logger, ct);
        var upstreamToClient = PumpAsync(clientWs, browserWs, "server", record, store, logger, ct);

        try
        {
            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WebSocket pump terminated");
        }

        await CloseGracefullyAsync(browserWs, clientWs);

        try { await Task.WhenAll(clientToUpstream, upstreamToClient); }
        catch { /* already logged inside the pump */ }

        record.StreamCompleted = true;
        store.Update(record);
    }

    private static async Task PumpAsync(
        WebSocket from,
        WebSocket to,
        string direction,
        RequestRecord record,
        IRequestStore store,
        ILogger logger,
        CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(BufferSize);
        try
        {
            using var assembled = new MemoryStream();
            WebSocketMessageType currentType = WebSocketMessageType.Text;
            long messageBytes = 0;

            while (!ct.IsCancellationRequested && from.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (WebSocketException ex)
                {
                    logger.LogDebug(ex, "WebSocket {Direction} receive failed", direction);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeMsg = new WebSocketMessage(
                        Timestamp: DateTimeOffset.UtcNow,
                        Direction: direction,
                        Type: "close",
                        Text: null,
                        Base64: null,
                        Size: 0,
                        Truncated: false,
                        CloseStatus: result.CloseStatus.HasValue ? (int)result.CloseStatus.Value : null,
                        CloseDescription: result.CloseStatusDescription);
                    store.AppendWebSocketMessage(record, closeMsg);

                    try
                    {
                        await to.CloseOutputAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            ct);
                    }
                    catch { /* downstream may already be gone */ }
                    return;
                }

                if (messageBytes == 0)
                {
                    currentType = result.MessageType;
                }

                messageBytes += result.Count;

                if (messageBytes > MaxMessageBytes)
                {
                    // Nothing legitimate reassembles to this; a peer that keeps sending
                    // continuation frames is just holding memory hostage. Abort rather
                    // than negotiate a close it may never complete.
                    logger.LogWarning(
                        "WebSocket {Direction} message exceeded {Limit} bytes; aborting connection",
                        direction, MaxMessageBytes);
                    record.Error = $"WebSocket message exceeded {MaxMessageBytes} bytes";
                    store.Update(record);
                    try { from.Abort(); } catch { /* already torn down */ }
                    return;
                }

                // Keep forwarding every byte — the proxy must stay transparent — but stop
                // buffering once we have as much as we would ever show in the UI.
                if (assembled.Length < MaxCaptureBytes)
                {
                    var room = MaxCaptureBytes - (int)assembled.Length;
                    assembled.Write(buffer, 0, Math.Min(result.Count, room));
                }

                try
                {
                    await to.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "WebSocket {Direction} forward failed", direction);
                    return;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                // GetBuffer over ToArray: the captured span is handed straight to the
                // encoder, so there is no reason to copy the payload a second time.
                var message = BuildMessage(
                    direction,
                    currentType,
                    assembled.GetBuffer().AsSpan(0, (int)assembled.Length),
                    messageBytes);
                assembled.SetLength(0);
                messageBytes = 0;

                // SetLength keeps the grown array, so one fat message would pin a megabyte
                // for the rest of the connection. Give it back.
                if (assembled.Capacity > BufferSize)
                {
                    assembled.Capacity = BufferSize;
                }

                store.AppendWebSocketMessage(record, message);
            }
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    /// <summary><paramref name="captured"/> is the buffered prefix; <paramref name="totalSize"/>
    /// is the true message size, which may be larger because buffering stops at the cap.</summary>
    private static WebSocketMessage BuildMessage(
        string direction,
        WebSocketMessageType type,
        ReadOnlySpan<byte> captured,
        long totalSize)
    {
        var truncated = totalSize > captured.Length;
        var size = (int)Math.Min(totalSize, int.MaxValue);

        if (type == WebSocketMessageType.Text)
        {
            string text;
            try { text = Encoding.UTF8.GetString(captured); }
            catch { text = string.Empty; truncated = true; }
            return new WebSocketMessage(
                Timestamp: DateTimeOffset.UtcNow,
                Direction: direction,
                Type: "text",
                Text: text,
                Base64: null,
                Size: size,
                Truncated: truncated,
                CloseStatus: null,
                CloseDescription: null);
        }

        return new WebSocketMessage(
            Timestamp: DateTimeOffset.UtcNow,
            Direction: direction,
            Type: "binary",
            Text: null,
            Base64: Convert.ToBase64String(captured),
            Size: size,
            Truncated: truncated,
            CloseStatus: null,
            CloseDescription: null);
    }

    private static async Task CloseGracefullyAsync(WebSocket browserWs, WebSocket clientWs)
    {
        async Task TryCloseAsync(WebSocket ws)
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "tap closing", cts.Token);
                }
                catch { /* best-effort */ }
            }
        }

        await Task.WhenAll(TryCloseAsync(browserWs), TryCloseAsync(clientWs));
    }

    private static bool IsHopByHopOrUpgradeHeader(string name) =>
        name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase);
}
