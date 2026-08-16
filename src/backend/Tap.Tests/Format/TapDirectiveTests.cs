using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Rendering;

namespace Tap.Tests.Format;

/// <summary>
/// The <c># @tap-*</c> directives — Tap's features carried in comments so a file keeps working
/// unchanged in Visual Studio, REST Client, JetBrains, httpyac, and Kulala. The point of the
/// design is that a Tap-enhanced .http file is still an ordinary .http file, so these tests care
/// as much about what stays inert as about what takes effect.
/// </summary>
public class TapDirectiveTests
{
    private static RequestFile ParseOne(string content)
    {
        var result = HttpFileParser.Parse("collections/demo/orders.http", content);
        Assert.DoesNotContain(result.Errors, e => e.Severity == WorkspaceErrorSeverity.Error);
        return Assert.Single(result.Requests);
    }

    [Fact]
    public void Directives_do_not_leak_into_the_request()
    {
        // They are comments; the wire must not see them.
        var request = ParseOne("""
            # @tap-collection billing
            # @tap-auth ../../auth/admin.auth.tap
            # @tap-tag smoke, orders
            # @tap-assert status == 200
            GET /orders
            """);

        Assert.DoesNotContain("@tap-", request.HttpBlock, StringComparison.Ordinal);
        Assert.Equal("GET /orders", request.HttpBlock);
    }

    [Fact]
    public void Collection_auth_protocol_and_tags_are_read()
    {
        var request = ParseOne("""
            # @tap-collection billing
            # @tap-auth ../../auth/admin.auth.tap
            # @tap-protocol websocket
            # @tap-tag smoke, orders
            GET /orders
            """);

        Assert.Equal("billing", request.CollectionRef);
        Assert.Equal("../../auth/admin.auth.tap", request.Auth?.RelativePath);
        Assert.Equal(RequestProtocol.WebSocket, request.Protocol);
        Assert.Equal(["smoke", "orders"], request.Tags);
    }

    [Fact]
    public void An_auth_ref_may_be_an_id()
    {
        var request = ParseOne("# @tap-auth id:0192f0a0-0000-7000-8000-000000000001\nGET /orders");
        Assert.Equal("0192f0a0-0000-7000-8000-000000000001", request.Auth?.Id);
        Assert.Null(request.Auth?.RelativePath);
    }

    [Fact]
    public void Both_comment_markers_carry_directives()
    {
        var request = ParseOne("// @tap-tag jetbrains\nGET /orders");
        Assert.Equal(["jetbrains"], request.Tags);
    }

    // ---- Scope ------------------------------------------------------------------------------

    [Fact]
    public void File_level_directives_apply_to_every_request()
    {
        var result = HttpFileParser.Parse("orders.http", """
            # @tap-collection billing
            # @tap-tag api

            ###
            GET /a

            ###
            GET /b
            """);

        Assert.Equal(2, result.Requests.Count);
        Assert.All(result.Requests, r =>
        {
            Assert.Equal("billing", r.CollectionRef);
            Assert.Equal(["api"], r.Tags);
        });
    }

    [Fact]
    public void A_request_overrides_the_file_default_but_accumulates_lists()
    {
        // Scalars are overrides; assertions and tags accumulate. A file-level assertion applying
        // to every request is the reason to write one, so a request adding its own must not
        // silently drop it.
        var result = HttpFileParser.Parse("orders.http", """
            # @tap-collection billing
            # @tap-tag api
            # @tap-assert status == 200

            ###
            # @tap-collection orders
            # @tap-tag slow
            # @tap-assert duration < 5000
            GET /a
            """);

        var request = Assert.Single(result.Requests);
        Assert.Equal("orders", request.CollectionRef);
        Assert.Equal(["api", "slow"], request.Tags);
        Assert.Equal(2, request.Assertions.Count);
    }

    // ---- Assertions --------------------------------------------------------------------------

    [Theory]
    [InlineData("status == 200", "status = 200")]
    [InlineData("status 200", "status = 200")]                       // bare value means equals
    [InlineData("status 2xx", "status = 2xx")]                       // wildcards pass through
    [InlineData("duration < 2000", "duration < 2000")]
    [InlineData("header content-type contains json", "header content-type contains json")]
    [InlineData("header etag", "header etag exists")]                // nothing means exists
    [InlineData("body $.id exists", "$.id exists")]
    [InlineData("$.items count 3", "$.items count = 3")]
    [InlineData("body contains hello world", "body contains hello world")]  // value keeps its spaces
    public void The_expression_form_produces_the_same_spec_as_the_yaml_form(string expression, string described)
    {
        Assert.True(AssertExpression.TryParse(expression, out var spec, out var error), error);
        Assert.Equal(described, spec.Describe());
    }

    [Fact]
    public void Assertions_reach_the_request()
    {
        var request = ParseOne("""
            # @tap-assert status == 200
            # @tap-assert header content-type contains json
            GET /orders
            """);

        Assert.Equal(2, request.Assertions.Count);
        Assert.Equal(AssertSource.Status, request.Assertions[0].Source);
        Assert.Equal(AssertSource.Header, request.Assertions[1].Source);
        Assert.Equal("content-type", request.Assertions[1].Selector);
    }

    [Fact]
    public void A_malformed_assertion_reports_its_line_number()
    {
        var result = HttpFileParser.Parse("orders.http", """
            GET /orders

            ###
            # @tap-assert nonsense here
            GET /other
            """);

        var error = Assert.Single(result.Errors, e => e.Code == WorkspaceErrorCode.E_ASSERT_INVALID);
        Assert.Equal(4, error.Line);
        Assert.Contains("not an extractor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_that_cannot_apply_is_rejected_the_same_way_yaml_rejects_it()
    {
        // Runs through AssertSpec.Validate, the same funnel the YAML parser uses — so the
        // expression form inherits every rule rather than re-stating any of them.
        Assert.False(AssertExpression.TryParse("status exists", out _, out var statusExists));
        Assert.Contains("always present", statusExists, StringComparison.Ordinal);

        Assert.False(AssertExpression.TryParse("duration matches foo", out _, out var durationMatches));
        Assert.Contains("String operators do not apply to duration", durationMatches, StringComparison.Ordinal);
    }

    // ---- Lint -------------------------------------------------------------------------------

    [Fact]
    public void A_typod_directive_warns_rather_than_being_silently_inert()
    {
        // Silence is the worst outcome: the user believes the assertion is running.
        var result = HttpFileParser.Parse("orders.http", "# @tap-asert status == 200\nGET /orders");

        var warning = Assert.Single(result.Errors);
        Assert.Equal(WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT, warning.Code);
        Assert.Contains("@tap-assert", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directive_with_no_value_warns()
    {
        var result = HttpFileParser.Parse("orders.http", "# @tap-collection\nGET /orders");
        Assert.Contains(result.Errors, e => e.Message.Contains("needs a collection slug", StringComparison.Ordinal));
    }

    // ---- Attribution ------------------------------------------------------------------------

    [Fact]
    public void A_file_outside_collections_can_claim_one()
    {
        // The whole appeal of a portable .http file is living next to the code it exercises.
        var root = Directory.CreateTempSubdirectory("tap-directive").FullName;
        try
        {
            void Write(string rel, string content)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
            Write("collections/billing/_collection.tap",
                "---\nkind: collection\nname: billing\nbaseUrl: https://billing.test\n---\n");
            // Deliberately NOT under collections/.
            Write("src/Billing.Api/api.http", "# @tap-collection billing\n### Ping\nGET /ping\n");

            var ws = new WorkspaceLoader().Load(root);
            var request = Assert.Single(ws.Requests);

            // Attribution by location would find nothing here.
            Assert.Null(CollectionLocator.ForFile(ws, request.RelativePath));

            var owner = CollectionLocator.ForRequest(ws, request);
            Assert.NotNull(owner);
            Assert.Equal("billing", owner.Name);

            // And the path-based overload agrees, so every caller sees the same answer.
            Assert.Equal(owner.RelativePath, CollectionLocator.ForRequestPath(ws, request.RelativePath)?.RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_named_secret_marks_the_variable_so_the_redactor_covers_it()
    {
        var request = ParseOne("""
            @apiKey = super-secret-value
            # @tap-secret apiKey
            GET /orders
            X-Api-Key: {{apiKey}}
            """);

        Assert.True(request.PortableVars["apiKey"].Secret);
        Assert.Equal("super-secret-value", request.PortableVars["apiKey"].Default);
    }
}
