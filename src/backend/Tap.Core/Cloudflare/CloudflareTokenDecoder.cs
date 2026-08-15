using System.Text.Json;

namespace Tap.Core.Cloudflare;

/// <summary>
/// Decodes a cloudflared connector token (the long base64 string from the Zero Trust UI).
/// The token is base64-encoded JSON of the form { "a": account, "t": tunnelId, "s": secret }.
/// </summary>
public static class CloudflareTokenDecoder
{
    public readonly record struct DecodedToken(string AccountId, string TunnelId);

    public static bool TryDecode(string token, out DecodedToken result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var padded = token.Length % 4 == 0 ? token : token + new string('=', 4 - token.Length % 4);
            var bytes = Convert.FromBase64String(padded);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("a", out var a) && doc.RootElement.TryGetProperty("t", out var t))
            {
                var account = a.GetString();
                var tunnel = t.GetString();
                if (!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(tunnel))
                {
                    result = new DecodedToken(account, tunnel);
                    return true;
                }
            }
        }
        catch { /* swallow — token may be opaque or malformed */ }
        return false;
    }
}
