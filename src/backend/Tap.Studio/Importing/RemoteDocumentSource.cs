using System.Text;
using Tap.Execution.Http;

namespace Tap.Studio.Importing;

/// <summary>
/// Fetches an API description over HTTP for the importers' "import from URL" path.
///
/// <para>Shared by the OpenAPI and WSDL wizards. The redirect policy below is the security
/// boundary for a user-supplied URL, and a second copy of it is exactly the kind of thing that
/// drifts — so there is one, parameterized only by what differs between the formats (the
/// <c>Accept</c> header and the size cap).</para>
/// </summary>
public sealed class RemoteDocumentSource(HttpClient http)
{
    /// <summary>Long enough for a cold-starting dev API, short enough that a wrong URL fails
    /// while the user is still looking at the dialog.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public sealed record FetchResult(string? Text, string? Error)
    {
        public bool Ok => Text is not null;
        public static FetchResult Failed(string error) => new(null, error);
    }

    /// <param name="accept">Sent verbatim as the <c>Accept</c> header.</param>
    /// <param name="maxBytes">Refusal threshold, checked against the advertised length and again
    /// while reading.</param>
    /// <param name="exampleUrl">Shown in the "that isn't a URL" message, so the hint names the
    /// format the user is actually importing.</param>
    public async Task<FetchResult> FetchAsync(
        string url, string accept, int maxBytes, string exampleUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            return FetchResult.Failed($"Enter an absolute URL, for example {exampleUrl}.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return FetchResult.Failed($"'{uri.Scheme}' URLs are not supported — use http or https.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);

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
            if (response.Content.Headers.ContentLength > maxBytes)
                return FetchResult.Failed(TooLarge(maxBytes));

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var text = await ReadBoundedAsync(stream, maxBytes, cts.Token).ConfigureAwait(false);
            return text is null ? FetchResult.Failed(TooLarge(maxBytes)) : new FetchResult(text, null);
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

    /// <summary>Reads at most <paramref name="maxBytes"/>, returning null if the stream has more —
    /// so a server that lies about (or omits) Content-Length still can't exhaust memory.</summary>
    private static async Task<string?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (accumulated.Length + read > maxBytes) return null;
            accumulated.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(accumulated.ToArray());
    }

    public static string TooLarge(int maxBytes)
        => $"The document is larger than {maxBytes / (1024 * 1024)} MB.";
}
