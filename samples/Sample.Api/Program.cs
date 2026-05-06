using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

app.MapGet("/", () => Results.Json(new { hello = "Sample.Api" }));

app.MapGet("/hello/{name}", (string name) =>
    Results.Json(new { greeting = $"Hello, {name}!" }));

app.MapPost("/echo", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Json(new { you_sent = body });
});

// SSE endpoint: emits `count` events at `interval` ms apart, then closes.
app.MapGet("/sse", async (HttpContext ctx, int? count, int? interval, CancellationToken ct) =>
{
    var n = Math.Clamp(count ?? 5, 1, 200);
    var delayMs = Math.Clamp(interval ?? 1000, 50, 60_000);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await ctx.Response.WriteAsync(": stream start\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);

    for (var i = 1; i <= n && !ct.IsCancellationRequested; i++)
    {
        var payload = JsonSerializer.Serialize(new SseTick(i, n, DateTimeOffset.UtcNow), SseJson.Default.SseTick);
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

// Endpoint catalog the Sample.Client uses to render its test cards.
app.MapGet("/endpoints", () => Results.Json(new EndpointDescriptor[]
{
    new("GET", "/", "Hello root"),
    new("GET", "/hello/{name}", "Greet by name", new[] { new EndpointParameter("name", "World") }),
    new("POST", "/echo", "Echo a JSON body", null, "{ \"hello\": \"world\" }"),
    new("GET", "/sse", "Server-Sent Events stream",
        new[]
        {
            new EndpointParameter("count", "5", "How many events"),
            new EndpointParameter("interval", "750", "Milliseconds between events"),
        },
        IsStream: true),
}, EndpointJson.Default.EndpointDescriptorArray));

app.Run();

internal sealed record SseTick(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SseTick))]
internal sealed partial class SseJson : JsonSerializerContext;

internal sealed record EndpointDescriptor(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] EndpointParameter[]? Parameters = null,
    [property: JsonPropertyName("sampleBody")] string? SampleBody = null,
    [property: JsonPropertyName("isStream")] bool IsStream = false);

internal sealed record EndpointParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("default")] string Default,
    [property: JsonPropertyName("description")] string? Description = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EndpointDescriptor[]))]
internal sealed partial class EndpointJson : JsonSerializerContext;
