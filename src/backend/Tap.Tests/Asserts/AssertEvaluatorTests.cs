using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using static Tap.Tests.Asserts.AssertTestData;

namespace Tap.Tests.Asserts;

/// <summary>
/// Matching semantics, one extractor family at a time. The invariant behind every case:
/// the evaluator never throws — a broken selector, a body of the wrong flavor, or a runaway
/// regex all come back as a failed assertion carrying an explanation.
/// </summary>
public class AssertEvaluatorTests
{
    private const string OrderJson = """
        {
          "order": {
            "id": "ord-4417",
            "total": 129.5,
            "paid": true,
            "customer": { "email": "jane@example.test", "name": "Jane" },
            "lines": [
              { "sku": "A-1", "qty": 2 },
              { "sku": "B-7", "qty": 1 },
              { "sku": "C-3", "qty": 4 }
            ],
            "coupon": null
          }
        }
        """;

    private const string OrderXml = """
        <order id="ord-4417">
          <total currency="EUR">129.50</total>
          <lines>
            <line sku="A-1" />
            <line sku="B-7" />
          </lines>
        </order>
        """;

    // ------------------------------------------------------------------------- status

    [Theory]
    [InlineData("  - status: 201", 201, true)]
    [InlineData("  - status: 201", 200, false)]
    [InlineData("  - status: 2xx", 204, true)]
    [InlineData("  - status: 2xx", 404, false)]
    [InlineData("  - status: 20x", 201, true)]
    [InlineData("  - status: 20x", 226, false)]
    [InlineData("  - status:\n    in: [200, 201, 204]", 204, true)]
    [InlineData("  - status:\n    in: [200, 201]", 204, false)]
    [InlineData("  - status:\n    between: [200, 299]", 299, true)]
    [InlineData("  - status:\n    between: [200, 299]", 300, false)]
    [InlineData("  - status:\n    gte: 500", 503, true)]
    [InlineData("  - status:\n    notEquals: 500", 200, true)]
    public void Status_assertions(string yaml, int status, bool expectedOk)
    {
        var result = EvaluateOne(yaml, Response(status: status));
        Assert.Equal(expectedOk, result.Ok);
        Assert.Equal(status.ToString(), result.Actual);
    }

    [Fact]
    public void Status_class_wildcard_only_applies_to_status()
    {
        // 'x' is a literal everywhere else — a header value of "2xx" matches, a body of "204"
        // does not.
        Assert.True(EvaluateOne("  - header: x-code\n    equals: 2xx", Response(headers: [("x-code", "2xx")])).Ok);
        Assert.False(EvaluateOne("  - header: x-code\n    equals: 2xx", Response(headers: [("x-code", "204")])).Ok);
    }

    // ----------------------------------------------------------------------- duration

    [Theory]
    [InlineData("  - duration:\n    lt: 800", 120.0, true)]
    [InlineData("  - duration:\n    lt: 100", 120.0, false)]
    [InlineData("  - duration:\n    lte: 120", 120.0, true)]
    [InlineData("  - duration:\n    gt: 50", 120.0, true)]
    public void Duration_assertions(string yaml, double durationMs, bool expectedOk)
    {
        Assert.Equal(expectedOk, EvaluateOne(yaml, Response(durationMs: durationMs)).Ok);
    }

    // ------------------------------------------------------------------------ headers

    [Fact]
    public void Header_lookup_is_case_insensitive_on_the_name()
    {
        var result = EvaluateOne("  - header: Content-Type\n    contains: json", Response(headers: [("content-type", "application/json; charset=utf-8")]));
        Assert.True(result.Ok);
    }

    [Fact]
    public void Header_value_comparison_is_case_sensitive_unless_asked()
    {
        var response = Response(headers: [("x-trace", "ABC123")]);
        Assert.False(EvaluateOne("  - header: x-trace\n    equals: abc123", response).Ok);
        Assert.True(EvaluateOne("  - header: x-trace\n    equals: abc123\n    ignoreCase: true", response).Ok);
    }

    [Fact]
    public void Missing_header_fails_a_value_check_with_an_explanation()
    {
        var result = EvaluateOne("  - header: etag\n    equals: abc", Response());
        Assert.False(result.Ok);
        Assert.Null(result.Actual);
        Assert.Contains("not present", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_existence_both_ways()
    {
        var response = Response(headers: [("etag", "W/\"7\"")]);
        Assert.True(EvaluateOne("  - header: etag", response).Ok);
        Assert.False(EvaluateOne("  - header: etag\n    exists: false", response).Ok);
        Assert.True(EvaluateOne("  - header: x-missing\n    exists: false", response).Ok);
        Assert.False(EvaluateOne("  - header: x-missing", response).Ok);
    }

    [Fact]
    public void Repeated_header_reads_the_first_value()
    {
        var response = Response(headers: [("set-cookie", "a=1"), ("set-cookie", "b=2")]);
        Assert.Equal("a=1", EvaluateOne("  - header: set-cookie\n    equals: a=1", response).Actual);
    }

    // --------------------------------------------------------------------------- body

    [Theory]
    [InlineData("  - body:\n    contains: Thank you", true)]
    [InlineData("  - body:\n    notContains: Sorry", true)]
    [InlineData("  - body:\n    startsWith: Thank", true)]
    [InlineData("  - body:\n    endsWith: order!", true)]
    [InlineData("  - body:\n    contains: refund", false)]
    public void Body_text_assertions(string yaml, bool expectedOk)
    {
        Assert.Equal(expectedOk, EvaluateOne(yaml, Response(body: "Thank you for your order!")).Ok);
    }

    [Fact]
    public void Body_length_counts_characters()
    {
        Assert.True(EvaluateOne("  - body:\n    length: 5", Response(body: "hello")).Ok);
    }

    [Fact]
    public void Regex_sugar_matches_the_body()
    {
        var response = Response(body: """{"id": "ord-4417"}""");
        Assert.True(EvaluateOne("""  - regex: '"id":\s*"ord-\d+"'""", response).Ok);
        Assert.False(EvaluateOne("""  - regex: '"id":\s*"cust-\d+"'""", response).Ok);
    }

    [Fact]
    public void Invalid_regex_fails_the_assertion_instead_of_throwing()
    {
        var result = EvaluateOne("  - regex: '[unterminated'", Response(body: "anything"));
        Assert.False(result.Ok);
        Assert.Contains("not a valid regular expression", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_body_is_not_evaluated()
    {
        foreach (var yaml in new[] { "  - body:\n    contains: x", "  - jsonpath: $.a", "  - xpath: /a", "  - regex: x" })
        {
            var result = EvaluateOne(yaml, Response(body: "partial", truncated: true));
            Assert.False(result.Ok);
            Assert.Contains("truncated", result.Message!, StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------------- jsonpath

    [Theory]
    [InlineData("$.order.id", "equals", "ord-4417", true)]
    [InlineData("$.order.id", "equals", "ord-0000", false)]
    [InlineData("$.order.total", "equals", "129.5", true)]
    [InlineData("$.order.total", "gt", "100", true)]
    [InlineData("$.order.total", "lt", "100", false)]
    [InlineData("$.order.paid", "equals", "true", true)]
    [InlineData("$.order.customer.name", "startsWith", "Ja", true)]
    [InlineData("$.order.id", "matches", "^ord-\\d+$", true)]
    public void Jsonpath_scalar_assertions(string path, string op, string expected, bool expectedOk)
    {
        var yaml = $"  - jsonpath: {path}\n    {op}: '{expected}'";
        Assert.Equal(expectedOk, EvaluateOne(yaml, Response(body: OrderJson)).Ok);
    }

    [Fact]
    public void Json_string_compares_as_its_contents_not_its_literal()
    {
        Assert.Equal("ord-4417", EvaluateOne("  - jsonpath: $.order.id\n    equals: ord-4417", Response(body: OrderJson)).Actual);
    }

    [Fact]
    public void Number_coercion_lets_a_string_expectation_match_a_json_number()
    {
        // Expected values are always strings (a {{var}} can only expand to one), so "129.50"
        // has to match the number 129.5 — otherwise every numeric assertion would need
        // the author to guess the server's formatting.
        Assert.True(EvaluateOne("  - jsonpath: $.order.total\n    equals: '129.50'", Response(body: OrderJson)).Ok);
    }

    [Fact]
    public void Jsonpath_count_and_length_on_a_collection()
    {
        var response = Response(body: OrderJson);
        Assert.True(EvaluateOne("  - jsonpath: $.order.lines[*]\n    count: 3", response).Ok);
        Assert.False(EvaluateOne("  - jsonpath: $.order.lines[*]\n    count: 2", response).Ok);
        // The array itself is one node whose natural length is its element count.
        Assert.True(EvaluateOne("  - jsonpath: $.order.lines\n    length: 3", response).Ok);
    }

    [Theory]
    [InlineData("$.order.id", "string")]
    [InlineData("$.order.total", "number")]
    [InlineData("$.order.paid", "boolean")]
    [InlineData("$.order.customer", "object")]
    [InlineData("$.order.lines", "array")]
    [InlineData("$.order.coupon", "null")]
    public void Jsonpath_type_matcher(string path, string type)
    {
        Assert.True(EvaluateOne($"  - jsonpath: {path}\n    type: {type}", Response(body: OrderJson)).Ok);
    }

    [Fact]
    public void Zero_matches_only_satisfies_exists_false_and_count_zero()
    {
        var response = Response(body: OrderJson);
        Assert.True(EvaluateOne("  - jsonpath: $.order.refund\n    exists: false", response).Ok);
        Assert.True(EvaluateOne("  - jsonpath: $.order.refund\n    count: 0", response).Ok);

        var equals = EvaluateOne("  - jsonpath: $.order.refund\n    equals: x", response);
        Assert.False(equals.Ok);
        Assert.Contains("did not match anything", equals.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_matches_support_membership_but_not_scalar_comparison()
    {
        var response = Response(body: OrderJson);

        Assert.True(EvaluateOne("  - jsonpath: $.order.lines[*].sku\n    contains: B-7", response).Ok);
        Assert.False(EvaluateOne("  - jsonpath: $.order.lines[*].sku\n    contains: Z-9", response).Ok);
        Assert.True(EvaluateOne("  - jsonpath: $.order.lines[*].sku\n    notContains: Z-9", response).Ok);

        var equals = EvaluateOne("  - jsonpath: $.order.lines[*].sku\n    equals: A-1", response);
        Assert.False(equals.Ok);
        Assert.Contains("matched 3 nodes", equals.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_json_body_fails_a_jsonpath_assertion_with_an_explanation()
    {
        var result = EvaluateOne("  - jsonpath: $.order.id\n    equals: x", Response(body: "<html>nope</html>"));
        Assert.False(result.Ok);
        Assert.Contains("not valid JSON", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_jsonpath_fails_the_assertion()
    {
        var result = EvaluateOne("  - jsonpath: '$$[bogus'\n    equals: x", Response(body: OrderJson));
        Assert.False(result.Ok);
        Assert.Contains("not a valid JSONPath", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_body_fails_a_jsonpath_assertion()
    {
        var result = EvaluateOne("  - jsonpath: $.a\n    equals: x", Response(body: null));
        Assert.False(result.Ok);
        Assert.Contains("no body", result.Message!, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------- xpath

    [Fact]
    public void Xpath_reads_elements_attributes_and_counts()
    {
        var response = Response(body: OrderXml);
        Assert.True(EvaluateOne("  - xpath: /order/total\n    equals: '129.50'", response).Ok);
        Assert.True(EvaluateOne("  - xpath: /order/total\n    gt: 100", response).Ok);
        Assert.True(EvaluateOne("  - xpath: /order/@id\n    equals: ord-4417", response).Ok);
        Assert.True(EvaluateOne("  - xpath: /order/total/@currency\n    equals: EUR", response).Ok);
        Assert.True(EvaluateOne("  - xpath: /order/lines/line\n    count: 2", response).Ok);
        Assert.True(EvaluateOne("  - xpath: /order/missing\n    exists: false", response).Ok);
    }

    [Fact]
    public void Xpath_function_results_are_comparable()
    {
        Assert.True(EvaluateOne("  - xpath: count(/order/lines/line)\n    equals: 2", Response(body: OrderXml)).Ok);
    }

    [Fact]
    public void Non_xml_body_fails_an_xpath_assertion_with_an_explanation()
    {
        var result = EvaluateOne("  - xpath: /order\n    exists: true", Response(body: "{\"not\":\"xml\"}"));
        Assert.False(result.Ok);
        Assert.Contains("not valid XML", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_xpath_fails_the_assertion()
    {
        var result = EvaluateOne("  - xpath: '///'\n    exists: true", Response(body: OrderXml));
        Assert.False(result.Ok);
        Assert.Contains("not a valid XPath", result.Message!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- results and summary

    [Fact]
    public void Skipped_assertions_are_neither_passed_nor_failed()
    {
        var result = EvaluateOne("  - status: 500\n    skip: true", Response(status: 200));
        Assert.True(result.Skipped);
        Assert.True(result.Ok);
        Assert.Null(result.Actual);
    }

    [Fact]
    public void Name_falls_back_to_a_generated_description()
    {
        Assert.Equal("status = 201", EvaluateOne("  - status: 201", Response()).Name);
        Assert.Equal("$.order.id exists", EvaluateOne("  - jsonpath: $.order.id", Response(body: OrderJson)).Name);
        Assert.Equal("duration < 800", EvaluateOne("  - duration:\n    lt: 800", Response()).Name);
        Assert.Equal("header etag does not exist", EvaluateOne("  - header: etag\n    exists: false", Response()).Name);
        Assert.Equal("my check", EvaluateOne("  - name: my check\n    status: 200", Response()).Name);
    }

    [Fact]
    public void Secret_expected_values_are_masked_in_results()
    {
        var spec = ParseAssertions("assertions:\n  - header: authorization\n    equals: hunter2")[0];
        var result = Assert.Single(AssertEvaluator.Evaluate(
            [new ResolvedAssert(spec, ExpectedSecret: true)],
            Response(headers: [("authorization", "hunter2")])));

        Assert.True(result.Ok);
        Assert.Equal("***", result.Expected);
    }

    [Fact]
    public void Long_actual_values_are_truncated_for_display()
    {
        var body = new string('x', AssertEvaluator.ActualPreviewLimit + 500);
        var result = EvaluateOne("  - body:\n    contains: x", Response(body: body));
        Assert.True(result.Ok);
        Assert.True(result.Actual!.Length < body.Length);
        Assert.Contains("+500 chars", result.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Results_carry_the_index_of_their_assertion()
    {
        var assertions = ParseAssertions("assertions:\n  - status: 200\n  - header: etag\n  - body:\n    contains: hi");
        var results = AssertEvaluator.Evaluate(
            assertions.Select(a => new ResolvedAssert(a)).ToArray(),
            Response(body: "hi"));

        Assert.Equal<int>([0, 1, 2], results.Select(r => r.Index));
    }

    [Fact]
    public void Summary_counts_passes_failures_and_skips()
    {
        var assertions = ParseAssertions(
            "assertions:\n  - status: 200\n  - status: 500\n  - status: 404\n    skip: true");
        var results = AssertEvaluator.Evaluate(assertions.Select(a => new ResolvedAssert(a)).ToArray(), Response(status: 200));
        var summary = AssertSummary.From(results);

        Assert.False(summary.Ok);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void Summary_of_nothing_is_a_pass()
    {
        Assert.True(AssertSummary.From([]).Ok);
        Assert.Empty(AssertEvaluator.Evaluate([], Response()));
    }

    [Fact]
    public void SkipAll_reports_every_assertion_with_one_reason()
    {
        var assertions = ParseAssertions("assertions:\n  - status: 200\n  - header: etag");
        var results = AssertEvaluator.SkipAll(assertions.Select(a => new ResolvedAssert(a)).ToArray(), "not supported here");

        Assert.All(results, r =>
        {
            Assert.True(r.Skipped);
            Assert.Equal("not supported here", r.Message);
        });
        Assert.Equal(2, AssertSummary.From(results).Skipped);
    }
}
