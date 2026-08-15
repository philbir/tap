using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Api.Endpoints;

/// <summary>
/// Server-Sent Events + WebSocket samples. Both endpoints emit predictable, timed traffic
/// so the Tap capture middleware has something deterministic to record.
/// </summary>
public static class StreamingEndpoints
{
    public static void Map(WebApplication app)
    {
        // SSE — emits `count` tick events at `interval` ms intervals, then a terminating "done".
        app.MapGet("/demo/stream/sse", async (HttpContext ctx, int? count, int? interval, CancellationToken ct) =>
        {
            var n = Math.Clamp(count ?? 5, 1, 500);
            var delayMs = Math.Clamp(interval ?? 500, 50, 60_000);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.WriteAsync(": stream start\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            for (var i = 1; i <= n && !ct.IsCancellationRequested; i++)
            {
                var payload = JsonSerializer.Serialize(
                    new SseTick(i, n, DateTimeOffset.UtcNow),
                    StreamingJson.Default.SseTick);
                await ctx.Response.WriteAsync($"id: {i}\nevent: tick\ndata: {payload}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);

                if (i < n)
                {
                    try { await Task.Delay(delayMs, ct); }
                    catch (TaskCanceledException) { break; }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        // WebSocket echo + heartbeat. Text frames are echoed back with an `echo:` prefix,
        // binary frames echo verbatim, and the server emits a tick frame every `interval` ms.
        app.Map("/demo/stream/ws", async (HttpContext ctx, int? interval, CancellationToken ct) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Expected a WebSocket upgrade.", ct);
                return;
            }

            var delayMs = Math.Clamp(interval ?? 1500, 100, 60_000);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

            var greeting = JsonSerializer.Serialize(
                new WsHello("Demo.Api WebSocket connected", DateTimeOffset.UtcNow),
                StreamingJson.Default.WsHello);
            await ws.SendAsync(Encoding.UTF8.GetBytes(greeting), WebSocketMessageType.Text, true, ct);

            var heartbeat = Task.Run(async () =>
            {
                var i = 0;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(delayMs, ct); }
                    catch (TaskCanceledException) { break; }
                    if (ws.State != WebSocketState.Open) break;
                    var tick = JsonSerializer.Serialize(
                        new WsTick(++i, DateTimeOffset.UtcNow),
                        StreamingJson.Default.WsTick);
                    try
                    {
                        await ws.SendAsync(Encoding.UTF8.GetBytes(tick),
                            WebSocketMessageType.Text, true, ct);
                    }
                    catch (WebSocketException) { break; }
                }
            }, ct);

            var buffer = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription, ct);
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var reply = $"echo: {text}";
                        await ws.SendAsync(Encoding.UTF8.GetBytes(reply),
                            WebSocketMessageType.Text, true, ct);
                    }
                    else
                    {
                        await ws.SendAsync(buffer.AsMemory(0, result.Count),
                            WebSocketMessageType.Binary, true, ct);
                    }
                }
            }
            catch (OperationCanceledException) { /* client gone */ }
            catch (WebSocketException) { /* client gone */ }

            try { await heartbeat; } catch { /* heartbeat already exited */ }
        });
    }
}

internal sealed record SseTick(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

internal sealed record WsHello(
    [property: JsonPropertyName("hello")] string Hello,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

internal sealed record WsTick(
    [property: JsonPropertyName("tick")] int Tick,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SseTick))]
[JsonSerializable(typeof(WsHello))]
[JsonSerializable(typeof(WsTick))]
internal sealed partial class StreamingJson : JsonSerializerContext;
