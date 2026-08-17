using System.Text;
using Tap.Studio.Importing;

namespace Tap.Studio.OpenApi;

/// <summary>Derives the file/request slug for an operation. Shared so a collection imported as
/// <c>.req.tap</c> and the same one imported as <c>.http</c> name their requests identically.</summary>
public static class RequestSlug
{
    /// <summary>
    /// <c>operationId</c> when the document has one — it is the name the API's own authors chose,
    /// and it is what their SDKs use. Otherwise a readable name built from method and path, with
    /// path parameters folded in as <c>by-{name}</c> so <c>GET /pets/{petId}</c> becomes
    /// <c>get-pets-by-pet-id</c> rather than <c>get-pets-petid</c>.
    /// </summary>
    public static string For(MappedOperation op)
    {
        // operationIds are camelCase by near-universal convention (`listPets`, `getPetById`), and
        // slugifying one directly would collapse it to `getpetbyid`. Splitting first is what makes
        // the generated filename readable.
        if (op.OperationId is { Length: > 0 } id
            && ImportSlug.Slugify(SplitCamelCase(id)) is { Length: > 0 } fromId)
            return fromId;

        var sb = new StringBuilder(op.Method.ToLowerInvariant());
        foreach (var segment in op.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var name = segment[1..^1];
                sb.Append("-by-").Append(ImportSlug.Slugify(SplitCamelCase(name)));
            }
            else
            {
                sb.Append('-').Append(ImportSlug.Slugify(segment));
            }
        }

        var slug = ImportSlug.Slugify(sb.ToString());
        return slug.Length > 0 ? slug : "request";
    }

    /// <summary><c>petId</c> → <c>pet Id</c>, so slugification produces <c>pet-id</c>.</summary>
    public static string Humanize(string value) => SplitCamelCase(value);

    /// <summary>
    /// Slug for an OpenAPI tag — the <c>.http</c> filename or the folder in the structured layout.
    ///
    /// <para>Single-sourced because two places derive it: the importer when laying files out, and
    /// re-sync when deciding which existing file a new operation belongs in. If those disagreed,
    /// re-sync would start a second file beside the first every time.</para>
    /// </summary>
    public static string ForTag(string tag) => ImportSlug.Slugify(SplitCamelCase(tag));

    private static string SplitCamelCase(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1])) sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}

/// <summary>Builds the request URL line for an operation.</summary>
public static class UrlBuilder
{
    /// <summary>
    /// Converts the OpenAPI path template to Tap's variable syntax and appends required query
    /// parameters.
    ///
    /// <para>OpenAPI writes <c>/pets/{petId}</c>; Tap (and REST Client, and every other tool that
    /// reads <c>.http</c>) writes <c>{{petId}}</c>. Doubling the braces is all that separates the
    /// two, which is why a path template drops straight into a request line.</para>
    ///
    /// <para><paramref name="prefix"/> is <c>{{baseUrl}}</c> for portable <c>.http</c> output and
    /// null for <c>.req.tap</c>, where a bare path is correct — Tap prepends the collection's base
    /// URL, so the request follows whatever stage is selected.</para>
    /// </summary>
    public static string Build(MappedOperation op, string? prefix, bool includeOptionalQuery = false)
    {
        var sb = new StringBuilder();
        if (prefix is { Length: > 0 }) sb.Append(prefix);

        var path = op.Path.StartsWith('/') ? op.Path : "/" + op.Path;
        sb.Append(Templatize(path));

        var query = op.Parameters
            .Where(p => p.In == ParameterIn.Query && (includeOptionalQuery || p.Required))
            .ToList();

        for (var i = 0; i < query.Count; i++)
        {
            sb.Append(i == 0 ? '?' : '&');
            sb.Append(query[i].Name).Append("={{").Append(query[i].Name).Append("}}");
        }

        return sb.ToString();
    }

    /// <summary>Single braces to double, leaving everything else untouched.</summary>
    private static string Templatize(string path)
    {
        if (!path.Contains('{')) return path;

        var sb = new StringBuilder(path.Length + 8);
        foreach (var ch in path)
        {
            if (ch is '{' or '}') sb.Append(ch).Append(ch);
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
