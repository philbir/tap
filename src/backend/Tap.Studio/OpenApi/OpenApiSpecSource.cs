using Tap.Studio.Importing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Fetches an OpenAPI document over HTTP for the "import from URL" path. The transport, the size
/// bound, and the redirect policy that makes a user-supplied URL safe all live in
/// <see cref="RemoteDocumentSource"/>; this only pins the OpenAPI-specific parameters.
/// </summary>
public sealed class OpenApiSpecSource(HttpClient http)
{
    private const string Accept = "application/json, application/yaml, text/yaml, text/plain, */*";

    private readonly RemoteDocumentSource _source = new(http);

    public static TimeSpan Timeout => RemoteDocumentSource.Timeout;

    public Task<RemoteDocumentSource.FetchResult> FetchAsync(string url, CancellationToken ct)
        => _source.FetchAsync(
            url, Accept, OpenApiDocumentReader.MaxDocumentBytes,
            "https://api.example.com/openapi.json", ct);
}
