using System.Text;
using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Tiny helper layer over <see cref="YamlMappingNode"/> shared by every spec emitter.
/// Each emitter builds a mapping in canonical order, then calls <see cref="ToFrontmatter"/>
/// to wrap it in <c>---</c> fences (plus an optional Markdown body).
///
/// Quoting policy: values that would otherwise be ambiguous YAML (start with <c>$</c>,
/// <c>{</c>, <c>*</c>, <c>&</c>, or contain a colon) get single-quoted to preserve them
/// verbatim — Tap relies on <c>${{secret}}</c> tokens and <c>{{var}}</c> templates round-
/// tripping bit-for-bit.
/// </summary>
internal static class SpecYaml
{
    public static void Set(this YamlMappingNode map, string key, string value)
        => map.Add(key, new YamlScalarNode(value) { Style = QuoteStyleFor(value) });

    public static void SetIfNotEmpty(this YamlMappingNode map, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        map.Add(key, new YamlScalarNode(value) { Style = QuoteStyleFor(value) });
    }

    public static void SetIfTrue(this YamlMappingNode map, string key, bool? value)
    {
        if (value != true) return;
        map.Add(key, new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Any });
    }

    public static void SetStringList(this YamlMappingNode map, string key, IReadOnlyList<string>? values, bool flow = true)
    {
        if (values is null || values.Count == 0) return;
        var seq = new YamlSequenceNode
        {
            Style = flow ? YamlDotNet.Core.Events.SequenceStyle.Flow : YamlDotNet.Core.Events.SequenceStyle.Block,
        };
        foreach (var v in values) seq.Add(new YamlScalarNode(v) { Style = QuoteStyleFor(v) });
        map.Add(key, seq);
    }

    public static void SetStringMap(this YamlMappingNode map, string key, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return;
        var inner = new YamlMappingNode();
        foreach (var (k, v) in values)
            inner.Add(k, new YamlScalarNode(v) { Style = QuoteStyleFor(v) });
        map.Add(key, inner);
    }

    public static void SetTransport(this YamlMappingNode map, RequestTransportSettingsDto? transport)
    {
        if (transport is null || transport.IgnoreTlsErrors is null && transport.TimeoutMs is null) return;
        var inner = new YamlMappingNode();
        if (transport.IgnoreTlsErrors is not null)
            inner.Add("ignoreTlsErrors", new YamlScalarNode(transport.IgnoreTlsErrors.Value ? "true" : "false"));
        if (transport.TimeoutMs is not null)
            inner.Add("timeoutMs", new YamlScalarNode(transport.TimeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        map.Add("transport", inner);
    }

    /// <summary>
    /// Variables: emit each as either a plain scalar (<c>name: value</c>) or the structured
    /// form (<c>name: { default: value, secret: true }</c>) when the variable is marked as
    /// a secret. <paramref name="secrets"/> is the set of variable names that need the
    /// structured form.
    /// </summary>
    public static void SetVarMap(this YamlMappingNode map, string key, IReadOnlyDictionary<string, string>? values, IReadOnlyCollection<string>? secrets = null)
    {
        if (values is null || values.Count == 0) return;
        var inner = new YamlMappingNode();
        var secretSet = secrets is null
            ? null
            : new HashSet<string>(secrets, StringComparer.Ordinal);
        foreach (var (k, v) in values)
        {
            if (secretSet is not null && secretSet.Contains(k))
            {
                var obj = new YamlMappingNode();
                obj.Add("default", new YamlScalarNode(v) { Style = QuoteStyleFor(v) });
                obj.Add("secret", new YamlScalarNode("true"));
                inner.Add(k, obj);
            }
            else
            {
                inner.Add(k, new YamlScalarNode(v) { Style = QuoteStyleFor(v) });
            }
        }
        map.Add(key, inner);
    }

    public static void SetMappingList(this YamlMappingNode map, string key, IReadOnlyList<YamlMappingNode>? values)
    {
        if (values is null || values.Count == 0) return;
        var seq = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        foreach (var v in values) seq.Add(v);
        map.Add(key, seq);
    }

    /// <summary>Wrap a YAML mapping in <c>---</c> frontmatter fences with an optional body.</summary>
    public static string ToFrontmatter(YamlMappingNode mapping, string? body = null, string? httpBlock = null)
    {
        var yamlText = SerializeMapping(mapping).TrimEnd();
        var sb = new StringBuilder();
        sb.Append("---\n").Append(yamlText).Append("\n---\n");

        if (!string.IsNullOrWhiteSpace(httpBlock))
        {
            sb.Append('\n').Append("```http\n").Append(httpBlock.TrimEnd('\n')).Append("\n```\n");
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.Append('\n').Append(body.TrimStart('\n'));
            if (!body.EndsWith('\n')) sb.Append('\n');
        }

        return sb.ToString();
    }

    private static YamlDotNet.Core.ScalarStyle QuoteStyleFor(string value)
    {
        if (value.Length == 0) return YamlDotNet.Core.ScalarStyle.Any;
        // Multi-line values (PEM keys, JWT payload JSON) go out as literal block scalars so
        // the line breaks survive verbatim and stay readable in a diff. Left to itself the
        // emitter picks folded style, which turns single newlines into spaces — fatal for a
        // PEM header. CRLF can't be expressed in any block scalar (it normalises to LF), so
        // those fall back to the double-quoted form, where \r survives as an escape.
        if (value.Contains('\n'))
        {
            return value.Contains('\r')
                ? YamlDotNet.Core.ScalarStyle.DoubleQuoted
                : YamlDotNet.Core.ScalarStyle.Literal;
        }
        var first = value[0];
        if (first == '$' || first == '{' || first == '*' || first == '&' || first == '!' || first == '@')
            return YamlDotNet.Core.ScalarStyle.SingleQuoted;
        // Quote anything containing a YAML-significant char to keep the round-trip stable.
        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':'))
            return YamlDotNet.Core.ScalarStyle.SingleQuoted;
        return YamlDotNet.Core.ScalarStyle.Any;
    }

    private static string SerializeMapping(YamlMappingNode mapping)
    {
        // YamlStream prepends a "---" doc marker and appends a "...". We strip both so the
        // caller can lay out its own fence — but only where they actually are (first line /
        // last line). Replacing them everywhere would chew through the dashes inside a value:
        // "-----BEGIN PRIVATE KEY-----\n" contains "---\n".
        var doc = new YamlDocument(mapping);
        var stream = new YamlStream(doc);
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        stream.Save(writer, assignAnchors: false);

        var text = sb.ToString();
        if (text.StartsWith("---\r\n", StringComparison.Ordinal)) text = text[5..];
        else if (text.StartsWith("---\n", StringComparison.Ordinal)) text = text[4..];

        text = text.TrimEnd();
        if (text.EndsWith("\n...", StringComparison.Ordinal)) text = text[..^4];
        else if (text.EndsWith("\r\n...", StringComparison.Ordinal)) text = text[..^5];
        return text.Trim();
    }
}
