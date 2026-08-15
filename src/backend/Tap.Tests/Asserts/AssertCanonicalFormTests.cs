using static Tap.Tests.Asserts.AssertTestData;

namespace Tap.Tests.Asserts;

/// <summary>
/// Executable documentation of the canonical <c>assertions:</c> block. Every form in §5.5
/// of <c>docs/workspace-format.md</c> appears here exactly as the emitter writes it, so the
/// doc, the samples, and the editor's Source tab can't drift apart unnoticed.
/// </summary>
public class AssertCanonicalFormTests
{
    private const string Canonical = """
        assertions:
        - status: 201
        - status: 2xx
        - header: etag
        - header: content-type
          contains: application/json
        - jsonpath: $.order.customer.email
          equals: '{{user.email}}'
        - jsonpath: $.order.lines
          count: 3
        - jsonpath: $.error
          exists: false
        - jsonpath: $.total
          type: number
        - xpath: /order/total
          gt: 100
        - body:
            contains: Thank you
        - regex: '"id":\s*"ord-\d+"'
        - duration:
            lt: 800
        - status:
            in: [200, 201, 204]
        - status:
            between: [200, 299]
        - name: order id
          jsonpath: $.order.id
          matches: ^ord-\d+$
          skip: true
        """;

    [Fact]
    public void Canonical_block_emits_itself()
    {
        var emitted = Emit(ParseAssertions(Canonical));

        var start = emitted.IndexOf("assertions:", StringComparison.Ordinal);
        var end = emitted.IndexOf("\n---", start, StringComparison.Ordinal);
        var block = emitted[start..end];

        Assert.Equal(Canonical.ReplaceLineEndings("\n"), block.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_selector_that_needs_quoting_still_gets_it()
    {
        // Plain style is preferred for path expressions, but the emitter must still fall back
        // when the value can't survive it — otherwise the file wouldn't parse back.
        var emitted = Emit(ParseAssertions("assertions:\n  - header: 'x: weird'\n    equals: v"));
        Assert.Contains("'x: weird'", emitted, StringComparison.Ordinal);

        var reparsed = ((Tap.Workspace.Model.RequestFile)Tap.Workspace.Parsing.FileParser.Parse(RequestPath, emitted)).Assertions;
        Assert.Equal("x: weird", reparsed[0].Selector);
    }
}
