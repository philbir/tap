using System.Text;
using Tap.Studio.Contracts;
using Tap.Workspace.Parsing;
using Tap.Workspace.Model;
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
    /// Emits a <c>history:</c> block, or nothing when the scope declares nothing. Collapses to
    /// the <c>history: true</c> shorthand when <c>enabled</c> is the only thing set, which is the
    /// overwhelmingly common case and doesn't deserve four lines of frontmatter.
    /// </summary>
    public static void SetHistory(this YamlMappingNode map, HistoryOptionsDto? history)
    {
        if (history is null) return;
        var (enabled, maxEntries, encrypt, maxBody, orphanDays) =
            (history.Enabled, history.MaxEntries, history.Encrypt, history.MaxBodyBytes, history.OrphanRetentionDays);
        if (enabled is null && maxEntries is null && encrypt is null && maxBody is null && orphanDays is null) return;

        if (enabled is not null && maxEntries is null && encrypt is null && maxBody is null && orphanDays is null)
        {
            map.Add("history", new YamlScalarNode(enabled.Value ? "true" : "false"));
            return;
        }

        var inner = new YamlMappingNode();
        if (enabled is not null) inner.Add("enabled", new YamlScalarNode(enabled.Value ? "true" : "false"));
        if (maxEntries is not null) inner.Add("maxEntries", Number(maxEntries.Value));
        if (encrypt is not null) inner.Add("encrypt", new YamlScalarNode(encrypt.Value ? "true" : "false"));
        // Written back in the unit the user is most likely to have typed — ByteSize.Format
        // renders exact multiples as '256kb' rather than a six-digit byte count.
        if (maxBody is not null) inner.Add("maxBodyBytes", new YamlScalarNode(ByteSize.Format(maxBody.Value)));
        if (orphanDays is not null) inner.Add("orphanRetentionDays", Number(orphanDays.Value));
        map.Add("history", inner);

        static YamlScalarNode Number(long value)
            => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            inner.Add(k, VarNode(v, secretSet is not null && secretSet.Contains(k)));
        map.Add(key, inner);
    }

    /// <summary>
    /// One <c>vars:</c> entry's value node: a bare scalar, or the
    /// <c>{ default: …, secret: true }</c> mapping a secret-marked variable takes. Shared with
    /// <see cref="VarDeclarationWriter"/>, which declares a single entry into a file it is not
    /// otherwise rewriting — both must emit the same shape or a later full save would show up
    /// as a spurious diff.
    /// </summary>
    public static YamlNode VarNode(string value, bool secret)
    {
        if (!secret) return new YamlScalarNode(value) { Style = QuoteStyleFor(value) };
        var obj = new YamlMappingNode();
        obj.Add("default", new YamlScalarNode(value) { Style = QuoteStyleFor(value) });
        obj.Add("secret", new YamlScalarNode("true"));
        return obj;
    }

    /// <summary>
    /// Emit the <c>assertions:</c> sequence, preferring the shorthand forms from §5.5 of
    /// <c>docs/workspace-format.md</c> whenever they express the assertion exactly:
    /// <c>- status: 200</c> for an equality on an argument-less extractor, <c>- header: etag</c>
    /// for a plain existence check, and <c>- regex: …</c> for a body match. Everything else
    /// goes out as the explicit extractor + matcher pair. Key order within an entry is fixed
    /// (name, extractor, matcher, ignoreCase, skip) so re-saving an unchanged request is a
    /// no-op in the diff.
    /// </summary>
    public static void SetAssertions(this YamlMappingNode map, IReadOnlyList<AssertSpec>? assertions)
    {
        if (assertions is null || assertions.Count == 0) return;

        var seq = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        foreach (var assertion in assertions) seq.Add(BuildAssertion(assertion));
        map.Add("assertions", seq);
    }

    private static YamlMappingNode BuildAssertion(AssertSpec assertion)
    {
        var entry = new YamlMappingNode();
        entry.SetIfNotEmpty("name", assertion.Name);

        var sourceKey = assertion.Source.ToWire();
        var takesSelector = assertion.Source.TakesSelector();

        // The extractor key's value slot holds its argument. `header`/`jsonpath`/`xpath` need
        // that slot for a selector, so their matcher goes alongside as a sibling; `status`/
        // `duration`/`body` have it free, so the matcher (or, in the sugar case, the expected
        // value itself) goes straight into it.
        //
        // `regex:` — body + matches, the most common body check by far.
        if (assertion is { Source: AssertSource.Body, Op: AssertOp.Matches })
        {
            entry.Set("regex", assertion.Expected ?? string.Empty);
        }
        // `status: 200` — a scalar on an argument-less extractor is an equality check.
        else if (!takesSelector && assertion.Op == AssertOp.Equals)
        {
            entry.Set(sourceKey, assertion.Expected ?? string.Empty);
        }
        // `header: etag` — a selector alone is an existence check.
        else if (takesSelector && assertion.Op == AssertOp.Exists
                 && !string.Equals(assertion.Expected, "false", StringComparison.OrdinalIgnoreCase))
        {
            entry.SetSelector(sourceKey, assertion.Selector);
        }
        else if (takesSelector)
        {
            entry.SetSelector(sourceKey, assertion.Selector);
            entry.SetMatcher(assertion);
        }
        else
        {
            var inner = new YamlMappingNode();
            inner.SetMatcher(assertion);
            entry.Add(sourceKey, inner);
        }

        entry.SetIfTrue("ignoreCase", assertion.IgnoreCase);
        entry.SetIfTrue("skip", assertion.Skip);
        return entry;
    }

    private static void SetMatcher(this YamlMappingNode map, AssertSpec assertion)
    {
        var key = assertion.Op.ToWire();
        if (assertion.ExpectedList is { Count: > 0 } list) map.SetStringList(key, list);
        else map.Set(key, assertion.Expected ?? string.Empty);
    }

    private static void SetSelector(this YamlMappingNode map, string key, string? selector)
        => map.SetPathLike(key, selector);

    /// <summary>
    /// Emit a path expression. Deliberately bypasses <see cref="QuoteStyleFor"/>: every
    /// JSONPath starts with <c>$</c>, which that policy single-quotes to protect
    /// <c>${{secret}}</c> tokens — correct there, pure noise on <c>$.order.id</c>. YAML has
    /// no special meaning for a leading <c>$</c>, and the emitter still falls back to a
    /// quoted style on its own for anything plain style can't carry.
    /// </summary>
    public static void SetPathLike(this YamlMappingNode map, string key, string? selector)
        => map.Add(key, new YamlScalarNode(selector ?? string.Empty) { Style = YamlDotNet.Core.ScalarStyle.Any });

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

    /// <summary>
    /// Re-fences <paramref name="mapping"/> over a body that is passed through <b>byte for
    /// byte</b> — no blank line inserted, none removed.
    ///
    /// <para><see cref="ToFrontmatter"/> lays a file out canonically, which is right when the
    /// whole file is being emitted from a spec. It is wrong for a one-key patch: a file whose
    /// body already abuts the closing fence would gain a blank line, and that line is a diff
    /// hunk in a file the user never opened.</para>
    /// </summary>
    public static string ToFrontmatterOver(YamlMappingNode mapping, string body)
        => "---\n" + SerializeMapping(mapping).TrimEnd() + "\n---\n" + body;

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
