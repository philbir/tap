using Tap.Workspace.Model;
using static Tap.Tests.Testing.TestingTestData;

namespace Tap.Tests.Testing;

public class TestSetParserTests
{
    [Fact]
    public void Reads_request_and_flow_entries()
    {
        var set = ParseTestSet("""
            vars:
              customer: cus_demo
            onFailure: stop
            tests:
            - name: Rejects an unknown SKU
              request: ../collections/demo/create-order.req.tap
              vars:
                item: nope
              assertions:
              - status: 404
            - name: Full checkout
              flow: ./checkout.flow.tap
              skip: true
            """);

        Assert.Equal("cus_demo", set.Vars["customer"].Default);
        Assert.Equal(TestFailureMode.Stop, set.OnFailure);
        Assert.Equal(2, set.Tests.Count);

        var first = set.Tests[0];
        Assert.Equal("request", first.TargetKind);
        Assert.Equal("../collections/demo/create-order.req.tap", first.Request!.SourceText);
        Assert.Null(first.Flow);
        Assert.Equal("nope", first.Vars["item"]);
        Assert.Equal("404", Assert.Single(first.Assertions).Expected);

        var second = set.Tests[1];
        Assert.Equal("flow", second.TargetKind);
        Assert.Equal("./checkout.flow.tap", second.Flow!.SourceText);
        Assert.True(second.Skip);
    }

    [Fact]
    public void OnFailure_defaults_to_continue()
    {
        Assert.Equal(TestFailureMode.Continue, ParseTestSet("tests:\n- request: ./a.req.tap").OnFailure);
    }

    [Fact]
    public void OnFailure_rejects_anything_else()
    {
        var error = TestSetParseError("onFailure: halt\ntests:\n- request: ./a.req.tap");
        Assert.Equal(WorkspaceErrorCode.E_TEST_INVALID, error.Code);
        Assert.Contains("Expected 'continue' or 'stop'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_set_with_no_tests_yet_is_not_an_error()
    {
        // Same reasoning as a flow's steps — a set created a second ago has none, and that
        // file still has to load.
        Assert.Empty(ParseTestSet("vars:\n  a: b").Tests);
        Assert.Empty(ParseTestSet("tests: []").Tests);
    }

    [Fact]
    public void Tests_has_to_be_a_list()
    {
        Assert.Equal(WorkspaceErrorCode.E_TEST_INVALID, TestSetParseError("tests: nope").Code);
    }

    [Fact]
    public void A_test_targets_exactly_one_thing()
    {
        var neither = TestSetParseError("tests:\n- name: Nothing");
        Assert.Contains("names neither", neither.Message, StringComparison.Ordinal);

        var both = TestSetParseError("tests:\n- request: ./a.req.tap\n  flow: ./b.flow.tap");
        Assert.Contains("names both", both.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_test_keys_are_rejected_by_name()
    {
        var error = TestSetParseError("tests:\n- request: ./a.req.tap\n  extract:\n  - var: x\n    jsonpath: $.a");
        Assert.Equal(WorkspaceErrorCode.E_TEST_INVALID, error.Code);
        Assert.Contains("unknown key 'extract'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_assertion_inside_a_test_names_the_test()
    {
        var error = TestSetParseError("tests:\n- request: ./a.req.tap\n- request: ./b.req.tap\n  assertions:\n  - jsonpath: $.a\n    type: widget");
        Assert.Equal(WorkspaceErrorCode.E_ASSERT_INVALID, error.Code);
        Assert.Contains("Test #2", error.Message, StringComparison.Ordinal);
    }
}
