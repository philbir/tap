using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Turns a request-body JSON Schema into an example body a developer can actually send.
///
/// <para><b>Output is deterministic.</b> No timestamps, no random GUIDs, no ordering that depends
/// on a hash seed. Re-sync decides "did the user edit this?" by comparing a hash of what we last
/// generated against the file on disk, so a generator that produced a fresh GUID on every run
/// would report every request as locally edited, forever.</para>
///
/// <para><b>Cycles are normal, not exceptional.</b> The Petstore spec alone has
/// <c>Pet → Category → Pet</c>. Recursion is bounded by <see cref="MaxDepth"/> rather than by
/// tracking visited nodes: the reader hands back a fresh proxy object per <c>$ref</c> access, so
/// reference identity is not a reliable cycle key, while a depth cap always terminates.</para>
/// </summary>
public static class SchemaExampleBuilder
{
    /// <summary>Deep enough for realistic payloads, shallow enough that a recursive schema
    /// produces something a human still recognizes.</summary>
    public const int MaxDepth = 5;

    /// <summary>Guards against generated specs with pathologically wide objects.</summary>
    private const int MaxProperties = 60;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Builds a pretty-printed JSON body, or null when there is nothing useful to write.</summary>
    public static string? Build(IOpenApiSchema? schema)
    {
        if (schema is null) return null;
        var node = BuildNode(schema, 0);
        if (node is null) return null;
        return node.ToJsonString(Pretty);
    }

    /// <summary>
    /// Prefers what the spec author actually wrote. An <c>example</c> on the media type or schema
    /// is hand-written documentation of a real payload — always better than anything synthesized
    /// from types.
    /// </summary>
    public static string? BuildFromMediaType(IOpenApiMediaType? media)
    {
        if (media is null) return null;

        if (media.Example is { } example)
            return example.ToJsonString(Pretty);

        // `examples` is a named map; take the first by name so the choice is stable across runs.
        var named = media.Examples?
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value?.Value)
            .FirstOrDefault(v => v is not null);
        if (named is not null) return named.ToJsonString(Pretty);

        return Build(media.Schema);
    }

    private static JsonNode? BuildNode(IOpenApiSchema schema, int depth)
    {
        if (depth > MaxDepth) return null;

        // Author-provided values win over synthesis at every level. `Examples` (plural) is the
        // 3.1 spelling; the singular `Example` is obsolete in this library.
        if (SchemaExample(schema) is { } ex) return ex.DeepClone();
        if (schema.Default is { } def) return def.DeepClone();
        if (schema.Enum is { Count: > 0 } enumValues && enumValues[0] is { } first) return first.DeepClone();

        // allOf is composition — merge every branch into one object, which is what a real payload
        // for the composed type looks like.
        if (schema.AllOf is { Count: > 0 } allOf)
        {
            var merged = new JsonObject();
            foreach (var part in allOf)
            {
                if (BuildNode(part, depth) is JsonObject obj)
                {
                    foreach (var kv in obj.ToList())
                    {
                        obj.Remove(kv.Key);
                        merged[kv.Key] = kv.Value;
                    }
                }
            }
            if (merged.Count > 0) return merged;
        }

        // oneOf/anyOf: pick the first branch. Any choice is arbitrary; the first is at least stable
        // and matches the order the author wrote.
        var variant = (schema.OneOf ?? schema.AnyOf)?.FirstOrDefault();
        if (variant is not null && schema.Type is null) return BuildNode(variant, depth);

        var type = schema.Type;

        if (Has(type, JsonSchemaType.Object) || (type is null && schema.Properties is { Count: > 0 }))
            return BuildObject(schema, depth);

        if (Has(type, JsonSchemaType.Array))
        {
            var item = schema.Items is null ? null : BuildNode(schema.Items, depth + 1);
            return new JsonArray(item ?? JsonValue.Create(string.Empty));
        }

        if (Has(type, JsonSchemaType.Boolean)) return JsonValue.Create(true);
        if (Has(type, JsonSchemaType.Integer)) return JsonValue.Create(0);
        if (Has(type, JsonSchemaType.Number)) return JsonValue.Create(0);
        if (Has(type, JsonSchemaType.String)) return JsonValue.Create(StringFor(schema.Format));

        // Untyped and unresolvable — most often a `$ref` we could not follow because external
        // refs are deliberately not loaded. An empty object is honest and still valid JSON.
        return depth == 0 ? new JsonObject() : null;
    }

    private static JsonObject BuildObject(IOpenApiSchema schema, int depth)
    {
        var obj = new JsonObject();
        var properties = schema.Properties;
        if (properties is null) return obj;

        var count = 0;
        foreach (var (name, property) in properties)
        {
            if (count++ >= MaxProperties) break;
            var value = BuildNode(property, depth + 1);
            // A property whose value bottomed out on the depth cap still belongs in the shape —
            // emit null so the developer sees the field exists and can fill it in.
            obj[name] = value ?? JsonValue.Create((string?)null);
        }
        return obj;
    }

    /// <summary><c>JsonSchemaType</c> is a flags enum in OpenAPI 3.1 (a schema may be
    /// <c>["string","null"]</c>), so membership is a bit test, not equality.</summary>
    private static bool Has(JsonSchemaType? type, JsonSchemaType flag)
        => type is { } t && (t & flag) == flag;

    /// <summary>First declared example, taken positionally so the choice is stable across runs.</summary>
    internal static JsonNode? SchemaExample(IOpenApiSchema schema)
        => schema.Examples is { Count: > 0 } examples ? examples.FirstOrDefault(e => e is not null) : null;

    /// <summary>Format-aware placeholders. Fixed values, never <c>DateTime.Now</c> or a fresh
    /// GUID — see the determinism note on the class.</summary>
    private static string StringFor(string? format) => format?.ToLowerInvariant() switch
    {
        "date" => "2026-01-01",
        "date-time" => "2026-01-01T00:00:00Z",
        "uuid" or "guid" => "00000000-0000-0000-0000-000000000000",
        "email" => "user@example.com",
        "uri" or "url" => "https://example.com",
        "hostname" => "example.com",
        "ipv4" => "192.0.2.1",
        "ipv6" => "2001:db8::1",
        "byte" => "",
        "password" => "",
        _ => "string",
    };
}
