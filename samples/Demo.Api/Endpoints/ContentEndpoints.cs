using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demo.Api.Endpoints;

/// <summary>
/// Response-side content type playground. Each route returns the same logical payload in
/// a different content type so Tap's body-rendering can be exercised end-to-end.
/// </summary>
public static class ContentEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/demo/content");

        g.MapGet("/json", () =>
            Results.Json(new ContentPayload("json", "Hello from Demo.Api", DateTimeOffset.UtcNow),
                ContentJson.Default.ContentPayload));

        g.MapGet("/xml", () =>
        {
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + $"<payload kind=\"xml\"><message>Hello from Demo.Api</message><time>{DateTimeOffset.UtcNow:o}</time></payload>";
            return Results.Content(xml, "application/xml", Encoding.UTF8);
        });

        g.MapGet("/yaml", () =>
        {
            var yaml = $"kind: yaml\nmessage: Hello from Demo.Api\ntime: {DateTimeOffset.UtcNow:o}\n";
            return Results.Content(yaml, "application/yaml", Encoding.UTF8);
        });

        g.MapGet("/text", () => Results.Text(
            $"kind: text\nmessage: Hello from Demo.Api\ntime: {DateTimeOffset.UtcNow:o}\n",
            "text/plain"));

        g.MapGet("/html", () => Results.Content(
            "<!doctype html><html><head><title>Demo</title></head>"
            + "<body><h1>Hello from Demo.Api</h1><p>HTML content type.</p></body></html>",
            "text/html"));

        g.MapGet("/css", () => Results.Content(
            "body { font-family: system-ui; color: #333; }\nh1 { color: tomato; }\n",
            "text/css"));

        g.MapGet("/javascript", () => Results.Content(
            "export const greet = (name) => `Hello, ${name}!`;\n",
            "application/javascript"));

        g.MapGet("/csv", () =>
        {
            var csv = "id,name,score\n1,alice,9.7\n2,bob,8.3\n3,carol,9.1\n";
            return Results.Content(csv, "text/csv");
        });

        g.MapGet("/markdown", () => Results.Content(
            "# Demo\n\nMarkdown response.\n\n- one\n- two\n- three\n",
            "text/markdown"));

        // Tiny 3x3 PNG (red square) — base64-decoded once and cached.
        g.MapGet("/png", () => Results.Bytes(Png3x3, "image/png"));

        // Tiny JPEG (solid red) — base64-decoded once and cached.
        g.MapGet("/jpeg", () => Results.Bytes(JpegRed, "image/jpeg"));

        // 12-byte SVG (red circle).
        g.MapGet("/svg", () => Results.Content(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\">"
            + "<circle cx=\"32\" cy=\"32\" r=\"28\" fill=\"tomato\"/></svg>",
            "image/svg+xml"));

        g.MapGet("/binary", () =>
        {
            // 256 bytes of incrementing data — useful for exercising the inspector's binary preview.
            var data = new byte[256];
            for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
            return Results.Bytes(data, "application/octet-stream", "demo.bin");
        });

        g.MapGet("/problem", () => Results.Problem(
            title: "Demo problem",
            detail: "This is RFC 7807 application/problem+json.",
            statusCode: StatusCodes.Status418ImATeapot,
            type: "https://example.com/probs/demo"));

        g.MapGet("/empty", () => Results.NoContent());

        g.MapGet("/large/{kib:int}", (int kib) =>
        {
            kib = Math.Clamp(kib, 1, 4096);
            var bytes = new byte[kib * 1024];
            new Random(42).NextBytes(bytes);
            return Results.Bytes(bytes, "application/octet-stream", $"demo-{kib}KiB.bin");
        });

        g.MapGet("/slow/{ms:int}", async (int ms, CancellationToken ct) =>
        {
            ms = Math.Clamp(ms, 0, 30_000);
            await Task.Delay(ms, ct);
            return Results.Json(new { waitedMs = ms, time = DateTimeOffset.UtcNow },
                ContentJson.Default.SlowResponse);
        });
    }

    private static readonly byte[] Png3x3 = Convert.FromBase64String(
        // 3x3 solid red PNG.
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAADCAIAAADZSiLoAAAAFklEQVQI12P8z8DwHwAFBQIAX8jx6gAAAABJRU5ErkJggg==");

    private static readonly byte[] JpegRed = Convert.FromBase64String(
        // 8x8 solid red JPEG.
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAYEBAUEBAYFBQUGBgYHCQ4JCQgICRINDQoOFRIWFhUSFBQXGiEcFxgfGRQUHScdHyIjJSUlFhwpLCgkKyEkJST/2wBDAQYGBgkICREJCREkGBQYJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCT/wAARCAAIAAgDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAj/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwBVAH//2Q==");
}

internal sealed record ContentPayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

internal sealed record SlowResponse(
    [property: JsonPropertyName("waitedMs")] int WaitedMs,
    [property: JsonPropertyName("time")] DateTimeOffset Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ContentPayload))]
[JsonSerializable(typeof(SlowResponse))]
internal sealed partial class ContentJson : JsonSerializerContext;
