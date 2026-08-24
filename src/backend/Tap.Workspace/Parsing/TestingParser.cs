using Tap.Workspace.Model;
using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Reads the <c>steps:</c> sequence of a <c>*.flow.tap</c> file (§10 of
/// <c>docs/workspace-format.md</c>) into <see cref="FlowStep"/> values.
///
/// <para>Unlike <see cref="AssertParser"/> there is no sugar here: a step is a mapping with
/// named keys, and every diagnostic names the step by position because a flow with six steps
/// that says only "unknown key" is a file you have to bisect by hand.</para>
/// </summary>
internal static class FlowParser
{
    public static IReadOnlyList<FlowStep> ParseSteps(YamlMappingNode fm, string relativePath)
    {
        // A flow with no steps yet is a file someone just created, not a broken one — the
        // editor writes the frontmatter before the first step exists. Malformed *entries*
        // are still rejected; that's where a typo actually lands.
        if (!fm.Children.TryGetValue(new YamlScalarNode("steps"), out var node)) return [];

        if (node is not YamlSequenceNode seq)
        {
            throw Invalid("'steps:' must be a list of step entries.", relativePath);
        }

        var list = new List<FlowStep>(seq.Children.Count);
        for (var i = 0; i < seq.Children.Count; i++)
        {
            if (seq.Children[i] is not YamlMappingNode map)
            {
                throw Invalid($"Step #{i + 1} must be a mapping with a 'request:' key.", relativePath);
            }
            list.Add(ParseStep(map, i + 1, relativePath));
        }
        return list;
    }

    private static FlowStep ParseStep(YamlMappingNode map, int ordinal, string relativePath)
    {
        var where = $"Step #{ordinal}: ";

        foreach (var (keyNode, _) in map.Children)
        {
            if (keyNode is not YamlScalarNode ks || ks.Value is null) continue;
            if (ks.Value is "request" or "name" or "vars" or "extract" or "assertions"
                or "continueOnFailure" or "skip") continue;

            throw Invalid(
                $"{where}unknown key '{ks.Value}'. A step takes: request, name, vars, extract, " +
                "assertions, continueOnFailure, skip.",
                relativePath);
        }

        var request = map.Ref("request")
            ?? throw Invalid(
                $"{where}'request:' is required — a step runs one request, referenced by path or 'id:<uuid>'.",
                relativePath);

        return new FlowStep
        {
            Request = request,
            Name = Trimmed(map.String("name")),
            Vars = map.StringMap("vars"),
            Extract = ParseExtractions(map, ordinal, relativePath),
            Assertions = AssertParser.Parse(map, relativePath, where),
            ContinueOnFailure = map.Bool("continueOnFailure"),
            Skip = map.Bool("skip"),
        };
    }

    /// <summary>
    /// Reads a step's <c>extract:</c> list. Each entry pairs a <c>var</c> with exactly one
    /// source key, which is why the source is discovered by scanning rather than looked up:
    /// naming two sources has to be an error, and the message should say which two.
    /// </summary>
    private static IReadOnlyList<ExtractSpec> ParseExtractions(
        YamlMappingNode map, int stepOrdinal, string relativePath)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode("extract"), out var node)) return [];

        var where = $"Step #{stepOrdinal}: ";
        if (node is not YamlSequenceNode seq)
        {
            throw Invalid($"{where}'extract:' must be a list of extraction entries.", relativePath);
        }

        var list = new List<ExtractSpec>(seq.Children.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < seq.Children.Count; i++)
        {
            if (seq.Children[i] is not YamlMappingNode entry)
            {
                throw Invalid(
                    $"{where}extraction #{i + 1} must be a mapping — e.g. '- var: orderId' with 'jsonpath: $.id'.",
                    relativePath);
            }

            var spec = ParseExtraction(entry, i + 1, where, relativePath);
            if (!seen.Add(spec.Var))
            {
                throw Invalid(
                    $"{where}extraction #{i + 1} binds '{spec.Var}', which an earlier extraction in the " +
                    "same step already binds. Two bindings of one name in one step have no defined order.",
                    relativePath);
            }
            list.Add(spec);
        }
        return list;
    }

    private static ExtractSpec ParseExtraction(
        YamlMappingNode entry, int ordinal, string where, string relativePath)
    {
        string? var = null;
        ExtractSource? source = null;
        string? selector = null;
        string? sourceKey = null;
        int? group = null;
        string? fallback = null;
        var required = true;

        foreach (var (keyNode, value) in entry.Children)
        {
            if (keyNode is not YamlScalarNode ks || ks.Value is null) continue;
            var key = ks.Value;

            switch (key)
            {
                case "var":
                    var = Trimmed(Scalar(value));
                    continue;
                case "group":
                    group = ScalarInt(value, key, ordinal, where, relativePath);
                    continue;
                case "default":
                    fallback = Scalar(value);
                    continue;
                case "required":
                    required = ScalarBool(value, key, ordinal, where, relativePath);
                    continue;
            }

            if (ExtractWire.ParseSource(key) is { } parsed)
            {
                if (source is not null)
                {
                    throw Invalid(
                        $"{where}extraction #{ordinal} names two sources ('{sourceKey}' and '{key}'). " +
                        "One extraction binds one value — split it into two entries.",
                        relativePath);
                }
                source = parsed;
                sourceKey = key;
                selector = Scalar(value);
                continue;
            }

            throw Invalid(
                $"{where}extraction #{ordinal}: unknown key '{key}'. Expected 'var', a source " +
                "(status, duration, header, body, jsonpath, xpath, regex), or a modifier (group, default, required).",
                relativePath);
        }

        if (var is null)
        {
            throw Invalid(
                $"{where}extraction #{ordinal} has no 'var:' — an extraction has to say which variable it binds.",
                relativePath);
        }

        if (source is null)
        {
            throw Invalid(
                $"{where}extraction #{ordinal} has no source. Add one of: status, duration, header, body, " +
                "jsonpath, xpath, regex.",
                relativePath);
        }

        var spec = new ExtractSpec
        {
            Var = var,
            Source = source.Value,
            Selector = source.Value.TakesSelector() ? Trimmed(selector) : null,
            Group = group,
            Default = fallback,
            Required = required,
        };

        if (ExtractSpec.Validate(spec) is { } error)
        {
            throw Invalid($"{where}extraction #{ordinal}: {error}", relativePath);
        }

        return spec;
    }

    private static string? Scalar(YamlNode node) => node is YamlScalarNode s ? s.Value : null;

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ScalarBool(YamlNode node, string key, int ordinal, string where, string relativePath)
    {
        var raw = Scalar(node);
        if (bool.TryParse(raw, out var parsed)) return parsed;
        throw Invalid($"{where}extraction #{ordinal}: '{key}:' takes true or false, not '{raw}'.", relativePath);
    }

    private static int ScalarInt(YamlNode node, string key, int ordinal, string where, string relativePath)
    {
        var raw = Scalar(node);
        if (int.TryParse(raw, out var parsed)) return parsed;
        throw Invalid($"{where}extraction #{ordinal}: '{key}:' takes a number, not '{raw}'.", relativePath);
    }

    internal static WorkspaceParseException Invalid(string message, string relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_FLOW_INVALID, message, relativePath));
}

/// <summary>
/// Reads the <c>tests:</c> sequence of a <c>*.test.tap</c> file (§11 of
/// <c>docs/workspace-format.md</c>) into <see cref="TestEntry"/> values.
/// </summary>
internal static class TestSetParser
{
    public static IReadOnlyList<TestEntry> ParseTests(YamlMappingNode fm, string relativePath)
    {
        // Same as a flow's steps: an empty set is an unfinished one, not an invalid one.
        if (!fm.Children.TryGetValue(new YamlScalarNode("tests"), out var node)) return [];

        if (node is not YamlSequenceNode seq)
        {
            throw Invalid("'tests:' must be a list of test entries.", relativePath);
        }

        var list = new List<TestEntry>(seq.Children.Count);
        for (var i = 0; i < seq.Children.Count; i++)
        {
            if (seq.Children[i] is not YamlMappingNode map)
            {
                throw Invalid($"Test #{i + 1} must be a mapping with a 'request:' or 'flow:' key.", relativePath);
            }
            list.Add(ParseTest(map, i + 1, relativePath));
        }
        return list;
    }

    public static TestFailureMode ParseOnFailure(YamlMappingNode fm, string relativePath)
    {
        var raw = fm.String("onFailure");
        if (raw is null) return TestFailureMode.Continue;
        return TestFailureModeWire.Parse(raw)
            ?? throw Invalid($"'onFailure: {raw}' is not recognized. Expected 'continue' or 'stop'.", relativePath);
    }

    private static TestEntry ParseTest(YamlMappingNode map, int ordinal, string relativePath)
    {
        var where = $"Test #{ordinal}: ";

        foreach (var (keyNode, _) in map.Children)
        {
            if (keyNode is not YamlScalarNode ks || ks.Value is null) continue;
            if (ks.Value is "request" or "flow" or "name" or "vars" or "assertions" or "skip") continue;

            throw Invalid(
                $"{where}unknown key '{ks.Value}'. A test takes: request or flow, name, vars, assertions, skip.",
                relativePath);
        }

        var request = map.Ref("request");
        var flow = map.Ref("flow");

        if (request is not null && flow is not null)
        {
            throw Invalid(
                $"{where}names both 'request:' and 'flow:'. A test runs one or the other — split it into two tests.",
                relativePath);
        }

        if (request is null && flow is null)
        {
            throw Invalid(
                $"{where}names neither 'request:' nor 'flow:'. A test has to say what it runs.",
                relativePath);
        }

        return new TestEntry
        {
            Name = Trimmed(map.String("name")),
            Request = request,
            Flow = flow,
            Vars = map.StringMap("vars"),
            Assertions = AssertParser.Parse(map, relativePath, where),
            Skip = map.Bool("skip"),
        };
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkspaceParseException Invalid(string message, string relativePath)
        => new(new WorkspaceError(WorkspaceErrorCode.E_TEST_INVALID, message, relativePath));
}
