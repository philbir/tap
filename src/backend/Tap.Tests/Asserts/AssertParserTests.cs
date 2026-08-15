using Tap.Workspace.Model;
using static Tap.Tests.Asserts.AssertTestData;

namespace Tap.Tests.Asserts;

/// <summary>
/// The YAML surface: the three shorthands, the explicit form, the modifiers, and the
/// rejections. Everything here is about turning text into a normalized
/// <see cref="AssertSpec"/> — matching semantics live in <see cref="AssertEvaluatorTests"/>.
/// </summary>
public class AssertParserTests
{
    [Fact]
    public void No_assertions_key_yields_an_empty_list()
    {
        Assert.Empty(ParseAssertions("tags: [demo]"));
    }

    // ---------------------------------------------------------------------------- sugar

    [Fact]
    public void Scalar_on_an_argument_less_extractor_means_equals()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - status: 201"));
        Assert.Equal(AssertSource.Status, a.Source);
        Assert.Equal(AssertOp.Equals, a.Op);
        Assert.Equal("201", a.Expected);
        Assert.Null(a.Selector);
    }

    [Fact]
    public void Selector_alone_means_exists()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - header: etag"));
        Assert.Equal(AssertSource.Header, a.Source);
        Assert.Equal(AssertOp.Exists, a.Op);
        Assert.Equal("etag", a.Selector);
        Assert.Equal("true", a.Expected);
    }

    [Fact]
    public void Jsonpath_alone_means_exists()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - jsonpath: $.order.id"));
        Assert.Equal(AssertSource.JsonPath, a.Source);
        Assert.Equal(AssertOp.Exists, a.Op);
        Assert.Equal("$.order.id", a.Selector);
    }

    [Fact]
    public void Regex_expands_to_body_matches()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - regex: '\"id\":\\s*\\d+'"));
        Assert.Equal(AssertSource.Body, a.Source);
        Assert.Equal(AssertOp.Matches, a.Op);
        Assert.Equal("\"id\":\\s*\\d+", a.Expected);
        Assert.Null(a.Selector);
    }

    // ------------------------------------------------------------------- explicit forms

    [Fact]
    public void Extractor_and_matcher_pair_parses()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - header: content-type\n    contains: application/json"));
        Assert.Equal(AssertSource.Header, a.Source);
        Assert.Equal("content-type", a.Selector);
        Assert.Equal(AssertOp.Contains, a.Op);
        Assert.Equal("application/json", a.Expected);
    }

    [Fact]
    public void Argument_less_extractor_with_an_empty_value_takes_a_sibling_matcher()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - duration:\n    lt: 800"));
        Assert.Equal(AssertSource.Duration, a.Source);
        Assert.Equal(AssertOp.LessThan, a.Op);
        Assert.Equal("800", a.Expected);
    }

    [Fact]
    public void Indented_matcher_is_read_as_the_nested_form()
    {
        // Two extra spaces turn the sibling into a nested mapping. YAML accepts both and the
        // difference is invisible in an editor, so the parser has to take either.
        var a = Assert.Single(ParseAssertions("assertions:\n  - duration:\n      lt: 800"));
        Assert.Equal(AssertSource.Duration, a.Source);
        Assert.Equal(AssertOp.LessThan, a.Op);
        Assert.Equal("800", a.Expected);
    }

    [Fact]
    public void List_matchers_read_their_values()
    {
        var assertions = ParseAssertions(
            "assertions:\n  - status:\n    in: [200, 201, 204]\n  - status:\n    between: [200, 299]");

        Assert.Equal(AssertOp.In, assertions[0].Op);
        Assert.Equal<string>(["200", "201", "204"], assertions[0].ExpectedList!);
        Assert.Null(assertions[0].Expected);

        Assert.Equal(AssertOp.Between, assertions[1].Op);
        Assert.Equal<string>(["200", "299"], assertions[1].ExpectedList!);
    }

    [Fact]
    public void Modifiers_are_read()
    {
        var a = Assert.Single(ParseAssertions(
            "assertions:\n  - name: content type\n    header: content-type\n    equals: APPLICATION/JSON\n    ignoreCase: true\n    skip: true"));
        Assert.Equal("content type", a.Name);
        Assert.True(a.IgnoreCase);
        Assert.True(a.Skip);
    }

    [Fact]
    public void Exists_false_is_preserved()
    {
        var a = Assert.Single(ParseAssertions("assertions:\n  - jsonpath: $.error\n    exists: false"));
        Assert.Equal(AssertOp.Exists, a.Op);
        Assert.Equal("false", a.Expected);
    }

    [Fact]
    public void Order_is_preserved()
    {
        var assertions = ParseAssertions("assertions:\n  - status: 200\n  - body:\n    contains: ok\n  - header: etag");
        Assert.Equal(3, assertions.Count);
        Assert.Equal(AssertSource.Status, assertions[0].Source);
        Assert.Equal(AssertSource.Body, assertions[1].Source);
        Assert.Equal(AssertSource.Header, assertions[2].Source);
    }

    // ------------------------------------------------------------------------- rejection

    [Theory]
    // structural
    [InlineData("assertions:\n  status: 200", "must be a list")]
    [InlineData("assertions:\n  - 200", "must be a mapping")]
    [InlineData("assertions:\n  - skip: true", "no extractor")]
    [InlineData("assertions:\n  - status: 200\n    body: hi", "more than one extractor")]
    [InlineData("assertions:\n  - body:\n    contains: a\n    equals: b", "more than one matcher")]
    [InlineData("assertions:\n  - status: 200\n    lt: 300", "Keep one of the two")]
    [InlineData("assertions:\n  - stat: 200", "unknown key 'stat'")]
    [InlineData("assertions:\n  - regex: abc\n    equals: x", "already implies")]
    // missing arguments
    [InlineData("assertions:\n  - header:\n    equals: x", "needs a header name")]
    [InlineData("assertions:\n  - jsonpath:\n    equals: x", "needs an expression")]
    [InlineData("assertions:\n  - status:", "needs either a value")]
    [InlineData("assertions:\n  - regex:", "needs a pattern")]
    // value shapes
    [InlineData("assertions:\n  - duration:\n    lt: soon", "takes a number")]
    [InlineData("assertions:\n  - status:\n    between: [200]", "exactly two bounds")]
    [InlineData("assertions:\n  - status:\n    between: [a, b]", "bounds must be numbers")]
    [InlineData("assertions:\n  - status:\n    in: []", "at least one value")]
    [InlineData("assertions:\n  - status:\n    equals: [200, 201]", "single value, not a list")]
    [InlineData("assertions:\n  - jsonpath: $.a\n    type: date", "is not a JSON type")]
    [InlineData("assertions:\n  - header: etag\n    exists: maybe", "takes true or false")]
    [InlineData("assertions:\n  - status: 200\n    skip: yesplease", "takes true or false")]
    // impossible combinations
    [InlineData("assertions:\n  - status:\n    exists: true", "'exists' applies to header")]
    [InlineData("assertions:\n  - body:\n    count: 2", "'count' applies to jsonpath")]
    [InlineData("assertions:\n  - xpath: /a\n    type: string", "'type' applies to jsonpath")]
    [InlineData("assertions:\n  - duration:\n    startsWith: 1", "do not apply to duration")]
    public void Invalid_entries_are_rejected(string fragment, string expectedFragment)
    {
        var error = ParseError(fragment);
        Assert.Equal(WorkspaceErrorCode.E_ASSERT_INVALID, error.Code);
        Assert.Contains(expectedFragment, error.Message, StringComparison.Ordinal);
        Assert.Equal(RequestPath, error.RelativePath);
    }

    [Fact]
    public void Rejection_names_the_offending_entry()
    {
        var error = ParseError("assertions:\n  - status: 200\n  - header: etag\n  - nope: 1");
        Assert.Contains("Assertion #3", error.Message, StringComparison.Ordinal);
    }
}
