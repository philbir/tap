using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// The suffix table is the gate that decides what is a Tap file at all, and since 0.7.0 it has
/// to answer for two extension families at once — canonical <c>.tap</c> and legacy <c>.md</c>.
/// Getting it wrong in either direction is expensive: too strict and a user's workspace stops
/// loading, too loose and ordinary repo files get parsed as Tap files.
/// </summary>
public class KindResolverTests
{
    [Theory]
    [InlineData("orders.req.tap", WorkspaceKind.Request)]
    [InlineData("admin.auth.tap", WorkspaceKind.Auth)]
    [InlineData("dev.env.tap", WorkspaceKind.Env)]
    [InlineData("checkout.flow.tap", WorkspaceKind.Flow)]
    [InlineData("smoke.test.tap", WorkspaceKind.Test)]
    [InlineData("_collection.tap", WorkspaceKind.Collection)]
    [InlineData("workspace.tap", WorkspaceKind.Workspace)]
    public void The_canonical_family_resolves(string fileName, WorkspaceKind expected)
    {
        Assert.Equal(new KindResolver.Match(expected, IsLegacy: false), KindResolver.Resolve(fileName));
    }

    [Theory]
    [InlineData("orders.req.md", WorkspaceKind.Request)]
    [InlineData("admin.auth.md", WorkspaceKind.Auth)]
    [InlineData("dev.env.md", WorkspaceKind.Env)]
    [InlineData("checkout.flow.md", WorkspaceKind.Flow)]
    [InlineData("smoke.test.md", WorkspaceKind.Test)]
    [InlineData("_collection.md", WorkspaceKind.Collection)]
    [InlineData("tap.md", WorkspaceKind.Workspace)]
    public void The_legacy_family_resolves_to_the_same_kinds_and_is_flagged(string fileName, WorkspaceKind expected)
    {
        Assert.Equal(new KindResolver.Match(expected, IsLegacy: true), KindResolver.Resolve(fileName));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.tap")]
    [InlineData("CHANGELOG.md")]
    // The two whole-name kinds match exactly, so a file that merely ends with the same letters
    // stays an ordinary file. Suffix-matching these would swallow real filenames.
    [InlineData("my_collection.tap")]
    [InlineData("myworkspace.tap")]
    // ".test.tap" needs its leading dot — "latest.tap" is not a test set.
    [InlineData("latest.tap")]
    public void Everything_else_is_not_a_workspace_file(string fileName)
    {
        Assert.Null(KindResolver.Resolve(fileName));
        Assert.Null(KindResolver.FromFileName(fileName));
    }

    [Theory]
    [InlineData("orders.req.md", "orders.req.tap")]
    [InlineData("tap.md", "workspace.tap")]
    [InlineData("_collection.md", "_collection.tap")]
    [InlineData("01-get.req.md", "01-get.req.tap")]
    // A stem containing dots keeps every one of them — only the trailing extension moves.
    [InlineData("v2.orders.req.md", "v2.orders.req.tap")]
    public void Legacy_names_migrate_to_their_canonical_spelling(string legacy, string canonical)
    {
        Assert.Equal(canonical, KindResolver.ToCanonicalFileName(legacy));
    }

    [Theory]
    [InlineData("orders.req.tap")]
    [InlineData("workspace.tap")]
    [InlineData("README.md")]
    public void Migration_is_idempotent_and_leaves_non_workspace_files_alone(string fileName)
    {
        // What makes `tap-studio migrate` safe to run twice.
        Assert.Equal(fileName, KindResolver.ToCanonicalFileName(fileName));
    }

    [Fact]
    public void Canonical_and_legacy_names_are_inverses_of_each_other()
    {
        foreach (var legacy in new[] { "orders.req.md", "admin.auth.md", "dev.env.md", "checkout.flow.md", "smoke.test.md", "_collection.md", "tap.md" })
        {
            var canonical = KindResolver.ToCanonicalFileName(legacy);
            Assert.Equal(legacy, KindResolver.ToLegacyFileName(canonical));
        }
    }

    [Fact]
    public void Suffixes_are_matched_case_insensitively()
    {
        Assert.Equal(WorkspaceKind.Request, KindResolver.FromFileName("Orders.REQ.Tap"));
        Assert.Equal(WorkspaceKind.Workspace, KindResolver.FromFileName("WORKSPACE.TAP"));
    }

    [Fact]
    public void New_files_are_named_canonically()
    {
        Assert.Equal("orders.req.tap", KindResolver.FileNameFor(WorkspaceKind.Request, "orders"));
        Assert.Equal("dev.env.tap", KindResolver.FileNameFor(WorkspaceKind.Env, "dev"));

        // The whole-name kinds ignore the slug — their identity is the folder they sit in.
        Assert.Equal("_collection.tap", KindResolver.FileNameFor(WorkspaceKind.Collection, "ignored"));
        Assert.Equal("workspace.tap", KindResolver.FileNameFor(WorkspaceKind.Workspace, "ignored"));
    }

    [Fact]
    public void The_known_names_message_is_generated_from_the_table()
    {
        // The old hardcoded copy of this list had drifted — it never gained .flow/.test.
        foreach (var expected in new[] { "*.req.tap", "*.auth.tap", "*.env.tap", "*.flow.tap", "*.test.tap", "_collection.tap", "workspace.tap" })
        {
            Assert.Contains(expected, KindResolver.KnownNamesDescription, StringComparison.Ordinal);
        }
    }
}
