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
    /// <exception cref="ArgumentException">
    /// The name or the value is blank. A blank value is almost always a missing configuration key
    /// (<c>WithHeaderAuth("X-Tap-Key", cfg["Tap:Key"]!)</c>) and would otherwise publish a tunnel
    /// whose gate has nothing to compare against.
    /// </exception>
    public static TapHandle WithHeaderAuth(this TapHandle tap, string headerName, string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            throw new ArgumentException("WithHeaderAuth requires a header name.", nameof(headerName));
        }
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            throw new ArgumentException(
                $"WithHeaderAuth('{headerName}') requires a non-empty value — a blank key registers the " +
                "auth gate with nothing to check. Check that the configuration key it comes from is set.",
                nameof(headerValue));
        }

        tap.WithEnvironment("Inspector__Auth__Header__Name", headerName);
        tap.WithEnvironment("Inspector__Auth__Header__Value", headerValue);
        return tap;
    }

    /// <summary>
    /// Allowlist source IPs (CIDR). The client IP comes from <c>CF-Connecting-IP</c> on a Cloudflare
    /// tap and from the forwarded-headers resolution otherwise — never from a header a client could
    /// have set itself. Use Cloudflare's published IP ranges (https://www.cloudflare.com/ips/) to
    /// restrict to traffic that actually went through the tunnel.
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
    /// set by Cloudflare on tunneled requests. Cloudflare-only: on any other provider there is no
    /// trustworthy country to read, and the gate rejects rather than silently skipping the check.
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
    /// Require a valid JWT Bearer token. Configure either an OIDC well-known endpoint
    /// (with expected issuer) for asymmetric validation against the published JWKS, or
    /// a shared <paramref name="secretKey"/> for symmetric HS256/HS384/HS512.
    /// Optional <paramref name="requiredClaims"/> are matched by exact value after the
    /// signature is validated.
    /// </summary>
    /// <param name="allowAnyAudience">
    /// Explicitly accept any <c>aud</c> claim. Required when <paramref name="audience"/> is omitted:
    /// an unset audience is a configuration gap, not a decision to accept tokens minted for a
    /// different application.
    /// </param>
    public static TapHandle WithJwtAuth(
        this TapHandle tap,
        string? wellKnownEndpoint = null,
        string? issuer = null,
        string? secretKey = null,
        string? audience = null,
        IDictionary<string, string>? requiredClaims = null,
        bool allowAnyAudience = false)
    {
        if (string.IsNullOrWhiteSpace(wellKnownEndpoint) && string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException("WithJwtAuth requires either a wellKnownEndpoint or a secretKey.");
        }

        // A multi-tenant JWKS (login.microsoftonline.com/common and friends) signs tokens for every
        // tenant, so a JWKS gate without an issuer check accepts all of them.
        if (!string.IsNullOrWhiteSpace(wellKnownEndpoint) && string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException(
                "WithJwtAuth requires an issuer when a wellKnownEndpoint is used.", nameof(issuer));
        }

        if (string.IsNullOrWhiteSpace(audience) && !allowAnyAudience)
        {
            throw new ArgumentException(
                "WithJwtAuth requires an audience, or allowAnyAudience: true to state that tokens for " +
                "any audience are acceptable.", nameof(audience));
        }

        if (!string.IsNullOrWhiteSpace(wellKnownEndpoint))
        {
            tap.WithEnvironment("Inspector__Auth__Jwt__WellKnownEndpoint", wellKnownEndpoint);
        }
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            tap.WithEnvironment("Inspector__Auth__Jwt__Issuer", issuer);
        }
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            tap.WithEnvironment("Inspector__Auth__Jwt__SecretKey", secretKey);
        }
        if (!string.IsNullOrWhiteSpace(audience))
        {
            tap.WithEnvironment("Inspector__Auth__Jwt__Audience", audience);
        }
        if (allowAnyAudience)
        {
            tap.WithEnvironment("Inspector__Auth__Jwt__AllowAnyAudience", "true");
        }
        if (requiredClaims is { Count: > 0 })
        {
            foreach (var (claim, value) in requiredClaims)
            {
                tap.WithEnvironment($"Inspector__Auth__Jwt__RequiredClaims__{claim}", value);
            }
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
