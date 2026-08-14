using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using static Tap.Tests.Testing.TestingTestData;

namespace Tap.Tests.Testing;

/// <summary>
/// parse → emit → parse has to be a fixed point. The Studio rewrites the whole file on every
/// save, so a step that doesn't survive the trip either loses meaning or churns the diff on
/// files nobody edited.
/// </summary>
public class TestingRoundTripTests
{
    [Theory]
    [InlineData("steps:\n- request: ./a.req.md")]
    [InlineData("steps:\n- name: First\n  request: ./a.req.md\n- name: Second\n  request: ./b.req.md")]
    [InlineData("steps:\n- request: id:0192-3a4d-7000-7b91-a0c1d2e3f405")]
    [InlineData("steps:\n- request: ./a.req.md\n  vars:\n    id: '{{orderId}}'")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: orderId\n    jsonpath: $.order.id")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: raw\n    body:")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: code\n    status:")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: token\n    regex: 'session=([^;]+)'\n    group: 1")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: page\n    header: x-page\n    default: '1'")]
    [InlineData("steps:\n- request: ./a.req.md\n  extract:\n  - var: next\n    jsonpath: $.next\n    required: false")]
    [InlineData("steps:\n- request: ./a.req.md\n  assertions:\n  - status: 201\n  - jsonpath: $.id")]
    [InlineData("steps:\n- request: ./a.req.md\n  continueOnFailure: true\n  skip: true")]
    [InlineData("vars:\n  sku: ABC-1\nsteps:\n- request: ./a.req.md\ntags: [smoke]")]
    public void Flow_survives_parse_emit_parse(string fragment)
    {
        var first = ParseFlow(fragment);
        var emitted = Emit(first);
        var second = (FlowFile)FileParser.Parse(FlowPath, emitted);

        Assert.Equal(first.Steps.Count, second.Steps.Count);
        for (var i = 0; i < first.Steps.Count; i++) AssertSame(first.Steps[i], second.Steps[i]);
        Assert.Equal(first.Tags, second.Tags);

        // And emitting again changes nothing — the second save of an untouched file is a no-op.
        Assert.Equal(emitted, Emit(second));
    }

    [Theory]
    [InlineData("tests:\n- request: ./a.req.md")]
    [InlineData("tests:\n- name: One\n  request: ./a.req.md\n- name: Two\n  flow: ./b.flow.md")]
    [InlineData("tests:\n- flow: id:0192-3a4d-7000-7b91-a0c1d2e3f405")]
    [InlineData("tests:\n- request: ./a.req.md\n  vars:\n    item: nope\n  assertions:\n  - status: 404")]
    [InlineData("tests:\n- request: ./a.req.md\n  skip: true")]
    [InlineData("onFailure: stop\ntests:\n- request: ./a.req.md")]
    [InlineData("vars:\n  customer: cus_demo\ntests:\n- request: ./a.req.md\ntags: [smoke, orders]")]
    public void Test_set_survives_parse_emit_parse(string fragment)
    {
        var first = ParseTestSet(fragment);
        var emitted = Emit(first);
        var second = (TestSetFile)FileParser.Parse(TestSetPath, emitted);

        Assert.Equal(first.OnFailure, second.OnFailure);
        Assert.Equal(first.Tests.Count, second.Tests.Count);
        for (var i = 0; i < first.Tests.Count; i++) AssertSame(first.Tests[i], second.Tests[i]);
        Assert.Equal(first.Tags, second.Tags);

        Assert.Equal(emitted, Emit(second));
    }

    [Fact]
    public void Variable_tokens_in_step_vars_survive_verbatim()
    {
        var flow = ParseFlow("steps:\n- request: ./a.req.md\n  vars:\n    id: '{{orderId}}'");
        var emitted = Emit(flow);

        Assert.Contains("'{{orderId}}'", emitted, StringComparison.Ordinal);
        var reparsed = (FlowFile)FileParser.Parse(FlowPath, emitted);
        Assert.Equal("{{orderId}}", reparsed.Steps[0].Vars["id"]);
    }

    [Fact]
    public void A_jsonpath_selector_is_not_quoted_into_noise()
    {
        var emitted = Emit(ParseFlow("steps:\n- request: ./a.req.md\n  extract:\n  - var: id\n    jsonpath: $.order.id"));
        Assert.Contains("jsonpath: $.order.id", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_onFailure_leaves_the_key_out()
    {
        Assert.DoesNotContain("onFailure", Emit(ParseTestSet("tests:\n- request: ./a.req.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Client_supplied_steps_are_validated_on_save()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => TestingSpecMapper.ToModel(
            new[]
            {
                new FlowStepSpecDto
                {
                    Request = "./a.req.md",
                    Extract = [new ExtractSpecDto { Var = "id", Source = "jsonpath" }],
                },
            },
            FlowPath));

        Assert.Equal(WorkspaceErrorCode.E_FLOW_INVALID, ex.Error.Code);
        Assert.Contains("needs an expression", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_supplied_tests_have_to_target_one_thing()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => TestingSpecMapper.ToModel(
            new[] { new TestEntrySpecDto { Request = "./a.req.md", Flow = "./b.flow.md" } },
            TestSetPath));

        Assert.Equal(WorkspaceErrorCode.E_TEST_INVALID, ex.Error.Code);
        Assert.Contains("names both", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_half_typed_variable_row_never_reaches_the_file()
    {
        var emitted = FlowSpecEmitter.ToFileSource(new FlowSpecDto
        {
            Path = FlowPath,
            Name = "Checkout",
            Steps =
            [
                new FlowStepSpecDto
                {
                    Request = "./a.req.md",
                    Vars = new Dictionary<string, string> { ["  "] = "orphan", ["id"] = "7" },
                },
            ],
        });

        Assert.DoesNotContain("orphan", emitted, StringComparison.Ordinal);
        Assert.Contains("id: 7", emitted, StringComparison.Ordinal);
    }
}
