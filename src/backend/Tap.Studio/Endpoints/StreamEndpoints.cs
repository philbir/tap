using System.Text;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>GET /api/stream</c> — Server-Sent Events feed used by the UI for live workspace updates
/// (file changed, parse error, execution events later). Mirrors the convention used by
/// <c>Tap.Server</c>'s inspector at <c>/api/stream</c>.
/// </summary>
public static class StreamEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stream", async (HttpContext ctx, WorkspaceService svc, CancellationToken ct) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var tcs = new TaskCompletionSource();
            void OnChanged() => _ = WriteAsync(ctx, "workspace-changed", "{}");
            svc.Changed += OnChanged;

            // Initial hello so the client knows the stream is alive.
            await WriteAsync(ctx, "hello", "{}").ConfigureAwait(false);

            try
            {
                using var reg = ct.Register(() => tcs.TrySetResult());
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                svc.Changed -= OnChanged;
            }
        });
    }

    private static async Task WriteAsync(HttpContext ctx, string evt, string dataJson)
    {
        var payload = $"event: {evt}\ndata: {dataJson}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Client disconnected.
        }
    }
}
