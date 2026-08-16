using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using static Tap.Tests.Asserts.AssertTestData;
using static Tap.Tests.Testing.TestingTestData;

namespace Tap.Tests.Testing;

/// <summary>
/// The extraction half of a flow: what a step binds out of its response. Written against the
/// YAML rather than hand-built specs so the parser and the extractor are exercised as the pair
/// a workspace author actually uses.
/// </summary>
public class ValueExtractorTests
{
    private const string OrderJson = """
        {"order":{"id":"ord-42","total":129.5,"lines":[{"sku":"A"},{"sku":"B"}],"note":null,"paid":true}}
        """;

    private static ExtractedValue ExtractOne(string entry, ResponseSnapshot response)
    {
        var flow = ParseFlow($"steps:\n- request: ./a.req.tap\n  extract:\n  {entry}");
        var results = ValueExtractor.Extract(Assert.Single(flow.Steps).Extract, response);
        return Assert.Single(results);
    }

    [Fact]
    public void Binds_a_jsonpath_value()
    {
        var bound = ExtractOne("- var: orderId\n    jsonpath: $.order.id", Response(body: OrderJson));
        Assert.Equal("orderId", bound.Var);
        Assert.Equal("ord-42", bound.Value);
        Assert.True(bound.Bound);
    }

    [Fact]
    public void A_json_string_binds_its_contents_not_its_quoted_literal()
    {
        Assert.Equal("ord-42", ExtractOne("- var: id\n    jsonpath: $.order.id", Response(body: OrderJson)).Value);
    }

    [Theory]
    [InlineData("$.order.total", "129.5")]
    [InlineData("$.order.paid", "true")]
    [InlineData("$.order.note", "null")]
    public void Binds_non_string_json_values_as_text(string path, string expected)
    {
        Assert.Equal(expected, ExtractOne($"- var: v\n    jsonpath: {path}", Response(body: OrderJson)).Value);
    }

    [Fact]
    public void Binds_a_header()
    {
        var bound = ExtractOne("- var: tag\n    header: ETag", Response(headers: [("etag", "W/\"7\"")]));
        Assert.Equal("W/\"7\"", bound.Value);
    }

    [Fact]
    public void Binds_the_status_and_the_duration()
    {
        Assert.Equal("201", ExtractOne("- var: code\n    status:", Response(status: 201)).Value);
        Assert.Equal("42", ExtractOne("- var: ms\n    duration:", Response(durationMs: 42)).Value);
    }

    [Fact]
    public void Binds_the_whole_body()
    {
        Assert.Equal("hello", ExtractOne("- var: raw\n    body:", Response(body: "hello")).Value);
    }

    [Fact]
    public void Binds_an_xpath_value()
    {
        var xml = "<order><total>129.5</total></order>";
        Assert.Equal("129.5", ExtractOne("- var: total\n    xpath: /order/total", Response(body: xml)).Value);
    }

    [Fact]
    public void Regex_binds_the_first_capture_group_by_default()
    {
        var bound = ExtractOne("- var: token\n    regex: 'session=([^;]+)'", Response(body: "session=abc123; Path=/"));
        Assert.Equal("abc123", bound.Value);
    }

    [Fact]
    public void Regex_with_no_groups_binds_the_whole_match()
    {
        var bound = ExtractOne("- var: id\n    regex: 'ord-\\d+'", Response(body: "created ord-42 ok"));
        Assert.Equal("ord-42", bound.Value);
    }

    [Fact]
    public void Regex_group_selects_the_capture()
    {
        var bound = ExtractOne(
            "- var: minor\n    regex: 'v(\\d+)\\.(\\d+)'\n    group: 2",
            Response(body: "v1.7"));
        Assert.Equal("7", bound.Value);
    }

    [Fact]
    public void Regex_group_0_binds_the_whole_match()
    {
        var bound = ExtractOne(
            "- var: whole\n    regex: 'v(\\d+)'\n    group: 0",
            Response(body: "v9"));
        Assert.Equal("v9", bound.Value);
    }

    [Fact]
    public void A_missing_capture_group_is_an_error()
    {
        var bound = ExtractOne("- var: x\n    regex: 'v(\\d+)'\n    group: 4", Response(body: "v9"));
        Assert.False(bound.Ok);
        Assert.Contains("no capture group 4", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_value_fails_the_extraction_by_default()
    {
        var bound = ExtractOne("- var: id\n    jsonpath: $.nope", Response(body: OrderJson));
        Assert.False(bound.Ok);
        Assert.Contains("did not match anything", bound.Error!, StringComparison.Ordinal);
        Assert.Contains("'id' has no value", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_covers_a_missing_value()
    {
        var bound = ExtractOne("- var: page\n    header: x-page\n    default: '1'", Response());
        Assert.True(bound.Ok);
        Assert.Equal("1", bound.Value);
    }

    [Fact]
    public void An_optional_extraction_binds_nothing_and_carries_on()
    {
        var bound = ExtractOne("- var: next\n    jsonpath: $.next\n    required: false", Response(body: OrderJson));
        Assert.True(bound.Ok);
        Assert.Null(bound.Value);
        Assert.False(bound.Bound);
    }

    [Fact]
    public void A_multi_node_match_is_an_error_rather_than_a_silent_first_pick()
    {
        var bound = ExtractOne("- var: sku\n    jsonpath: $.order.lines[*].sku", Response(body: OrderJson));
        Assert.False(bound.Ok);
        Assert.Contains("matched 2 nodes", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_json_body_is_an_error_that_a_default_does_not_paper_over()
    {
        var bound = ExtractOne("- var: id\n    jsonpath: $.id\n    default: fallback", Response(body: "not json"));
        Assert.False(bound.Ok);
        Assert.Contains("not valid JSON", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_selector_is_reported_not_thrown()
    {
        var bound = ExtractOne("- var: id\n    jsonpath: '$$$['", Response(body: OrderJson));
        Assert.False(bound.Ok);
        Assert.Contains("not a valid JSONPath", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_body_refuses_to_bind_a_prefix()
    {
        var bound = ExtractOne("- var: raw\n    body:", Response(body: "half a bod", truncated: true));
        Assert.False(bound.Ok);
        Assert.Contains("truncated", bound.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bound_value_is_capped_for_reporting()
    {
        var huge = new string('x', ValueExtractor.ValuePreviewLimit + 500);
        var bound = ExtractOne("- var: raw\n    body:", Response(body: huge));
        Assert.Equal(ValueExtractor.ValuePreviewLimit, bound.Value!.Length);
    }

    [Fact]
    public void Extractions_are_independent_of_each_other()
    {
        var flow = ParseFlow("""
            steps:
            - request: ./a.req.tap
              extract:
              - var: missing
                jsonpath: $.nope
              - var: id
                jsonpath: $.order.id
            """);

        var results = ValueExtractor.Extract(Assert.Single(flow.Steps).Extract, Response(body: OrderJson));
        Assert.False(results[0].Ok);
        Assert.True(results[1].Ok);
        Assert.Equal("ord-42", results[1].Value);
    }
}
