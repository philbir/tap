using Tap.Studio.Importing;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Fetches a WSDL over HTTP for the "import from URL" path. The transport, the size bound, and the
/// redirect policy that makes a user-supplied URL safe all live in
/// <see cref="RemoteDocumentSource"/>; this only pins the WSDL-specific parameters.
/// </summary>
public sealed class WsdlSpecSource(HttpClient http)
{
    private const string Accept = "text/xml, application/xml, application/wsdl+xml, */*";

    private readonly RemoteDocumentSource _source = new(http);

    public Task<RemoteDocumentSource.FetchResult> FetchAsync(string url, CancellationToken ct)
        => _source.FetchAsync(
            url, Accept, WsdlDocumentReader.MaxDocumentBytes,
            "https://api.example.com/service.asmx?wsdl", ct);
}
