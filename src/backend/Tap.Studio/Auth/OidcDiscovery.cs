using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tap.Studio.Auth;

/// <summary>
/// Fetches and caches OpenID Connect discovery documents from
/// <c>{authority}/.well-known/openid-configuration</c>. Used to auto-fill the
/// token / authorize endpoints when the user toggles "Use Discovery" on an OAuth2
/// auth profile. Mirrors dreamr's <c>OAuthClient.GetWellknownOpenIdConnectConfiguration</c>
/// but returns just the fields the UI cares about.
///
/// Results are cached for 10 minutes per authority — these documents change rarely and the
/// UI typically asks again right after the user types.
/// </summary>
public sealed class OidcDiscoveryClient
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    public OidcDiscoveryClient(HttpClient http) => _http = http;

    public async Task<OidcDiscoveryDocument> FetchAsync(string authority, CancellationToken ct)
    {
        var key = authority.TrimEnd('/');
        // Enforced here rather than at the call sites: /api/auth/discovery hands this method
        // a caller-supplied authority verbatim, so this is the chokepoint that stops Studio
        // being turned into a fetcher for arbitrary schemes and cleartext hosts.
        AuthUrlGuard.RequireSecure(key, "OIDC authority");

        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
            return hit.Document;

        var url = key + "/.well-known/openid-configuration";
        // Use a forgiving deserializer — only a handful of fields matter and providers add
        // many extras we don't care about.
        var raw = await _http.GetFromJsonAsync(url, OidcJson.Default.OidcRawDocument, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"OIDC discovery at {url} returned an empty body.");

        var doc = new OidcDiscoveryDocument(
            Issuer: raw.Issuer ?? key,
            AuthorizationEndpoint: raw.AuthorizationEndpoint ?? string.Empty,
            TokenEndpoint: raw.TokenEndpoint ?? string.Empty,
            UserInfoEndpoint: raw.UserInfoEndpoint,
            JwksUri: raw.JwksUri,
            EndSessionEndpoint: raw.EndSessionEndpoint,
            ScopesSupported: raw.ScopesSupported ?? [],
            ResponseTypesSupported: raw.ResponseTypesSupported ?? [],
            GrantTypesSupported: raw.GrantTypesSupported ?? [],
            CodeChallengeMethodsSupported: raw.CodeChallengeMethodsSupported ?? []);

        _cache[key] = new CacheEntry(doc, DateTimeOffset.UtcNow + TimeToLive);
        return doc;
    }

    private readonly record struct CacheEntry(OidcDiscoveryDocument Document, DateTimeOffset Expires);
}

/// <summary>
/// Transport guard for every endpoint an auth profile can point the runner at. Client
/// secrets, resource-owner passwords, authorization codes and OBO assertions all ride
/// these requests, so a workspace file must not be able to downgrade one to cleartext or
/// steer it at a non-HTTP scheme. Loopback over plain http stays allowed — a local
/// identity provider is a normal dev setup and never leaves the machine.
/// </summary>
internal static class AuthUrlGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless <paramref name="url"/> is an
    /// absolute https URL, or an absolute http URL whose host is loopback.
    /// <paramref name="what"/> names the field in the error the user sees.
    /// </summary>
    public static void RequireSecure(string? url, string what)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{what} URL is invalid: '{url}'.");

        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;

        throw new InvalidOperationException(
            $"{what} must use https — plain http is accepted only for a loopback host: '{url}'.");
    }
}

/// <summary>Subset of an OIDC discovery document we surface to the UI.</summary>
public sealed record OidcDiscoveryDocument(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    string? EndSessionEndpoint,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> ResponseTypesSupported,
    IReadOnlyList<string> GrantTypesSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported);

/// <summary>Wire shape — mirrors the raw JSON from .well-known. snake_case via [JsonPropertyName].</summary>
internal sealed record OidcRawDocument
{
    [JsonPropertyName("issuer")] public string? Issuer { get; init; }
    [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; init; }
    [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; init; }
    [JsonPropertyName("userinfo_endpoint")] public string? UserInfoEndpoint { get; init; }
    [JsonPropertyName("jwks_uri")] public string? JwksUri { get; init; }
    [JsonPropertyName("end_session_endpoint")] public string? EndSessionEndpoint { get; init; }
    [JsonPropertyName("scopes_supported")] public IReadOnlyList<string>? ScopesSupported { get; init; }
    [JsonPropertyName("response_types_supported")] public IReadOnlyList<string>? ResponseTypesSupported { get; init; }
    [JsonPropertyName("grant_types_supported")] public IReadOnlyList<string>? GrantTypesSupported { get; init; }
    [JsonPropertyName("code_challenge_methods_supported")] public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }
}

[JsonSerializable(typeof(OidcRawDocument))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class OidcJson : JsonSerializerContext
{
}
