using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Tap.Workspace.Parsing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Parses an OpenAPI document from raw text — JSON or YAML, 2.0 / 3.0 / 3.1 — and reports what
/// went wrong without throwing for anything recoverable.
/// </summary>
public static class OpenApiDocumentReader
{
    /// <summary>Documents larger than this are refused. Comfortably above any hand-maintained
    /// spec, well below the size at which parsing becomes a denial of service.</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>Operations beyond this are dropped with a diagnostic rather than silently, so a
    /// generated 50k-operation document can't wedge the UI.</summary>
    public const int MaxOperations = 5000;

    /// <summary>Real specs nest deeply through composed schemas, so this is far looser than the
    /// workspace-frontmatter limit; it exists only to stop recursive-descent stack overflow.</summary>
    private const int MaxYamlDepth = 64;

    public sealed record Diagnostic(string Severity, string Message, string? Pointer);

    public sealed record ReadOutcome(
        OpenApiDocument? Document,
        string SpecVersion,
        IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool Ok => Document is not null;
    }

    /// <summary>
    /// Reads the document. <paramref name="sourceName"/> only labels diagnostics.
    ///
    /// <para>Parse failure is returned, not thrown: a spec with one bad path is still worth
    /// importing the other ninety operations from, and the caller decides.</para>
    /// </summary>
    public static ReadOutcome Read(string text, string sourceName)
    {
        var diagnostics = new List<Diagnostic>();

        if (string.IsNullOrWhiteSpace(text))
            return new ReadOutcome(null, "unknown", [Error("The document is empty.")]);

        // Screen YAML *before* handing it to a YamlDotNet-backed reader. A spec fetched from a URL
        // is strictly less trustworthy than a file in the user's own repo, and the pinned
        // YamlDotNet expands an alias bomb at ~9x per nesting level (520 bytes -> 105 s). JSON
        // skips the screen: System.Text.Json has its own depth limit and no alias concept.
        if (!LooksLikeJson(text) && YamlSafety.Screen(text, MaxYamlDepth) is { } rejection)
        {
            var reason = rejection.Kind switch
            {
                YamlRejectionKind.Alias or YamlRejectionKind.Anchor =>
                    $"{sourceName} uses YAML anchors or aliases (line {rejection.Line}). These are "
                    + "refused because they can be expanded into a denial-of-service payload. "
                    + "Convert the document to JSON, or inline the anchored values.",
                _ => $"{sourceName} nests more than {MaxYamlDepth} levels deep (line {rejection.Line}).",
            };
            return new ReadOutcome(null, "unknown", [Error(reason)]);
        }

        var settings = new OpenApiReaderSettings
        {
            // Never let the *document* choose what we fetch next. One user-supplied URL must not
            // fan out into N requests to hostnames named inside the file. Unresolved refs then
            // degrade to warnings, which is already this library's behaviour for a missing target.
            LoadExternalRefs = false,
        };
        settings.AddYamlReader();

        ReadResult result;
        try
        {
            // format: null => try JSON first (fast path), fall back to the registered YAML reader.
            result = OpenApiDocument.Parse(text, format: null, settings);
        }
        catch (Exception ex)
        {
            return new ReadOutcome(null, "unknown", [Error($"Could not parse the document: {ex.Message}")]);
        }

        var version = result.Diagnostic?.SpecificationVersion switch
        {
            OpenApiSpecVersion.OpenApi2_0 => "2.0",
            OpenApiSpecVersion.OpenApi3_0 => "3.0",
            OpenApiSpecVersion.OpenApi3_1 => "3.1",
            _ => "unknown",
        };

        foreach (var e in result.Diagnostic?.Errors ?? [])
            diagnostics.Add(new Diagnostic("error", e.Message, e.Pointer));
        foreach (var w in result.Diagnostic?.Warnings ?? [])
            diagnostics.Add(new Diagnostic("warning", w.Message, w.Pointer));

        if (result.Document is null)
        {
            if (diagnostics.All(d => d.Severity != "error"))
                diagnostics.Add(Error("The document could not be read as an OpenAPI description."));
            return new ReadOutcome(null, version, diagnostics);
        }

        if (version == "2.0")
        {
            diagnostics.Add(new Diagnostic("warning",
                "This is a Swagger 2.0 document. It was converted to OpenAPI 3, which moves "
                + "'consumes'/'produces' and turns body parameters into a request body — check the "
                + "generated content types.", null));
        }

        return new ReadOutcome(result.Document, version, diagnostics);
    }

    private static Diagnostic Error(string message) => new("error", message, null);

    /// <summary>Cheap sniff so a JSON document skips the YAML screen. Mirrors what the reader
    /// itself does when no explicit format is given.</summary>
    private static bool LooksLikeJson(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch == '{';
        }
        return false;
    }
}
