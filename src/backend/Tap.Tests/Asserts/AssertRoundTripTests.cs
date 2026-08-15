using Tap.Studio.Specs;
using Tap.Workspace.Model;
using static Tap.Tests.Asserts.AssertTestData;

namespace Tap.Tests.Asserts;

/// <summary>
/// parse → emit → parse has to be a fixed point. The Studio rewrites a whole request file
/// on every save, so an assertion that doesn't survive the trip either loses meaning or
/// churns the diff on files nobody edited.
/// </summary>
public class AssertRoundTripTests
{
    [Theory]
    [InlineData("  - status: 201")]
    [InlineData("  - status: 2xx")]
    [InlineData("  - header: etag")]
    [InlineData("  - header: content-type\n    contains: application/json")]
    [InlineData("  - jsonpath: $.order.id")]
    [InlineData("  - jsonpath: $.order.customer.email\n    equals: jane@example.test")]
    [InlineData("  - jsonpath: $.order.lines\n    count: 3")]
    [InlineData("  - jsonpath: $.error\n    exists: false")]
    [InlineData("  - jsonpath: $.total\n    type: number")]
    [InlineData("  - xpath: /order/total\n    gt: 100")]
    [InlineData("  - body:\n    contains: Thank you")]
    [InlineData("  - regex: '\"id\":\\s*\"ord-\\d+\"'")]
    [InlineData("  - duration:\n    lt: 800")]
    [InlineData("  - status:\n    in: [200, 201, 204]")]
    [InlineData("  - status:\n    between: [200, 299]")]
    [InlineData("  - name: order id\n    jsonpath: $.order.id\n    matches: ^ord-\\d+$\n    skip: true")]
    [InlineData("  - header: x-trace\n    equals: ABC\n    ignoreCase: true")]
    [InlineData("  - regex: hello\n    ignoreCase: true")]
    public void Assertion_survives_parse_emit_parse(string entry)
    {
        var first = ParseAssertions("assertions:\n" + entry);
        var emitted = Emit(first);
        var second = ((RequestFile)Tap.Workspace.Parsing.FileParser.Parse(RequestPath, emitted)).Assertions;

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++) AssertSame(first[i], second[i]);

        // And emitting again changes nothing — the second save of an untouched file is a no-op.
        Assert.Equal(emitted, Emit(second));
    }

    [Fact]
    public void Variable_tokens_survive_verbatim()
    {
        var first = ParseAssertions("assertions:\n  - jsonpath: $.customer.email\n    equals: '{{user.email}}'");
        Assert.Equal("{{user.email}}", first[0].Expected);

        var emitted = Emit(first);
        Assert.Contains("'{{user.email}}'", emitted, StringComparison.Ordinal);

        var second = ((RequestFile)Tap.Workspace.Parsing.FileParser.Parse(RequestPath, emitted)).Assertions;
        Assert.Equal("{{user.email}}", second[0].Expected);
    }

    [Fact]
    public void Explicit_form_normalizes_to_the_shorthand()
    {
        // Hand-written long form collapses to the sugar on the next save — the same
        // normalization every other spec field gets.
        var parsed = ParseAssertions("assertions:\n  - status:\n    equals: 200\n  - header: etag\n    exists: true");
        var emitted = Emit(parsed);

        Assert.Contains("- status: 200", emitted, StringComparison.Ordinal);
        Assert.Contains("- header: etag", emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("equals:", emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("exists:", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_assertions_leave_the_key_out()
    {
        Assert.DoesNotContain("assertions", Emit([]), StringComparison.Ordinal);
    }

    [Fact]
    public void Assertions_sit_between_vars_and_tags()
    {
        var emitted = RequestSpecEmitter.ToFileSource(new Tap.Studio.Contracts.RequestSpecDto
        {
            Path = RequestPath,
            Name = "Sample",
            Method = "GET",
            Url = "https://example.test/",
            Vars = new Dictionary<string, string> { ["who"] = "world" },
            Tags = ["demo"],
            Assertions = AssertSpecMapper.ToDto(ParseAssertions("assertions:\n  - status: 200")),
        });

        var vars = emitted.IndexOf("vars:", StringComparison.Ordinal);
        var assertions = emitted.IndexOf("assertions:", StringComparison.Ordinal);
        var tags = emitted.IndexOf("tags:", StringComparison.Ordinal);

        Assert.True(vars < assertions, "vars should precede assertions");
        Assert.True(assertions < tags, "assertions should precede tags");
    }

    [Fact]
    public void Client_supplied_assertions_are_validated_on_save()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => AssertSpecMapper.ToModel(
        [
            new Tap.Studio.Contracts.AssertSpecDto { Source = "body", Op = "count", Expected = "2" },
        ]));

        Assert.Equal(WorkspaceErrorCode.E_ASSERT_INVALID, ex.Error.Code);
        Assert.Contains("'count' applies to jsonpath", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_supplied_selector_is_required()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => AssertSpecMapper.ToModel(
        [
            new Tap.Studio.Contracts.AssertSpecDto { Source = "jsonpath", Op = "exists", Expected = "true" },
        ]));

        Assert.Contains("needs an expression", ex.Error.Message, StringComparison.Ordinal);
    }
}
