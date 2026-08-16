using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Asserts;

/// <summary>Builders shared by the assertion parser, round-trip, and evaluator tests.</summary>
internal static class AssertTestData
{
    public const string RequestPath = "collections/demo/sample.req.tap";

    /// <summary>Wraps a frontmatter fragment in an otherwise-minimal request file.</summary>
    public static string RequestSource(string frontmatterFragment)
        => "---\nkind: request\nname: Sample\n"
           + frontmatterFragment.TrimEnd('\n') + "\n"
           + "---\n\n```http\nGET https://example.test/\n```\n";

    /// <summary>Parses a frontmatter fragment and returns just the assertions.</summary>
    public static IReadOnlyList<AssertSpec> ParseAssertions(string frontmatterFragment)
        => ParseRequest(frontmatterFragment).Assertions;

    public static RequestFile ParseRequest(string frontmatterFragment)
        => (RequestFile)FileParser.Parse(RequestPath, RequestSource(frontmatterFragment));

    /// <summary>Runs a set of assertions through the real emitter and returns the file source.</summary>
    public static string Emit(IReadOnlyList<AssertSpec> assertions)
        => RequestSpecEmitter.ToFileSource(new RequestSpecDto
        {
            Path = RequestPath,
            Name = "Sample",
            Method = "GET",
            Url = "https://example.test/",
            Assertions = AssertSpecMapper.ToDto(assertions),
        });

    public static ResponseSnapshot Response(
        int status = 200,
        string? body = null,
        double durationMs = 12,
        bool truncated = false,
        (string Name, string Value)[]? headers = null)
        => new()
        {
            Status = status,
            Headers = (headers ?? []).Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToArray(),
            BodyText = body,
            BodyTruncated = truncated,
            DurationMs = durationMs,
        };

    /// <summary>Field-wise comparison. <see cref="AssertSpec"/> is a record, so its generated
    /// equality compares <see cref="AssertSpec.ExpectedList"/> by reference — two parses of the
    /// same file would never be "equal".</summary>
    public static void AssertSame(AssertSpec expected, AssertSpec actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Selector, actual.Selector);
        Assert.Equal(expected.Op, actual.Op);
        Assert.Equal(expected.Expected, actual.Expected);
        Assert.Equal(expected.ExpectedList ?? [], actual.ExpectedList ?? []);
        Assert.Equal(expected.IgnoreCase, actual.IgnoreCase);
        Assert.Equal(expected.Skip, actual.Skip);
    }

    /// <summary>Evaluates a single assertion written in YAML against a response.</summary>
    public static AssertResult EvaluateOne(string assertionYaml, ResponseSnapshot response)
    {
        var assertions = ParseAssertions("assertions:\n" + assertionYaml);
        Assert.Single(assertions);
        var results = AssertEvaluator.Evaluate([new ResolvedAssert(assertions[0])], response);
        return Assert.Single(results);
    }

    /// <summary>The error code + message of the parse failure a fragment produces.</summary>
    public static WorkspaceError ParseError(string frontmatterFragment)
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => ParseRequest(frontmatterFragment));
        return ex.Error;
    }
}
