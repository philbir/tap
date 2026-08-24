using System.Text.Json.Serialization;
using Tap.Core.Redaction;

namespace Tap.Core.Capture;

/// <summary>One field that differs between two exchanges.</summary>
/// <param name="Left">The first request's value, or null when it had none.</param>
public sealed record CaptureDifference(
    [property: JsonPropertyName("what")] string What,
    [property: JsonPropertyName("left")] string? Left,
    [property: JsonPropertyName("right")] string? Right);

/// <summary>The answer to "why does this one work and that one not".</summary>
public sealed record CaptureDiffEnvelope(
    [property: JsonPropertyName("trust")] string Trust,
    [property: JsonPropertyName("left")] CapturedRequestSummary Left,
    [property: JsonPropertyName("right")] CapturedRequestSummary Right,
    [property: JsonPropertyName("identical")] bool Identical,
    [property: JsonPropertyName("differences")] IReadOnlyList<CaptureDifference> Differences);

/// <summary>
/// Compares two captured exchanges.
///
/// <para>The second question of every debugging session, right after "show me the request" —
/// and the one a human is worst at, because it means eyeballing two walls of headers for the
/// one that changed.</para>
///
/// <para>It works on redacted data, which sounds like it should cripple it and does not:
/// fingerprints make masked values comparable. <c>Authorization differs (#a91f3c2d vs
/// #4b2ec7d1)</c> is the finding, and it is available without either token being visible to
/// anyone. Two masks with the same fingerprint are the same credential; two with different
/// fingerprints are not. That is the whole question.</para>
/// </summary>
public static class CaptureDiff
{
    public static CaptureDiffEnvelope Compare(CapturedRequestDetail left, CapturedRequestDetail right)
    {
        var differences = new List<CaptureDifference>();

        Scalar(differences, "method", left.Summary.Method, right.Summary.Method);
        Scalar(differences, "host", left.Summary.Host, right.Summary.Host);
        Scalar(differences, "status", left.Summary.Status.ToString(), right.Summary.Status.ToString());
        Scalar(differences, "error", left.Summary.Error, right.Summary.Error);
        Scalar(differences, "client", left.Summary.Client, right.Summary.Client);

        ComparePaths(differences, left.Summary.Path, right.Summary.Path);
        CompareHeaders(differences, "request.header", left.RequestHeaders, right.RequestHeaders);
        CompareHeaders(differences, "response.header", left.ResponseHeaders, right.ResponseHeaders);
        CompareBodies(differences, "request.body", left.RequestBody, right.RequestBody);
        CompareBodies(differences, "response.body", left.ResponseBody, right.ResponseBody);

        return new CaptureDiffEnvelope(
            CaptureTrust.Notice, left.Summary, right.Summary, differences.Count == 0, differences);
    }

    private static void Scalar(List<CaptureDifference> into, string what, string? left, string? right)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal)) into.Add(new CaptureDifference(what, left, right));
    }

    /// <summary>Path and query separately: a different query parameter is a different bug from
    /// a different route, and collapsing them into one string diff hides which.</summary>
    private static void ComparePaths(List<CaptureDifference> into, string left, string right)
    {
        var (leftPath, leftQuery) = SplitTarget(left);
        var (rightPath, rightQuery) = SplitTarget(right);

        Scalar(into, "path", leftPath, rightPath);

        foreach (var key in leftQuery.Keys.Union(rightQuery.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Scalar(
                into,
                $"query:{key}",
                leftQuery.GetValueOrDefault(key),
                rightQuery.GetValueOrDefault(key));
        }
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var mark = target.IndexOf('?');
        if (mark < 0) return (target, new Dictionary<string, string>(StringComparer.Ordinal));

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) query[pair] = string.Empty;
            else query[pair[..eq]] = pair[(eq + 1)..];
        }

        return (target[..mark], query);
    }

    private static void CompareHeaders(
        List<CaptureDifference> into,
        string scope,
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null) return;

        // Volatile by nature and different on every request — reporting them would bury the
        // one header that actually explains the difference.
        string[] noise = ["Date", "Content-Length", "User-Agent", "X-Request-Id", "Traceparent"];

        var names = left.Keys
            .Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(n => !noise.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            left.TryGetValue(name, out var leftValue);
            right.TryGetValue(name, out var rightValue);
            Scalar(into, $"{scope}:{name}", leftValue, rightValue);
        }
    }

    private static void CompareBodies(
        List<CaptureDifference> into, string what, RedactedBody? left, RedactedBody? right)
    {
        if (left is null || right is null) return;

        // sha256 first: it covers the bytes the reader is not allowed to see, so it settles
        // "same payload?" even for a binary body with no text at all.
        if (string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)) return;

        Scalar(into, $"{what}.kind", left.Kind, right.Kind);
        Scalar(into, $"{what}.size", left.OriginalSize.ToString(), right.OriginalSize.ToString());

        if (left.Text is null || right.Text is null)
        {
            into.Add(new CaptureDifference($"{what}.sha256", left.Sha256, right.Sha256));
            return;
        }

        if (!string.Equals(left.Text, right.Text, StringComparison.Ordinal))
        {
            into.Add(new CaptureDifference(what, left.Text, right.Text));
        }
    }
}
