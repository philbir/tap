namespace Tap.Core.Auth;

/// <summary>
/// Inspector-level authentication options. All enabled mechanisms are AND'ed:
/// every request must satisfy every configured check before reaching the upstream.
/// Bound from configuration section <c>Inspector:Auth</c>.
/// </summary>
public sealed class TapAuthOptions
{
    /// <summary>Static API key required in a request header.</summary>
    public HeaderAuthOptions? Header { get; set; }

    /// <summary>Allowlist of CIDR ranges. Real client IP is read from CF-Connecting-IP, then X-Forwarded-For, then RemoteIpAddress.</summary>
    public List<string>? AllowedCidrs { get; set; }

    /// <summary>Allowlist of ISO 3166-1 alpha-2 country codes. Read from the CF-IPCountry header set by Cloudflare.</summary>
    public List<string>? AllowedCountries { get; set; }

    /// <summary>OIDC code-flow with cookie session.</summary>
    public OidcAuthOptions? Oidc { get; set; }

    public bool AnyConfigured =>
        Header is not null || (AllowedCidrs?.Count > 0) || (AllowedCountries?.Count > 0) || Oidc is not null;
}

public sealed class HeaderAuthOptions
{
    public string Name { get; set; } = "X-Api-Key";
    public string Value { get; set; } = "";
}

public sealed class OidcAuthOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string ResponseType { get; set; } = "code";
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    /// <summary>Optional callback path (default /signin-oidc).</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";
}
