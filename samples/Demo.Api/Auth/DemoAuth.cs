using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Demo.Api.Auth;

/// <summary>
/// OpenIddict-backed OAuth2 / OIDC server intended for local Tap demos. Supports the grants
/// Tap's <c>AuthRunner</c> drives end-to-end without an external IdP:
///
///   * <c>client_credentials</c> — m2m, no UI.
///   * <c>password</c> — ROPC; seeded test user below.
///   * <c>authorization_code</c> + PKCE — interactive popup. A bare-bones HTML consent
///     page is hosted at <c>/connect/authorize</c> so the demo doesn't need an external
///     IdP UI.
///   * <c>refresh_token</c> — silent renew off any of the above.
///
/// Device-code (RFC 8628) and On-Behalf-Of (JWT-bearer) are exercised by the runner against
/// real identity providers — wiring them into OpenIddict in a sample isn't worth the API
/// drag. The Azure-CLI auth types target real AAD; the device-code grant works against any
/// OIDC IdP that advertises <c>device_authorization_endpoint</c> in discovery.
///
/// Tokens are signed with development certs OpenIddict mints on first run — fine for samples,
/// never for production.
/// </summary>
public static class DemoAuth
{
    public const string ClientId = "tap-demo";
    public const string ClientSecret = "tap-demo-secret";
    public const string PublicClientId = "tap-demo-public";
    public const string TestUser = "alice";
    public const string TestPassword = "wonderland";
    /// <summary>Fallback when the AppHost didn't plumb <c>STUDIO_CALLBACK_URL</c> through.
    /// Real runs always override this via env var so the seeded redirect URI matches the
    /// Aspire-assigned Studio port.</summary>
    public const string DefaultStudioRedirectUri = "http://localhost:5298/api/auth/callback";

    /// <summary>Live redirect URI Demo.Api should accept on the OAuth code flow. Resolved
    /// at boot from <c>STUDIO_CALLBACK_URL</c> (set by Studio.AppHost) and falls back to
    /// the localhost constant so standalone runs still work.</summary>
    public static string StudioRedirectUri =>
        Environment.GetEnvironmentVariable("STUDIO_CALLBACK_URL") ?? DefaultStudioRedirectUri;

    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddDbContext<DemoDbContext>(o =>
        {
            o.UseInMemoryDatabase("demo-api-openiddict");
            o.UseOpenIddict();
        });

        builder.Services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<DemoDbContext>())
            .AddServer(o =>
            {
                o.SetTokenEndpointUris("/connect/token");
                o.SetAuthorizationEndpointUris("/connect/authorize");
                o.SetUserInfoEndpointUris("/connect/userinfo");
                o.SetIntrospectionEndpointUris("/connect/introspect");
                o.SetConfigurationEndpointUris("/.well-known/openid-configuration");
                o.SetJsonWebKeySetEndpointUris("/.well-known/jwks");

                o.AllowClientCredentialsFlow();
                o.AllowPasswordFlow();
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                o.AllowRefreshTokenFlow();

                o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess, "api");

                o.AddDevelopmentEncryptionCertificate();
                o.AddDevelopmentSigningCertificate();

                // Plain HTTP is fine on localhost; spare the demo from needing dev-https.
                o.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            });

        builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        builder.Services.AddAuthorization();
    }

    /// <summary>Idempotently registers the demo clients on first boot.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        var apps = services.GetRequiredService<IOpenIddictApplicationManager>();

        // Confidential client — drives client_credentials + password + auth-code (with secret).
        // Auth-code is allowed too so users experimenting with the existing CC profile by
        // flipping the grant type get a working flow without remembering to also swap
        // clientId to tap-demo-public.
        if (await apps.FindByClientIdAsync(ClientId) is null)
        {
            await apps.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = ClientId,
                ClientSecret = ClientSecret,
                DisplayName = "Tap demo client (confidential)",
                ClientType = ClientTypes.Confidential,
                RedirectUris = { new Uri(StudioRedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Introspection,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.GrantTypes.Password,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Email,
                    Permissions.Prefixes.Scope + "api",
                    Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                },
            });
        }

        // Public (PKCE) client — for the auth-code popup driven by Tap.Studio. No secret;
        // PKCE provides the proof. Redirect URI must match AuthRunner.DefaultRedirectUri.
        if (await apps.FindByClientIdAsync(PublicClientId) is null)
        {
            await apps.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = PublicClientId,
                DisplayName = "Tap demo public client (PKCE)",
                ClientType = ClientTypes.Public,
                RedirectUris = { new Uri(StudioRedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.Authorization,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Email,
                    Permissions.Prefixes.Scope + "api",
                    Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                },
                Requirements =
                {
                    Requirements.Features.ProofKeyForCodeExchange,
                },
            });
        }
    }

    public static void MapEndpoints(WebApplication app)
    {
        // ----- /connect/token : branches by grant_type. ------------------------------
        app.MapMethods("/connect/token", new[] { "POST" }, async (HttpContext ctx) =>
        {
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("OpenID Connect request not found.");

            if (request.IsClientCredentialsGrantType())
                return IssueClientCredentials(request);

            if (request.IsPasswordGrantType())
                return IssuePassword(request);

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                // Both replay the principal OpenIddict stashed on the original sign-in.
                var info = await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                if (!info.Succeeded)
                    return BadGrant("The token request was rejected by the authentication server.");
                return Results.SignIn(info.Principal!,
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.BadRequest(new
            {
                error = Errors.UnsupportedGrantType,
                error_description = "Grant not supported by this demo.",
            });
        });

        // ----- /connect/authorize : tiny inline consent page for code+PKCE. ---------
        app.MapMethods("/connect/authorize", new[] { "GET", "POST" }, async (HttpContext ctx) =>
        {
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("OpenID Connect request not found.");

            // First hit (GET) — render a tiny login form that POSTs back to /connect/authorize
            // with the same query string. Tap.Studio's popup completes the round-trip when
            // the IdP redirects to /api/auth/callback with code + state.
            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(RenderLoginPage(ctx.Request.Query));
                return Results.Empty;
            }

            var form = await ctx.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            if (!CredentialsOk(username, password))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                // Re-emit hidden inputs from the form body so the user can retry without
                // the browser losing the original OAuth parameters.
                await ctx.Response.WriteAsync(RenderLoginPage(FormToQuery(form), "Invalid username or password."));
                return Results.Empty;
            }

            var principal = BuildUserPrincipal(username, request.GetScopes());
            return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });

        // ----- /connect/userinfo : echoes the user's standard claims. ---------------
        app.MapGet("/connect/userinfo", (HttpContext ctx) =>
        {
            var user = ctx.User;
            if (user.Identity is null || !user.Identity.IsAuthenticated) return Results.Unauthorized();
            return Results.Json(new
            {
                sub = user.FindFirstValue(Claims.Subject),
                name = user.FindFirstValue(Claims.Name),
                email = user.FindFirstValue(Claims.Email),
                role = user.FindFirstValue(Claims.Role),
            });
        }).RequireAuthorization();

        // ----- /demo/auth/whoami : sample protected resource. ----------------------
        app.MapGet("/demo/auth/whoami", (HttpContext ctx) =>
        {
            var claims = ctx.User.Claims
                .GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray());
            return Results.Json(new
            {
                authenticated = ctx.User.Identity?.IsAuthenticated == true,
                claims,
            });
        }).RequireAuthorization();
    }

    // ---- Grant-handling helpers ------------------------------------------------------

    private static IResult IssueClientCredentials(OpenIddictRequest request)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, request.ClientId!));
        identity.AddClaim(new Claim(Claims.Name, request.ClientId!));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources("api");
        foreach (var claim in principal.Claims) claim.SetDestinations(GetDestinations(claim));
        return Results.SignIn(principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult IssuePassword(OpenIddictRequest request)
    {
        if (!CredentialsOk(request.Username, request.Password))
            return BadGrant("Bad username or password.");

        var principal = BuildUserPrincipal(request.Username!, request.GetScopes());
        return Results.SignIn(principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static ClaimsPrincipal BuildUserPrincipal(string username, ImmutableArray<string> scopes)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, username));
        identity.AddClaim(new Claim(Claims.Name, username));
        identity.AddClaim(new Claim(Claims.Email, $"{username}@example.com"));
        identity.AddClaim(new Claim(Claims.Role, "demo-user"));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources("api");
        // offline_access is what unlocks refresh-token issuance; OpenIddict only mints
        // refresh tokens when the principal carries that scope.
        foreach (var claim in principal.Claims) claim.SetDestinations(GetDestinations(claim));
        return principal;
    }

    private static bool CredentialsOk(string? username, string? password)
        => string.Equals(username, TestUser, StringComparison.Ordinal)
        && string.Equals(password, TestPassword, StringComparison.Ordinal);

    private static IResult BadGrant(string description)
        => Results.Forbid(
            authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }));

    /// <summary>Decide which token a claim belongs in. Subject + name always make it into
    /// the access token; the rest gate on the relevant scope.</summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        yield return Destinations.AccessToken;

        switch (claim.Type)
        {
            case Claims.Name when claim.Subject?.HasScope(Scopes.Profile) == true:
            case Claims.Email when claim.Subject?.HasScope(Scopes.Email) == true:
            case Claims.Role when claim.Subject?.HasScope(Scopes.Profile) == true:
                yield return Destinations.IdentityToken;
                break;
        }
    }

    /// <summary>Adapt the submitted form back into an IQueryCollection so the retry-render
    /// path can keep using the same builder.</summary>
    private static IQueryCollection FormToQuery(IFormCollection form)
    {
        var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        foreach (var (k, v) in form) dict[k] = v;
        return new QueryCollection(dict);
    }

    // ---- Tiny inline UI ------------------------------------------------------------

    /// <summary>
    /// Bare-bones consent page for the authorization-code flow. POSTs back to
    /// <c>/connect/authorize</c> with every original OAuth parameter re-emitted as a hidden
    /// input — OpenIddict reads parameters from the form body on POST and a bare
    /// <c>action="/connect/authorize?…"</c> drops the query when the browser submits.
    /// Without these inputs OpenIddict's validators report ID2029 (mandatory client_id missing).
    /// No CSRF token because the demo runs on localhost over plain HTTP.
    /// </summary>
    private static string RenderLoginPage(IQueryCollection query, string? error = null)
    {
        var errorHtml = error is null ? string.Empty : $"<p class='err'>{System.Net.WebUtility.HtmlEncode(error)}</p>";
        var hiddenInputs = new System.Text.StringBuilder();
        foreach (var kv in query)
        {
            // username/password are user-submitted; everything else (client_id, response_type,
            // scope, code_challenge, …) is forwarded verbatim so OpenIddict sees a complete
            // authorize request on POST.
            if (string.Equals(kv.Key, "username", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kv.Key, "password", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var v in kv.Value)
            {
                hiddenInputs.Append("<input type=\"hidden\" name=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(kv.Key))
                    .Append("\" value=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(v ?? string.Empty))
                    .Append("\">");
            }
        }

        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Demo.Api · Sign in</title>
            <style>
              body{font:14px/1.5 system-ui;background:#f8fafc;color:#0f172a;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
              .card{background:#fff;border:1px solid #e2e8f0;border-radius:8px;padding:24px 28px;width:320px;box-shadow:0 4px 12px rgba(15,23,42,.04)}
              h1{font-size:16px;margin:0 0 16px}
              label{display:block;font-size:12px;color:#475569;margin-top:10px}
              input{display:block;width:100%;box-sizing:border-box;padding:8px 10px;margin-top:4px;border:1px solid #cbd5e1;border-radius:4px;font:inherit}
              button{margin-top:18px;width:100%;padding:8px;background:#4f46e5;color:#fff;border:0;border-radius:4px;font:600 13px system-ui;cursor:pointer}
              .err{color:#b91c1c;font-size:12px}
              .hint{margin-top:14px;font-size:11px;color:#64748b}
            </style></head>
            <body><form method="post" action="/connect/authorize" class="card">
              {{hiddenInputs}}
              <h1>Demo.Api · Sign in</h1>
              {{errorHtml}}
              <label>Username<input name="username" autocomplete="username" autofocus value="{{TestUser}}"></label>
              <label>Password<input name="password" autocomplete="current-password" type="password" value="{{TestPassword}}"></label>
              <button type="submit">Sign in</button>
              <p class="hint">Seeded demo user: <code>{{TestUser}}</code> / <code>{{TestPassword}}</code>.</p>
            </form></body></html>
            """;
    }
}
