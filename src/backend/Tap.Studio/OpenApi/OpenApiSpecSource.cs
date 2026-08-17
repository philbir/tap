using System.Text;
using Tap.Execution.Http;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Fetches an OpenAPI document over HTTP for the "import from URL" path.
/// </summary>
public sealed class OpenApiSpecSource(HttpClient http)
{
    /// <summary>Long enough for a cold-starting dev API, short enough that a wrong URL fails
    /// while the user is still looking at the dialog.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public sealed record FetchResult(string? Text, string? Error)
    {
        public bool Ok => Text is not null;
        public static FetchResult Failed(string error) => new(null, error);
    }

    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            return FetchResult.Failed("Enter an absolute URL, for example https://api.example.com/openapi.json.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return FetchResult.Failed($"'{uri.Scheme}' URLs are not supported — use http or https.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json, application/yaml, text/yaml, text/plain, */*");

            // The first hop is the user's own choice and is deliberately not network-restricted —
            // `http://localhost:5001/openapi/v1.json` is the single most common case, and the
            // Aspire scaffold depends on it. Redirects are a different matter: a Location chosen
            // by a remote server must not walk the request into loopback or link-local space.
            // SendFollowingRedirectsAsync enforces exactly that asymmetry; do not "harden" the
            // first hop here or the Aspire path breaks.
            using var response = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                http, request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Failed(
                    $"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            // Check the advertised length first so an oversized document is refused before it is
            // read, then bound the read itself — Content-Length is a hint, not a promise.
            if (response.Content.Headers.ContentLength is > OpenApiDocumentReader.MaxDocumentBytes)
                return FetchResult.Failed(TooLarge());

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var text = await ReadBoundedAsync(stream, cts.Token).ConfigureAwait(false);
            return text is null ? FetchResult.Failed(TooLarge()) : new FetchResult(text, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FetchResult.Failed($"{uri} did not respond within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return FetchResult.Failed($"Could not fetch {uri}: {ex.Message}");
        }
    }

    /// <summary>Reads at most <see cref="OpenApiDocumentReader.MaxDocumentBytes"/>, returning null
    /// if the stream has more — so a server that lies about (or omits) Content-Length still can't
    /// exhaust memory.</summary>
    private static async Task<string?> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (accumulated.Length + read > OpenApiDocumentReader.MaxDocumentBytes) return null;
            accumulated.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(accumulated.ToArray());
    }

    private static string TooLarge()
        => $"The document is larger than {OpenApiDocumentReader.MaxDocumentBytes / (1024 * 1024)} MB.";
}
