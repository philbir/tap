using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Projects an <see cref="OpenApiDocument"/> onto <see cref="MappedOperation"/>. The only place in
/// the codebase that touches the <c>Microsoft.OpenApi</c> object model beyond reading it.
/// </summary>
public static class OpenApiOperationMapper
{
    /// <summary>Content types we can put in a request body as text, best first.</summary>
    private static readonly string[] PreferredContentTypes =
    [
        "application/json",
        "application/problem+json",
        "text/json",
        "application/x-www-form-urlencoded",
        "text/plain",
    ];

    public static IReadOnlyList<MappedOperation> Map(OpenApiDocument document, List<string>? warnings = null)
    {
        warnings ??= [];
        var operations = new List<MappedOperation>();

        // An operationId is only a safe identity if it is actually unique. Duplicates are common in
        // hand-maintained specs, and silently keying on one would make two operations fight over
        // the same file on every re-sync.
        var duplicateIds = FindDuplicateOperationIds(document);

        var documentSecurity = SecurityKeys(document.Security);

        foreach (var (path, pathItem) in document.Paths ?? [])
        {
            if (pathItem?.Operations is not { } ops) continue;

            foreach (var (method, operation) in ops)
            {
                if (operation is null) continue;

                if (operations.Count >= OpenApiDocumentReader.MaxOperations)
                {
                    warnings.Add($"Document declares more than {OpenApiDocumentReader.MaxOperations} "
                        + "operations; the rest were skipped.");
                    return operations;
                }

                operations.Add(MapOne(path, method.Method, operation, pathItem, duplicateIds, documentSecurity));
            }
        }

        return operations;
    }

    private static MappedOperation MapOne(
        string path,
        string method,
        OpenApiOperation operation,
        IOpenApiPathItem pathItem,
        HashSet<string> duplicateIds,
        IReadOnlyList<string> documentSecurity)
    {
        var opWarnings = new List<string>();
        var upperMethod = method.ToUpperInvariant();

        var operationId = string.IsNullOrWhiteSpace(operation.OperationId) ? null : operation.OperationId.Trim();
        var usableId = operationId is not null && !duplicateIds.Contains(operationId);
        if (operationId is not null && !usableId)
            opWarnings.Add($"operationId '{operationId}' is used more than once; identity falls back to method + path.");

        var opKey = usableId ? operationId! : $"{upperMethod} {path}";

        // Path-level parameters apply to every operation on that path; the operation's own list
        // overrides by (name, in). Merging here means nothing downstream has to know the rule.
        var parameters = MergeParameters(pathItem.Parameters, operation.Parameters);

        var (body, contentType) = MapRequestBody(operation.RequestBody, opWarnings);

        var security = operation.Security is { Count: > 0 }
            ? SecurityKeys(operation.Security)
            : documentSecurity;

        var mapped = new MappedOperation
        {
            OpKey = opKey,
            OperationId = operationId,
            Method = upperMethod,
            Path = path,
            Summary = Trim(operation.Summary),
            Description = Trim(operation.Description),
            Tags = operation.Tags?.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                       .Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            Deprecated = operation.Deprecated,
            Parameters = parameters,
            RequestBody = body,
            RequestContentType = contentType,
            SecurityKeys = security,
            SourceHash = string.Empty,
            Warnings = opWarnings,
        };

        return mapped with { SourceHash = HashOperation(mapped) };
    }

    private static IReadOnlyList<MappedParameter> MergeParameters(
        IList<IOpenApiParameter>? pathLevel, IList<IOpenApiParameter>? operationLevel)
    {
        var merged = new Dictionary<(string, ParameterIn), MappedParameter>();

        void Add(IEnumerable<IOpenApiParameter>? source)
        {
            foreach (var p in source ?? [])
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var location = MapLocation(p.In);
                if (location is null) continue;
                merged[(p.Name!, location.Value)] = new MappedParameter(
                    Name: p.Name!,
                    In: location.Value,
                    Required: p.Required || location == ParameterIn.Path, // path params are always required
                    Description: Trim(p.Description),
                    Example: p.Example?.ToJsonString().Trim('"') is { Length: > 0 } e ? e : ExampleFromSchema(p.Schema),
                    TypeHint: TypeHint(p.Schema));
            }
        }

        Add(pathLevel);
        Add(operationLevel); // operation-level wins on collision, per the spec

        return merged.Values
            .OrderBy(p => p.In)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string? Body, string? ContentType) MapRequestBody(
        IOpenApiRequestBody? requestBody, List<string> warnings)
    {
        if (requestBody?.Content is not { Count: > 0 } content) return (null, null);

        foreach (var candidate in PreferredContentTypes)
        {
            var hit = content.FirstOrDefault(kv =>
                kv.Key.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is null) continue;
            return (SchemaExampleBuilder.BuildFromMediaType(hit.Value), hit.Key);
        }

        // multipart/form-data and friends: Tap models file bodies with a `< ./file` ref, which we
        // cannot synthesize. Name the type so the user knows what to build by hand.
        var first = content.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        warnings.Add($"Request body is '{first}', which is not generated — add the body by hand.");
        return (null, first);
    }

    private static IReadOnlyList<string> SecurityKeys(IList<OpenApiSecurityRequirement>? requirements)
    {
        if (requirements is not { Count: > 0 }) return [];
        var keys = new List<string>();
        foreach (var requirement in requirements)
        {
            foreach (var scheme in requirement.Keys)
            {
                // A reference holder's id is the key into components.securitySchemes.
                if (scheme is IOpenApiReferenceHolder { UnresolvedReference: true }) continue;
                var name = SchemeKey(scheme);
                if (name is not null && !keys.Contains(name, StringComparer.Ordinal)) keys.Add(name);
            }
        }
        return keys;
    }

    /// <summary>The components key for a security scheme, which is what the document's
    /// <c>security</c> entries point at.</summary>
    internal static string? SchemeKey(IOpenApiSecurityScheme scheme)
        => scheme switch
        {
            OpenApiSecuritySchemeReference r => r.Reference?.Id,
            _ => scheme.Name,
        };

    private static HashSet<string> FindDuplicateOperationIds(OpenApiDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, pathItem) in document.Paths ?? [])
        {
            foreach (var (_, operation) in pathItem?.Operations ?? [])
            {
                var id = operation?.OperationId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!seen.Add(id)) duplicates.Add(id);
            }
        }
        return duplicates;
    }

    private static ParameterIn? MapLocation(ParameterLocation? location) => location switch
    {
        ParameterLocation.Path => ParameterIn.Path,
        ParameterLocation.Query => ParameterIn.Query,
        ParameterLocation.Header => ParameterIn.Header,
        ParameterLocation.Cookie => ParameterIn.Cookie,
        _ => null,
    };

    private static string? ExampleFromSchema(IOpenApiSchema? schema)
    {
        if (schema is null) return null;
        if (SchemaExampleBuilder.SchemaExample(schema) is { } ex) return ex.ToJsonString().Trim('"');
        if (schema.Default is { } def) return def.ToJsonString().Trim('"');
        if (schema.Enum is { Count: > 0 } values && values[0] is { } first) return first.ToJsonString().Trim('"');
        return null;
    }

    private static string? TypeHint(IOpenApiSchema? schema)
    {
        if (schema?.Type is not { } type) return null;
        var name = type switch
        {
            var t when (t & JsonSchemaType.String) == JsonSchemaType.String => "string",
            var t when (t & JsonSchemaType.Integer) == JsonSchemaType.Integer => "integer",
            var t when (t & JsonSchemaType.Number) == JsonSchemaType.Number => "number",
            var t when (t & JsonSchemaType.Boolean) == JsonSchemaType.Boolean => "boolean",
            var t when (t & JsonSchemaType.Array) == JsonSchemaType.Array => "array",
            _ => null,
        };
        return schema.Format is { Length: > 0 } f && name is not null ? $"{name} ({f})" : name;
    }

    private static string? Trim(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>
    /// A stable fingerprint of everything about the operation that Tap turns into file content.
    /// Re-sync compares this against the value stored in the lock to answer "did upstream change?"
    /// without keeping the previous document. Field order is fixed and values are normalized, so
    /// re-serializing the same spec always produces the same hash.
    /// </summary>
    private static string HashOperation(MappedOperation op)
    {
        var sb = new StringBuilder();
        sb.Append(op.Method).Append('\n').Append(op.Path).Append('\n');
        sb.Append(op.OperationId).Append('\n');
        sb.Append(op.Summary).Append('\n').Append(op.Description).Append('\n');
        sb.Append(op.Deprecated).Append('\n');
        sb.Append(string.Join(",", op.Tags.OrderBy(t => t, StringComparer.Ordinal))).Append('\n');
        foreach (var p in op.Parameters)
        {
            sb.Append(p.In.ToString()).Append(':').Append(p.Name).Append(':')
              .Append(p.Required.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(p.Description).Append(':').Append(p.Example).Append(':').Append(p.TypeHint).Append('\n');
        }
        sb.Append(op.RequestContentType).Append('\n').Append(op.RequestBody).Append('\n');
        sb.Append(string.Join(",", op.SecurityKeys)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
