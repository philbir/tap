using Tap.Workspace.Model;
using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Reads the <c>assertions:</c> sequence from request frontmatter into
/// <see cref="AssertSpec"/> values. Split out of <see cref="FileParser"/> because the sugar
/// normalization (§5.5 of <c>docs/workspace-format.md</c>) is the fiddliest part of the
/// format: an entry is always an (extractor, matcher) pair, but three shorthands collapse
/// the common cases —
/// <list type="bullet">
///   <item>a scalar on an argument-less extractor means <c>equals</c> (<c>- status: 200</c>);</item>
///   <item>an argument-taking extractor alone means <c>exists</c> (<c>- header: etag</c>);</item>
///   <item><c>regex:</c> expands to <c>body</c> + <c>matches</c>.</item>
/// </list>
///
/// <para>Both the flat layout (matcher as a sibling of the extractor key) and the nested
/// layout (matcher indented under it) are accepted. The two differ only by indentation and
/// YAML silently reads the indented form as a nested mapping, so rejecting it would turn a
/// stray two spaces into a parse error nobody can see.</para>
/// </summary>
internal static class AssertParser
{
    private const string SugarRegexKey = "regex";

    /// <param name="where">Prefix for every error this raises, naming the enclosing entry when
    /// the assertions aren't at the top of a request file — "Step #2: " for a flow step, say.
    /// Without it "Assertion #1 has no extractor" doesn't say which of six steps to look at.</param>
    public static IReadOnlyList<AssertSpec> Parse(YamlMappingNode fm, string relativePath, string? where = null)
    {
        if (!fm.Children.TryGetValue(new YamlScalarNode("assertions"), out var node)) return [];

        if (node is not YamlSequenceNode seq)
        {
            throw Invalid($"{where}'assertions:' must be a list of assertion entries.", relativePath);
        }

        var list = new List<AssertSpec>(seq.Children.Count);
        for (var i = 0; i < seq.Children.Count; i++)
        {
            if (seq.Children[i] is not YamlMappingNode map)
            {
                throw Invalid($"{where}Assertion #{i + 1} must be a mapping — e.g. '- status: 200'.", relativePath);
            }
            list.Add(ParseOne(map, i + 1, relativePath, where));
        }
        return list;
    }

    private static AssertSpec ParseOne(YamlMappingNode map, int ordinal, string relativePath, string? where)
    {
        string? name = null;
        var skip = false;
        var ignoreCase = false;

        AssertSource? source = null;
        string? sourceArg = null;      // scalar attached to the extractor key
        var isRegexSugar = false;
        YamlMappingNode? nestedMatcher = null;

        AssertOp? op = null;
        string? expected = null;
        IReadOnlyList<string>? expectedList = null;

        foreach (var (keyNode, value) in map.Children)
        {
            if (keyNode is not YamlScalarNode ks || ks.Value is null) continue;
            var key = ks.Value;

            switch (key)
            {
                case "name":
                    name = Scalar(value);
                    continue;
                case "skip":
                    skip = ScalarBool(value, key, ordinal, relativePath, where);
                    continue;
                case "ignoreCase":
                    ignoreCase = ScalarBool(value, key, ordinal, relativePath, where);
                    continue;
            }

            if (key == SugarRegexKey)
            {
                RequireFirstExtractor(source, ordinal, relativePath, where);
                source = AssertSource.Body;
                isRegexSugar = true;
                sourceArg = Scalar(value);
                if (string.IsNullOrEmpty(sourceArg))
                {
                    throw Invalid($"{where}Assertion #{ordinal}: 'regex:' needs a pattern.", relativePath);
                }
                continue;
            }

            if (AssertWire.ParseSource(key) is { } parsedSource)
            {
                RequireFirstExtractor(source, ordinal, relativePath, where);
                source = parsedSource;
                if (value is YamlMappingNode inner) nestedMatcher = inner;
                else sourceArg = Scalar(value);
                continue;
            }

            if (AssertWire.ParseOp(key) is { } parsedOp)
            {
                if (op is not null)
                {
                    throw Invalid(
                        $"{where}Assertion #{ordinal} declares more than one matcher ('{op.Value.ToWire()}' and '{key}'). Split it into two assertions.",
                        relativePath);
                }
                op = parsedOp;
                (expected, expectedList) = ReadExpected(value, key, ordinal, relativePath, where);
                continue;
            }

            throw Invalid(
                $"{where}Assertion #{ordinal}: unknown key '{key}'. Expected an extractor (status, duration, header, body, jsonpath, xpath, regex), " +
                "a matcher (equals, contains, matches, lt, gt, between, in, exists, count, length, type, …), or a modifier (name, skip, ignoreCase).",
                relativePath);
        }

        if (source is null)
        {
            throw Invalid(
                $"{where}Assertion #{ordinal} has no extractor. Each entry needs exactly one of: status, duration, header, body, jsonpath, xpath, regex.",
                relativePath);
        }

        // Indented matcher form: `jsonpath:` followed by an indented `equals:` reads as a
        // nested mapping. Fold it into the same slots the flat form fills.
        if (nestedMatcher is not null)
        {
            foreach (var (keyNode, value) in nestedMatcher.Children)
            {
                if (keyNode is not YamlScalarNode ks || ks.Value is null) continue;
                var key = ks.Value;

                if (AssertWire.ParseOp(key) is not { } parsedOp)
                {
                    throw Invalid(
                        $"{where}Assertion #{ordinal}: '{key}' is not a matcher, so it cannot be nested under '{source.Value.ToWire()}:'.",
                        relativePath);
                }
                if (op is not null)
                {
                    throw Invalid(
                        $"{where}Assertion #{ordinal} declares more than one matcher ('{op.Value.ToWire()}' and '{key}'). Split it into two assertions.",
                        relativePath);
                }
                op = parsedOp;
                (expected, expectedList) = ReadExpected(value, key, ordinal, relativePath, where);
            }
        }

        if (isRegexSugar)
        {
            if (op is not null)
            {
                throw Invalid(
                    $"{where}Assertion #{ordinal}: 'regex:' already implies the 'matches' matcher — drop the '{op.Value.ToWire()}:' key or use 'body:' instead.",
                    relativePath);
            }
            op = AssertOp.Matches;
            expected = sourceArg;
            sourceArg = null;
        }

        string? selector = null;
        if (source.Value.TakesSelector())
        {
            selector = sourceArg;
            if (string.IsNullOrWhiteSpace(selector))
            {
                var what = source.Value == AssertSource.Header ? "a header name" : "an expression";
                throw Invalid($"{where}Assertion #{ordinal}: '{source.Value.ToWire()}:' needs {what}.", relativePath);
            }

            // Sugar: a selector with no matcher is an existence check.
            op ??= AssertOp.Exists;
        }
        else if (op is null)
        {
            // Sugar: a scalar on an argument-less extractor is an equality check.
            if (string.IsNullOrEmpty(sourceArg))
            {
                throw Invalid(
                    $"{where}Assertion #{ordinal}: '{source.Value.ToWire()}:' needs either a value ('{source.Value.ToWire()}: 200') or a matcher ('lt: 800').",
                    relativePath);
            }
            op = AssertOp.Equals;
            expected = sourceArg;
        }
        else if (!string.IsNullOrEmpty(sourceArg))
        {
            throw Invalid(
                $"{where}Assertion #{ordinal} sets a value on '{source.Value.ToWire()}:' and also a '{op.Value.ToWire()}:' matcher. Keep one of the two.",
                relativePath);
        }

        // `exists` with no value is the true case — the sugar form `- header: etag` lands here.
        if (op == AssertOp.Exists) expected ??= "true";

        var spec = new AssertSpec
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            Source = source.Value,
            Selector = selector,
            Op = op.Value,
            Expected = expected,
            ExpectedList = expectedList,
            IgnoreCase = ignoreCase,
            Skip = skip,
        };

        if (AssertSpec.Validate(spec) is { } error)
        {
            throw Invalid($"{where}Assertion #{ordinal}: {error}", relativePath);
        }

        return spec;
    }

    private static void RequireFirstExtractor(AssertSource? existing, int ordinal, string relativePath, string? where)
    {
        if (existing is null) return;
        throw Invalid(
            $"{where}Assertion #{ordinal} declares more than one extractor. Each entry reads exactly one thing from the response.",
            relativePath);
    }

    private static (string? Expected, IReadOnlyList<string>? ExpectedList) ReadExpected(
        YamlNode value, string key, int ordinal, string relativePath, string? where)
    {
        switch (value)
        {
            case YamlScalarNode scalar:
                return (scalar.Value, null);
            case YamlSequenceNode seq:
                {
                    var items = new List<string>(seq.Children.Count);
                    foreach (var child in seq.Children)
                    {
                        if (child is not YamlScalarNode s || s.Value is null)
                        {
                            throw Invalid($"{where}Assertion #{ordinal}: '{key}:' list entries must be plain values.", relativePath);
                        }
                        items.Add(s.Value);
                    }
                    return (null, items);
                }
            default:
                throw Invalid($"{where}Assertion #{ordinal}: '{key}:' must be a value or a list of values.", relativePath);
        }
    }

    private static string? Scalar(YamlNode node) => node is YamlScalarNode s ? s.Value : null;

    private static bool ScalarBool(YamlNode node, string key, int ordinal, string relativePath, string? where)
    {
        var raw = Scalar(node);
        if (bool.TryParse(raw, out var parsed)) return parsed;
        throw Invalid($"{where}Assertion #{ordinal}: '{key}:' takes true or false, not '{raw}'.", relativePath);
    }

    private static WorkspaceParseException Invalid(string message, string relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_ASSERT_INVALID, message, relativePath));
}
