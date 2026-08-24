using Tap.Workspace;
using Tap.Workspace.Migration;
using Tap.Workspace.Model;

namespace Tap.Tests.Format;

/// <summary>
/// The rename half of a migration is trivial; the ref-rewrite half is where a workspace gets
/// silently broken. Refs are literal relative path strings carrying an extension, written in
/// whatever style the author chose, so these tests pin the rewrite down: right targets, no
/// collateral edits, authored style preserved, and safe to run twice.
/// </summary>
public class WorkspaceMigratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-migrate").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string relativePath)
        => File.ReadAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string relativePath)
        => File.Exists(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private LoadedWorkspace Load() => new WorkspaceLoader().Load(_root);

    private MigrationPlan Plan() => WorkspaceMigrator.Plan(Load(), Read);

    /// <summary>Applies a plan the way the CLI does: content first, then renames.</summary>
    private void Apply(MigrationPlan plan)
    {
        foreach (var c in plan.Changes.Where(c => c.RewrittenContent is not null))
            Write(c.FromRelativePath, c.RewrittenContent!);
        foreach (var c in plan.Changes.Where(c => c.IsRename))
        {
            File.Move(
                Path.Combine(_root, c.FromRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(_root, c.ToRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private void WriteLegacySampleWorkspace()
    {
        Write("tap.md", "---\nkind: workspace\nname: sample\ndefaultEnv: environments/dev.env.md\n---\n");
        Write("environments/dev.env.md", "---\nkind: env\nname: dev\n---\n");
        Write("auth/admin.auth.md", "---\nkind: auth\nname: admin\ntype: bearer\ntoken: t\n---\n");
        Write("collections/demo/_collection.md",
            "---\nkind: collection\nname: demo\nbaseUrl: https://example.test\ndefaultAuth: ../../auth/admin.auth.md\n---\n");
        Write("collections/demo/orders.req.md",
            "---\nkind: request\nname: orders\nauth: ../../auth/admin.auth.md\n---\n\n```http\nGET /orders\n```\n");
        Write("tests/checkout.flow.md",
            "---\nkind: flow\nname: checkout\nsteps:\n  - request: ../collections/demo/orders.req.md\n---\n");
        Write("tests/smoke.test.md",
            "---\nkind: test\nname: smoke\ntests:\n  - flow: ./checkout.flow.md\n  - request: ../collections/demo/orders.req.md\n---\n");
    }

    [Fact]
    public void Every_legacy_file_is_renamed_and_every_ref_follows()
    {
        WriteLegacySampleWorkspace();
        var before = Load();
        Assert.DoesNotContain(before.Errors, e => e.Severity == WorkspaceErrorSeverity.Error);

        var plan = Plan();
        Assert.Empty(plan.Blockers);
        Assert.Equal(7, plan.RenameCount);

        Apply(plan);

        // The manifest gets a new *name*, not just a new extension.
        Assert.True(Exists("workspace.tap"));
        Assert.False(Exists("tap.md"));
        Assert.True(Exists("collections/demo/_collection.tap"));
        Assert.True(Exists("collections/demo/orders.req.tap"));
        Assert.True(Exists("tests/checkout.flow.tap"));

        var after = Load();
        Assert.Empty(after.Errors);
        Assert.Equal(before.Files.Count, after.Files.Count);

        // Every ref still resolves — the thing a rename-only migration would have destroyed.
        var request = Assert.Single(after.Requests);
        Assert.NotNull(after.Resolve(request.Auth, "collections/demo"));

        var collection = Assert.Single(after.Collections);
        Assert.NotNull(after.Resolve(collection.DefaultAuth, "collections/demo"));

        var flow = Assert.Single(after.Flows);
        Assert.NotNull(after.Resolve(flow.Steps[0].Request, "tests"));

        var testSet = Assert.Single(after.TestSets);
        Assert.All(testSet.Tests, t => Assert.NotNull(after.Resolve(t.Target, "tests")));

        Assert.NotNull(after.Resolve(after.Manifest!.DefaultEnv, string.Empty));
    }

    [Fact]
    public void Running_it_twice_is_a_no_op()
    {
        WriteLegacySampleWorkspace();
        Apply(Plan());

        var second = Plan();
        Assert.True(second.IsNoOp);
        Assert.Empty(second.Blockers);
    }

    [Fact]
    public void A_workspace_that_does_not_parse_is_refused()
    {
        WriteLegacySampleWorkspace();
        Write("collections/demo/broken.req.md", "---\nkind: auth\nname: wrong-kind-for-suffix\n---\n");

        var plan = Plan();

        Assert.NotEmpty(plan.Blockers);
        Assert.Contains(plan.Blockers, b => b.Contains("E_KIND_MISMATCH", StringComparison.Ordinal));
    }

    [Fact]
    public void Id_refs_are_left_alone()
    {
        // An id ref is stable across a rename — that is the entire reason to use one, and
        // rewriting it would be a regression.
        Write("tap.md", "---\nkind: workspace\nname: w\n---\n");
        Write("auth/admin.auth.md", "---\nkind: auth\nname: admin\nid: 0192f0a0-0000-7000-8000-000000000001\ntype: bearer\ntoken: t\n---\n");
        Write("collections/demo/_collection.md", "---\nkind: collection\nname: demo\nbaseUrl: https://e.test\n---\n");
        Write("collections/demo/orders.req.md",
            "---\nkind: request\nname: orders\nauth: 'id:0192f0a0-0000-7000-8000-000000000001'\n---\n\n```http\nGET /o\n```\n");

        Apply(Plan());

        Assert.Contains("auth: 'id:0192f0a0-0000-7000-8000-000000000001'", Read("collections/demo/orders.req.tap"), StringComparison.Ordinal);
        Assert.Empty(Load().Errors);
    }

    [Fact]
    public void The_authored_style_of_each_ref_survives()
    {
        Write("tap.md", "---\nkind: workspace\nname: w\n---\n");
        Write("auth/admin.auth.md", "---\nkind: auth\nname: admin\ntype: bearer\ntoken: t\n---\n");
        Write("auth/other.auth.md", "---\nkind: auth\nname: other\ntype: bearer\ntoken: t\n---\n");
        Write("collections/demo/_collection.md", "---\nkind: collection\nname: demo\nbaseUrl: https://e.test\n---\n");
        // Bare, quoted, and dot-prefixed refs all have to come back in the same shape.
        Write("collections/demo/a.req.md", "---\nkind: request\nname: a\nauth: \"../../auth/admin.auth.md\"\n---\n\n```http\nGET /a\n```\n");
        Write("auth/nested.req.md", "---\nkind: request\nname: n\nauth: ./other.auth.md\n---\n\n```http\nGET /n\n```\n");

        Apply(Plan());

        Assert.Contains("auth: \"../../auth/admin.auth.tap\"", Read("collections/demo/a.req.tap"), StringComparison.Ordinal);
        Assert.Contains("auth: ./other.auth.tap", Read("auth/nested.req.tap"), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_outside_the_ref_value_is_touched()
    {
        // A migration diff has to be reviewable. Body text, comments, key order, and a `request:`
        // that is part of the prose rather than the frontmatter must all come through unchanged.
        Write("tap.md", "---\nkind: workspace\nname: w\n---\n");
        Write("auth/admin.auth.md", "---\nkind: auth\nname: admin\ntype: bearer\ntoken: t\n---\n");
        Write("collections/demo/_collection.md", "---\nkind: collection\nname: demo\nbaseUrl: https://e.test\n---\n");
        var source = "---\nkind: request\nname: a\n# a comment about auth\nauth: ../../auth/admin.auth.md  # inline note\ntags: [x]\n---\n\n"
            + "Prose mentioning auth: ../../auth/admin.auth.md which is not frontmatter.\n\n```http\nGET /a\n```\n";
        Write("collections/demo/a.req.md", source);

        Apply(Plan());
        var after = Read("collections/demo/a.req.tap");

        Assert.Contains("auth: ../../auth/admin.auth.tap  # inline note", after, StringComparison.Ordinal);
        Assert.Contains("# a comment about auth\n", after, StringComparison.Ordinal);
        Assert.Contains("tags: [x]", after, StringComparison.Ordinal);
        // The body mention is prose, not a ref — it must not be rewritten.
        Assert.Contains("Prose mentioning auth: ../../auth/admin.auth.md which is not frontmatter.", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Crlf_line_endings_survive()
    {
        // Splitting on \n and rejoining would rewrite every line of a CRLF workspace, drowning
        // the real change in a whole-file diff.
        Write("tap.md", "---\r\nkind: workspace\r\nname: w\r\n---\r\n");
        Write("auth/admin.auth.md", "---\r\nkind: auth\r\nname: admin\r\ntype: bearer\r\ntoken: t\r\n---\r\n");
        Write("collections/demo/_collection.md", "---\r\nkind: collection\r\nname: demo\r\nbaseUrl: https://e.test\r\n---\r\n");
        Write("collections/demo/a.req.md",
            "---\r\nkind: request\r\nname: a\r\nauth: ../../auth/admin.auth.md\r\n---\r\n\r\n```http\r\nGET /a\r\n```\r\n");

        Apply(Plan());
        var after = Read("collections/demo/a.req.tap");

        Assert.Contains("auth: ../../auth/admin.auth.tap\r\n", after, StringComparison.Ordinal);
        Assert.DoesNotContain("auth: ../../auth/admin.auth.tap\n\n", after, StringComparison.Ordinal);
        Assert.Empty(Load().Errors);
    }

    [Fact]
    public void A_canonical_file_pointing_at_a_legacy_one_is_rewritten_without_being_renamed()
    {
        // Partially-migrated workspaces are the normal case if someone renamed a few files by
        // hand. The already-canonical file still needs its ref fixed.
        Write("workspace.tap", "---\nkind: workspace\nname: w\n---\n");
        Write("auth/admin.auth.md", "---\nkind: auth\nname: admin\ntype: bearer\ntoken: t\n---\n");
        Write("collections/demo/_collection.tap", "---\nkind: collection\nname: demo\nbaseUrl: https://e.test\n---\n");
        Write("collections/demo/a.req.tap", "---\nkind: request\nname: a\nauth: ../../auth/admin.auth.md\n---\n\n```http\nGET /a\n```\n");

        var plan = Plan();
        var edit = Assert.Single(plan.Changes, c => !c.IsRename);
        Assert.Equal("collections/demo/a.req.tap", edit.FromRelativePath);

        Apply(plan);

        Assert.Contains("auth: ../../auth/admin.auth.tap", Read("collections/demo/a.req.tap"), StringComparison.Ordinal);
        Assert.Empty(Load().Errors);
    }

    [Fact]
    public void The_plan_reports_what_it_will_change()
    {
        // --dry-run is only useful if the plan carries reviewable detail.
        WriteLegacySampleWorkspace();
        var plan = Plan();

        var request = Assert.Single(plan.Changes, c => c.FromRelativePath == "collections/demo/orders.req.md");
        Assert.Equal("collections/demo/orders.req.tap", request.ToRelativePath);

        var rewrite = Assert.Single(request.Refs);
        Assert.Equal("auth", rewrite.Key);
        Assert.Equal("../../auth/admin.auth.md", rewrite.From);
        Assert.Equal("../../auth/admin.auth.tap", rewrite.To);
        Assert.Equal(4, rewrite.Line);
    }
}
