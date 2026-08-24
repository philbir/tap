using System.Text.RegularExpressions;

namespace Tap.Core.Redaction;

/// <summary>
/// Shape detectors for layer 4 — the credentials and personal data that no key name
/// announces, because they turn up in a log line, a stack trace, a free-text field, or a
/// header nobody thought about.
///
/// <para>Every pattern here is deliberately simple: no nested quantifiers, no backreferences,
/// no lookaround. Bodies reach these detectors straight off the wire from whoever felt like
/// sending them, so a pattern that can backtrack catastrophically is a denial-of-service hole
/// rather than a redaction bug. Each carries a match timeout as well, and
/// <see cref="CaptureRedactor"/> treats a timeout as "mask the whole payload" — the one
/// outcome that cannot leak.</para>
/// </summary>
internal static partial class CapturePatterns
{
    private const int TimeoutMs = 1000;

    /// <summary>A detector: what it finds, how, and an optional second opinion for shapes
    /// that are cheap to match and expensive to get wrong.</summary>
    internal sealed record Detector(string Kind, Regex Pattern, Func<string, bool>? Accept = null);

    /// <summary>
    /// Order matters. The structured, unmistakable shapes run first so their innards are
    /// already masked before the broad detectors (email, digits) get a look at them —
    /// otherwise a JWT payload's <c>@</c> would be reported as a leaked email address inside a
    /// token that was about to be redacted anyway.
    /// </summary>
    internal static IReadOnlyList<Detector> All { get; } =
    [
        new("pem", PrivateKeyBlock()),
        new("jwt", Jwt()),
        new("stripe", StripeKey()),
        new("github", GitHubToken()),
        new("slack", SlackToken()),
        new("aws", AwsAccessKeyId()),
        new("gcp", GoogleApiKey()),
        new("iban", Iban()),
        new("pan", PrimaryAccountNumber(), IsLuhnValid),
        new("email", EmailAddress()),
        new("phone", E164PhoneNumber()),
    ];

    [GeneratedRegex(
        @"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z]+ )?PRIVATE KEY-----",
        RegexOptions.None, TimeoutMs)]
    private static partial Regex PrivateKeyBlock();

    /// <summary>Three base64url segments. The signature may be empty (<c>alg=none</c>), which
    /// is precisely the token you most want to notice.</summary>
    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*",
        RegexOptions.None, TimeoutMs)]
    private static partial Regex Jwt();

    /// <summary>Whether a value is itself a JWT, used to pick a mask kind. A token that the
    /// reader can at least name as a JWT is far more debuggable than an anonymous blob.</summary>
    internal static bool LooksLikeJwt(string value)
    {
        try
        {
            return Jwt().IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\b[sprk]k_(?:live|test)_[A-Za-z0-9]{10,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex AwsAccessKeyId();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex Iban();

    /// <summary>13–19 digits, optionally grouped by spaces or dashes. Far too broad on its
    /// own — an order id is also a run of digits — so every hit is Luhn-checked before it
    /// counts.</summary>
    [GeneratedRegex(@"\b(?:[0-9][ -]?){12,18}[0-9]\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex PrimaryAccountNumber();

    [GeneratedRegex(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.None, TimeoutMs)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"\+[1-9][0-9]{7,14}\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex E164PhoneNumber();

    /// <summary>The check digit every real card number carries. Turns a digit-run detector
    /// that would shred order ids and timestamps into one that mostly fires on cards.</summary>
    private static bool IsLuhnValid(string candidate)
    {
        var sum = 0;
        var digits = 0;
        var doubling = false;

        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (c is ' ' or '-') continue;
            if (!char.IsAsciiDigit(c)) return false;

            var value = c - '0';
            if (doubling)
            {
                value *= 2;
                if (value > 9) value -= 9;
            }

            sum += value;
            digits++;
            doubling = !doubling;
        }

        return digits is >= 13 and <= 19 && sum % 10 == 0;
    }
}
