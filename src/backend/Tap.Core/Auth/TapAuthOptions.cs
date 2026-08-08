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

    /// <summary>
    /// Tunnel provider sitting in front of the gate ("cloudflare" | "tailscale" | null).
    /// Set by the host from its own configuration, never bound from <c>Inspector:Auth</c>:
    /// it decides whether the <c>CF-*</c> headers may be believed at all.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// True only when Cloudflare's edge is the thing in front of us, so <c>CF-Connecting-IP</c>
    /// and <c>CF-IPCountry</c> can be attributed to it rather than to whoever sent the request.
    /// </summary>
    public bool TrustsCloudflareHeaders =>
        string.Equals(Provider, "cloudflare", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the header credential is present <em>and</em> has a value to compare against.</summary>
    public bool HeaderConfigured => Header is { } h && !string.IsNullOrEmpty(h.Value);

    /// <summary>True when the JWT options carry a usable key source (symmetric secret or JWKS endpoint).</summary>
    public bool JwtConfigured => Jwt is { } j &&
        (!string.IsNullOrWhiteSpace(j.SecretKey) || !string.IsNullOrWhiteSpace(j.WellKnownEndpoint));

    /// <summary>
    /// The checks that will actually run, in pipeline order. Startup banners and logs are derived
    /// from this rather than from non-nullness, so "auth enforced" can never be printed for a check
    /// the middleware would skip.
    /// </summary>
    public IReadOnlyList<string> EnforcedChecks
    {
        get
        {
            var checks = new List<string>();
            if (HeaderConfigured) checks.Add("header");
            if (JwtConfigured) checks.Add("jwt");
            if (AllowedCidrs?.Count > 0) checks.Add("cidr");
            if (AllowedCountries?.Count > 0) checks.Add("country");
            if (Oidc is not null) checks.Add("oidc");
            return checks;
        }
    }

    /// <summary>
    /// True when at least one check is configured <em>and usable</em>. A credential that is present
    /// but blank (an unset <c>$TAP_KEY</c>, a missing config key) must not count: it would register
    /// the middleware with nothing to enforce and pass every request straight to the upstream.
    /// </summary>
    public bool AnyConfigured => EnforcedChecks.Count > 0;
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

    /// <summary>
    /// Expected token issuer (iss claim). Required when <see cref="WellKnownEndpoint"/> is used —
    /// a multi-tenant JWKS signs tokens for every tenant, so without it any of them would validate.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>Symmetric key for HS256/HS384/HS512 validation. Mutually exclusive with <see cref="WellKnownEndpoint"/>.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Expected audience (aud claim). Required unless <see cref="AllowAnyAudience"/> is set.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Explicit opt-out from audience validation. Leaving <see cref="Audience"/> unset is a
    /// configuration mistake, not a request to accept every audience, so the opt-out has to be said
    /// out loud.
    /// </summary>
    public bool AllowAnyAudience { get; set; }

    /// <summary>HTTP header to read the token from (default Authorization).</summary>
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>Token prefix to strip from the header value (default "Bearer ").</summary>
    public string HeaderScheme { get; set; } = "Bearer ";

    /// <summary>Clock-skew tolerance in seconds (default 300).</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    /// <summary>Optional claim/value pairs that must be present and equal after signature validation.</summary>
    public Dictionary<string, string>? RequiredClaims { get; set; }
}
