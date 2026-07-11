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

    /// <summary>JWT Bearer token validation (machine-to-machine).</summary>
    public JwtAuthOptions? Jwt { get; set; }

    public bool AnyConfigured =>
        Header is not null || (AllowedCidrs?.Count > 0) || (AllowedCountries?.Count > 0) || Oidc is not null || Jwt is not null;
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

/// <summary>
/// JWT Bearer token validation. Configure either an OIDC <see cref="WellKnownEndpoint"/>
/// (which supplies the signing keys via JWKS) or a <see cref="SecretKey"/> for symmetric
/// HS256/HS384/HS512 validation. Optional <see cref="RequiredClaims"/> are matched by value
/// after signature validation.
/// </summary>
public sealed class JwtAuthOptions
{
    /// <summary>OIDC well-known endpoint (e.g. https://login.example.com/.well-known/openid-configuration). Mutually exclusive with <see cref="SecretKey"/>.</summary>
    public string? WellKnownEndpoint { get; set; }

    /// <summary>Expected token issuer (iss claim). Required when <see cref="WellKnownEndpoint"/> is used.</summary>
    public string? Issuer { get; set; }

    /// <summary>Symmetric key for HS256/HS384/HS512 validation. Mutually exclusive with <see cref="WellKnownEndpoint"/>.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Optional expected audience (aud claim). When null, audience is not validated.</summary>
    public string? Audience { get; set; }

    /// <summary>HTTP header to read the token from (default Authorization).</summary>
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>Token prefix to strip from the header value (default "Bearer ").</summary>
    public string HeaderScheme { get; set; } = "Bearer ";

    /// <summary>Clock-skew tolerance in seconds (default 300).</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    /// <summary>Optional claim/value pairs that must be present and equal after signature validation.</summary>
    public Dictionary<string, string>? RequiredClaims { get; set; }
}
