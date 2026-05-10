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

        record.IsWebSocket = true;
        record.IsStream = true;
        record.Upstream = wsBuilder.Uri.ToString();
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
            await clientWs.ConnectAsync(wsBuilder.Uri, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebSocket upstream connect failed: {Uri}", wsBuilder.Uri);
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

                if (assembled.Length == 0)
                {
                    currentType = result.MessageType;
                }

                assembled.Write(buffer, 0, result.Count);

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

                var bytes = assembled.ToArray();
                assembled.SetLength(0);

                var message = BuildMessage(direction, currentType, bytes);
                store.AppendWebSocketMessage(record, message);
            }
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static WebSocketMessage BuildMessage(string direction, WebSocketMessageType type, byte[] bytes)
    {
        var size = bytes.Length;
        var truncated = size > MaxCaptureBytes;
        var captured = truncated ? bytes.AsSpan(0, MaxCaptureBytes).ToArray() : bytes;

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
