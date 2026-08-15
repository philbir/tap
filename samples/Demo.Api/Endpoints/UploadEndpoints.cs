using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Demo.Api.Endpoints;

/// <summary>
/// Request-side content types — JSON, form-urlencoded, multipart, raw, and text. Each
/// handler echoes back what it parsed so request-body capture in the Tap inspector can
/// be verified end-to-end.
/// </summary>
public static class UploadEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/demo/upload");

        // application/json — bind any object via System.Text.Json.
        g.MapPost("/json", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return Results.Json(new
            {
                received = "json",
                contentType = req.ContentType,
                bytes = req.ContentLength,
                payload = doc.RootElement.Clone(),
            });
        }).Accepts<object>("application/json");

        // application/x-www-form-urlencoded — echo the field map.
        g.MapPost("/form", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            return Results.Json(new
            {
                received = "form",
                contentType = req.ContentType,
                fields = form.ToDictionary(f => f.Key, f => f.Value.ToString()),
            });
        }).Accepts<object>("application/x-www-form-urlencoded");

        // multipart/form-data — list fields and file metadata.
        g.MapPost("/multipart", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var files = form.Files.Select(f => new
            {
                f.Name,
                fileName = f.FileName,
                contentType = f.ContentType,
                length = f.Length,
            }).ToArray();
            return Results.Json(new
            {
                received = "multipart",
                contentType = req.ContentType,
                fields = form.ToDictionary(f => f.Key, f => f.Value.ToString()),
                files,
            });
        }).Accepts<object>("multipart/form-data").DisableAntiforgery();

        // application/xml — parse, echo the root element + leaf elements.
        g.MapPost("/xml", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var xml = await reader.ReadToEndAsync();
            try
            {
                var doc = XDocument.Parse(xml);
                var root = doc.Root;
                var leaves = root is null
                    ? []
                    : root.Descendants()
                        .Where(e => !e.HasElements)
                        .ToDictionary(e => e.Name.LocalName, e => e.Value);
                return Results.Json(new
                {
                    received = "xml",
                    contentType = req.ContentType,
                    bytes = xml.Length,
                    root = root?.Name.LocalName,
                    fields = leaves,
                });
            }
            catch (XmlException ex)
            {
                return Results.Json(new
                {
                    received = "xml",
                    contentType = req.ContentType,
                    error = ex.Message,
                }, statusCode: 400);
            }
        });

        // text/plain — echo verbatim.
        g.MapPost("/text", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var text = await reader.ReadToEndAsync();
            return Results.Json(new
            {
                received = "text",
                contentType = req.ContentType,
                length = text.Length,
                text,
            });
        });

        // Raw byte upload — count bytes, hash, and report content type.
        g.MapPost("/raw", async (HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return Results.Json(new RawUploadReply(
                Received: "raw",
                ContentType: req.ContentType,
                Length: bytes.Length,
                Sha256Hex: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()),
                UploadJson.Default.RawUploadReply);
        });
    }
}

internal sealed record RawUploadReply(
    [property: JsonPropertyName("received")] string Received,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("sha256Hex")] string Sha256Hex);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RawUploadReply))]
internal sealed partial class UploadJson : JsonSerializerContext;
