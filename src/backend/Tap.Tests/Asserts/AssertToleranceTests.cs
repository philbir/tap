using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Asserts;
using static Tap.Tests.Asserts.AssertTestData;

namespace Tap.Tests.Asserts;

/// <summary>
/// The interactive re-check path. Someone editing an assertion spends most of their
/// keystrokes holding an invalid one, so a bad entry has to fail on its own row rather
/// than blanking out the verdicts beside it.
/// </summary>
public class AssertToleranceTests
{
    [Fact]
    public void A_bad_entry_fails_alone()
    {
        var converted = AssertSpecMapper.ToModelTolerant(
        [
            new AssertSpecDto { Source = "status", Op = "equals", Expected = "200" },
            new AssertSpecDto { Source = "body", Op = "count", Expected = "2" },
            new AssertSpecDto { Source = "header", Op = "exists", Selector = "etag" },
        ]);

        Assert.Null(converted[0].Error);
        Assert.Contains("'count' applies to jsonpath", converted[1].Error!, StringComparison.Ordinal);
        Assert.Null(converted[2].Error);

        var resolved = converted
            .Select(c => new ResolvedAssert(c.Spec, false, c.Error))
            .ToArray();
        var results = AssertEvaluator.Evaluate(resolved, Response(status: 200, headers: [("etag", "W/\"1\"")]));

        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Contains("'count' applies to jsonpath", results[1].Message!, StringComparison.Ordinal);
        Assert.True(results[2].Ok);

        var summary = AssertSummary.From(results);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public void An_unresolvable_variable_fails_only_its_own_row()
    {
        var spec = ParseAssertions("assertions:\n  - status: 200")[0];
        var results = AssertEvaluator.Evaluate(
        [
            new ResolvedAssert(spec),
            new ResolvedAssert(spec, false, "Unknown variable '{{ typo }}'."),
        ], Response(status: 200));

        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Contains("Unknown variable", results[1].Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_mapping_still_rejects_the_whole_batch()
    {
        Assert.Throws<Tap.Workspace.Model.WorkspaceParseException>(() => AssertSpecMapper.ToModel(
        [
            new AssertSpecDto { Source = "status", Op = "equals", Expected = "200" },
            new AssertSpecDto { Source = "body", Op = "count", Expected = "2" },
        ]));
    }
}
