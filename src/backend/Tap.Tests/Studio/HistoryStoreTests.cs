using System.Text.Json;
using Tap.Studio.History;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Security;

namespace Tap.Tests.Studio;

/// <summary>
/// The store is where a recorded exchange becomes a file someone might later commit, back up,
/// or hand to a colleague — so what it writes, and what it refuses to write, is a security
/// property rather than a formatting detail. These cover the redacted/encrypted pair, the
/// fail-closed rule, pruning, ordering, and orphan handling.
/// </summary>
public class HistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tap-history-tests-" + Guid.NewGuid().ToString("n"));

    public HistoryStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // -- Writing ---------------------------------------------------------------------------

    [Fact]
    public void A_plaintext_entry_is_written_and_reads_back()
    {
        var store = NewStore();
        var entry = SampleEntry("req-1");

        var path = store.TryWrite(entry, Enabled(), out var problem);

        Assert.Null(problem);
        Assert.NotNull(path);
        Assert.EndsWith(".json", path);

        var read = store.Read("req-1", entry.Id);
        Assert.NotNull(read);
        Assert.Equal(entry.Request.Url, read.Request.Url);
    }

    [Fact]
    public void The_folder_excludes_itself_from_git()
    {
        // Recorded traffic is not source, and with encrypt on it is real credentials. Relying on
        // the consumer's repo to already have the right ignore line is relying on luck.
        var store = NewStore();
        store.TryWrite(SampleEntry("req-1"), Enabled(), out _);

        var gitignore = Path.Combine(_root, HistoryStore.DirectoryName, ".gitignore");
        Assert.True(File.Exists(gitignore));
        Assert.Contains("*", File.ReadAllText(gitignore));
    }

    [Fact]
    public void An_encrypted_entry_is_unreadable_on_disk_and_readable_through_the_store()
    {
        var store = NewStore(passphrase: "test-passphrase");
        var entry = SampleEntry("req-1") with { Redacted = false };

        var path = store.TryWrite(entry, Enabled(encrypt: true), out var problem);

        Assert.Null(problem);
        Assert.NotNull(path);
        Assert.EndsWith(".json.enc", path);

        var raw = File.ReadAllText(path);
        Assert.StartsWith(SecretEnvelope.Prefix, raw);
        Assert.DoesNotContain("super-secret-token", raw);

        var read = store.Read("req-1", entry.Id);
        Assert.NotNull(read);
        Assert.Equal("Bearer super-secret-token", read.Request.Headers["Authorization"]);
    }

    [Fact]
    public void Encrypt_with_no_key_writes_nothing_rather_than_falling_back_to_plaintext()
    {
        // This is the rule the whole redaction/encryption pairing rests on: encryption is what
        // licenses storing unredacted secrets, so a silent downgrade would spill credentials to
        // disk in the exact configuration that asked for the opposite.
        var store = NewStore(passphrase: null);
        var entry = SampleEntry("req-1") with { Redacted = false };

        var path = store.TryWrite(entry, Enabled(encrypt: true), out var problem);

        Assert.Null(path);
        Assert.NotNull(problem);
        Assert.Contains("encryption key", problem);
        // The key is checked before anything is created, so the refusal leaves no trace at all —
        // not an empty folder, not a zero-byte file.
        Assert.False(Directory.Exists(Path.Combine(_root, HistoryStore.DirectoryName)));
    }

    [Fact]
    public void An_encrypted_entry_reads_as_locked_without_the_key()
    {
        var written = NewStore(passphrase: "the-key");
        var entry = SampleEntry("req-1");
        written.TryWrite(entry, Enabled(encrypt: true), out _);

        var keyless = NewStore(passphrase: null);
        Assert.Null(keyless.Read("req-1", entry.Id));
        Assert.True(keyless.IsLocked("req-1", entry.Id));

        // The row still appears — "there are entries here you can't open" beats an empty list.
        var rows = keyless.ListForRequest("req-1", Workspace());
        var row = Assert.Single(rows);
        Assert.True(row.Locked);
        Assert.True(row.Encrypted);
    }

    // -- Pruning ---------------------------------------------------------------------------

    [Fact]
    public void Only_the_newest_maxEntries_survive()
    {
        var store = NewStore();
        var options = Enabled(maxEntries: 3);
        var ids = new List<string>();

        for (var i = 0; i < 6; i++)
        {
            var entry = SampleEntry("req-1") with
            {
                Id = $"20260822T0939{i:D2}.000Z-{i:D8}",
                Request = new HistoryRequest("GET", $"https://example.test/{i}", Headers(), null, "http"),
            };
            ids.Add(entry.Id);
            store.TryWrite(entry, options, out _);
        }

        var rows = store.ListForRequest("req-1", Workspace());
        Assert.Equal(3, rows.Count);
        // Newest first, and the three oldest are gone.
        Assert.Equal([ids[5], ids[4], ids[3]], rows.Select(r => r.Id));
    }

    [Fact]
    public void The_timeline_orders_across_requests_by_time()
    {
        var store = NewStore();
        var options = Enabled();

        // Interleaved on purpose: ordering has to come from the filename, not from which folder
        // the enumeration happened to walk first.
        store.TryWrite(SampleEntry("req-a") with { Id = "20260822T090000.000Z-aaaaaaaa" }, options, out _);
        store.TryWrite(SampleEntry("req-b") with { Id = "20260822T093000.000Z-bbbbbbbb" }, options, out _);
        store.TryWrite(SampleEntry("req-a") with { Id = "20260822T091500.000Z-cccccccc" }, options, out _);

        var rows = store.ListRecent(Workspace());
        Assert.Equal(
            ["20260822T093000.000Z-bbbbbbbb", "20260822T091500.000Z-cccccccc", "20260822T090000.000Z-aaaaaaaa"],
            rows.Select(r => r.Id));
    }

    // -- Orphans ---------------------------------------------------------------------------

    [Fact]
    public void History_for_a_request_that_no_longer_exists_is_marked_orphaned_not_dropped()
    {
        var store = NewStore();
        store.TryWrite(SampleEntry("gone") with { RequestName = "Deleted request" }, Enabled(), out _);

        var row = Assert.Single(store.ListRecent(Workspace()));
        Assert.True(row.Orphaned);
        // The name recorded at the time is what keeps the row readable — the file it came from
        // isn't there to ask any more.
        Assert.Equal("Deleted request", row.RequestName);
    }

    [Fact]
    public void An_orphan_relinks_by_itself_when_its_request_comes_back()
    {
        var store = NewStore();
        store.TryWrite(SampleEntry("req-1"), Enabled(), out _);

        Assert.True(Assert.Single(store.ListRecent(Workspace())).Orphaned);
        // Same id, file restored — a reverted delete, a branch switch back.
        Assert.False(Assert.Single(store.ListRecent(Workspace("req-1"))).Orphaned);
    }

    [Fact]
    public void Sweeping_removes_orphans_and_leaves_live_requests_alone()
    {
        var store = NewStore();
        store.TryWrite(SampleEntry("req-1"), Enabled(), out _);
        store.TryWrite(SampleEntry("gone"), Enabled(), out _);

        var swept = store.SweepOrphans(Workspace("req-1"), olderThanDays: 0);

        Assert.Equal(1, swept);
        Assert.False(Directory.Exists(Path.Combine(_root, HistoryStore.DirectoryName, "gone")));
        Assert.True(Directory.Exists(Path.Combine(_root, HistoryStore.DirectoryName, "req-1")));
    }

    [Fact]
    public void A_retention_window_keeps_a_recently_deleted_requests_history()
    {
        // The window is what makes "delete, realise, git checkout" recoverable.
        var store = NewStore();
        store.TryWrite(SampleEntry("gone"), Enabled(), out _);

        Assert.Equal(0, store.SweepOrphans(Workspace(), olderThanDays: 30));
        Assert.Single(store.ListRecent(Workspace()));
    }

    // -- Deleting --------------------------------------------------------------------------

    [Fact]
    public void Entries_and_whole_requests_can_be_cleared()
    {
        var store = NewStore();
        var entry = SampleEntry("req-1");
        store.TryWrite(entry, Enabled(), out _);
        store.TryWrite(SampleEntry("req-1") with { Id = "20260822T100000.000Z-dddddddd" }, Enabled(), out _);

        Assert.True(store.DeleteEntry("req-1", entry.Id));
        Assert.Single(store.ListForRequest("req-1", Workspace("req-1")));

        Assert.True(store.DeleteRequest("req-1"));
        Assert.Empty(store.ListForRequest("req-1", Workspace("req-1")));
    }

    [Fact]
    public void A_traversal_shaped_id_cannot_escape_the_history_folder()
    {
        // Ids reach the store from route parameters, so they are strangers.
        var store = NewStore();
        Assert.False(store.DeleteRequest("../../etc"));
        Assert.Null(store.Read("../..", "passwd"));
        Assert.True(Directory.Exists(_root));
    }

    // -- Helpers ---------------------------------------------------------------------------

    private HistoryStore NewStore(string? passphrase = "test-passphrase")
        => new(_root, new StaticEncryptionKeySource(passphrase));

    private static HistoryOptions Enabled(bool encrypt = false, int maxEntries = 25)
        => new() { Enabled = true, Encrypt = encrypt, MaxEntries = maxEntries };

    private static Dictionary<string, string> Headers() =>
        new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer super-secret-token" };

    private static HistoryEntry SampleEntry(string requestId) => new()
    {
        Id = HistoryStore.NewEntryId(DateTimeOffset.UtcNow),
        At = DateTimeOffset.UtcNow,
        RequestId = requestId,
        RequestPath = $"collections/demo/{requestId}.req.tap",
        RequestName = requestId,
        Collection = "demo",
        Redacted = true,
        Request = new HistoryRequest("GET", "https://example.test/one", Headers(), null, "http"),
        Response = new HistoryResponse(200, "OK", new Dictionary<string, string>(), "application/json", "{}", 2, false),
        DurationMs = 12,
    };

    /// <summary>A workspace holding requests with the given ids — everything else is irrelevant
    /// to the store, which only ever asks "does this id still exist".</summary>
    private static LoadedWorkspace Workspace(params string[] requestIds)
    {
        var files = requestIds.Select(id => (WorkspaceFile)new RequestFile
        {
            Kind = WorkspaceKind.Request,
            RelativePath = $"collections/demo/{id}.req.tap",
            Id = id,
            Name = id,
        }).ToList();
        return new LoadedWorkspace("/tmp/does-not-matter", "/tmp/does-not-matter", files, []);
    }
}
