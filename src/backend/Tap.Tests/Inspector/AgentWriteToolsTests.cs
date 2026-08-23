using Tap.Core.Capture;
using Tap.Core.Redaction;
using Tap.Server;
using Tap.Server.Agent;

namespace Tap.Tests.Inspector;

/// <summary>
/// P3's tools: replaying a capture, exporting one to a file, and searching across them —
/// each with the property that makes it safe to hand an agent.
/// </summary>
public class AgentWriteToolsTests
{
    private static readonly CaptureRedactor Redactor = new();

    private const string Jwt =
        "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJodHRwczovL2F1dGguZXhhbXBsZS5jb20ifQ.c2ln";

    private static RequestRecord Record(
        long sequence = 1,
        string method = "POST",
        string path = "/v1/orders",
        int status = 201,
        string? body = null)
        => new()
        {
            Sequence = sequence,
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UnixEpoch,
            Method = method,
            Host = "api.example.com",
            Path = path,
            Scheme = "https",
            RemoteIp = "203.0.113.7",
            RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + Jwt,
                ["Content-Type"] = "application/json",
                ["Content-Length"] = "23",
                ["X-Tenant"] = "acme",
            },
            StatusCode = status,
            RequestBody = body ?? """{"sku":"A1","qty":2}""",
            RequestContentType = "application/json",
            ResponseBody = """{"orderId":"ord-4021"}""",
            ResponseContentType = "application/json",
        };

    private static CapturedRequestDetail Detail(RequestRecord record)
        => CaptureProjection.Describe(record, Redactor, new CaptureDetailOptions { IncludeFrames = false });

    private static (InMemoryRequestStore Store, StoreCaptureProvider Provider) Fixture(bool allowReplay = false)
    {
        var store = new InMemoryRequestStore();
        var options = new InspectorAgentOptions { Enabled = true, AllowReplay = allowReplay };
        return (store, new StoreCaptureProvider(store, options, new AgentActivity(enabled: true)));
    }

    // ------------------------------------------------------------------------- replay

    [Fact]
    public async Task Replay_is_off_even_when_reading_is_on()
    {
        var (store, provider) = Fixture();
        var record = Record();
        store.Add(record);

        var result = await provider.ReplayAsync(
            new CaptureReplayRequest(record.Id.ToString()), TestContext.Current.CancellationToken);

        Assert.False(result.Replayed);
        Assert.Contains("AllowReplay", result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example.com/steal")]
    [InlineData("//evil.example.com/steal")]
    [InlineData("v1/orders")]
    public void An_edited_path_that_leaves_the_host_is_refused(string path)
    {
        // A replay carries the captured credential. Letting the caller choose the destination
        // would be letting it choose where somebody else's token goes.
        var rejection = new CaptureReplayRequest("id", Path: path).Rejection();

        Assert.NotNull(rejection);
    }

    [Fact]
    public void A_relative_path_edit_is_allowed()
        => Assert.Null(new CaptureReplayRequest("id", Path: "/v1/orders?page=2").Rejection());

    [Theory]
    [InlineData("Host")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("Forwarded")]
    public void Headers_that_redirect_the_credential_cannot_be_edited(string header)
    {
        var rejection = new CaptureReplayRequest(
            "id",
            Headers: new Dictionary<string, string> { [header] = "evil.example.com" }).Rejection();

        Assert.NotNull(rejection);
        Assert.Contains(header, rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_header_edit_is_allowed()
        => Assert.Null(new CaptureReplayRequest(
            "id", Headers: new Dictionary<string, string> { ["X-Tenant"] = "globex" }).Rejection());

    [Fact]
    public async Task Replaying_an_unknown_id_is_refused_not_thrown()
    {
        var (_, provider) = Fixture(allowReplay: true);

        var result = await provider.ReplayAsync(
            new CaptureReplayRequest(Guid.NewGuid().ToString()), TestContext.Current.CancellationToken);

        Assert.False(result.Replayed);
        Assert.NotNull(result.Error);
    }

    // ------------------------------------------------------------------------- export

    [Fact]
    public void An_exported_tap_file_turns_redacted_values_into_placeholders()
    {
        var export = CaptureExport.Export(Detail(Record()), "tap");

        Assert.Equal("tap", export.Format);
        Assert.EndsWith(".req.tap", export.SuggestedFileName, StringComparison.Ordinal);

        // A file containing [redacted:…] would be a request that cannot work and a mask that
        // looks like a value.
        Assert.DoesNotContain("[redacted:", export.Document, StringComparison.Ordinal);
        Assert.DoesNotContain(Jwt, export.Document, StringComparison.Ordinal);

        Assert.Contains("Authorization: Bearer {{authorization}}", export.Document, StringComparison.Ordinal);
        Assert.Contains("authorization", export.Placeholders);
    }

    [Fact]
    public void A_mask_inside_a_body_becomes_a_named_placeholder_too()
    {
        // The header path was covered; this is the one a smoke test caught. A mask left in a
        // body is a request file that sends the literal text "[redacted:opaque …]" as a
        // password — broken, and to a careless eye it reads like the real thing.
        var export = CaptureExport.Export(
            Detail(Record(body: """{"sku":"A1","password":"hunter2!"}""")), "tap");

        Assert.DoesNotContain("[redacted:", export.Document, StringComparison.Ordinal);
        Assert.Contains(""""password":"{{password}}"""", export.Document, StringComparison.Ordinal);
        Assert.Contains("password", export.Placeholders);

        // Non-secret fields are untouched.
        Assert.Contains(""""sku":"A1"""", export.Document, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_masked_body_fields_each_get_their_own_placeholder()
    {
        var export = CaptureExport.Export(
            Detail(Record(body: """{"password":"hunter2!","apiKey":"live_abcdef123456"}""")), "tap");

        Assert.DoesNotContain("[redacted:", export.Document, StringComparison.Ordinal);
        Assert.Contains("password", export.Placeholders);
        Assert.Contains("apiKey", export.Placeholders);
    }

    [Fact]
    public void An_exported_tap_file_has_the_frontmatter_and_blocks_the_parser_expects()
    {
        var document = CaptureExport.Export(Detail(Record()), "tap").Document;

        Assert.StartsWith("---\nkind: request\n", document.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("```http\nPOST /v1/orders", document.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("```assert\nstatus 201", document.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exported_http_file_carries_a_baseUrl_and_the_request()
    {
        var export = CaptureExport.Export(Detail(Record()), "http");

        Assert.Equal("http", export.Format);
        Assert.EndsWith(".http", export.SuggestedFileName, StringComparison.Ordinal);
        Assert.Contains("@baseUrl = https://api.example.com", export.Document, StringComparison.Ordinal);
        Assert.Contains("POST {{baseUrl}}/v1/orders", export.Document, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted:", export.Document, StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_the_sender_sets_itself_are_not_copied_into_an_export()
    {
        var document = CaptureExport.Export(Detail(Record()), "http").Document;

        // A stale Content-Length against an edited body fails in a way nobody enjoys diagnosing.
        Assert.DoesNotContain("Content-Length:", document, StringComparison.Ordinal);
        Assert.Contains("X-Tenant: acme", document, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- search

    [Fact]
    public void Search_finds_a_term_in_a_response_body_and_says_where()
    {
        var hit = CaptureSearch.Find(Detail(Record()), "ord-4021");

        Assert.NotNull(hit);
        Assert.Equal("response.body", hit.Where);
        Assert.Contains("ord-4021", hit.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_cannot_be_used_as_an_oracle_over_a_hidden_value()
    {
        var detail = Detail(Record());

        // The token is in the record. It is not in what search can see, so no query — and no
        // sequence of queries narrowing character by character — produces a signal.
        Assert.Null(CaptureSearch.Find(detail, Jwt));
        foreach (var prefix in (string[])["eyJhbGciOiJSUzI1NiJ9", "eyJhbGciOiJSUzI1", "eyJhbGciOiJ", "eyJhbGc", "eyJ"])
        {
            Assert.Null(CaptureSearch.Find(detail, prefix));
        }
    }

    [Fact]
    public async Task Search_respects_the_host_allowlist()
    {
        var store = new InMemoryRequestStore();
        var provider = new StoreCaptureProvider(
            store,
            new InspectorAgentOptions { Enabled = true, AllowHosts = ["other.example.com"] },
            new AgentActivity(enabled: true));

        store.Add(Record());

        var hits = await provider.SearchAsync("ord-4021", CaptureQuery.All, TestContext.Current.CancellationToken);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_returns_newest_first_and_honours_the_limit()
    {
        var (store, provider) = Fixture();
        for (var i = 1; i <= 5; i++) store.Add(Record(sequence: i));

        var hits = await provider.SearchAsync(
            "ord-4021", new CaptureQuery { Limit = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, hits.Count);
        Assert.Equal(5, hits[0].Request.Seq);
    }

    [Fact]
    public async Task An_empty_term_finds_nothing_rather_than_everything()
    {
        var (store, provider) = Fixture();
        store.Add(Record());

        Assert.Empty(await provider.SearchAsync("   ", CaptureQuery.All, TestContext.Current.CancellationToken));
    }
}
