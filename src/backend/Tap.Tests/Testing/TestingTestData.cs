using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Testing;

/// <summary>Builders shared by the flow / test-set parser, emitter, and runner tests.</summary>
internal static class TestingTestData
{
    public const string FlowPath = "tests/checkout.flow.tap";
    public const string TestSetPath = "tests/orders.test.tap";

    public static string FlowSource(string frontmatterFragment, string body = "")
        => "---\nkind: flow\nname: Checkout\n"
           + frontmatterFragment.TrimEnd('\n') + "\n"
           + "---\n" + (body.Length == 0 ? "" : "\n" + body + "\n");

    public static string TestSetSource(string frontmatterFragment, string body = "")
        => "---\nkind: test\nname: Orders\n"
           + frontmatterFragment.TrimEnd('\n') + "\n"
           + "---\n" + (body.Length == 0 ? "" : "\n" + body + "\n");

    public static FlowFile ParseFlow(string frontmatterFragment)
        => (FlowFile)FileParser.Parse(FlowPath, FlowSource(frontmatterFragment));

    public static TestSetFile ParseTestSet(string frontmatterFragment)
        => (TestSetFile)FileParser.Parse(TestSetPath, TestSetSource(frontmatterFragment));

    /// <summary>Round-trips a parsed flow through the real emitter and returns the file source.</summary>
    public static string Emit(FlowFile flow)
        => FlowSpecEmitter.ToFileSource(new FlowSpecDto
        {
            Path = flow.RelativePath,
            Id = flow.Id,
            Name = flow.Name ?? "Checkout",
            Vars = flow.Vars.ToDictionary(v => v.Key, v => v.Value.Default ?? string.Empty),
            Secrets = flow.Vars.Where(v => v.Value.Secret).Select(v => v.Key).ToArray(),
            Tags = flow.Tags,
            Body = flow.Body,
            Steps = TestingSpecMapper.ToDto(flow.Steps),
        });

    public static string Emit(TestSetFile set)
        => TestSetSpecEmitter.ToFileSource(new TestSetSpecDto
        {
            Path = set.RelativePath,
            Id = set.Id,
            Name = set.Name ?? "Orders",
            Vars = set.Vars.ToDictionary(v => v.Key, v => v.Value.Default ?? string.Empty),
            Secrets = set.Vars.Where(v => v.Value.Secret).Select(v => v.Key).ToArray(),
            OnFailure = set.OnFailure.ToWire(),
            Tags = set.Tags,
            Body = set.Body,
            Tests = TestingSpecMapper.ToDto(set.Tests),
        });

    public static WorkspaceError FlowParseError(string frontmatterFragment)
        => Assert.Throws<WorkspaceParseException>(() => ParseFlow(frontmatterFragment)).Error;

    public static WorkspaceError TestSetParseError(string frontmatterFragment)
        => Assert.Throws<WorkspaceParseException>(() => ParseTestSet(frontmatterFragment)).Error;

    /// <summary>Field-wise comparison — <see cref="FlowStep"/> is a record, so generated equality
    /// compares its collections by reference and two parses of one file would never be equal.</summary>
    public static void AssertSame(FlowStep expected, FlowStep actual)
    {
        Assert.Equal(expected.Request.SourceText, actual.Request.SourceText);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Vars, actual.Vars);
        Assert.Equal(expected.ContinueOnFailure, actual.ContinueOnFailure);
        Assert.Equal(expected.Skip, actual.Skip);

        Assert.Equal(expected.Extract.Count, actual.Extract.Count);
        for (var i = 0; i < expected.Extract.Count; i++) AssertSame(expected.Extract[i], actual.Extract[i]);

        Assert.Equal(expected.Assertions.Count, actual.Assertions.Count);
        for (var i = 0; i < expected.Assertions.Count; i++)
            Asserts.AssertTestData.AssertSame(expected.Assertions[i], actual.Assertions[i]);
    }

    public static void AssertSame(ExtractSpec expected, ExtractSpec actual)
    {
        Assert.Equal(expected.Var, actual.Var);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Selector, actual.Selector);
        Assert.Equal(expected.Group, actual.Group);
        Assert.Equal(expected.Default, actual.Default);
        Assert.Equal(expected.Required, actual.Required);
    }

    public static void AssertSame(TestEntry expected, TestEntry actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Request?.SourceText, actual.Request?.SourceText);
        Assert.Equal(expected.Flow?.SourceText, actual.Flow?.SourceText);
        Assert.Equal(expected.Vars, actual.Vars);
        Assert.Equal(expected.Skip, actual.Skip);

        Assert.Equal(expected.Assertions.Count, actual.Assertions.Count);
        for (var i = 0; i < expected.Assertions.Count; i++)
            Asserts.AssertTestData.AssertSame(expected.Assertions[i], actual.Assertions[i]);
    }
}
