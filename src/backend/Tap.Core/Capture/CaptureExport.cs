using System.Text;
using System.Text.Json.Serialization;
using Tap.Core.Redaction;

namespace Tap.Core.Capture;

/// <summary>The exported document, ready to be written to a file by whoever asked for it.</summary>
public sealed record CaptureExportEnvelope(
    [property: JsonPropertyName("trust")] string Trust,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("suggestedFileName")] string SuggestedFileName,
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("placeholders")] IReadOnlyList<string> Placeholders);

/// <summary>
/// Turns a captured exchange into a request file you can keep — a Tap <c>.req.tap</c> or a
/// portable <c>.http</c>.
///
/// <para>This <em>returns</em> the document rather than writing it into a workspace, which is
/// deliberate on two counts. The inspector and Studio share nothing at runtime and this is not
/// the place to start; and an agent already has a perfectly good way to write a file, so
/// handing it text keeps the inspector out of the business of knowing where somebody's
/// workspace lives.</para>
///
/// <para>Redacted values never survive into the document. A file containing
/// <c>Authorization: Bearer [redacted:jwt …]</c> would be a request that cannot work and a
/// mask that looks like a value; instead each one becomes a <c>{{variable}}</c>, listed in
/// <see cref="CaptureExportEnvelope.Placeholders"/> so the reader knows exactly what they have
/// to supply.</para>
/// </summary>
public static class CaptureExport
{
    public static CaptureExportEnvelope Export(CapturedRequestDetail detail, string format)
    {
        var tap = !format.Equals("http", StringComparison.OrdinalIgnoreCase);
        var placeholders = new List<string>();
        var summary = detail.Summary;

        var headers = new List<string>();
        foreach (var (name, value) in detail.RequestHeaders ?? new Dictionary<string, string>())
        {
            if (Skip(name)) continue;
            headers.Add($"{name}: {Substitute(name, value, placeholders)}");
        }

        var body = SubstituteBody(detail.RequestBody, placeholders);
        var stem = SuggestName(summary.Method, summary.Path);

        var document = tap
            ? BuildTap(summary, headers, body, stem)
            : BuildHttp(summary, headers, body, stem);

        return new CaptureExportEnvelope(
            CaptureTrust.Notice,
            tap ? "tap" : "http",
            tap ? $"{stem}.req.tap" : $"{stem}.http",
            document,
            placeholders);
    }

    /// <summary>Headers the sender will set for itself. Copying a captured
    /// <c>Content-Length</c> into a file whose body someone then edits produces a request that
    /// fails in a way nobody enjoys diagnosing.</summary>
    private static bool Skip(string name)
        => name is "Content-Length" or "Host" or "Connection" or "Accept-Encoding"
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces a redacted value with a variable reference. The mask cannot be sent and must
    /// not look like it could be, so what lands in the file is a name the reader has to fill in.
    /// </summary>
    private static string Substitute(string header, string value, List<string> placeholders)
    {
        if (!value.Contains("[redacted:", StringComparison.Ordinal)) return value;

        var variable = VariableName(header);
        if (!placeholders.Contains(variable, StringComparer.Ordinal)) placeholders.Add(variable);

        // Keep an unredacted prefix when there is one — "Bearer {{token}}" is more useful than
        // "{{token}}", because the scheme was never the secret.
        var mask = value.IndexOf("[redacted:", StringComparison.Ordinal);
        var prefix = value[..mask].TrimEnd();
        return prefix.Length > 0 ? $"{prefix} {{{{{variable}}}}}" : $"{{{{{variable}}}}}";
    }

    /// <summary>
    /// Replaces every mask inside a body with a named placeholder.
    ///
    /// <para>Masks are matched back to their <see cref="RedactionNote"/> by fingerprint, which
    /// is what lets <c>[redacted:opaque #d8db1990 len=8]</c> become <c>{{password}}</c> rather
    /// than an anonymous <c>{{secret_1}}</c> — the note already knows the value came from
    /// <c>request.body:$.password</c>.</para>
    ///
    /// <para>Skipping this would leave a mask sitting in a request file where a value belongs:
    /// a password of literally "[redacted:opaque …]" that fails in a confusing way, and reads
    /// to a careless eye like the real thing.</para>
    /// </summary>
    private static string? SubstituteBody(RedactedBody? body, List<string> placeholders)
    {
        var text = body?.Text;
        if (text is null || !text.Contains("[redacted:", StringComparison.Ordinal)) return text;

        var byFingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var note in body!.Notes)
        {
            if (note.Fingerprint is { } print) byFingerprint.TryAdd(print, LeafOf(note.Location));
        }

        var result = new StringBuilder();
        var cursor = 0;
        var anonymous = 0;

        while (true)
        {
            var open = text.IndexOf("[redacted:", cursor, StringComparison.Ordinal);
            if (open < 0) break;

            var close = text.IndexOf(']', open);
            if (close < 0) break;

            var mask = text[open..(close + 1)];
            var variable = FingerprintIn(mask) is { } print && byFingerprint.TryGetValue(print, out var named)
                ? named
                : $"secret_{++anonymous}";

            if (!placeholders.Contains(variable, StringComparer.Ordinal)) placeholders.Add(variable);

            result.Append(text, cursor, open - cursor).Append("{{").Append(variable).Append("}}");
            cursor = close + 1;
        }

        return result.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static string? FingerprintIn(string mask)
    {
        var hash = mask.IndexOf('#');
        if (hash < 0) return null;

        var end = mask.IndexOf(' ', hash);
        return end < 0 ? mask[hash..^1] : mask[hash..end];
    }

    /// <summary>The last name in a redaction location: <c>request.body:$.user.password</c> to
    /// <c>password</c>, <c>$.items[1].apiKey</c> to <c>apiKey</c>.</summary>
    private static string LeafOf(string location)
    {
        var leaf = location;
        foreach (var separator in (ReadOnlySpan<char>)['.', ':', '/'])
        {
            var at = leaf.LastIndexOf(separator);
            if (at >= 0 && at < leaf.Length - 1) leaf = leaf[(at + 1)..];
        }

        var cleaned = new StringBuilder();
        foreach (var c in leaf)
        {
            if (char.IsAsciiLetterOrDigit(c)) cleaned.Append(c);
            else if (cleaned.Length > 0 && cleaned[^1] != '_') cleaned.Append('_');
        }

        var name = cleaned.ToString().Trim('_');
        return name.Length > 0 ? name : "secret";
    }

    private static string VariableName(string header)
    {
        var cleaned = new StringBuilder();
        foreach (var c in header)
        {
            if (char.IsAsciiLetterOrDigit(c)) cleaned.Append(char.ToLowerInvariant(c));
            else if (cleaned.Length > 0 && cleaned[^1] != '_') cleaned.Append('_');
        }

        return cleaned.ToString().Trim('_') is { Length: > 0 } s ? s : "secret";
    }

    private static string BuildHttp(
        CapturedRequestSummary summary, List<string> headers, string? body, string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Captured by Tap on {summary.At:yyyy-MM-dd HH:mm:ss}Z — {summary.Status} in {summary.DurationMs}ms.");
        sb.AppendLine("# Any {{placeholder}} below replaced a value the inspector redacted; supply your own.");
        sb.AppendLine();
        sb.AppendLine($"@baseUrl = {summary.Scheme}://{summary.Host}");
        sb.AppendLine();
        sb.AppendLine($"### {name}");
        sb.AppendLine($"# @name {name}");
        sb.AppendLine($"# @tap-assert status {summary.Status}");
        sb.AppendLine($"{summary.Method} {{{{baseUrl}}}}{summary.Path}");
        foreach (var header in headers) sb.AppendLine(header);

        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine();
            sb.AppendLine(body);
        }

        return sb.ToString();
    }

    private static string BuildTap(
        CapturedRequestSummary summary, List<string> headers, string? body, string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: request");
        sb.AppendLine($"name: {name}");
        sb.AppendLine("tags: [captured]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("```http");
        sb.AppendLine($"{summary.Method} {summary.Path}");
        foreach (var header in headers) sb.AppendLine(header);

        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine();
            sb.AppendLine(body);
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("```assert");
        sb.AppendLine($"status {summary.Status}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Captured by Tap on {summary.At:yyyy-MM-dd HH:mm:ss}Z against `{summary.Host}`.");
        sb.AppendLine();
        sb.AppendLine("Set the collection's `baseUrl` to this host, and supply any `{{placeholder}}`");
        sb.AppendLine("above — each one replaced a value the inspector redacted.");
        return sb.ToString();
    }

    private static string SuggestName(string method, string path)
    {
        var mark = path.IndexOf('?');
        if (mark >= 0) path = path[..mark];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Contains("[redacted:", StringComparison.Ordinal))
            .TakeLast(2)
            .ToArray();

        var stem = segments.Length > 0 ? string.Join('-', segments) : "request";
        var cleaned = new StringBuilder(method.ToLowerInvariant());
        cleaned.Append('-');
        foreach (var c in stem)
        {
            if (char.IsAsciiLetterOrDigit(c)) cleaned.Append(char.ToLowerInvariant(c));
            else if (cleaned.Length > 0 && cleaned[^1] != '-') cleaned.Append('-');
        }

        return cleaned.ToString().Trim('-');
    }
}
