using System.Reflection;
using Tap.Core.Capture;
using Tap.Core.Redaction;
using Tap.Server;
using Tap.Server.Agent;

namespace Tap.Tests.Redaction;

/// <summary>
/// The projection from <see cref="RequestRecord"/> to the agent-facing DTOs, and the guardrail
/// that keeps the two from drifting apart silently.
/// </summary>
public class CaptureProjectionTests
{
    private static readonly CaptureRedactor Redactor = new();

    private const string Jwt =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9." +
        "eyJzdWIiOiIxMjM0NTY3ODkwIn0." +
        "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static RequestRecord Record(Action<RequestRecord>? customise = null, string path = "/v1/orders")
    {
        var record = new RequestRecord
        {
            Sequence = 42,
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UnixEpoch,
            Method = "POST",
            Host = "api.example.com",
            Path = path,
            Scheme = "https",
            RemoteIp = "203.0.113.44",
            RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + Jwt,
                ["Content-Type"] = "application/json",
            },
            StatusCode = 201,
            DurationMs = 37,
        };

        customise?.Invoke(record);
        return record;
    }

    // ------------------------------------------------------------------- the guardrail

    /// <summary>
    /// Every public property of <see cref="RequestRecord"/> must either reach the agent DTOs —
    /// under its own name or a declared rename — or be named in
    /// <see cref="CaptureProjection.WithheldFields"/> with a reason.
    ///
    /// <para>Adding a field to the record then fails here until someone decides where it
    /// belongs, which is the only reliable defence against the failure mode this whole design
    /// exists to prevent: a capture field quietly reaching an agent because nobody thought
    /// about it.</para>
    /// </summary>
    [Fact]
    public void Every_record_field_is_either_projected_or_deliberately_withheld()
    {
        var projected = ProjectedPropertyNames();
        var unaccounted = new List<string>();

        foreach (var property in PublicProperties(typeof(RequestRecord)))
        {
            if (CaptureProjection.WithheldFields.ContainsKey(property.Name)) continue;

            var expected = CaptureProjection.RenamedFields.TryGetValue(property.Name, out var renamed)
                ? renamed
                : property.Name;

            if (!projected.Contains(expected)) unaccounted.Add($"{property.Name} (looked for '{expected}')");
        }

        Assert.True(
            unaccounted.Count == 0,
            "RequestRecord has properties that neither reach the agent DTOs nor appear in " +
            $"CaptureProjection.WithheldFields: {string.Join(", ", unaccounted)}. Decide whether " +
            "each one is safe to show an agent, then map it or withhold it explicitly.");
    }

    /// <summary>The inverse, and the one that actually protects a secret: a withheld field must
    /// not have quietly reappeared on the DTOs under its own name.</summary>
    [Fact]
    public void Withheld_fields_are_absent_from_the_agent_dtos()
    {
        var projected = ProjectedPropertyNames();

        foreach (var (name, reason) in CaptureProjection.WithheldFields)
        {
            Assert.False(projected.Contains(name), $"'{name}' is withheld ({reason}) but appears on the agent DTOs.");
        }

        foreach (var (name, reason) in CaptureProjection.WithheldFrameFields)
        {
            Assert.False(projected.Contains(name), $"'{name}' is withheld ({reason}) but appears on the agent DTOs.");
        }
    }

    [Fact]
    public void Every_withheld_field_still_exists_on_the_type_it_is_withheld_from()
    {
        // Otherwise the deny-list rots: a stale entry silently exempts nothing while reading
        // like protection.
        var recordFields = PublicProperties(typeof(RequestRecord)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in CaptureProjection.WithheldFields.Keys)
        {
            Assert.Contains(name, recordFields);
        }

        var frameFields = PublicProperties(typeof(SseEvent)).Concat(PublicProperties(typeof(WebSocketMessage)))
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in CaptureProjection.WithheldFrameFields.Keys)
        {
            Assert.Contains(name, frameFields);
        }
    }

    private static HashSet<string> ProjectedPropertyNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in (ReadOnlySpan<Type>)
                 [
                     typeof(CapturedRequestSummary), typeof(CapturedRequestDetail),
                     typeof(RedactedBody), typeof(SseEventView), typeof(WebSocketFrameView),
                 ])
        {
            foreach (var property in PublicProperties(type)) names.Add(property.Name);
        }

        return names;
    }

    private static PropertyInfo[] PublicProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    // ------------------------------------------------------------------- the projection

    [Fact]
    public void A_summary_carries_no_bodies_and_fingerprints_the_caller()
    {
        var summary = CaptureProjection.Summarize(
            Record(r =>
            {
                r.RequestBody = """{"password":"hunter2!"}""";
                r.RequestBodyOriginalSize = 24;
            }),
            Redactor);

        Assert.Equal(42, summary.Seq);
        Assert.Equal("POST", summary.Method);
        Assert.Equal(201, summary.Status);
        Assert.Equal(24, summary.RequestBytes);

        // The address itself never appears; its fingerprint answers "same device?" instead.
        Assert.NotNull(summary.Client);
        Assert.DoesNotContain("203.0.113", summary.Client, StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_credential_is_masked_in_the_summary_and_reported()
    {
        var summary = CaptureProjection.Summarize(
            Record(path: "/v1/me?access_token=s3cr3tvalue123456&page=2"), Redactor);

        Assert.DoesNotContain("s3cr3tvalue", summary.Path, StringComparison.Ordinal);
        Assert.Contains("page=2", summary.Path, StringComparison.Ordinal);
        Assert.Equal("url:access_token", Assert.Single(summary.Redactions).Location);
    }

    [Fact]
    public void Detail_redacts_headers_and_bodies_and_reports_everything_in_one_list()
    {
        var detail = CaptureProjection.Describe(
            Record(r =>
            {
                r.RequestBody = """{"user":"ada","password":"hunter2!"}""";
                r.RequestContentType = "application/json";
                r.ResponseBody = """{"ok":true}""";
                r.ResponseContentType = "application/json";
            }),
            Redactor);

        Assert.NotNull(detail.RequestHeaders);
        Assert.StartsWith("Bearer [redacted:jwt ", detail.RequestHeaders["Authorization"], StringComparison.Ordinal);

        Assert.NotNull(detail.RequestBody?.Text);
        Assert.DoesNotContain("hunter2!", detail.RequestBody.Text, StringComparison.Ordinal);
        Assert.Contains("\"user\":\"ada\"", detail.RequestBody.Text, StringComparison.Ordinal);

        Assert.Contains(detail.Redactions, n => n.Location == "request.header:Authorization");
        Assert.Contains(detail.Redactions, n => n.Location == "request.body:$.password");
    }

    [Fact]
    public void A_binary_response_body_never_reaches_the_dto()
    {
        var detail = CaptureProjection.Describe(
            Record(r =>
            {
                r.ResponseBody = null;
                r.ResponseBodyBase64 = "iVBORw0KGgoAAAANSUhEUg==";
                r.ResponseContentType = "image/png";
                r.ResponseBodyOriginalSize = 48_213;
            }),
            Redactor);

        Assert.Null(detail.ResponseBody?.Text);
        Assert.Equal("empty", detail.ResponseBody?.Kind);
        Assert.DoesNotContain("iVBORw0KGgo", System.Text.Json.JsonSerializer.Serialize(detail), StringComparison.Ordinal);
    }

    [Fact]
    public void Sse_frames_are_redacted_and_the_dropped_count_survives()
    {
        var detail = CaptureProjection.Describe(
            Record(r =>
            {
                r.IsStream = true;
                r.SseEvents.Add(new SseEvent(
                    DateTimeOffset.UnixEpoch, "message", $$"""{"token":"{{Jwt}}"}""", "1", null, null));
                r.SseEventsDropped = 118;
            }),
            Redactor);

        var frame = Assert.Single(detail.Sse!);
        Assert.NotNull(frame.Data);
        Assert.DoesNotContain(Jwt, frame.Data, StringComparison.Ordinal);
        Assert.Equal(118, detail.SseDropped);
    }

    [Fact]
    public void A_binary_websocket_frame_reports_its_size_and_nothing_else()
    {
        var detail = CaptureProjection.Describe(
            Record(r =>
            {
                r.IsWebSocket = true;
                r.WebSocketMessages.Add(new WebSocketMessage(
                    DateTimeOffset.UnixEpoch, "client", "binary", null, "QklOQVJZ", 6, false, null, null));
            }),
            Redactor);

        var frame = Assert.Single(detail.WebSocket!);
        Assert.Null(frame.Text);
        Assert.Equal(6, frame.Size);
        Assert.DoesNotContain("QklOQVJZ", System.Text.Json.JsonSerializer.Serialize(detail), StringComparison.Ordinal);
        Assert.Contains(detail.Redactions, n => n.Reason == RedactionReason.Binary);
    }

    [Fact]
    public void Body_budgets_never_cut_through_the_middle_of_a_mask()
    {
        var body = $$"""{"pad":"{{new string('x', 200)}}","token":"{{Jwt}}"}""";

        var detail = CaptureProjection.Describe(
            Record(r =>
            {
                r.RequestBody = body;
                r.RequestContentType = "application/json";
            }),
            Redactor,
            // A budget chosen to land inside the mask that replaces the token.
            new CaptureDetailOptions { MaxBodyChars = 225 });

        var text = detail.RequestBody?.Text;
        Assert.NotNull(text);
        Assert.True(detail.RequestBody!.Truncated);

        // Either the mask is whole or it is absent — never a dangling "[redacted:" prefix.
        var open = text.LastIndexOf("[redacted:", StringComparison.Ordinal);
        if (open >= 0) Assert.Contains(']', text[open..]);
    }

    [Fact]
    public void Headers_and_bodies_can_be_left_out_entirely()
    {
        var detail = CaptureProjection.Describe(
            Record(r => r.RequestBody = "{}"),
            Redactor,
            new CaptureDetailOptions { IncludeHeaders = false, IncludeBodies = false, IncludeFrames = false });

        Assert.Null(detail.RequestHeaders);
        Assert.Null(detail.RequestBody);
        Assert.Null(detail.ResponseHeaders);
        Assert.Null(detail.ResponseBody);
    }
}
