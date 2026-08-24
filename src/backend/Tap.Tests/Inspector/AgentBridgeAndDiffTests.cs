using Tap.Core.Capture;
using Tap.Core.Redaction;
using Tap.Server;
using Tap.Server.Agent;

namespace Tap.Tests.Inspector;

/// <summary>
/// The pieces P2 adds around the tools: how a bridge finds and proves itself, how two
/// exchanges are compared, and what the person at the inspector gets to see about all of it.
/// </summary>
public class AgentBridgeAndDiffTests
{
    private static readonly CaptureRedactor Redactor = new();

    // Invented Stripe-shaped values, assembled rather than written out: see the note in
    // CaptureRedactorTests. Nothing here was ever a real key.
    private const string StripePrefix = "sk" + "_live_";
    private const string TokenA = StripePrefix + "goodtoken1234567890";
    private const string TokenB = StripePrefix + "staletoken098765432";
    private const string TokenSame = StripePrefix + "abcdef1234567890";

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "tap-bridge-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static RequestRecord Record(
        long sequence,
        string path = "/v1/orders",
        int status = 200,
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null)
        => new()
        {
            Sequence = sequence,
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Method = "GET",
            Host = "api.example.com",
            Path = path,
            Scheme = "https",
            RemoteIp = "203.0.113.7",
            RequestHeaders = headers ?? new Dictionary<string, string>(),
            StatusCode = status,
            RequestBody = body,
            RequestContentType = body is null ? null : "application/json",
        };

    // ------------------------------------------------------------------- the bridge handle

    [Fact]
    public void A_handle_round_trips_and_carries_a_fresh_token()
    {
        var root = TempRoot();

        var written = AgentBridgeFile.Write(5198, rootOverride: root);
        var read = AgentBridgeFile.Read(5198, root);

        Assert.NotNull(read);
        Assert.Equal(written.Token, read.Token);
        Assert.Equal(5198, read.UiPort);
        Assert.Equal(64, written.Token.Length); // 32 random bytes, hex
    }

    [Fact]
    public void Each_run_mints_a_different_token()
    {
        var root = TempRoot();

        // A token that survived a restart would let a bridge from a previous session keep
        // reading a process that never authorized it.
        Assert.NotEqual(AgentBridgeFile.Write(5198, rootOverride: root).Token, AgentBridgeFile.Write(5198, rootOverride: root).Token);
    }

    [Fact]
    public void Discovery_prefers_the_most_recently_started_inspector()
    {
        var root = TempRoot();
        AgentBridgeFile.Write(5198, rootOverride: root);
        var newest = AgentBridgeFile.Write(5298, rootOverride: root);

        Assert.Equal(newest.Token, AgentBridgeFile.Discover(root)?.Token);
    }

    [Fact]
    public void A_missing_or_deleted_handle_is_null_not_an_exception()
    {
        var root = TempRoot();

        Assert.Null(AgentBridgeFile.Read(5198, root));
        Assert.Null(AgentBridgeFile.Discover(root));

        AgentBridgeFile.Write(5198, rootOverride: root);
        AgentBridgeFile.Delete(5198, root);
        Assert.Null(AgentBridgeFile.Read(5198, root));
    }

    [Fact]
    public void A_handle_left_by_a_dead_process_is_ignored()
    {
        var root = TempRoot();
        var path = AgentBridgeFile.PathFor(5198, root);

        // Hand-write a handle claiming a pid that cannot be running. A crash or kill -9 skips
        // the cleanup hook, so this is the ordinary case — and following it would send a live
        // token to whatever process has since taken that port.
        AgentBridgeFile.Write(5198, rootOverride: root);
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(
                $"\"pid\": {Environment.ProcessId}", "\"pid\": 2147483churn".Replace("churn", "00")));

        Assert.Null(AgentBridgeFile.Read(5198, root));
        Assert.Null(AgentBridgeFile.Discover(root));
    }

    [Fact]
    public void The_handle_file_is_readable_only_by_its_owner()
    {
        if (OperatingSystem.IsWindows()) return; // Unix mode bits; Windows uses ACLs

        var root = TempRoot();
        AgentBridgeFile.Write(5198, rootOverride: root);

        // The permissions ARE the authorization here — the token is only a secret for as long
        // as this holds.
        var mode = File.GetUnixFileMode(AgentBridgeFile.PathFor(5198, root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    // --------------------------------------------------------------------------- the diff

    private static CapturedRequestDetail Detail(RequestRecord record)
        => CaptureProjection.Describe(record, Redactor, new CaptureDetailOptions { IncludeFrames = false });

    [Fact]
    public void Identical_exchanges_report_no_differences()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + TokenSame };
        var diff = CaptureDiff.Compare(
            Detail(Record(1, headers: headers, body: """{"a":1}""")),
            Detail(Record(2, headers: headers, body: """{"a":1}""")));

        Assert.True(diff.Identical);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void Two_different_credentials_are_reported_without_either_being_visible()
    {
        var diff = CaptureDiff.Compare(
            Detail(Record(1, status: 200, headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + TokenA,
            })),
            Detail(Record(2, status: 401, headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + TokenB,
            })));

        var auth = Assert.Single(diff.Differences, d => d.What == "request.header:Authorization");
        Assert.DoesNotContain("goodtoken", auth.Left!, StringComparison.Ordinal);
        Assert.DoesNotContain("staletoken", auth.Right!, StringComparison.Ordinal);

        // The finding is that they differ, and fingerprints are what make that visible at all.
        Assert.NotEqual(auth.Left, auth.Right);
        Assert.Contains(diff.Differences, d => d.What == "status");
    }

    [Fact]
    public void The_same_credential_on_both_sides_is_not_a_difference()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + TokenSame };

        var diff = CaptureDiff.Compare(
            Detail(Record(1, headers: headers)),
            Detail(Record(2, status: 500, headers: headers)));

        Assert.DoesNotContain(diff.Differences, d => d.What == "request.header:Authorization");
        Assert.Contains(diff.Differences, d => d.What == "status");
    }

    [Fact]
    public void Query_parameters_are_compared_one_by_one()
    {
        var diff = CaptureDiff.Compare(
            Detail(Record(1, path: "/v1/orders?page=1&sort=asc")),
            Detail(Record(2, path: "/v1/orders?page=2&sort=asc")));

        var page = Assert.Single(diff.Differences, d => d.What == "query:page");
        Assert.Equal("1", page.Left);
        Assert.Equal("2", page.Right);
        Assert.DoesNotContain(diff.Differences, d => d.What == "query:sort");
        Assert.DoesNotContain(diff.Differences, d => d.What == "path");
    }

    [Fact]
    public void Volatile_headers_do_not_bury_the_real_difference()
    {
        var diff = CaptureDiff.Compare(
            Detail(Record(1, headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "curl/8.7.1",
                ["X-Request-Id"] = "req-aaaa",
                ["X-Tenant"] = "acme",
            })),
            Detail(Record(2, headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "curl/9.0.0",
                ["X-Request-Id"] = "req-bbbb",
                ["X-Tenant"] = "globex",
            })));

        Assert.Equal("request.header:X-Tenant", Assert.Single(diff.Differences).What);
    }

    // ------------------------------------------------------------------------- visibility

    [Fact]
    public async Task Reads_are_counted_so_the_ui_can_show_them()
    {
        var store = new InMemoryRequestStore();
        var activity = new AgentActivity(enabled: true);
        var provider = new StoreCaptureProvider(store, new InspectorAgentOptions { Enabled = true }, activity);
        store.Add(Record(1));

        Assert.False(activity.Snapshot().Attached);

        await provider.ListAsync(CaptureQuery.All, TestContext.Current.CancellationToken);
        await provider.ListAsync(CaptureQuery.All, TestContext.Current.CancellationToken);

        var snapshot = activity.Snapshot();
        Assert.Equal(2, snapshot.Reads);
        Assert.True(snapshot.Attached);
        Assert.NotNull(snapshot.LastReadAt);
    }

    [Fact]
    public async Task A_parked_wait_is_visible_while_it_waits_and_clears_afterwards()
    {
        var store = new InMemoryRequestStore();
        var activity = new AgentActivity(enabled: true);
        var provider = new StoreCaptureProvider(store, new InspectorAgentOptions { Enabled = true }, activity);

        var waiting = provider.WaitAsync(
            new CaptureQuery { PathGlob = "/never/*" },
            TimeSpan.FromMilliseconds(600),
            TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(1, activity.Snapshot().Waiting);

        await waiting;
        Assert.Equal(0, activity.Snapshot().Waiting);
    }

    // ------------------------------------------------------------------------ since-attach

    [Fact]
    public async Task Since_attach_hides_everything_captured_before_the_agent_looked()
    {
        var store = new InMemoryRequestStore();
        var provider = new StoreCaptureProvider(
            store,
            new InspectorAgentOptions { Enabled = true, Scope = "since-attach" },
            new AgentActivity(enabled: true));

        var before = Record(1);
        store.Add(before);

        // First call is what "attach" means — the provider is a singleton created at inspector
        // startup, so construction time would have made the scope meaningless.
        var first = await provider.ListAsync(CaptureQuery.All, TestContext.Current.CancellationToken);
        Assert.Equal(0, first.Count);

        store.Add(Record(2));
        var second = await provider.ListAsync(CaptureQuery.All, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Count);
        Assert.Equal(2, second.Requests[0].Seq);

        // And the older one stays unreachable by id, not merely absent from the listing.
        Assert.Null(await provider.GetAsync(
            before.Id.ToString(), CaptureDetailOptions.Default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Scope_all_is_the_default_and_shows_history()
    {
        var store = new InMemoryRequestStore();
        var provider = new StoreCaptureProvider(
            store, new InspectorAgentOptions { Enabled = true }, new AgentActivity(enabled: true));

        store.Add(Record(1));
        var listing = await provider.ListAsync(CaptureQuery.All, TestContext.Current.CancellationToken);

        Assert.Equal(1, listing.Count);
    }
}
