using System.Text.Json.Serialization;

namespace Tap.Core.Capture;

/// <summary>Where a search term turned up, and just enough text around it to judge.</summary>
public sealed record CaptureSearchHit(
    [property: JsonPropertyName("request")] CapturedRequestSummary Request,
    [property: JsonPropertyName("where")] string Where,
    [property: JsonPropertyName("excerpt")] string Excerpt);

public sealed record CaptureSearchEnvelope(
    [property: JsonPropertyName("trust")] string Trust,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("hits")] IReadOnlyList<CaptureSearchHit> Hits);

/// <summary>
/// Free-text search across captured exchanges — "which request carried order 4021?", the one
/// question the structured filters cannot answer.
///
/// <para><b>It searches redacted text and nothing else, and that is the entire design.</b> A
/// search that matched the raw record would be an oracle: an agent could binary-search a
/// masked token one character at a time and read the answer off the result count, which would
/// quietly undo every other guarantee on this surface. Matching post-redaction means a query
/// aimed at a hidden value simply finds nothing — there is no signal to extract, not even a
/// count. Excerpts come from the same redacted text, so they cannot leak either.</para>
///
/// <para>The cost is honest and worth stating: you cannot search for a token. That is the
/// point.</para>
/// </summary>
public static class CaptureSearch
{
    private const int ExcerptRadius = 60;

    public static CaptureSearchHit? Find(CapturedRequestDetail detail, string term)
    {
        foreach (var (where, text) in Searchable(detail))
        {
            if (text is null) continue;

            var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            return new CaptureSearchHit(detail.Summary, where, Excerpt(text, at, term.Length));
        }

        return null;
    }

    private static IEnumerable<(string Where, string? Text)> Searchable(CapturedRequestDetail detail)
    {
        yield return ("path", detail.Summary.Path);
        yield return ("error", detail.Summary.Error);
        yield return ("request.body", detail.RequestBody?.Text);
        yield return ("response.body", detail.ResponseBody?.Text);

        foreach (var (name, value) in detail.RequestHeaders ?? new Dictionary<string, string>())
        {
            yield return ($"request.header:{name}", value);
        }

        foreach (var (name, value) in detail.ResponseHeaders ?? new Dictionary<string, string>())
        {
            yield return ($"response.header:{name}", value);
        }
    }

    private static string Excerpt(string text, int at, int length)
    {
        var start = Math.Max(0, at - ExcerptRadius);
        var end = Math.Min(text.Length, at + length + ExcerptRadius);
        var slice = text[start..end];

        return (start > 0 ? "…" : "") + slice + (end < text.Length ? "…" : "");
    }
}
