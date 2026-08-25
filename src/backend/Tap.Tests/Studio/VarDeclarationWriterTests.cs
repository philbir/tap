using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Studio;

/// <summary>
/// Declaring one variable into a file the editor doing it never opened.
///
/// <para>The property under test throughout is <b>restraint</b>: the entry appears, and nothing
/// else about the file moves. That matters more here than in the spec emitters, because the
/// caller is a field in some other editor — a header value in a request, say, being converted
/// to a workspace-scoped variable. Rewriting <c>workspace.tap</c> beyond the one key would be
/// collateral damage from an action the user never framed as editing that file.</para>
/// </summary>
public sealed class VarDeclarationWriterTests
{
    private const string Manifest = """
        ---
        kind: workspace
        name: Demo
        defaultEnv: env/dev.env.tap
        vars:
          baseUrl: https://api.example.com
          apiToken:
            default: '{{file:apiToken}}'
            secret: true
            description: The caller's token.
            required: true
        ---

        Notes about this workspace.
        """;

    [Fact]
    public void A_plain_value_is_declared_as_a_bare_scalar()
    {
        var result = VarDeclarationWriter.Apply(Manifest, "workspace.tap", "region", "westeurope", secret: false);

        var vars = VarsOf(result);
        Assert.Equal("westeurope", vars["region"].Default);
        Assert.False(vars["region"].Secret);
    }

    [Fact]
    public void A_secret_is_declared_as_default_plus_secret_true()
    {
        var result = VarDeclarationWriter.Apply(
            Manifest, "workspace.tap", "stripeKey", "{{file:stripeKey}}", secret: true);

        var declared = VarsOf(result)["stripeKey"];
        Assert.Equal("{{file:stripeKey}}", declared.Default);
        Assert.True(declared.Secret);
    }

    /// <summary>
    /// A value opening with <c>{</c> is the whole point of the secret path — it is a
    /// <c>{{provider:key}}</c> reference — and YAML reads an unquoted one as a flow mapping.
    /// </summary>
    [Fact]
    public void A_reference_value_survives_the_round_trip_verbatim()
    {
        var result = VarDeclarationWriter.Apply(
            Manifest, "workspace.tap", "stripeKey", "{{kv-prod:stripe-live-key}}", secret: true);

        Assert.Contains("'{{kv-prod:stripe-live-key}}'", result, StringComparison.Ordinal);
        Assert.Equal("{{kv-prod:stripe-live-key}}", VarsOf(result)["stripeKey"].Default);
    }

    [Fact]
    public void The_other_variables_keep_their_rich_spec_fields()
    {
        var result = VarDeclarationWriter.Apply(Manifest, "workspace.tap", "region", "westeurope", secret: false);

        // The spec DTOs flatten vars to map<string,string> + a secrets list, so a round-trip
        // through them would silently drop these three. That is why this writer patches nodes.
        var untouched = VarsOf(result)["apiToken"];
        Assert.Equal("{{file:apiToken}}", untouched.Default);
        Assert.True(untouched.Secret);
        Assert.Equal("The caller's token.", untouched.Description);
        Assert.True(untouched.Required);
    }

    [Fact]
    public void Unrelated_frontmatter_keys_and_the_body_are_left_alone()
    {
        var result = VarDeclarationWriter.Apply(Manifest, "workspace.tap", "region", "westeurope", secret: false);

        var manifest = Assert.IsType<WorkspaceManifestFile>(FileParser.Parse("workspace.tap", result));
        Assert.Equal("Demo", manifest.Name);
        Assert.Equal("env/dev.env.tap", manifest.DefaultEnv?.RelativePath);
        Assert.Contains("Notes about this workspace.", manifest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Declaring_an_existing_name_replaces_it_rather_than_duplicating_it()
    {
        var result = VarDeclarationWriter.Apply(
            Manifest, "workspace.tap", "baseUrl", "https://staging.example.com", secret: false);

        Assert.Equal("https://staging.example.com", VarsOf(result)["baseUrl"].Default);
        // A duplicate key would make the file fail to parse at all, so parsing is the assertion.
        Assert.Single(VarsOf(result), v => v.Key == "baseUrl");
    }

    /// <summary>Re-declaring a plain variable as a secret has to drop the literal, not sit
    /// beside it — leaving both would keep the cleartext in the file the flag says it left.</summary>
    [Fact]
    public void Re_declaring_a_plain_variable_as_a_secret_replaces_the_literal()
    {
        var result = VarDeclarationWriter.Apply(
            Manifest, "workspace.tap", "baseUrl", "{{file:baseUrl}}", secret: true);

        Assert.DoesNotContain("https://api.example.com", result, StringComparison.Ordinal);
        Assert.True(VarsOf(result)["baseUrl"].Secret);
    }

    [Fact]
    public void A_file_with_no_vars_block_gets_one()
    {
        const string collection = """
            ---
            kind: collection
            name: Demo
            baseUrl: https://api.example.com
            ---
            """;

        var result = VarDeclarationWriter.Apply(collection, "demo/_collection.tap", "region", "westeurope", secret: false);

        var parsed = Assert.IsType<CollectionFile>(FileParser.Parse("demo/_collection.tap", result));
        Assert.Equal("westeurope", parsed.Vars["region"].Default);
        Assert.Equal("https://api.example.com", parsed.BaseUrl);
    }

    /// <summary>A request's <c>http</c> block lives in the body, and the body is passed through
    /// whole — but the fence is the one part of a request file that must not shift.</summary>
    [Fact]
    public void A_requests_http_block_survives()
    {
        const string request = """
            ---
            kind: request
            name: Get user
            ---

            ```http
            GET {{baseUrl}}/users/1
            Accept: application/json
            ```
            """;

        var result = VarDeclarationWriter.Apply(request, "demo/get-user.req.tap", "userId", "1", secret: false);

        var parsed = Assert.IsType<RequestFile>(FileParser.Parse("demo/get-user.req.tap", result));
        Assert.Equal("1", parsed.Vars["userId"].Default);
        Assert.Contains("GET {{baseUrl}}/users/1", parsed.HttpBlock, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", parsed.HttpBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole file, byte for byte, with one line more than it started with.
    ///
    /// <para>Asserting on the exact text rather than on the parse is the point: everything else
    /// here would still pass if the writer quietly re-laid-out the body, and a blank line
    /// appearing after the fence is a diff hunk in a file the user never opened.</para>
    /// </summary>
    [Fact]
    public void Nothing_but_the_declared_line_moves()
    {
        const string before = "---\nkind: collection\nname: Demo\nvars:\n  a: one\n---\n# Demo\n\nProse.\n";

        var after = VarDeclarationWriter.Apply(before, "demo/_collection.tap", "b", "two", secret: false);

        Assert.Equal("---\nkind: collection\nname: Demo\nvars:\n  a: one\n  b: two\n---\n# Demo\n\nProse.\n", after);
    }

    /// <summary>Re-declaring the same variable with the same value has to be a no-op on the
    /// text, or a second conversion would churn the file for nothing.</summary>
    [Fact]
    public void Re_declaring_an_identical_entry_leaves_the_file_byte_identical()
    {
        const string before = "---\nkind: collection\nname: Demo\nvars:\n  a: one\n---\n# Demo\n";

        var once = VarDeclarationWriter.Apply(before, "demo/_collection.tap", "a", "one", secret: false);

        Assert.Equal(before, once);
    }

    /// <summary>
    /// The reason this writer splices text instead of re-serializing the parsed frontmatter.
    /// YamlDotNet's representation model carries no comments, so a node round-trip deletes every
    /// one — in a file the user is not even editing.
    /// </summary>
    [Fact]
    public void Comments_survive()
    {
        const string before = """
            ---
            kind: collection
            # what this collection is for
            name: Demo
            vars:
              # why this one exists
              a: one
            ---
            # Demo
            """;

        var after = VarDeclarationWriter.Apply(before, "demo/_collection.tap", "b", "two", secret: false);

        Assert.Contains("# what this collection is for", after, StringComparison.Ordinal);
        Assert.Contains("# why this one exists", after, StringComparison.Ordinal);
        Assert.Equal("two", CollectionVarsOf(after)["b"].Default);
        Assert.Equal("one", CollectionVarsOf(after)["a"].Default);
    }

    /// <summary>Four-space blocks are as valid as two-space ones, and the entry has to join the
    /// block rather than start a differently-indented one beside it.</summary>
    [Fact]
    public void The_blocks_own_indentation_is_matched()
    {
        const string before = "---\nkind: collection\nname: Demo\nvars:\n    a: one\n---\n";

        var after = VarDeclarationWriter.Apply(before, "demo/_collection.tap", "b", "two", secret: false);

        Assert.Contains("\n    b: two", after, StringComparison.Ordinal);
        Assert.Equal("two", CollectionVarsOf(after)["b"].Default);
    }

    /// <summary>
    /// Flow style is a shape the line scan does not read. The point is not that it is handled
    /// gracefully but that it is handled <i>correctly</i>: the acceptance test rejects the splice
    /// and the node patch takes over, so the entry still lands.
    /// </summary>
    [Fact]
    public void A_flow_style_vars_block_falls_back_and_still_lands_the_entry()
    {
        const string before = "---\nkind: collection\nname: Demo\nvars: {a: one}\n---\n";

        var after = VarDeclarationWriter.Apply(before, "demo/_collection.tap", "b", "two", secret: false);

        var vars = CollectionVarsOf(after);
        Assert.Equal("two", vars["b"].Default);
        Assert.Equal("one", vars["a"].Default);
    }

    [Fact]
    public void A_file_without_frontmatter_is_refused_rather_than_rewritten()
    {
        var ex = Assert.Throws<WorkspaceParseException>(
            () => VarDeclarationWriter.Apply("just some text\n", "notes.tap", "region", "westeurope", secret: false));

        Assert.Equal(WorkspaceErrorCode.E_FRONTMATTER_MISSING, ex.Error.Code);
    }

    private static IReadOnlyDictionary<string, VarSpec> CollectionVarsOf(string content)
        => Assert.IsType<CollectionFile>(FileParser.Parse("demo/_collection.tap", content)).Vars;

    private static IReadOnlyDictionary<string, VarSpec> VarsOf(string content)
        => FileParser.Parse("workspace.tap", content) switch
        {
            WorkspaceManifestFile m => m.Vars,
            _ => throw new InvalidOperationException("expected a manifest"),
        };
}
