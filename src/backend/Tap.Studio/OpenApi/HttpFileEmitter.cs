using System.Text;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Writes a <c>.http</c> file Tap authored itself.
///
/// <para><b>This does not contradict "Tap never reformats a <c>.http</c> file".</b> That rule
/// protects files the <i>user</i> brought — Tap parses those and writes them back verbatim. A file
/// generated from an OpenAPI document is Tap's own output, the same way the Aspire scaffold's
/// starter request already is.</para>
///
/// <para>Everything emitted stays portable: the request lines, headers, and bodies open and send
/// in Visual Studio, VS Code REST Client, JetBrains, httpyac, and Kulala. The <c># @tap-*</c> lines
/// are inert comments in all of them.</para>
/// </summary>
public static class HttpFileEmitter
{
    /// <summary>
    /// Marker tying a section back to the OpenAPI operation it came from, so re-sync can find it
    /// again after the file has been edited, reordered, or renamed.
    ///
    /// <para>Deliberately <b>not</b> a <c># @tap-</c> directive: <c>TapDirectiveParser</c> reports
    /// an unknown <c>@tap-</c> key as <c>W_HTTP_UNSUPPORTED_CONSTRUCT</c>, which would add one
    /// workspace warning per operation. A plain comment is inert everywhere and silent here.</para>
    /// </summary>
    public const string OperationMarkerPrefix = "# tap-openapi ";

    public sealed record FileOptions(
        string? CollectionSlug,
        string? AuthRef,
        string? PortableBaseUrl,
        string? Title);

    /// <summary>One emitted <c>###</c> section, kept separately so re-sync can hash and replace an
    /// individual operation without rewriting the sections around it.</summary>
    public sealed record Section(string OpKey, string Name, string Text);

    public sealed record EmitResult(string Content, IReadOnlyList<Section> Sections);

    public static string Emit(IReadOnlyList<MappedOperation> operations, FileOptions options)
        => EmitWithSections(operations, options).Content;

    public static EmitResult EmitWithSections(IReadOnlyList<MappedOperation> operations, FileOptions options)
    {
        var header = EmitHeader(options);
        var sections = new List<Section>(operations.Count);

        var sb = new StringBuilder(header);
        foreach (var op in operations)
        {
            var body = new StringBuilder();
            AppendOperation(body, op, useBaseUrlVariable: options.PortableBaseUrl is { Length: > 0 });
            var text = body.ToString();
            sections.Add(new Section(op.OpKey, RequestSlug.For(op), text));
            sb.Append('\n').Append(text);
        }

        return new EmitResult(sb.ToString(), sections);
    }

    private static string EmitHeader(FileOptions options)
    {
        var sb = new StringBuilder();

        if (options.Title is { Length: > 0 } title)
            sb.Append("# ").Append(title).Append('\n');
        sb.Append("# Generated from an OpenAPI description by Tap Studio.\n");
        sb.Append("#\n");
        sb.Append("# An ordinary .http file — it opens and sends in Visual Studio, VS Code REST Client,\n");
        sb.Append("# JetBrains, httpyac, and Kulala. The '# @tap-*' lines are inert comments there and\n");
        sb.Append("# Tap features here.\n");

        if (options.CollectionSlug is { Length: > 0 } slug)
            sb.Append("\n# @tap-collection ").Append(slug).Append('\n');
        if (options.AuthRef is { Length: > 0 } auth)
            sb.Append("# @tap-auth ").Append(auth).Append('\n');

        // The portable fallback for tools that have no idea this file sits in a Tap collection.
        // Inside Tap the collection's baseUrl wins — a file variable is the weakest scope in the
        // cascade — so the same file follows the selected stage here and still runs elsewhere.
        if (options.PortableBaseUrl is { Length: > 0 } baseUrl)
        {
            sb.Append('\n');
            sb.Append("# Fallback for other tools; Tap uses the collection's baseUrl instead.\n");
            sb.Append("@baseUrl = ").Append(baseUrl).Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendOperation(StringBuilder sb, MappedOperation op, bool useBaseUrlVariable)
    {
        var title = op.Summary is { Length: > 0 } s ? s : $"{op.Method} {op.Path}";
        sb.Append("### ").Append(OneLine(title)).Append('\n');
        sb.Append(OperationMarkerPrefix).Append(op.OpKey).Append('\n');
        sb.Append("# @name ").Append(RequestSlug.For(op)).Append('\n');

        if (op.Deprecated)
            sb.Append("# Deprecated in the API description.\n");

        if (op.Description is { Length: > 0 } description)
        {
            foreach (var line in description.Split('\n'))
                sb.Append("# ").Append(line.TrimEnd()).Append('\n');
        }

        foreach (var tag in op.Tags)
            sb.Append("# @tap-tag ").Append(tag).Append('\n');

        // Path/query parameters are documented as comments rather than declared: a .http file has
        // no frontmatter to hold a var spec, and inventing one would break portability. Only the
        // ones that actually appear in the request line are listed, so the comment matches the URL.
        foreach (var p in op.VariableParameters.Where(p => p.In == ParameterIn.Path || p.Required))
        {
            var required = p.Required ? "required" : "optional";
            var detail = p.Description is { Length: > 0 } d ? $" — {OneLine(d)}" : string.Empty;
            sb.Append("# {{").Append(p.Name).Append("}} (").Append(p.In.ToString().ToLowerInvariant())
              .Append(", ").Append(required).Append(')').Append(detail).Append('\n');
        }

        var url = UrlBuilder.Build(op, useBaseUrlVariable ? "{{baseUrl}}" : null);
        sb.Append(op.Method).Append(' ').Append(url).Append('\n');

        if (op.RequestContentType is { Length: > 0 } contentType && op.RequestBody is not null)
            sb.Append("Content-Type: ").Append(contentType).Append('\n');
        sb.Append("Accept: application/json\n");

        if (op.RequestBody is { Length: > 0 } body)
        {
            sb.Append('\n');
            sb.Append(body.TrimEnd()).Append('\n');
        }
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
