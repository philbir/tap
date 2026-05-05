using Tap.Core.Auth;

namespace Aspire.Hosting;

/// <summary>
/// Tap-level authentication: gates the proxy branch so untrusted internet traffic
/// from a Cloudflare tunnel can be filtered before it reaches the upstream.
/// </summary>
public static class TapAuthExtensions
{
    /// <summary>
    /// Require an API key in a request header. Backed by <see cref="HeaderAuthOptions"/>.
    /// </summary>
    public static TapHandle WithHeaderAuth(this TapHandle tap, string headerName, string headerValue)
    {
        tap.WithEnvironment("Inspector__Auth__Header__Name", headerName);
        tap.WithEnvironment("Inspector__Auth__Header__Value", headerValue);
        return tap;
    }

    /// <summary>
    /// Allowlist source IPs (CIDR). Real client IP is read from CF-Connecting-IP / X-Forwarded-For.
    /// Use Cloudflare's published IP ranges (https://www.cloudflare.com/ips/) to restrict to
    /// traffic that actually went through the tunnel.
    /// </summary>
    public static TapHandle WithIpAllowList(this TapHandle tap, params string[] cidrs)
    {
        for (var i = 0; i < cidrs.Length; i++)
        {
            tap.WithEnvironment($"Inspector__Auth__AllowedCidrs__{i}", cidrs[i]);
        }
        return tap;
    }

    /// <summary>
    /// Allowlist ISO 3166-1 alpha-2 country codes, read from the <c>CF-IPCountry</c> header
    /// set by Cloudflare on tunneled requests.
    /// </summary>
    public static TapHandle WithCountryAllowList(this TapHandle tap, params string[] countries)
    {
        for (var i = 0; i < countries.Length; i++)
        {
            tap.WithEnvironment($"Inspector__Auth__AllowedCountries__{i}", countries[i].ToUpperInvariant());
        }
        return tap;
    }

    /// <summary>
    /// Require an OIDC sign-in (cookie session) before any tunneled request reaches the upstream.
    /// </summary>
    public static TapHandle WithOidcAuth(
        this TapHandle tap,
        string authority,
        string clientId,
        string? clientSecret = null,
        string callbackPath = "/signin-oidc",
        params string[] scopes)
    {
        tap.WithEnvironment("Inspector__Auth__Oidc__Authority", authority);
        tap.WithEnvironment("Inspector__Auth__Oidc__ClientId", clientId);
        if (!string.IsNullOrEmpty(clientSecret))
        {
            tap.WithEnvironment("Inspector__Auth__Oidc__ClientSecret", clientSecret);
        }
        tap.WithEnvironment("Inspector__Auth__Oidc__CallbackPath", callbackPath);
        var actualScopes = scopes is { Length: > 0 } ? scopes : ["openid", "profile", "email"];
        for (var i = 0; i < actualScopes.Length; i++)
        {
            tap.WithEnvironment($"Inspector__Auth__Oidc__Scopes__{i}", actualScopes[i]);
        }
        return tap;
    }
}
