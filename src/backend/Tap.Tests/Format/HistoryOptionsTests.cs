using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// The <c>history:</c> block decides whether a workspace starts writing exchanges to disk and
/// whether those files hold real credentials, so a value that parses as something other than
/// what was written is not a cosmetic bug. These cover both frontmatter shapes, the per-key
/// cascade, the emitter round-trip, and the clamping the store relies on.
/// </summary>
public class HistoryOptionsTests
{
    [Fact]
    public void Silence_means_off()
    {
        var manifest = ParseManifest("""
            ---
            kind: workspace
            name: demo
            ---
            """);

        Assert.True(manifest.History.IsEmpty);
        Assert.False(manifest.History.EffectiveEnabled);
    }

    [Fact]
    public void The_bool_shorthand_sets_only_enabled()
    {
        var collection = ParseCollection("""
            ---
            kind: collection
            name: Demo
            history: true
            ---
            """);

        Assert.True(collection.History.Enabled);
        Assert.True(collection.History.IsShorthand);
        // Everything else stays unset so the tier below still gets a say.
        Assert.Null(collection.History.MaxEntries);
        Assert.Null(collection.History.Encrypt);
    }

    [Fact]
    public void The_mapping_shape_reads_every_key()
    {
        var request = ParseRequest("""
            ---
            kind: request
            name: One
            history:
              enabled: true
              maxEntries: 50
              encrypt: true
              maxBodyBytes: 512kb
            ---

            ```http
            GET /one
            ```
            """);

        Assert.True(request.History.Enabled);
        Assert.Equal(50, request.History.MaxEntries);
        Assert.True(request.History.Encrypt);
        Assert.Equal(512 * 1024, request.History.MaxBodyBytes);
    }

    [Theory]
    [InlineData("history: sometimes", "history: sometimes")]
    [InlineData("history:\n  enabled: yes-please", "history.enabled")]
    [InlineData("history:\n  maxEntries: loads", "history.maxEntries")]
    [InlineData("history:\n  maxBodyBytes: biggish", "history.maxBodyBytes")]
    public void A_value_that_is_not_valid_is_an_error_rather_than_a_silent_default(string block, string mentions)
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => ParseManifest($"""
            ---
            kind: workspace
            name: demo
            {block}
            ---
            """));

        Assert.Equal(WorkspaceErrorCode.E_UNKNOWN_FIELD, ex.Error.Code);
        Assert.Contains(mentions, ex.Error.Message);
    }

    [Fact]
    public void Orphan_retention_belongs_to_the_workspace_alone()
    {
        // A collection or request that sets it is configuring something nothing can read: by the
        // time a history folder is orphaned, both of those files are gone.
        var ex = Assert.Throws<WorkspaceParseException>(() => ParseCollection("""
            ---
            kind: collection
            name: Demo
            history:
              orphanRetentionDays: 5
            ---
            """));

        Assert.Contains("orphanRetentionDays", ex.Error.Message);
        Assert.Contains("workspace.tap", ex.Error.Message);

        var manifest = ParseManifest("""
            ---
            kind: workspace
            name: demo
            history:
              orphanRetentionDays: 5
            ---
            """);
        Assert.Equal(5, manifest.History.OrphanRetentionDays);
    }

    // -- Cascade ---------------------------------------------------------------------------

    [Fact]
    public void Nearest_scope_wins_per_key()
    {
        var workspace = new HistoryOptions { Enabled = true, MaxEntries = 10, Encrypt = false };
        var collection = new HistoryOptions { MaxEntries = 50 };
        var request = new HistoryOptions { Encrypt = true };

        var resolved = HistoryOptions.Resolve(workspace, collection, request);

        Assert.True(resolved.Enabled);        // only the workspace said anything
        Assert.Equal(50, resolved.MaxEntries); // collection overrode the workspace
        Assert.True(resolved.Encrypt);         // request overrode the workspace
    }

    [Fact]
    public void A_request_can_opt_out_of_a_collection_that_opted_in()
    {
        var resolved = HistoryOptions.Resolve(
            new HistoryOptions { Enabled = true },
            new HistoryOptions { Enabled = true },
            new HistoryOptions { Enabled = false });

        Assert.False(resolved.EffectiveEnabled);
    }

    [Fact]
    public void Unset_keys_fall_all_the_way_through_to_the_defaults()
    {
        var resolved = HistoryOptions.Resolve(null, null, null);

        Assert.False(resolved.EffectiveEnabled);
        Assert.Equal(HistoryOptions.DefaultMaxEntries, resolved.EffectiveMaxEntries);
        Assert.Equal(HistoryOptions.DefaultMaxBodyBytes, resolved.EffectiveMaxBodyBytes);
        Assert.False(resolved.EffectiveEncrypt);
    }

    [Fact]
    public void Orphan_retention_is_read_from_the_workspace_even_when_a_request_is_nearer()
    {
        var resolved = HistoryOptions.Resolve(
            new HistoryOptions { OrphanRetentionDays = 7 }, new HistoryOptions(), new HistoryOptions());
        Assert.Equal(7, resolved.EffectiveOrphanRetentionDays);
    }

    [Fact]
    public void Limits_are_clamped_to_something_a_person_could_browse()
    {
        var huge = new HistoryOptions { MaxEntries = int.MaxValue, MaxBodyBytes = long.MaxValue };
        Assert.Equal(HistoryOptions.AbsoluteMaxEntries, huge.EffectiveMaxEntries);
        Assert.Equal(HistoryOptions.AbsoluteMaxBodyBytes, huge.EffectiveMaxBodyBytes);

        // Keeping zero entries would mean writing a file and immediately deleting it.
        Assert.Equal(1, new HistoryOptions { MaxEntries = 0 }.EffectiveMaxEntries);
    }

    // -- Round-trip ------------------------------------------------------------------------

    [Fact]
    public void Enabled_on_its_own_round_trips_as_the_shorthand()
    {
        var source = RequestSpecEmitter.ToFileSource(new RequestSpecDto
        {
            Path = "collections/demo/one.req.tap",
            Name = "One",
            Method = "GET",
            Url = "/one",
            History = new HistoryOptionsDto(Enabled: true, null, null, null, null),
        });

        Assert.Contains("history: true", source);
        Assert.True(ParseRequest(source).History.Enabled);
    }

    [Fact]
    public void The_full_block_round_trips_in_human_sizes()
    {
        var source = CollectionSpecEmitter.ToFileSource(new CollectionSpecDto
        {
            Slug = "demo",
            Name = "Demo",
            History = new HistoryOptionsDto(Enabled: true, MaxEntries: 50, Encrypt: true, MaxBodyBytes: 512 * 1024, null),
        });

        Assert.Contains("maxEntries: 50", source);
        Assert.Contains("maxBodyBytes: 512kb", source);

        var reparsed = ParseCollection(source);
        Assert.True(reparsed.History.Enabled);
        Assert.Equal(50, reparsed.History.MaxEntries);
        Assert.True(reparsed.History.Encrypt);
        Assert.Equal(512 * 1024, reparsed.History.MaxBodyBytes);
    }

    [Fact]
    public void A_scope_that_declares_nothing_writes_no_history_block()
    {
        // Otherwise saving a request would pin whatever it happened to inherit that day, and the
        // collection's policy would stop reaching it.
        var source = RequestSpecEmitter.ToFileSource(new RequestSpecDto
        {
            Path = "collections/demo/one.req.tap",
            Name = "One",
            Method = "GET",
            Url = "/one",
        });

        Assert.DoesNotContain("history", source);
    }

    private static WorkspaceManifestFile ParseManifest(string source)
        => Assert.IsType<WorkspaceManifestFile>(FileParser.Parse("workspace.tap", source));

    private static CollectionFile ParseCollection(string source)
        => Assert.IsType<CollectionFile>(FileParser.Parse("collections/demo/_collection.tap", source));

    private static RequestFile ParseRequest(string source)
        => Assert.IsType<RequestFile>(FileParser.Parse("collections/demo/one.req.tap", source));
}
