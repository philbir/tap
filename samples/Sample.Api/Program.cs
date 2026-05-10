using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Shared JWT secret for the /secure endpoint — Sample.Client signs tokens with the
// same value (passed in via VITE_JWT_SECRET) and the AppHost wires both sides up.
var jwtSecret = builder.Configuration["Jwt:SecretKey"] ?? "tap-sample-shared-secret-please-change-me-32+chars";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "tap-sample-client";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "tap-sample-api";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Json(new { hello = "Sample.Api" }));

app.MapGet("/hello/{name}", (string name) =>
    Results.Json(new { greeting = $"Hello, {name}!" }));

app.MapPost("/echo", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Json(new { you_sent = body });
});

// JWT-protected endpoint: returns the caller's claims when a valid bearer token is presented.
app.MapGet("/secure", (HttpContext ctx) =>
{
    var claims = ctx.User.Claims.ToDictionary(c => c.Type, c => c.Value);
    return Results.Json(new { authenticated = ctx.User.Identity?.IsAuthenticated == true, claims });
}).RequireAuthorization();

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

// WebSocket echo + heartbeat endpoint.
//   - Accepts text or binary frames; echoes them back prefixed with "echo:".
//   - Sends a periodic "tick" frame every `interval` ms (default 1500) so the inspector
//     has bidirectional traffic to display even when the client is idle.
app.Map("/ws", async (HttpContext ctx, int? interval, CancellationToken ct) =>
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
        new WsHello("Sample.Api WebSocket connected", DateTimeOffset.UtcNow),
        WsJson.Default.WsHello);
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
                new WsTick(++i, DateTimeOffset.UtcNow), WsJson.Default.WsTick);
            try
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(tick), WebSocketMessageType.Text, true, ct);
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
                await ws.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, ct);
            }
            else
            {
                var slice = buffer.AsMemory(0, result.Count);
                await ws.SendAsync(slice, WebSocketMessageType.Binary, true, ct);
            }
        }
    }
    catch (OperationCanceledException) { /* client gone */ }
    catch (WebSocketException) { /* client gone */ }

    try { await heartbeat; } catch { /* heartbeat already exited */ }
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
    new("WS", "/ws", "WebSocket echo + heartbeat",
        new[]
        {
            new EndpointParameter("interval", "1500", "Milliseconds between server tick frames"),
        },
        IsStream: false,
        RequiresAuth: false,
        IsWebSocket: true),
    new("GET", "/secure", "JWT-protected — client signs an HS256 token", null, null, false, RequiresAuth: true),
}, EndpointJson.Default.EndpointDescriptorArray));

app.Run();

internal sealed record SseTick(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SseTick))]
internal sealed partial class SseJson : JsonSerializerContext;

internal sealed record WsHello(
    [property: JsonPropertyName("hello")] string Hello,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

internal sealed record WsTick(
    [property: JsonPropertyName("tick")] int Tick,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsHello))]
[JsonSerializable(typeof(WsTick))]
internal sealed partial class WsJson : JsonSerializerContext;

internal sealed record EndpointDescriptor(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] EndpointParameter[]? Parameters = null,
    [property: JsonPropertyName("sampleBody")] string? SampleBody = null,
    [property: JsonPropertyName("isStream")] bool IsStream = false,
    [property: JsonPropertyName("requiresAuth")] bool RequiresAuth = false,
    [property: JsonPropertyName("isWebSocket")] bool IsWebSocket = false);

internal sealed record EndpointParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("default")] string Default,
    [property: JsonPropertyName("description")] string? Description = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EndpointDescriptor[]))]
internal sealed partial class EndpointJson : JsonSerializerContext;
