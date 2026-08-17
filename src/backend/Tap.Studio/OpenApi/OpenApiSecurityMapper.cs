using Microsoft.OpenApi;
using Tap.Studio.Contracts;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Turns a document's <c>securitySchemes</c> into a Tap auth profile.
///
/// <para>Auth is the main reason a generated request fails on first send, so mapping it is worth
/// real effort — but a spec only ever describes the <i>shape</i> of authentication, never the
/// credentials. Every generated profile therefore points at <c>{{variables}}</c> the user fills in
/// once, rather than at literals. Nothing secret is ever synthesized or guessed.</para>
/// </summary>
public static class OpenApiSecurityMapper
{
    /// <summary>One scheme, described well enough for the wizard to show it before writing anything.</summary>
    public sealed record MappedScheme(
        string Key,
        string Type,
        string? TapAuthType,
        string? Description,
        IReadOnlyList<string> Scopes,
        string? Warning)
    {
        /// <summary>False when Tap has no equivalent — the wizard shows it greyed with the reason.</summary>
        public bool Supported => TapAuthType is not null;
    }

    public static IReadOnlyList<MappedScheme> Describe(OpenApiDocument document)
    {
        var schemes = document.Components?.SecuritySchemes;
        if (schemes is not { Count: > 0 }) return [];

        var result = new List<MappedScheme>();
        foreach (var (key, scheme) in schemes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (scheme is null) continue;
            result.Add(Describe(key, scheme));
        }
        return result;
    }

    private static MappedScheme Describe(string key, IOpenApiSecurityScheme scheme)
    {
        var scopes = CollectScopes(scheme);

        return scheme.Type switch
        {
            SecuritySchemeType.Http when Is(scheme.Scheme, "bearer") =>
                new MappedScheme(key, "http bearer", "bearer",
                    scheme.BearerFormat is { Length: > 0 } f ? $"Bearer token ({f})" : "Bearer token", scopes, null),

            SecuritySchemeType.Http when Is(scheme.Scheme, "basic") =>
                new MappedScheme(key, "http basic", "basic", "Username and password", scopes, null),

            SecuritySchemeType.Http =>
                new MappedScheme(key, $"http {scheme.Scheme}", null, null, scopes,
                    $"HTTP scheme '{scheme.Scheme}' has no Tap equivalent — configure auth by hand."),

            SecuritySchemeType.ApiKey =>
                new MappedScheme(key, "apiKey", "apiKey",
                    $"API key in {Location(scheme.In)} '{scheme.Name}'", scopes,
                    scheme.In is ParameterLocation.Cookie
                        ? "API key is sent in a cookie; Tap sends it as a header or query value."
                        : null),

            SecuritySchemeType.OAuth2 =>
                new MappedScheme(key, "oauth2", "oauth2", DescribeOAuth(scheme), scopes, null),

            SecuritySchemeType.OpenIdConnect =>
                new MappedScheme(key, "openIdConnect", "oauth2",
                    scheme.OpenIdConnectUrl is { } url ? $"OpenID Connect ({url})" : "OpenID Connect", scopes,
                    "Discovery URL was carried over; pick the flow and fill in the client id."),

            _ => new MappedScheme(key, scheme.Type?.ToString() ?? "unknown", null, null, scopes,
                    "Tap has no equivalent for this scheme — configure auth by hand."),
        };
    }

    /// <summary>
    /// Builds the profile. <paramref name="collectionSlug"/> seeds the variable names so two
    /// imported collections don't collide on <c>{{CLIENT_ID}}</c>.
    /// </summary>
    public static AuthSpecDto? Build(
        OpenApiDocument document, string schemeKey, string collectionSlug, List<string> warnings)
    {
        var schemes = document.Components?.SecuritySchemes;
        if (schemes is null || !schemes.TryGetValue(schemeKey, out var scheme) || scheme is null) return null;

        var described = Describe(schemeKey, scheme);
        if (!described.Supported)
        {
            if (described.Warning is { } w) warnings.Add(w);
            return null;
        }

        var prefix = VarPrefix(collectionSlug);
        var name = $"{schemeKey} ({described.Type})";

        var spec = new AuthSpecDto
        {
            Path = "(placeholder)", // patched by the planner once the slug tree is known
            Name = name,
            Type = described.TapAuthType!,
            Body = BuildDocs(schemeKey, described),
        };

        return described.TapAuthType switch
        {
            "bearer" => spec with { Token = $"{{{{{prefix}_TOKEN}}}}" },

            "basic" => spec with
            {
                Username = $"{{{{{prefix}_USERNAME}}}}",
                Password = $"{{{{{prefix}_PASSWORD}}}}",
            },

            "apiKey" => spec with
            {
                In = scheme.In is ParameterLocation.Query ? "query" : "header",
                ApiKeyName = scheme.Name ?? "X-API-Key",
                ApiKeyValue = $"{{{{{prefix}_API_KEY}}}}",
            },

            "oauth2" => spec with
            {
                // client_credentials is the only flow that runs unattended, so it is the safest
                // default for a generated profile; the wizard lets the user switch to PKCE.
                Flow = PreferredFlow(scheme),
                UseDiscovery = scheme.OpenIdConnectUrl is not null,
                Authority = scheme.OpenIdConnectUrl?.ToString(),
                AuthorizeUrl = FlowUrl(scheme, f => f.AuthorizationUrl),
                TokenUrl = FlowUrl(scheme, f => f.TokenUrl),
                ClientId = $"{{{{{prefix}_CLIENT_ID}}}}",
                ClientSecret = $"{{{{{prefix}_CLIENT_SECRET}}}}",
                Scopes = described.Scopes.Count > 0 ? described.Scopes : null,
            },

            _ => null,
        };
    }

    private static string BuildDocs(string key, MappedScheme scheme)
    {
        var lines = new List<string>
        {
            $"# {key}",
            "",
            $"Generated from the OpenAPI security scheme `{key}` ({scheme.Type}).",
            "",
            "Credentials are referenced as variables, never written into this file. Set them in an",
            "environment or the variable store before sending.",
        };
        if (scheme.Scopes.Count > 0)
        {
            lines.Add("");
            lines.Add("Scopes declared by the document:");
            lines.AddRange(scheme.Scopes.Select(s => $"- `{s}`"));
        }
        if (scheme.Warning is { } w)
        {
            lines.Add("");
            lines.Add($"> {w}");
        }
        return string.Join("\n", lines);
    }

    private static IReadOnlyList<string> CollectScopes(IOpenApiSecurityScheme scheme)
    {
        if (scheme.Flows is not { } flows) return [];
        var scopes = new List<string>();
        foreach (var flow in new[] { flows.ClientCredentials, flows.AuthorizationCode, flows.Password, flows.Implicit })
        {
            foreach (var scope in flow?.Scopes?.Keys ?? [])
                if (!scopes.Contains(scope, StringComparer.Ordinal)) scopes.Add(scope);
        }
        scopes.Sort(StringComparer.Ordinal);
        return scopes;
    }

    private static string? PreferredFlow(IOpenApiSecurityScheme scheme)
    {
        var flows = scheme.Flows;
        if (flows?.ClientCredentials is not null) return "client_credentials";
        if (flows?.AuthorizationCode is not null) return "authorization_code";
        if (flows?.Password is not null) return "password";
        return flows?.Implicit is not null ? "authorization_code" : "client_credentials";
    }

    private static string? FlowUrl(IOpenApiSecurityScheme scheme, Func<OpenApiOAuthFlow, Uri?> pick)
    {
        var flows = scheme.Flows;
        if (flows is null) return null;
        foreach (var flow in new[] { flows.ClientCredentials, flows.AuthorizationCode, flows.Password, flows.Implicit })
        {
            if (flow is not null && pick(flow) is { } uri) return uri.ToString();
        }
        return null;
    }

    private static string DescribeOAuth(IOpenApiSecurityScheme scheme)
    {
        var flow = PreferredFlow(scheme);
        var token = FlowUrl(scheme, f => f.TokenUrl);
        return token is { Length: > 0 } ? $"OAuth 2.0 ({flow}) at {token}" : $"OAuth 2.0 ({flow})";
    }

    private static string Location(ParameterLocation? location) => location switch
    {
        ParameterLocation.Query => "query",
        ParameterLocation.Cookie => "cookie",
        _ => "header",
    };

    private static bool Is(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Upper snake-case prefix derived from the slug, so variables read as belonging to
    /// this API: <c>petstore</c> → <c>PETSTORE_TOKEN</c>.</summary>
    private static string VarPrefix(string slug)
    {
        var cleaned = new string(slug.Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_').ToArray());
        return cleaned.Trim('_') is { Length: > 0 } p ? p : "API";
    }
}
