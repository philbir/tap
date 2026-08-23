using Tap.Core.Redaction;

namespace Tap.Tests.Redaction;

/// <summary>
/// The five detection layers, plus the two properties the whole agent surface rests on:
/// redaction is reported rather than silent, and nothing here ever hands a value back.
/// </summary>
public class CaptureRedactorTests
{
    private static readonly CaptureRedactor Redactor = new();

    private const string Jwt =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9." +
        "eyJzdWIiOiIxMjM0NTY3ODkwIiwic2NvcGUiOiJyZWFkOm9yZGVycyJ9." +
        "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers)
        => headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ layer 1: headers

    [Fact]
    public void Authorization_keeps_the_scheme_and_drops_the_credential()
    {
        var result = Redactor.Headers(Headers(("Authorization", "Bearer " + Jwt)));

        var value = result.Headers["Authorization"];
        Assert.StartsWith("Bearer [redacted:jwt ", value, StringComparison.Ordinal);
        Assert.DoesNotContain(Jwt, value, StringComparison.Ordinal);

        var note = Assert.Single(result.Notes);
        Assert.Equal("header:Authorization", note.Location);
        Assert.Equal(RedactionReason.SensitiveHeader, note.Reason);
        Assert.NotNull(note.Fingerprint);
    }

    [Fact]
    public void Basic_credentials_are_named_as_basic()
        => Assert.StartsWith(
            "Basic [redacted:basic ",
            Redactor.Headers(Headers(("Authorization", "Basic YWxhZGRpbjpvcGVuc2VzYW1l"))).Headers["Authorization"],
            StringComparison.Ordinal);

    [Fact]
    public void Cookies_are_redacted_per_cookie_so_the_boring_ones_survive()
    {
        var result = Redactor.Headers(Headers((
            "Cookie", "sid=9f8c2b1a4e6d7c3b5a0f9e8d7c6b5a4f; theme=dark; locale=de-CH")));

        var value = result.Headers["Cookie"];
        Assert.DoesNotContain("9f8c2b1a", value, StringComparison.Ordinal);
        Assert.Contains("theme=dark", value, StringComparison.Ordinal);
        Assert.Contains("locale=de-CH", value, StringComparison.Ordinal);

        var note = Assert.Single(result.Notes);
        Assert.Equal("header:Cookie/sid", note.Location);
        Assert.Equal(RedactionReason.Cookie, note.Reason);
    }

    [Fact]
    public void Set_cookie_attributes_are_not_mistaken_for_cookies()
    {
        var value = Redactor.Headers(Headers((
                "Set-Cookie", "session=abcdef0123456789abcdef; Path=/; HttpOnly; SameSite=Lax")))
            .Headers["Set-Cookie"];

        Assert.DoesNotContain("abcdef0123456789", value, StringComparison.Ordinal);
        Assert.Contains("Path=/", value, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", value, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_header_is_sensitive_by_suffix()
        => Assert.Equal(
            RedactionReason.SensitiveHeader,
            Assert.Single(Redactor.Headers(Headers(("X-Hub-Signature-256", "sha256=deadbeefcafe"))).Notes).Reason);

    [Fact]
    public void An_unremarkable_header_is_still_scanned_for_shapes()
    {
        var result = Redactor.Headers(Headers(("X-Debug-Context", "retrying with " + Jwt)));

        Assert.Contains("retrying with [redacted:jwt ", result.Headers["X-Debug-Context"], StringComparison.Ordinal);
        Assert.Equal(RedactionReason.Pattern("jwt"), Assert.Single(result.Notes).Reason);
    }

    [Fact]
    public void Ordinary_headers_are_left_alone()
    {
        var result = Redactor.Headers(Headers(("Content-Type", "application/json"), ("Accept", "*/*")));

        Assert.Equal("application/json", result.Headers["Content-Type"]);
        Assert.Empty(result.Notes);
    }

    // -------------------------------------------------------------- layer 2: path and query

    [Fact]
    public void Query_credentials_go_but_the_rest_of_the_url_stays()
    {
        var result = Redactor.Target("/v1/orders?access_token=s3cr3tvalue123456&page=2&sort=asc");

        Assert.DoesNotContain("s3cr3tvalue", result.Text, StringComparison.Ordinal);
        Assert.Contains("/v1/orders?", result.Text, StringComparison.Ordinal);
        Assert.Contains("page=2", result.Text, StringComparison.Ordinal);
        Assert.Contains("sort=asc", result.Text, StringComparison.Ordinal);
        Assert.Equal("query:access_token", Assert.Single(result.Notes).Location);
    }

    [Fact]
    public void An_oauth_code_is_secret_but_the_state_nonce_is_not()
    {
        var result = Redactor.Target("/callback?code=4%2F0AeanS0abcdef&state=xyz789");

        Assert.DoesNotContain("0AeanS0abcdef", result.Text, StringComparison.Ordinal);
        Assert.Contains("state=xyz789", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_smuggled_into_a_path_segment_is_caught()
        => Assert.DoesNotContain(Jwt, Redactor.Target("/impersonate/" + Jwt).Text, StringComparison.Ordinal);

    [Fact]
    public void A_url_without_a_query_survives_intact()
    {
        var result = Redactor.Target("/healthz");

        Assert.Equal("/healthz", result.Text);
        Assert.Empty(result.Notes);
    }

    // ------------------------------------------------------------- layer 3: structured keys

    [Fact]
    public void A_secret_json_key_loses_its_value_and_keeps_its_path()
    {
        var body = Redactor.Body(
            """{"user":{"name":"ada","password":"hunter2!"},"orderId":4021}""",
            "application/json", 64);

        Assert.Equal("json", body.Kind);
        Assert.NotNull(body.Text);
        Assert.DoesNotContain("hunter2!", body.Text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"ada\"", body.Text, StringComparison.Ordinal);
        Assert.Contains("\"orderId\":4021", body.Text, StringComparison.Ordinal);

        var note = Assert.Single(body.Notes);
        Assert.Equal("body:$.user.password", note.Location);
        Assert.Equal(RedactionReason.KnownKey, note.Reason);
    }

    [Fact]
    public void A_secret_shaped_key_holding_an_object_is_recursed_into_not_blanked()
    {
        var body = Redactor.Body(
            """{"auth":{"scheme":"basic","password":"hunter2!"}}""", "application/json", 48);

        Assert.NotNull(body.Text);
        Assert.Contains("\"scheme\":\"basic\"", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2!", body.Text, StringComparison.Ordinal);
        Assert.Equal("body:$.auth.password", Assert.Single(body.Notes).Location);
    }

    [Fact]
    public void A_null_secret_stays_null_because_nothing_was_sent()
    {
        var body = Redactor.Body("""{"token":null}""", "application/json", 14);

        Assert.Equal("""{"token":null}""", body.Text);
        Assert.Empty(body.Notes);
    }

    [Fact]
    public void Array_elements_get_indexed_paths()
    {
        var body = Redactor.Body(
            """{"items":[{"sku":"A1"},{"apiKey":"live_abcdef123456"}]}""", "application/json", 64);

        Assert.Equal("body:$.items[1].apiKey", Assert.Single(body.Notes).Location);
    }

    [Fact]
    public void Form_bodies_are_treated_like_query_strings()
    {
        var body = Redactor.Body(
            "grant_type=password&username=ada&password=hunter2!", "application/x-www-form-urlencoded", 50);

        Assert.Equal("form", body.Kind);
        Assert.NotNull(body.Text);
        Assert.Contains("username=ada", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2!", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_json_body_still_loses_its_secrets()
    {
        // The store caps bodies at 1 MB, so half-parsed JSON is the common case, not the odd one.
        var body = Redactor.Body(
            """{"id":7,"password":"hunter2!","note":"cut off here""",
            "application/json", 2_000_000, truncated: true);

        Assert.NotNull(body.Text);
        Assert.DoesNotContain("hunter2!", body.Text, StringComparison.Ordinal);
        Assert.Contains(body.Notes, n => n.Reason == RedactionReason.Unparseable);
        Assert.Contains(body.Notes, n => n.Reason == RedactionReason.KnownKey);
    }

    // -------------------------------------------------------------------- layer 4: patterns

    [Theory]
    [InlineData("contact ada@example.com for access", "email")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE leaked", "aws")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789", "github")]
    [InlineData("using sk_live_abcdef1234567890", "stripe")]
    [InlineData("call +41791234567 now", "phone")]
    public void Shape_detectors_fire_on_free_text(string input, string kind)
    {
        var body = Redactor.Body(input, "text/plain", input.Length);

        Assert.NotNull(body.Text);
        Assert.Contains($"[redacted:{kind} ", body.Text, StringComparison.Ordinal);
        Assert.Equal(RedactionReason.Pattern(kind), Assert.Single(body.Notes).Reason);
    }

    [Fact]
    public void A_luhn_valid_card_number_is_redacted()
    {
        var body = Redactor.Body("card 4242 4242 4242 4242 charged", "text/plain", 32);

        Assert.NotNull(body.Text);
        Assert.DoesNotContain("4242 4242", body.Text, StringComparison.Ordinal);
        Assert.Equal(RedactionReason.Pattern("pan"), Assert.Single(body.Notes).Reason);
    }

    [Fact]
    public void A_long_number_that_is_not_a_card_survives()
    {
        var body = Redactor.Body("order 1111111111111111 shipped", "text/plain", 30);

        Assert.Equal("order 1111111111111111 shipped", body.Text);
        Assert.Empty(body.Notes);
    }

    // ---------------------------------------------------------- layer 5: binary and multipart

    [Fact]
    public void Binary_bodies_yield_metadata_and_nothing_else()
    {
        var body = Redactor.Body("PNG binary blob", "image/png", 48_213);

        Assert.Null(body.Text);
        Assert.Equal("binary", body.Kind);
        Assert.Equal(48_213, body.OriginalSize);
        Assert.NotNull(body.Sha256);
        Assert.Equal(RedactionReason.Binary, Assert.Single(body.Notes).Reason);
    }

    [Fact]
    public void Multipart_uploads_are_summarised_never_shown()
    {
        var payload = string.Join("\r\n",
            "--X",
            "Content-Disposition: form-data; name=\"note\"",
            "",
            "hello",
            "--X",
            "Content-Disposition: form-data; name=\"file\"; filename=\"passport.png\"",
            "Content-Type: image/png",
            "",
            "BINARYBYTESHERE",
            "--X--");

        var body = Redactor.Body(payload, "multipart/form-data; boundary=X", payload.Length);

        Assert.Equal("multipart", body.Kind);
        Assert.NotNull(body.Text);
        Assert.DoesNotContain("BINARYBYTESHERE", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", body.Text, StringComparison.Ordinal);
        Assert.Contains("[multipart: 2 parts]", body.Text, StringComparison.Ordinal);
        Assert.Contains("filename=passport.png", body.Text, StringComparison.Ordinal);
        Assert.Contains("contentType=image/png", body.Text, StringComparison.Ordinal);
        Assert.Equal(2, body.Notes.Count);
    }

    [Fact]
    public void An_empty_body_is_not_a_redaction()
    {
        var body = Redactor.Body(null, "application/json", 0);

        Assert.Null(body.Text);
        Assert.Equal("empty", body.Kind);
        Assert.Empty(body.Notes);
    }

    // ------------------------------------------------------------- fingerprints and masks

    [Fact]
    public void The_same_value_in_two_places_carries_the_same_fingerprint()
    {
        var result = Redactor.Headers(Headers(
            ("Authorization", "Bearer " + Jwt),
            ("X-Forwarded-Authorization", "Bearer " + Jwt)));

        Assert.Equal(2, result.Notes.Count);
        Assert.Equal(result.Notes[0].Fingerprint, result.Notes[1].Fingerprint);
        Assert.NotNull(result.Notes[0].Fingerprint);
    }

    [Fact]
    public void Different_values_carry_different_fingerprints()
        => Assert.NotEqual(Redactor.Fingerprint("token-alpha-1"), Redactor.Fingerprint("token-bravo-2"));

    [Fact]
    public void Fingerprints_do_not_survive_across_redactors_because_the_salt_is_per_run()
        => Assert.NotEqual(new CaptureRedactor().Fingerprint(Jwt), new CaptureRedactor().Fingerprint(Jwt));

    [Fact]
    public void Short_values_are_not_fingerprinted_at_all()
        => Assert.Null(Redactor.Fingerprint("abc"));

    [Fact]
    public void A_mask_names_its_kind_fingerprint_and_length()
    {
        var value = Redactor.Headers(Headers(("Authorization", "Basic YWxhZGRpbjpvcGVuc2VzYW1l"))).Headers["Authorization"];

        Assert.Matches(@"^Basic \[redacted:basic #[0-9a-f]{8} len=\d+\]$", value);
    }

    [Fact]
    public void A_secret_that_needs_json_escaping_is_still_caught()
    {
        // The value contains a quote and a backslash, so it appears escaped inside the
        // serialized body. Redaction runs on the parsed value rather than the raw text, which
        // is what keeps this from slipping past — the trap Tap.Workspace's SecretRedactor has
        // to solve with JSON-escaped variants.
        var body = Redactor.Body(
            """{"password":"hun\"ter\\2"}""", "application/json", 32);

        Assert.NotNull(body.Text);
        Assert.DoesNotContain("hun", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ter", body.Text, StringComparison.Ordinal);
        Assert.Equal(RedactionReason.KnownKey, Assert.Single(body.Notes).Reason);
    }
}
