using Tap.Workspace.Rendering;

namespace Tap.Tests.Agent;

/// <summary>
/// The redactor is the boundary between "the engine may hold secrets" and "the caller may
/// not see them" — so these tests read as statements about what is allowed out.
/// </summary>
public class SecretRedactorTests
{
    [Fact]
    public void Known_secret_values_are_replaced_wherever_they_appear()
    {
        var redactor = new SecretRedactor(["s3cret-value"], []);
        Assert.Equal(
            "token=*** in a url and *** in a body",
            redactor.Redact("token=s3cret-value in a url and s3cret-value in a body"));
    }

    [Fact]
    public void Null_and_secretless_text_pass_through()
    {
        var redactor = new SecretRedactor(["s3cret-value"], []);
        Assert.Null(redactor.Redact(null));
        Assert.Equal("nothing to hide", redactor.Redact("nothing to hide"));
    }

    [Fact]
    public void Tiny_values_are_not_value_replaced()
    {
        // Replacing every occurrence of "e" would shred the output; short values stay
        // protected by header-name masking instead.
        var redactor = new SecretRedactor(["abc"], []);
        Assert.Equal("abcdef", redactor.Redact("abcdef"));
    }

    [Fact]
    public void A_secret_containing_another_is_replaced_whole()
    {
        var redactor = new SecretRedactor(["inner", "outer-inner-tail"], []);
        Assert.Equal("=***=", redactor.Redact("=outer-inner-tail="));
    }

    [Fact]
    public void Authorization_is_masked_by_name_even_with_no_known_values()
    {
        var headers = SecretRedactor.None.RedactHeaders(new Dictionary<string, string>
        {
            ["authorization"] = "Bearer literal-from-an-auth-file",
            ["Accept"] = "application/json",
        });
        Assert.Equal(SecretRedactor.Mask, headers["authorization"]);
        Assert.Equal("application/json", headers["Accept"]);
    }

    [Fact]
    public void Auth_contributed_header_names_are_masked_case_insensitively()
    {
        var redactor = new SecretRedactor([], ["X-Api-Key"]);
        var headers = redactor.RedactHeaders(new Dictionary<string, string>
        {
            ["x-api-key"] = "whatever-the-renderer-derived",
        });
        Assert.Equal(SecretRedactor.Mask, headers["x-api-key"]);
    }

    [Fact]
    public void Other_headers_are_value_redacted_not_dropped()
    {
        var redactor = new SecretRedactor(["s3cret-value"], []);
        var headers = redactor.RedactHeaders(new Dictionary<string, string>
        {
            ["X-Token"] = "s3cret-value",
            ["X-Trace"] = "abc-123",
        });
        Assert.Equal(SecretRedactor.Mask, headers["X-Token"]);
        Assert.Equal("abc-123", headers["X-Trace"]);
    }

    [Fact]
    public void With_adds_a_value_without_mutating_the_original()
    {
        var original = SecretRedactor.None;
        var extended = original.With("minted-token-value");
        Assert.Equal("minted-token-value", original.Redact("minted-token-value"));
        Assert.Equal(SecretRedactor.Mask, extended.Redact("minted-token-value"));
    }

    [Fact]
    public void A_secret_survives_json_escaping_and_is_still_caught()
    {
        // The CLI redacts serialized JSON; a secret containing a quote or a non-ASCII
        // character appears escaped there (the default encoder writes `"` as "), so the
        // raw value alone would never match.
        var redactor = new SecretRedactor(["""va"lue-sécret"""], []);
        var json = System.Text.Json.JsonSerializer.Serialize(new { body = """token va"lue-sécret here""" });
        var safe = redactor.Redact(json)!;
        Assert.DoesNotContain("lue-s", safe);
        Assert.Contains(SecretRedactor.Mask, safe);
    }

    [Fact]
    public void Merge_unions_values_and_header_names()
    {
        var left = new SecretRedactor(["alpha-secret"], ["X-Left"]);
        var right = new SecretRedactor(["beta-secret"], ["X-Right"]);
        var merged = left.Merge(right);

        Assert.Equal("*** and ***", merged.Redact("alpha-secret and beta-secret"));
        var headers = merged.RedactHeaders(new Dictionary<string, string>
        {
            ["X-Left"] = "1",
            ["X-Right"] = "2",
        });
        Assert.Equal(SecretRedactor.Mask, headers["X-Left"]);
        Assert.Equal(SecretRedactor.Mask, headers["X-Right"]);
    }

    [Fact]
    public void Merging_with_none_changes_nothing()
    {
        var redactor = new SecretRedactor(["alpha-secret"], []);
        Assert.Same(redactor, redactor.Merge(SecretRedactor.None));
        Assert.Same(redactor, SecretRedactor.None.Merge(redactor));
    }
}
