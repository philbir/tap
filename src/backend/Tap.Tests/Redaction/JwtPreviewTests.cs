using System.Text;
using Tap.Core.Redaction;

namespace Tap.Tests.Redaction;

/// <summary>
/// A JWT is described rather than merely hidden. The signature — the only part that makes a
/// token usable — always goes; what survives answers the questions people actually have about
/// a credential they cannot see.
/// </summary>
public class JwtPreviewTests
{
    private static string Token(string header, string payload, string signature = "c2ln")
        => $"{Base64Url(header)}.{Base64Url(payload)}.{signature}";

    private static string Base64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Mask(string token, CaptureRedactionOptions? options = null)
        => new CaptureRedactor(options)
            .Headers(new Dictionary<string, string> { ["Authorization"] = "Bearer " + token })
            .Headers["Authorization"];

    [Fact]
    public void The_header_and_registered_claims_are_shown()
    {
        var value = Mask(Token(
            """{"alg":"RS256","typ":"JWT","kid":"key-7"}""",
            """{"iss":"https://auth.example.com","scope":"read:orders write:orders"}"""));

        Assert.Contains("alg=RS256", value, StringComparison.Ordinal);
        Assert.Contains("kid=key-7", value, StringComparison.Ordinal);
        Assert.Contains("iss=https://auth.example.com", value, StringComparison.Ordinal);
        Assert.Contains("scope=\"read:orders write:orders\"", value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_is_never_shown()
    {
        var value = Mask(Token("""{"alg":"HS256"}""", """{"iss":"x"}""", "dGhpc2lzdGhlc2lnbmF0dXJl"));

        Assert.DoesNotContain("dGhpc2lzdGhlc2lnbmF0dXJl", value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subject_is_fingerprinted_never_printed()
    {
        var value = Mask(Token("""{"alg":"HS256"}""", """{"sub":"user-90210-alice"}"""));

        Assert.DoesNotContain("user-90210-alice", value, StringComparison.Ordinal);
        Assert.Matches(@"sub=#[0-9a-f]{8}", value);
    }

    [Fact]
    public void Private_claims_are_dropped_by_default_because_that_is_where_the_pii_lives()
    {
        var value = Mask(Token(
            """{"alg":"HS256"}""",
            """{"iss":"x","email":"ada@example.com","name":"Ada Lovelace","phone_number":"+41791234567"}"""));

        Assert.DoesNotContain("ada@example.com", value, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada Lovelace", value, StringComparison.Ordinal);
        Assert.DoesNotContain("41791234567", value, StringComparison.Ordinal);
        Assert.Contains("iss=x", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_claims_can_be_opted_into()
    {
        var value = Mask(
            Token("""{"alg":"HS256"}""", """{"tenant":"acme","plan":"pro"}"""),
            new CaptureRedactionOptions { JwtClaims = JwtClaimPolicy.All });

        Assert.Contains("tenant=acme", value, StringComparison.Ordinal);
        Assert.Contains("plan=pro", value, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_token_says_so()
    {
        var expired = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        var value = Mask(Token("""{"alg":"HS256"}""", $$"""{"exp":{{expired}}}"""));

        Assert.Contains("EXPIRED", value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_token_is_dated_but_not_flagged()
    {
        var future = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var value = Mask(Token("""{"alg":"HS256"}""", $$"""{"exp":{{future}}}"""));

        Assert.Matches(@"exp=\d{4}-\d{2}-\d{2}T", value);
        Assert.DoesNotContain("EXPIRED", value, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alg_none_token_is_visible_as_such()
        => Assert.Contains(
            "alg=none",
            Mask(Token("""{"alg":"none"}""", """{"sub":"anyone-at-all"}""", "")),
            StringComparison.Ordinal);

    [Fact]
    public void Something_jwt_shaped_that_does_not_decode_is_still_masked()
    {
        // The shape detector matches on structure, and structure can lie. A preview that
        // cannot be produced must not become a value that gets shown.
        var value = Mask("eyJub3Rqc29u.eyJhbHNvbm90.c2ln");

        Assert.Contains("[redacted:", value, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJub3Rqc29u", value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preview_never_reproduces_the_token_itself()
    {
        var token = Token("""{"alg":"RS256","typ":"JWT"}""", """{"iss":"x","sub":"abc12345"}""");
        var value = Mask(token);

        Assert.DoesNotContain(token, value, StringComparison.Ordinal);
        foreach (var part in token.Split('.'))
        {
            Assert.DoesNotContain(part, value, StringComparison.Ordinal);
        }
    }
}
