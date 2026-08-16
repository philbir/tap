using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// The <c>.http</c> parser, exercised against the shapes the five tools that share this format
/// actually emit. The format is a de-facto standard with no specification, so these fixtures are
/// the specification: what Visual Studio scaffolds, what REST Client and JetBrains documentation
/// shows, and the constructs that belong to one tool and must degrade rather than break.
/// </summary>
public class HttpFileParserTests
{
    private static HttpFileParseResult Parse(string content, string path = "collections/demo/orders.http")
        => HttpFileParser.Parse(path, content);

    [Fact]
    public void The_visual_studio_scaffold_parses()
    {
        // Byte-for-byte what `dotnet new webapi` drops into a new project — the single most
        // important file to get right, because it is the one users already have.
        var result = Parse("""
            @HostAddress = http://localhost:5000

            GET {{HostAddress}}/weatherforecast/
            Accept: application/json

            ###
            """);

        Assert.Empty(result.Errors);
        var request = Assert.Single(result.Requests);
        Assert.Equal("collections/demo/orders.http#get-weatherforecast", request.RelativePath);
        Assert.Contains("GET {{HostAddress}}/weatherforecast/", request.HttpBlock, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", request.HttpBlock, StringComparison.Ordinal);

        // The file variable is carried as a *portable* variable — the cascade resolves it, but
        // below every Tap scope, because it is what this file means outside Tap.
        Assert.Equal("http://localhost:5000", request.PortableVars["HostAddress"].Default);
        Assert.Empty(request.Vars);
    }

    [Fact]
    public void One_file_becomes_many_requests()
    {
        var result = Parse("""
            ### Get order
            GET /orders/1

            ### Create order
            POST /orders
            Content-Type: application/json

            {"sku":"ABC"}
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Requests.Count);
        Assert.Equal(["Get order", "Create order"], result.Requests.Select(r => r.Name!).ToArray());
        Assert.Equal(
            ["collections/demo/orders.http#get-order", "collections/demo/orders.http#create-order"],
            result.Requests.Select(r => r.RelativePath).ToArray());

        // The body survives verbatim, including the blank line that separates it from headers.
        Assert.Contains("""{"sku":"ABC"}""", result.Requests[1].HttpBlock, StringComparison.Ordinal);
    }

    [Theory]
    // Explicit @name wins over the separator's title. The directive sits inside the block it
    // names, after the separator — putting it above would attach it to the previous request.
    [InlineData("### Some title\n# @name login\nPOST /sessions", "login")]
    // Then the ### title.
    [InlineData("### Some title\nPOST /sessions", "Some title")]
    // Then a slug derived from method + last meaningful path segment.
    [InlineData("POST /api/v1/sessions", "post-sessions")]
    public void Names_come_from_the_first_source_that_has_one(string content, string expected)
    {
        var request = Assert.Single(Parse(content).Requests);
        Assert.Equal(expected, request.Name);
    }

    [Fact]
    public void A_derived_name_skips_path_parameters()
    {
        // "get-orders" is useful; "get-{{orderId}}" is not.
        var request = Assert.Single(Parse("GET /orders/{{orderId}}").Requests);
        Assert.Equal("get-orders", request.Name);
    }

    [Fact]
    public void Requests_that_would_share_a_name_are_disambiguated()
    {
        // Names address requests from the CLI, so a duplicate would make one unreachable.
        var result = Parse("""
            GET /orders

            ###
            GET /orders
            """);

        Assert.Equal(2, result.Requests.Count);
        Assert.Distinct(result.Requests.Select(r => r.Name!).ToArray());
        Assert.Distinct(result.Requests.Select(r => r.RelativePath).ToArray());
    }

    [Fact]
    public void A_wrapped_query_string_folds_into_the_url()
    {
        var request = Assert.Single(Parse("""
            GET https://api.example.test/search
              ?q=widgets
              &page=2
            Accept: application/json
            """).Requests);

        Assert.Contains("GET https://api.example.test/search?q=widgets&page=2", request.HttpBlock, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", request.HttpBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_comment_markers_are_accepted()
    {
        // '#' is REST Client / VS; '//' is JetBrains. Files in the wild mix them.
        var result = Parse("""
            # a hash comment
            // a slash comment
            // @name mixed
            GET /things
            """);

        Assert.Empty(result.Errors);
        var request = Assert.Single(result.Requests);
        Assert.Equal("mixed", request.Name);
        Assert.DoesNotContain("comment", request.HttpBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void The_jetbrains_timeout_directive_becomes_a_transport_setting()
    {
        var request = Assert.Single(Parse("# @timeout 30\nGET /slow").Requests);
        Assert.Equal(30_000, request.Transport.TimeoutMs);
    }

    [Fact]
    public void A_body_include_is_passed_through_untouched()
    {
        // `< ./file` is resolved later by the same binary-ref machinery .tap requests use.
        var request = Assert.Single(Parse("""
            POST /uploads
            Content-Type: application/json

            < ./payload.json
            """).Requests);

        Assert.Contains("< ./payload.json", request.HttpBlock, StringComparison.Ordinal);
    }

    // ---- Constructs belonging to other tools -------------------------------------------------

    [Theory]
    [InlineData("> {% client.test(\"ok\", function() {}); %}", "flow")]
    [InlineData("?? status == 200", "@tap-assert")]
    [InlineData(">> ./response.json", "--output")]
    [InlineData("run ./other.http", "flow")]
    public void Foreign_constructs_warn_and_are_skipped(string construct, string expectedGuidance)
    {
        var result = Parse($"GET /things\n\n{construct}\n");

        var request = Assert.Single(result.Requests);
        var warning = Assert.Single(result.Errors);
        Assert.Equal(WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT, warning.Code);
        Assert.Equal(WorkspaceErrorSeverity.Warning, warning.Severity);
        // A warning that only says "unsupported" leaves the user stuck; each must name the
        // Tap equivalent.
        Assert.Contains(expectedGuidance, warning.Message, StringComparison.OrdinalIgnoreCase);
        // And the construct must not reach the wire.
        Assert.DoesNotContain(construct, request.HttpBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_chaining_warns_and_points_at_flows()
    {
        var result = Parse("""
            GET /orders/{{login.response.body.$.id}}
            """);

        var warning = Assert.Single(result.Errors);
        Assert.Equal(WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT, warning.Code);
        Assert.Contains("flow", warning.Message, StringComparison.OrdinalIgnoreCase);
        // The line is KEPT: the token surfaces as an unknown variable at render, which is a
        // clearer failure than a request that silently sends the literal text.
        var request = Assert.Single(result.Requests);
        Assert.Contains("login.response.body", request.HttpBlock, StringComparison.Ordinal);
    }

    // ---- Error isolation ---------------------------------------------------------------------

    [Fact]
    public void A_malformed_request_drops_itself_and_the_rest_of_the_file_survives()
    {
        // Per-file granularity would lose nineteen good requests over one bad one.
        var result = Parse("""
            ### good one
            GET /ok

            ### broken
            NOTAREQUESTLINE

            ### good two
            GET /also-ok
            """);

        Assert.Equal(2, result.Requests.Count);
        Assert.Equal(["good one", "good two"], result.Requests.Select(r => r.Name!).ToArray());

        // The error carries where to put an editor marker. HttpBlockParser can't know that (a
        // fence knows only its own offset), so the .http parser attaches it.
        var error = Assert.Single(result.Errors, e => e.Code == WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX);
        Assert.Equal("collections/demo/orders.http", error.RelativePath);
        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void A_file_with_no_requests_says_so()
    {
        var result = Parse("# just a comment\n@host = localhost\n");

        Assert.Empty(result.Requests);
        Assert.Contains(result.Errors, e => e.Code == WorkspaceErrorCode.E_NO_REQUEST_BLOCK);
    }

    [Fact]
    public void Trailing_and_leading_separators_do_not_produce_empty_requests()
    {
        // Scaffolds and hand-edited files are full of stray '###' lines.
        var result = Parse("""
            ###

            GET /things

            ###

            ###
            """);

        Assert.Single(result.Requests);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void File_variables_are_visible_to_requests_declared_above_them()
    {
        // Files commonly put the @host block at the bottom; a single-pass parser would miss it.
        var result = Parse("""
            GET {{host}}/things

            ###
            @host = https://api.example.test
            """);

        var request = Assert.Single(result.Requests);
        Assert.Equal("https://api.example.test", request.PortableVars["host"].Default);
    }
}
