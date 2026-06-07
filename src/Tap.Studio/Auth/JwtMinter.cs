using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tap.Studio.Auth;

/// <summary>
/// Mints a signed JWT from raw parameters. Mirrors dreamr's <c>JwtIssuer.CreateToken</c>
/// but supports both symmetric (HMAC) and asymmetric (RSA / ECDSA) signing algorithms so
/// the same auth profile shape can drive service-to-service "client assertion" patterns
/// as well as dev-mode HMAC tokens.
///
/// Standard claims (<c>iss</c>, <c>aud</c>, <c>sub</c>, <c>exp</c>, <c>iat</c>, <c>jti</c>)
/// are auto-filled from the request; any keys present in <c>PayloadJson</c> override them.
/// We assemble the JWT by hand (base64url(header).base64url(payload).base64url(sig))
/// instead of pulling in <c>System.IdentityModel.Tokens.Jwt</c> — the dependency would
/// be a 600KB tax for ~30 lines of code.
/// </summary>
internal static class JwtMinter
{
    public static string Mint(JwtMintRequest req)
    {
        var alg = req.Algorithm.ToUpperInvariant();

        // Header. kid is optional — if the caller passes one we include it verbatim,
        // otherwise (for HMAC) we derive a stable kid from the key bytes so different
        // secrets can be told apart in logs without leaking them.
        var header = new Dictionary<string, object> { ["alg"] = alg, ["typ"] = "JWT" };
        if (!string.IsNullOrEmpty(req.KeyId)) header["kid"] = req.KeyId;
        else if (IsHmac(alg))
            header["kid"] = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(req.Key)));

        // Payload. Start with the well-known claims, then merge whatever the user provided
        // — later keys win, so caller can override iss/aud/etc via PayloadJson.
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(req.ExpiresIn).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (!string.IsNullOrEmpty(req.Issuer)) payload["iss"] = req.Issuer;
        if (!string.IsNullOrEmpty(req.Audience)) payload["aud"] = req.Audience;
        if (!string.IsNullOrEmpty(req.Subject)) payload["sub"] = req.Subject;

        if (!string.IsNullOrWhiteSpace(req.PayloadJson))
        {
            try
            {
                var extras = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(req.PayloadJson!);
                if (extras is not null)
                {
                    foreach (var kv in extras) payload[kv.Key] = kv.Value;
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"'payload' is not valid JSON: {ex.Message}");
            }
        }

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);
        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsigned = $"{headerB64}.{payloadB64}";

        var signature = Sign(alg, unsigned, req.Key);
        return $"{unsigned}.{Base64UrlEncode(signature)}";
    }

    private static bool IsHmac(string alg) => alg is "HS256" or "HS384" or "HS512";

    private static byte[] Sign(string alg, string data, string key)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);
        return alg switch
        {
            "HS256" => SignHmac(dataBytes, Encoding.UTF8.GetBytes(key), HashAlgorithmName.SHA256),
            "HS384" => SignHmac(dataBytes, Encoding.UTF8.GetBytes(key), HashAlgorithmName.SHA384),
            "HS512" => SignHmac(dataBytes, Encoding.UTF8.GetBytes(key), HashAlgorithmName.SHA512),

            "RS256" => SignRsa(dataBytes, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "RS384" => SignRsa(dataBytes, key, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
            "RS512" => SignRsa(dataBytes, key, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1),

            "PS256" => SignRsa(dataBytes, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            "PS384" => SignRsa(dataBytes, key, HashAlgorithmName.SHA384, RSASignaturePadding.Pss),
            "PS512" => SignRsa(dataBytes, key, HashAlgorithmName.SHA512, RSASignaturePadding.Pss),

            "ES256" => SignEcdsa(dataBytes, key, HashAlgorithmName.SHA256),
            "ES384" => SignEcdsa(dataBytes, key, HashAlgorithmName.SHA384),
            "ES512" => SignEcdsa(dataBytes, key, HashAlgorithmName.SHA512),

            _ => throw new NotSupportedException(
                $"JWT signing algorithm '{alg}' is not supported. " +
                "Choose one of: HS256/384/512, RS256/384/512, PS256/384/512, ES256/384/512."),
        };
    }

    private static byte[] SignHmac(byte[] data, byte[] key, HashAlgorithmName alg)
    {
        using HMAC h = alg.Name switch
        {
            nameof(HashAlgorithmName.SHA256) => new HMACSHA256(key),
            nameof(HashAlgorithmName.SHA384) => new HMACSHA384(key),
            nameof(HashAlgorithmName.SHA512) => new HMACSHA512(key),
            _ => throw new NotSupportedException(alg.Name),
        };
        return h.ComputeHash(data);
    }

    private static byte[] SignRsa(byte[] data, string pem, HashAlgorithmName alg, RSASignaturePadding padding)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.SignData(data, alg, padding);
    }

    /// <summary>
    /// JWT ES* signatures use raw R || S (P1363 format), not the ASN.1 DER form
    /// <see cref="ECDsa.SignData(byte[], HashAlgorithmName)"/> returns by default.
    /// </summary>
    private static byte[] SignEcdsa(byte[] data, string pem, HashAlgorithmName alg)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        return ec.SignData(data, alg, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static string Base64UrlEncode(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Input record for <see cref="JwtMinter.Mint"/>. Mirrors dreamr's
/// <c>CreateRawJwtTokenRequest</c> with a few extra fields (Subject, KeyId) that fall out
/// naturally now that we don't have the symmetric-only restriction.</summary>
internal sealed record JwtMintRequest
{
    public required string Algorithm { get; init; }
    public required string Key { get; init; }
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public string? Subject { get; init; }
    public string? KeyId { get; init; }
    /// <summary>Optional raw JSON of additional claims. Merged onto the auto-filled
    /// claims; same-named entries override.</summary>
    public string? PayloadJson { get; init; }
    /// <summary>Seconds from now for the <c>exp</c> claim.</summary>
    public required int ExpiresIn { get; init; }
}
