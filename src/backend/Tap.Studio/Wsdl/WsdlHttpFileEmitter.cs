using System.Text;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Writes a <c>.http</c> file of SOAP requests, one per port.
///
/// <para>The counterpart to <c>HttpFileEmitter</c> for the other import format, and it keeps the
/// same promise: everything emitted is an ordinary <c>.http</c> file that opens and sends in
/// Visual Studio, VS Code REST Client, JetBrains, httpyac and Kulala. A SOAP request is just a
/// POST with an XML body, so nothing here needs a Tap-specific construct.</para>
/// </summary>
public static class WsdlHttpFileEmitter
{
    /// <summary>
    /// Marker tying a section back to the WSDL operation it came from. A plain comment rather than
    /// a <c># @tap-</c> directive for the same reason the OpenAPI emitter uses one: an unknown
    /// <c>@tap-</c> key raises <c>W_HTTP_UNSUPPORTED_CONSTRUCT</c>, which would add a workspace
    /// warning per operation.
    /// </summary>
    public const string OperationMarkerPrefix = "# tap-wsdl ";

    public sealed record FileOptions(string? AuthRef, string? PortableBaseUrl, string Title);

    /// <summary>One emitted <c>###</c> section, kept separately so its content can be hashed on
    /// its own — one edited request in a twenty-request file must not mark the other nineteen as
    /// locally modified.</summary>
    public sealed record Section(string OpKey, string Name, string Text);

    public sealed record EmitResult(string Content, IReadOnlyList<Section> Sections);

    public static EmitResult Emit(
        IReadOnlyList<MappedSoapOperation> operations,
        Func<MappedSoapOperation, string> urlFor,
        string? header,
        FileOptions options)
    {
        var sb = new StringBuilder(EmitHeader(options));
        var sections = new List<Section>(operations.Count);

        foreach (var operation in operations)
        {
            var text = EmitOperation(
                operation, urlFor(operation), header,
                useBaseUrlVariable: options.PortableBaseUrl is { Length: > 0 });

            sections.Add(new Section(operation.OpKey, SoapRequestSlug.For(operation), text));
            sb.Append('\n').Append(text);
        }

        return new EmitResult(sb.ToString(), sections);
    }

    private static string EmitHeader(FileOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(options.Title)).Append('\n');
        sb.Append("# Generated from a WSDL description by Tap Studio.\n");
        sb.Append("#\n");
        sb.Append("# An ordinary .http file — a SOAP call is a POST with an XML body, so it opens and\n");
        sb.Append("# sends in Visual Studio, VS Code REST Client, JetBrains, httpyac, and Kulala too.\n");

        if (options.AuthRef is { Length: > 0 } auth)
            sb.Append("\n# @tap-auth ").Append(auth).Append('\n');

        // The portable fallback for tools that have no idea this file sits in a Tap collection.
        // Inside Tap the collection's baseUrl wins — a file variable is the weakest scope in the
        // cascade — so the same file follows the selected environment here and still runs elsewhere.
        if (options.PortableBaseUrl is { Length: > 0 } baseUrl)
        {
            sb.Append('\n');
            sb.Append("# Fallback for other tools; Tap uses the collection's baseUrl instead.\n");
            sb.Append("@baseUrl = ").Append(baseUrl).Append('\n');
        }

        return sb.ToString();
    }

    private static string EmitOperation(
        MappedSoapOperation operation, string url, string? header, bool useBaseUrlVariable)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(operation.Name).Append('\n');
        sb.Append(OperationMarkerPrefix).Append(operation.OpKey).Append('\n');
        sb.Append("# @name ").Append(SoapRequestSlug.For(operation)).Append('\n');

        if (operation.Documentation is { Length: > 0 } documentation)
        {
            foreach (var line in documentation.Split('\n'))
                sb.Append("# ").Append(line.TrimEnd()).Append('\n');
        }

        sb.Append("POST ")
          .Append(useBaseUrlVariable && !url.Contains("://", StringComparison.Ordinal) ? "{{baseUrl}}" : string.Empty)
          .Append(url).Append('\n');

        sb.Append("Content-Type: ").Append(SoapEnvelope.ContentType(operation.Version, operation.SoapAction)).Append('\n');
        if (SoapEnvelope.SoapActionHeader(operation.Version, operation.SoapAction) is { } action)
            sb.Append("SOAPAction: ").Append(action).Append('\n');

        sb.Append('\n');
        sb.Append(SoapEnvelope.Build(
            operation.Version, operation.BodyElement, operation.BodyNamespace, operation.BodyPayload, header));
        sb.Append('\n');

        return sb.ToString();
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
