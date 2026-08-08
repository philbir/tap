using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Tap.Studio.Contracts;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

/// <summary>
/// Drives a single WebSocket exchange from a <see cref="ResolvedRequest"/> with
/// <see cref="Tap.Workspace.Model.RequestProtocol.WebSocket"/>. Mirrors the design of the
/// HTTP path in <see cref="ExecuteStreamEndpoint"/>:
/// <list type="bullet">
///   <item>One <c>event: meta</c> immediately after the upgrade response.</item>
///   <item>One <c>event: ws</c> per captured frame — both directions, plus a synthetic
///   <c>open</c> at the start and <c>close</c>/<c>error</c> at the end.</item>
///   <item>One <c>event: done</c> on completion.</item>
/// </list>
///
/// The studio side of the conversation is intentionally minimal in this first cut:
/// if the resolved request carries a body, it is sent as a single text frame after the
/// connection opens. After that we just drain inbound frames until the upstream closes,
/// the client aborts, or the configured idle ceiling elapses. Authoring richer scripted
/// exchanges (multiple sends, ping/pong cadence, expected-response assertions) is the
/// natural next step once this is wired through the UI.
/// </summary>
internal sealed class WebSocketExecutor
{
    /// <summary>Body cap per frame for the studio buffer — frames bigger than this come
    /// through as length-only summaries. Matches the http path's 2 MiB ceiling so the UI
    /// has consistent expectations.</summary>
    private const int FrameCap = 2 * 1024 * 1024;

    /// <summary>Hard ceiling on inbound idle time. The intent is "user clicks Send, gets a
    /// quick capture, can cancel for longer sessions" — not a long-running daemon. The UI
    /// owns the abort signal for streams the user wants to keep open.</summary>
    private static readonly TimeSpan IdleCeiling = TimeSpan.FromMinutes(5);

    public delegate ValueTask EmitMetaAsync(int status, string? statusText,
        IReadOnlyDictionary<string, string> responseHeaders, string? contentType, CancellationToken ct);
    public delegate ValueTask EmitFrameAsync(ExecuteStreamWsDto frame, CancellationToken ct);

    /// <summary>
    /// Run the exchange. <paramref name="emitMeta"/> fires once right after the upgrade
    /// response is observed (status 101 on success). <paramref name="emitFrame"/> fires for
    /// every captured frame including the synthetic open/close/error events.
    /// </summary>
    /// <returns>Total bytes pumped across both directions — fed to the <c>done</c> event.</returns>
    public static async Task<long> RunAsync(
        ResolvedRequest rendered,
        EmitMetaAsync emitMeta,
        EmitFrameAsync emitFrame,
        Stopwatch sw,
        CancellationToken ct)
    {
        using var ws = new ClientWebSocket();

        // Carry the rendered headers across the upgrade. ClientWebSocket validates a small
        // allowlist of header names — anything WS-handshake-managed (Connection, Upgrade,
        // Sec-WebSocket-*) is rejected, so we filter them out. Auth-derived headers and
        // user-defined ones (e.g. Authorization, X-API-Key) pass straight through.
        foreach (var (k, v) in rendered.Headers)
        {
            if (IsHandshakeManagedHeader(k)) continue;
            try { ws.Options.SetRequestHeader(k, v); }
            catch (ArgumentException) { /* reserved name — drop */ }
        }

        long totalBytes = 0;
        var seq = 0;
        Uri uri;
        try
        {
            uri = new Uri(rendered.Url);
        }
        catch (UriFormatException ex)
        {
            await emitMeta(0, ex.Message, new Dictionary<string, string>(), null, ct).ConfigureAwait(false);
            await emitFrame(new ExecuteStreamWsDto(seq++, "system", "error", ex.Message, null, 0, null, null,
                sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return 0;
        }

        // The renderer rewrites http/https to ws/wss for a websocket block, so anything else here
        // is an exotic scheme that slipped through — report it as a frame rather than letting
        // ClientWebSocket throw an opaque ArgumentException at connect time.
        if (uri.Scheme is not ("ws" or "wss"))
        {
            var message = $"WebSocket requests must use ws:// or wss:// — got '{uri.Scheme}://'.";
            await emitMeta(0, message, new Dictionary<string, string>(), null, ct).ConfigureAwait(false);
            await emitFrame(new ExecuteStreamWsDto(seq++, "system", "error", message, null, 0, null, null,
                sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return 0;
        }

        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failed handshake — surface the HTTP status if the server returned one, otherwise
            // just the exception. We still emit a meta event so the UI panel has something to
            // anchor the failure to.
            var status = ex is WebSocketException wse ? MapHandshakeStatus(wse) : 0;
            await emitMeta(status, ex.Message, new Dictionary<string, string>(), null, ct).ConfigureAwait(false);
            await emitFrame(new ExecuteStreamWsDto(seq++, "system", "error", ex.Message, null, 0, null, null,
                sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return 0;
        }

        // Successful upgrade: 101 by definition. ClientWebSocket doesn't expose the upstream's
        // response headers (the framework eats them), so the meta event carries the bare
        // minimum — status, the negotiated sub-protocol if any, and the resolved URL.
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(ws.SubProtocol))
            responseHeaders["Sec-WebSocket-Protocol"] = ws.SubProtocol;
        await emitMeta(101, "Switching Protocols", responseHeaders, null, ct).ConfigureAwait(false);

        await emitFrame(new ExecuteStreamWsDto(seq++, "system", "open", null, null, 0, null, null,
            sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);

        // First-frame send: if the rendered request carries a body, push it as a single
        // text frame. That's the simplest authoring model — paste your sample payload into
        // the Body tab and it becomes the opening message.
        if (!string.IsNullOrEmpty(rendered.Body))
        {
            var payload = Encoding.UTF8.GetBytes(rendered.Body);
            totalBytes += payload.Length;
            try
            {
                await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                await emitFrame(new ExecuteStreamWsDto(
                    Seq: seq++,
                    Direction: "client",
                    Type: "text",
                    Text: rendered.Body.Length <= FrameCap ? rendered.Body : rendered.Body[..FrameCap],
                    Base64: null,
                    Size: payload.Length,
                    CloseStatus: null,
                    CloseDescription: null,
                    TimestampMs: sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await emitFrame(new ExecuteStreamWsDto(seq++, "system", "error", ex.Message, null, 0, null, null,
                    sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            }
        }

        var buffer = new byte[64 * 1024];
        var frame = new MemoryStream();
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(IdleCeiling);

        try
        {
            while (ws.State == WebSocketState.Open && !idleCts.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller (HTTP client) walked away — just stop.
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Idle ceiling hit — emit a synthetic close so the UI shows it cleanly.
                    await emitFrame(new ExecuteStreamWsDto(seq++, "system", "close", "Idle ceiling reached", null, 0,
                        null, "studio idle ceiling", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    await emitFrame(new ExecuteStreamWsDto(seq++, "system", "error", ex.Message, null, 0, null, null,
                        sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                    break;
                }

                totalBytes += result.Count;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await emitFrame(new ExecuteStreamWsDto(
                        Seq: seq++,
                        Direction: "server",
                        Type: "close",
                        Text: result.CloseStatusDescription,
                        Base64: null,
                        Size: 0,
                        CloseStatus: result.CloseStatus is null ? null : (int)result.CloseStatus.Value,
                        CloseDescription: result.CloseStatusDescription,
                        TimestampMs: sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* already closing */ }
                    break;
                }

                if (frame.Length < FrameCap)
                {
                    var slack = FrameCap - (int)frame.Length;
                    frame.Write(buffer, 0, Math.Min(result.Count, slack));
                }

                if (!result.EndOfMessage) continue;

                var bytes = frame.ToArray();
                frame.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    await emitFrame(new ExecuteStreamWsDto(
                        Seq: seq++,
                        Direction: "server",
                        Type: "text",
                        Text: text,
                        Base64: null,
                        Size: bytes.Length,
                        CloseStatus: null,
                        CloseDescription: null,
                        TimestampMs: sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                }
                else
                {
                    await emitFrame(new ExecuteStreamWsDto(
                        Seq: seq++,
                        Direction: "server",
                        Type: "binary",
                        Text: null,
                        Base64: Convert.ToBase64String(bytes),
                        Size: bytes.Length,
                        CloseStatus: null,
                        CloseDescription: null,
                        TimestampMs: sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "studio closing",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* swallow — connection may already be torn down */ }
            }
        }

        return totalBytes;
    }

    /// <summary>Headers the ClientWebSocket handshake owns — silently dropping these avoids
    /// the noisy ArgumentException ClientWebSocket throws for reserved names.</summary>
    private static bool IsHandshakeManagedHeader(string name)
        => name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort recovery of the upstream HTTP status code from a handshake
    /// failure. .NET surfaces it in the message ("The server returned status code 'XYZ'").</summary>
    private static int MapHandshakeStatus(WebSocketException ex)
    {
        var msg = ex.Message;
        var marker = "status code '";
        var idx = msg.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var rest = msg.AsSpan(idx + marker.Length);
        var end = rest.IndexOf('\'');
        if (end <= 0) return 0;
        return int.TryParse(rest[..end], out var code) ? code : 0;
    }
}
