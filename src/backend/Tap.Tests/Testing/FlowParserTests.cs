using Tap.Workspace.Model;
using static Tap.Tests.Testing.TestingTestData;

namespace Tap.Tests.Testing;

public class FlowParserTests
{
    [Fact]
    public void Reads_a_step_with_every_field()
    {
        var flow = ParseFlow("""
            vars:
              sku: ABC-1
            steps:
            - name: Create order
              request: ../collections/demo/create-order.req.tap
              vars:
                item: '{{sku}}'
              extract:
              - var: orderId
                jsonpath: $.order.id
              assertions:
              - status: 201
              continueOnFailure: true
            """);

        Assert.Equal("ABC-1", flow.Vars["sku"].Default);
        var step = Assert.Single(flow.Steps);
        Assert.Equal("Create order", step.Name);
        Assert.Equal("../collections/demo/create-order.req.tap", step.Request.SourceText);
        Assert.Equal("{{sku}}", step.Vars["item"]);
        Assert.True(step.ContinueOnFailure);
        Assert.False(step.Skip);

        var extract = Assert.Single(step.Extract);
        Assert.Equal("orderId", extract.Var);
        Assert.Equal(ExtractSource.JsonPath, extract.Source);
        Assert.Equal("$.order.id", extract.Selector);
        Assert.True(extract.Required);

        var assertion = Assert.Single(step.Assertions);
        Assert.Equal(AssertSource.Status, assertion.Source);
        Assert.Equal("201", assertion.Expected);
    }

    [Fact]
    public void Reads_an_id_ref()
    {
        var flow = ParseFlow("steps:\n- request: id:0192-3a4d-7000-7b91-a0c1d2e3f405");
        var step = Assert.Single(flow.Steps);
        Assert.Equal("0192-3a4d-7000-7b91-a0c1d2e3f405", step.Request.Id);
    }

    [Theory]
    [InlineData("- var: code\n    status:", ExtractSource.Status, null)]
    [InlineData("- var: ms\n    duration:", ExtractSource.Duration, null)]
    [InlineData("- var: raw\n    body:", ExtractSource.Body, null)]
    [InlineData("- var: tag\n    header: etag", ExtractSource.Header, "etag")]
    [InlineData("- var: id\n    jsonpath: $.id", ExtractSource.JsonPath, "$.id")]
    [InlineData("- var: total\n    xpath: /order/total", ExtractSource.XPath, "/order/total")]
    [InlineData("- var: token\n    regex: 'session=([^;]+)'", ExtractSource.Regex, "session=([^;]+)")]
    public void Reads_every_extraction_source(string entry, ExtractSource source, string? selector)
    {
        var flow = ParseFlow($"steps:\n- request: ./a.req.tap\n  extract:\n  {entry}");
        var extract = Assert.Single(Assert.Single(flow.Steps).Extract);
        Assert.Equal(source, extract.Source);
        Assert.Equal(selector, extract.Selector);
    }

    [Fact]
    public void Reads_extraction_modifiers()
    {
        var flow = ParseFlow("""
            steps:
            - request: ./a.req.tap
              extract:
              - var: token
                regex: 'session=([^;]+)'
                group: 1
              - var: page
                header: x-page
                default: '1'
              - var: cursor
                jsonpath: $.next
                required: false
            """);

        var extract = Assert.Single(flow.Steps).Extract;
        Assert.Equal(1, extract[0].Group);
        Assert.Equal("1", extract[1].Default);
        Assert.False(extract[2].Required);
    }

    [Fact]
    public void A_flow_with_no_steps_yet_is_not_an_error()
    {
        // The editor writes the frontmatter before the first step exists. Refusing to load
        // that file would mean a freshly created flow could never be opened again.
        Assert.Empty(ParseFlow("vars:\n  a: b").Steps);
        Assert.Empty(ParseFlow("steps: []").Steps);
    }

    [Fact]
    public void Steps_has_to_be_a_list()
    {
        Assert.Equal(WorkspaceErrorCode.E_FLOW_INVALID, FlowParseError("steps: nope").Code);
    }

    [Fact]
    public void A_step_needs_a_request()
    {
        var error = FlowParseError("steps:\n- name: Nothing to run");
        Assert.Equal(WorkspaceErrorCode.E_FLOW_INVALID, error.Code);
        Assert.Contains("Step #1", error.Message, StringComparison.Ordinal);
        Assert.Contains("'request:' is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extraction_needs_exactly_one_source()
    {
        var none = FlowParseError("steps:\n- request: ./a.req.tap\n  extract:\n  - var: x");
        Assert.Contains("has no source", none.Message, StringComparison.Ordinal);

        var two = FlowParseError("steps:\n- request: ./a.req.tap\n  extract:\n  - var: x\n    jsonpath: $.a\n    header: etag");
        Assert.Contains("names two sources", two.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extraction_needs_a_var()
    {
        var error = FlowParseError("steps:\n- request: ./a.req.tap\n  extract:\n  - jsonpath: $.id");
        Assert.Contains("has no 'var:'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_extractions_cannot_bind_the_same_name_in_one_step()
    {
        var error = FlowParseError("""
            steps:
            - request: ./a.req.tap
              extract:
              - var: id
                jsonpath: $.a
              - var: id
                jsonpath: $.b
            """);
        Assert.Contains("already binds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_name_has_to_be_readable_as_a_token()
    {
        var error = FlowParseError("steps:\n- request: ./a.req.tap\n  extract:\n  - var: 'order id'\n    jsonpath: $.id");
        Assert.Contains("not a usable variable name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_belongs_to_regex_extractions()
    {
        var error = FlowParseError("steps:\n- request: ./a.req.tap\n  extract:\n  - var: x\n    jsonpath: $.a\n    group: 1");
        Assert.Contains("'group' applies to regex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_step_keys_are_rejected_by_name()
    {
        var error = FlowParseError("steps:\n- request: ./a.req.tap\n  retries: 3");
        Assert.Contains("unknown key 'retries'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_assertion_inside_a_step_names_the_step()
    {
        var error = FlowParseError("steps:\n- request: ./a.req.tap\n  assertions:\n  - body:\n      count: 2");
        Assert.Equal(WorkspaceErrorCode.E_ASSERT_INVALID, error.Code);
        Assert.Contains("Step #1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_suffix_and_the_kind_have_to_agree()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => Workspace.Parsing.FileParser.Parse(
            "tests/a.flow.tap", "---\nkind: test\nname: X\ntests: []\n---\n"));
        Assert.Equal(WorkspaceErrorCode.E_KIND_MISMATCH, ex.Error.Code);
    }
}
