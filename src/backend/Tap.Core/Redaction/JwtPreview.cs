using System.Text.Json;

namespace Tap.Core.Redaction;

/// <summary>Which JWT claims survive into a preview.</summary>
public enum JwtClaimPolicy
{
    /// <summary>Registered and protocol claims only. Private claims are dropped, because that
    /// is where <c>email</c>, <c>name</c>, <c>phone_number</c>, and whatever else an identity
    /// provider felt like stuffing in actually live.</summary>
    Registered,

    /// <summary>Every claim except <c>sub</c> (still fingerprinted). Opt in when you are
    /// debugging your own tokens and know what is in them.</summary>
    All,
}

/// <summary>
/// Describes a JWT without disclosing it.
///
/// <para>A masked token tells you nothing, and "the request had a bearer token" is rarely the
/// question. The questions are: is it expired, does it have the scope, is it from the issuer
/// you expected, is it even the same token as last time. All of those live in the header and
/// the registered claims, none of which are secret — the signature is what makes a JWT usable,
/// and the signature is what this always throws away.</para>
///
/// <para>So a preview is strictly more useful than a mask and strictly no more dangerous:
/// for mobile-app debugging it answers roughly nine questions in ten, with zero replay value
/// if the transcript leaks.</para>
/// </summary>
internal static class JwtPreview
{
    /// <summary>Header fields worth showing. <c>alg</c> especially: a token that arrived
    /// <c>alg=none</c> is a finding, not a detail.</summary>
    private static readonly string[] HeaderClaims = ["alg", "typ", "kid"];

    /// <summary>Registered (RFC 7519) and common protocol claims. Deliberately a list, not a
    /// blocklist: a new identity provider inventing <c>user_email</c> should not leak by
    /// default, and it will not, because it is not on this list.</summary>
    private static readonly string[] SafeClaims =
    [
        "iss", "aud", "exp", "iat", "nbf", "jti",
        "scope", "scp", "azp", "client_id", "token_use", "tid", "ver", "typ", "amr", "acr",
    ];

    /// <summary>Claims rendered as a fingerprint rather than a value: stable identifiers that
    /// are useful to correlate and unwise to print.</summary>
    private static readonly string[] FingerprintedClaims = ["sub", "oid", "upn", "uid"];

    /// <summary>
    /// A compact description, or <c>null</c> when the value does not decode as a JWT after
    /// all — the shape detector matches on structure, and structure can lie.
    /// </summary>
    public static string? Describe(string token, JwtClaimPolicy policy, Func<string, string?> fingerprint)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        var header = Decode(parts[0]);
        var payload = Decode(parts[1]);
        if (header is null && payload is null) return null;

        var fields = new List<string>();

        if (header is not null)
        {
            foreach (var name in HeaderClaims) Append(fields, header.Value, name, null);
        }

        if (payload is not null)
        {
            foreach (var name in SafeClaims)
            {
                if (policy == JwtClaimPolicy.All && name is "exp" or "iat" or "nbf") continue;
                Append(fields, payload.Value, name, null);
            }

            if (policy == JwtClaimPolicy.All)
            {
                foreach (var property in payload.Value.EnumerateObject())
                {
                    if (SafeClaims.Contains(property.Name, StringComparer.Ordinal)) continue;
                    if (FingerprintedClaims.Contains(property.Name, StringComparer.Ordinal)) continue;
                    Append(fields, payload.Value, property.Name, null);
                }
            }

            foreach (var name in FingerprintedClaims) Append(fields, payload.Value, name, fingerprint);

            var expiry = Expiry(payload.Value);
            if (expiry is not null)
            {
                fields.Add($"exp={expiry:yyyy-MM-ddTHH:mm:ssZ}");

                // The single most common cause of a 401 that "worked yesterday". Worth stating
                // outright rather than making a reader subtract timestamps.
                if (expiry < DateTimeOffset.UtcNow) fields.Add("EXPIRED");
            }
        }

        return fields.Count == 0 ? null : string.Join(' ', fields);
    }

    private static void Append(
        List<string> fields, JsonElement source, string name, Func<string, string?>? fingerprint)
    {
        if (name == "exp") return; // rendered separately, as a date rather than an epoch second
        if (!source.TryGetProperty(name, out var value)) return;

        var text = Render(value);
        if (string.IsNullOrEmpty(text)) return;

        if (fingerprint is not null)
        {
            var print = fingerprint(text);
            if (print is null) return;
            fields.Add($"{name}={print}");
            return;
        }

        fields.Add(text.Contains(' ', StringComparison.Ordinal) ? $"{name}=\"{text}\"" : $"{name}={text}");
    }

    private static string? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(
            ' ',
            value.EnumerateArray().Select(Render).Where(v => !string.IsNullOrEmpty(v))),
        _ => null,
    };

    private static DateTimeOffset? Expiry(JsonElement payload)
        => payload.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

    /// <summary>base64url, which is base64 with two characters swapped and the padding left
    /// off. Returns null rather than throwing: this runs on hostile input by definition.</summary>
    private static JsonElement? Decode(string segment)
    {
        try
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            if (padded.Length % 4 != 0) return null;

            var bytes = Convert.FromBase64String(padded);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
