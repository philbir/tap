namespace Tap.Workspace.Rendering;

/// <summary>
/// Removes secret material from anything about to be shown to a caller that must not see it —
/// an agent transcript, a CLI's JSON output, an MCP tool result.
///
/// <para>Built by the renderer from what actually went into one render: the clear-text values
/// of every secret that was resolved (cascade vars marked <c>secret: true</c>, provider hits
/// flagged <see cref="Variables.VariableValue.IsSecret"/>, runtime-minted tokens), plus the
/// names of the headers the auth profile contributed. Two defences, because each catches what
/// the other can't: value replacement finds a secret wherever it landed (URL query, body,
/// a header nobody expected), while header-name masking catches values the renderer derived
/// rather than resolved — a Basic credential is base64 of two fields and never appears
/// verbatim in any provider's output.</para>
///
/// <para>The class deliberately exposes no way to read the values back; it exists so callers
/// can hold "what must be hidden" without holding the secrets' plain text in their own
/// shapes. Serializing it yields nothing.</para>
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "***";

    /// <summary>Values shorter than this are not value-replaced. Replacing every occurrence
    /// of a two-character "secret" would shred unrelated output; such values still never
    /// surface through sensitive headers, which are masked by name.</summary>
    private const int MinValueLength = 4;

    /// <summary>Header names masked in every <see cref="RedactHeaders"/> result regardless of
    /// how the value got there — these carry credentials by definition.</summary>
    private static readonly string[] AlwaysSensitiveHeaders =
        ["Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie"];

    public static readonly SecretRedactor None = new([], []);

    private readonly string[] _rawValues;
    private readonly string[] _values;
    private readonly HashSet<string> _sensitiveHeaders;

    public SecretRedactor(IEnumerable<string> secretValues, IEnumerable<string> sensitiveHeaderNames)
    {
        _rawValues = secretValues
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= MinValueLength)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // Longest first, so when one secret contains another the containing value is
        // replaced whole instead of leaving its tail behind around a shorter match. Each
        // value also gets its JSON-escaped form: callers redact serialized JSON payloads,
        // where a secret containing a quote or a non-ASCII character appears escaped and
        // would otherwise slip past a raw-text match.
        _values = _rawValues
            .SelectMany(JsonVariants)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();
        _sensitiveHeaders = new HashSet<string>(AlwaysSensitiveHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var name in sensitiveHeaderNames) _sensitiveHeaders.Add(name);
    }

    private static IEnumerable<string> JsonVariants(string value)
    {
        yield return value;
        var encoded = System.Text.Json.JsonEncodedText.Encode(value).ToString();
        if (!string.Equals(encoded, value, StringComparison.Ordinal)) yield return encoded;
    }

    /// <summary>A copy that additionally treats <paramref name="secretValue"/> as secret.
    /// Used by the pipeline when it injects a runtime-minted token after the render.</summary>
    public SecretRedactor With(string secretValue)
        => new(_rawValues.Append(secretValue), _sensitiveHeaders);

    /// <summary>The union of two redactors. A multi-step run accumulates one redactor across
    /// its steps this way, so the run's serialized report can be scrubbed in one pass.</summary>
    public SecretRedactor Merge(SecretRedactor other)
    {
        if (ReferenceEquals(other, this) || ReferenceEquals(other, None)) return this;
        if (ReferenceEquals(this, None)) return other;
        return new(_rawValues.Concat(other._rawValues), _sensitiveHeaders.Concat(other._sensitiveHeaders));
    }

    /// <summary>Replaces every occurrence of a known secret value with <see cref="Mask"/>.
    /// Null in, null out.</summary>
    public string? Redact(string? text)
    {
        if (text is null || text.Length == 0 || _values.Length == 0) return text;
        foreach (var value in _values)
        {
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Headers safe to echo: sensitive names (auth-contributed plus the well-known
    /// credential carriers) are masked outright, everything else is value-redacted.</summary>
    public IReadOnlyDictionary<string, string> RedactHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            result[name] = _sensitiveHeaders.Contains(name) ? Mask : Redact(value)!;
        }
        return result;
    }
}
