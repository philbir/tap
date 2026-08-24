using Tap.Studio.Contracts;
using Tap.Workspace.Model;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits canonical YAML for a <c>*.flow.tap</c> file (§10 of <c>docs/workspace-format.md</c>).
///
/// <para>Everything routes through <see cref="TestingSpecMapper"/> on the way in, so a client
/// can't save a step the parser would refuse to load — the PUT fails with
/// <c>E_FLOW_INVALID</c> instead of writing a file that no longer opens.</para>
///
/// <para>Key order inside a step is fixed (name, request, vars, extract, assertions,
/// continueOnFailure, skip) so re-saving an unchanged flow is a no-op in the diff.</para>
/// </summary>
public static class FlowSpecEmitter
{
    public static string ToFileSource(FlowSpecDto spec)
    {
        var steps = TestingSpecMapper.ToModel(spec.Steps, spec.Path);

        var fm = new YamlMappingNode();
        fm.Set("kind", "flow");
        fm.Set("id", SpecIds.Ensure(spec.Id));
        fm.Set("name", spec.Name);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        fm.SetFlowSteps(steps);
        fm.SetStringList("tags", spec.Tags);

        return SpecYaml.ToFrontmatter(fm, body: spec.Body);
    }
}

/// <summary>
/// Emits canonical YAML for a <c>*.test.tap</c> file (§11 of <c>docs/workspace-format.md</c>).
/// <c>onFailure</c> is written only when it isn't the default, keeping existing files
/// diff-clean — the same rule <see cref="RequestSpecEmitter"/> applies to <c>protocol</c>.
/// </summary>
public static class TestSetSpecEmitter
{
    public static string ToFileSource(TestSetSpecDto spec)
    {
        var tests = TestingSpecMapper.ToModel(spec.Tests, spec.Path);
        var onFailure = TestingSpecMapper.ParseOnFailure(spec.OnFailure, spec.Path);

        var fm = new YamlMappingNode();
        fm.Set("kind", "test");
        fm.Set("id", SpecIds.Ensure(spec.Id));
        fm.Set("name", spec.Name);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        if (onFailure != TestFailureMode.Continue) fm.Set("onFailure", onFailure.ToWire());
        fm.SetTests(tests);
        fm.SetStringList("tags", spec.Tags);

        return SpecYaml.ToFrontmatter(fm, body: spec.Body);
    }
}

/// <summary>YAML emit helpers for the two testing kinds. Kept beside their emitters rather than
/// in <see cref="SpecYaml"/> because nothing else writes these shapes.</summary>
internal static class TestingYaml
{
    public static void SetFlowSteps(this YamlMappingNode map, IReadOnlyList<FlowStep> steps)
    {
        if (steps.Count == 0) return;

        var seq = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        foreach (var step in steps)
        {
            var entry = new YamlMappingNode();
            entry.SetIfNotEmpty("name", step.Name);
            entry.SetRef("request", step.Request);
            entry.SetStringMap("vars", step.Vars);
            entry.SetExtractions(step.Extract);
            entry.SetAssertions(step.Assertions);
            entry.SetIfTrue("continueOnFailure", step.ContinueOnFailure);
            entry.SetIfTrue("skip", step.Skip);
            seq.Add(entry);
        }
        map.Add("steps", seq);
    }

    public static void SetTests(this YamlMappingNode map, IReadOnlyList<TestEntry> tests)
    {
        if (tests.Count == 0) return;

        var seq = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        foreach (var test in tests)
        {
            var entry = new YamlMappingNode();
            entry.SetIfNotEmpty("name", test.Name);
            if (test.Flow is { } flow) entry.SetRef("flow", flow);
            else if (test.Request is { } request) entry.SetRef("request", request);
            entry.SetStringMap("vars", test.Vars);
            entry.SetAssertions(test.Assertions);
            entry.SetIfTrue("skip", test.Skip);
            seq.Add(entry);
        }
        map.Add("tests", seq);
    }

    private static void SetExtractions(this YamlMappingNode map, IReadOnlyList<ExtractSpec> extractions)
    {
        if (extractions.Count == 0) return;

        var seq = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        foreach (var extract in extractions)
        {
            var entry = new YamlMappingNode();
            entry.Set("var", extract.Var);

            // The source key carries its argument; the argument-less sources get an empty value,
            // which reads as `body:` — the same bare-marker shape the parser accepts.
            var sourceKey = extract.Source.ToWire();
            if (extract.Source.TakesSelector()) entry.SetPathLike(sourceKey, extract.Selector);
            else entry.Add(sourceKey, new YamlScalarNode(string.Empty));

            if (extract.Group is { } group)
                entry.Add("group", new YamlScalarNode(group.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (extract.Default is { } fallback) entry.Set("default", fallback);
            if (!extract.Required) entry.Add("required", new YamlScalarNode("false"));

            seq.Add(entry);
        }
        map.Add("extract", seq);
    }

    /// <summary>A cross-file ref goes out exactly as the author wrote it — that is what
    /// <see cref="WorkspaceRef.SourceText"/> is for, and rewriting a relative path into a
    /// normalized one would churn every diff for no gain.</summary>
    private static void SetRef(this YamlMappingNode map, string key, WorkspaceRef reference)
        => map.Set(key, reference.SourceText);
}
