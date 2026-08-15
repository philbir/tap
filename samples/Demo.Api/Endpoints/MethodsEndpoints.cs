using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Api.Endpoints;

/// <summary>
/// Exercises every HTTP method Tap may need to capture. Each handler echoes the request
/// envelope (method, path, query, headers, body) so the inspector can verify the round
/// trip without needing a separate response-validation flow.
/// </summary>
public static class MethodsEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/demo/methods");

        // Cast through Delegate so the route handler's Task<IResult> return value is
        // written to the response (rather than being silently discarded by RequestDelegate).
        Delegate echoDelegate = Echo;
        g.MapGet("/", echoDelegate).WithName("methods-get");
        g.MapPost("/", echoDelegate).WithName("methods-post");
        g.MapPut("/", echoDelegate).WithName("methods-put");
        g.MapPatch("/", echoDelegate).WithName("methods-patch");
        g.MapDelete("/", echoDelegate).WithName("methods-delete");

        // HEAD returns the same headers as GET but the framework will strip the body.
        g.MapMethods("/", new[] { "HEAD" }, echoDelegate).WithName("methods-head");

        // OPTIONS — advertise the supported verbs in the Allow header.
        g.MapMethods("/", new[] { "OPTIONS" }, (HttpResponse res) =>
        {
            res.Headers.Allow = "GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS";
            return Results.NoContent();
        }).WithName("methods-options");

        // Status-code playground: /demo/methods/status/{code} returns that status with a JSON body.
        g.MapMethods("/status/{code:int}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" }, (int code) =>
            Results.Json(new StatusReply(code, ReasonPhrases.Phrase(code)),
                MethodsJson.Default.StatusReply, statusCode: code))
            .WithName("methods-status");

        // Header passthrough — echoes request headers verbatim so tests can verify header
        // forwarding through the proxy.
        g.MapGet("/headers", (HttpRequest req) =>
            Results.Json(req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));

        // Query-string echo with arbitrary keys.
        g.MapGet("/query", (HttpRequest req) =>
            Results.Json(req.Query.ToDictionary(q => q.Key, q => q.Value.ToString())));
    }

    private static async Task<IResult> Echo(HttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        return Results.Json(new MethodEcho(
            Method: ctx.Request.Method,
            Path: ctx.Request.Path,
            Query: ctx.Request.QueryString.Value ?? string.Empty,
            ContentType: ctx.Request.ContentType,
            Headers: ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
            Body: body),
            MethodsJson.Default.MethodEcho);
    }
}

internal sealed record MethodEcho(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("headers")] IDictionary<string, string> Headers,
    [property: JsonPropertyName("body")] string Body);

internal sealed record StatusReply(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("description")] string Description);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MethodEcho))]
[JsonSerializable(typeof(StatusReply))]
internal sealed partial class MethodsJson : JsonSerializerContext;

internal static class ReasonPhrases
{
    public static string Phrase(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        418 => "I'm a teapot",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Status",
    };
}
