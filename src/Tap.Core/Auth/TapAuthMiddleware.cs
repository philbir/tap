using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Tap.Core.Auth;

public static class TapAuthRegistration
{
    /// <summary>
    /// Registers OIDC + cookie authentication when configured; the static checks (header / IP / country)
    /// are pure middleware and don't need DI registration.
    /// </summary>
    public static IServiceCollection AddTapAuth(this IServiceCollection services, TapAuthOptions options)
    {
        services.AddSingleton(options);

        if (options.Jwt is { } jwt && (
                !string.IsNullOrWhiteSpace(jwt.SecretKey) ||
                !string.IsNullOrWhiteSpace(jwt.WellKnownEndpoint)))
        {
            services.AddSingleton(new JwtTokenValidator(jwt));
        }

        if (options.Oidc is { } oidc && !string.IsNullOrWhiteSpace(oidc.Authority) && !string.IsNullOrWhiteSpace(oidc.ClientId))
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    opts.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(o =>
                {
                    o.Cookie.Name = "tap.auth";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Lax;
                    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    o.ExpireTimeSpan = TimeSpan.FromHours(8);
                })
                .AddOpenIdConnect(o =>
                {
                    o.Authority = oidc.Authority;
                    o.ClientId = oidc.ClientId;
                    o.ClientSecret = oidc.ClientSecret;
                    o.ResponseType = oidc.ResponseType;
                    o.SaveTokens = true;
                    o.GetClaimsFromUserInfoEndpoint = true;
                    o.CallbackPath = oidc.CallbackPath;
                    o.Scope.Clear();
                    foreach (var s in oidc.Scopes) o.Scope.Add(s);
                    o.TokenValidationParameters = new TokenValidationParameters { NameClaimType = "name" };
                });
            services.AddAuthorization();
        }

        // Trust X-Forwarded-* from cloudflared (which connects from loopback) so OIDC redirect_uri
        // resolves to the public URL like https://token-tap.p7e.dev/signin-oidc.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        return services;
    }
}

/// <summary>
/// Per-request gate enforcing all configured non-OIDC checks. OIDC is handled separately
/// via the [Authorize] attribute / .RequireAuthorization() chained after this middleware.
/// </summary>
public sealed class TapAuthMiddleware(
    RequestDelegate next,
    TapAuthOptions options,
    ILogger<TapAuthMiddleware> logger,
    IServiceProvider services)
{
    private readonly System.Net.IPNetwork[]? _cidrs = ParseCidrs(options.AllowedCidrs);
    private readonly HashSet<string>? _countries = options.AllowedCountries is { Count: > 0 }
        ? new HashSet<string>(options.AllowedCountries.Select(c => c.Trim().ToUpperInvariant()))
        : null;
    private readonly JwtTokenValidator? _jwt = services.GetService<JwtTokenValidator>();

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Let CORS preflights through unauthenticated. Browsers strip Authorization on
        // preflight, so applying any header/JWT gate here would 401 the preflight and make
        // the actual request never fire ("Failed to fetch" on the client).
        if (HttpMethods.IsOptions(ctx.Request.Method) &&
            ctx.Request.Headers.ContainsKey("Origin") &&
            ctx.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await next(ctx);
            return;
        }

        // Header check — short-circuit BEFORE the request reaches OIDC, since machine-to-machine
        // calls won't have a browser session.
        if (options.Header is { } h && !string.IsNullOrEmpty(h.Value))
        {
            if (!ctx.Request.Headers.TryGetValue(h.Name, out var supplied) || !ConstantTimeEquals(supplied!, h.Value))
            {
                await Reject(ctx, 401, $"Missing or invalid {h.Name} header.");
                return;
            }
        }

        // JWT Bearer validation.
        if (_jwt is not null)
        {
            var jwtOpts = options.Jwt!;
            if (!ctx.Request.Headers.TryGetValue(jwtOpts.HeaderName, out var raw) || raw.Count == 0)
            {
                await Reject(ctx, 401, $"Missing {jwtOpts.HeaderName} header.");
                return;
            }
            var token = raw.ToString();
            if (!string.IsNullOrEmpty(jwtOpts.HeaderScheme) &&
                token.StartsWith(jwtOpts.HeaderScheme, StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(jwtOpts.HeaderScheme.Length).Trim();
            }

            var (ok, error) = await _jwt.ValidateAsync(token, ctx.RequestAborted);
            if (!ok)
            {
                await Reject(ctx, 401, $"Invalid JWT: {error}");
                return;
            }
        }

        // CIDR check.
        if (_cidrs is { Length: > 0 })
        {
            var ip = ResolveClientIp(ctx);
            if (ip is null || !_cidrs.Any(n => n.Contains(ip)))
            {
                await Reject(ctx, 403, $"Source IP {ip} not in allowlist.");
                return;
            }
        }

        // Country check (CF-IPCountry).
        if (_countries is not null)
        {
            var country = IsTrustedForwarder(ctx.Connection.RemoteIpAddress)
                ? ctx.Request.Headers["CF-IPCountry"].ToString().Trim().ToUpperInvariant()
                : string.Empty;
            if (string.IsNullOrEmpty(country) || !_countries.Contains(country))
            {
                await Reject(ctx, 403, $"Country '{country}' not in allowlist.");
                return;
            }
        }

        await next(ctx);
    }

    private async Task Reject(HttpContext ctx, int status, string reason)
    {
        logger.LogWarning("Auth rejected {Method} {Path} from {Ip}: {Reason}",
            ctx.Request.Method, ctx.Request.Path, ctx.Connection.RemoteIpAddress, reason);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(reason);
    }

    private static System.Net.IPNetwork[]? ParseCidrs(List<string>? cidrs)
    {
        if (cidrs is not { Count: > 0 }) return null;
        return cidrs.Select(c => System.Net.IPNetwork.Parse(c.Trim())).ToArray();
    }

    private static IPAddress? ResolveClientIp(HttpContext ctx)
    {
        if (IsTrustedForwarder(ctx.Connection.RemoteIpAddress))
        {
            var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            if (!string.IsNullOrEmpty(cf) && IPAddress.TryParse(cf, out var ip)) return ip;

            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrEmpty(fwd))
            {
                var first = fwd.Split(',')[0].Trim();
                if (IPAddress.TryParse(first, out ip)) return ip;
            }
        }
        return ctx.Connection.RemoteIpAddress;
    }

    private static bool IsTrustedForwarder(IPAddress? remoteIp)
    {
        if (remoteIp is null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return true;
        return remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4());
    }

    private static bool ConstantTimeEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

/// <summary>
/// Validates JWT bearer tokens using either a symmetric secret (HS*) or signing keys
/// fetched from an OIDC well-known endpoint (RS*/ES*). Caches the JWKS via
/// <see cref="ConfigurationManager{T}"/>.
/// </summary>
public sealed class JwtTokenValidator
{
    private readonly JwtAuthOptions _options;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SymmetricSecurityKey? _symmetricKey;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public JwtTokenValidator(JwtAuthOptions options)
    {
        _options = options;
        if (!string.IsNullOrWhiteSpace(options.SecretKey))
        {
            _symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey));
        }
        else if (!string.IsNullOrWhiteSpace(options.WellKnownEndpoint))
        {
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                options.WellKnownEndpoint,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = options.WellKnownEndpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase) });
        }
        else
        {
            throw new InvalidOperationException("JwtAuthOptions requires either SecretKey or WellKnownEndpoint.");
        }
    }

    public async Task<(bool ok, string? error)> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return (false, "empty token");

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),
            ValidateIssuer = !string.IsNullOrWhiteSpace(_options.Issuer),
            ValidIssuer = _options.Issuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(_options.Audience),
            ValidAudience = _options.Audience,
        };

        if (_symmetricKey is not null)
        {
            parameters.IssuerSigningKey = _symmetricKey;
        }
        else if (_configManager is not null)
        {
            var config = await _configManager.GetConfigurationAsync(ct);
            parameters.IssuerSigningKeys = config.SigningKeys;
        }

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return (false, result.Exception?.Message ?? "validation failed");
        }

        if (_options.RequiredClaims is { Count: > 0 } required)
        {
            foreach (var (claim, expected) in required)
            {
                if (!result.Claims.TryGetValue(claim, out var actual) ||
                    !string.Equals(actual?.ToString(), expected, StringComparison.Ordinal))
                {
                    return (false, $"required claim '{claim}' missing or did not match");
                }
            }
        }

        return (true, null);
    }
}

public static class TapAuthBuilderExtensions
{
    public static IApplicationBuilder UseTapAuth(this IApplicationBuilder app, TapAuthOptions options)
    {
        if (!options.AnyConfigured) return app;

        // Trust forwarded headers from cloudflared so URLs (and OIDC redirect_uri) reflect
        // the public scheme/host the client actually used.
        app.UseForwardedHeaders();

        if (options.Oidc is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        // Static checks first (header / CIDR / country).
        app.UseMiddleware<TapAuthMiddleware>(options);

        // For OIDC: require authentication on every path EXCEPT the OIDC callback paths,
        // which the OpenIdConnect middleware needs to handle to complete the code-flow.
        if (options.Oidc is { } oidc)
        {
            var callback = oidc.CallbackPath;
            app.Use(async (ctx, nxt) =>
            {
                var path = ctx.Request.Path.Value ?? "";
                var isCallback =
                    path.StartsWith(callback, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/signout-oidc", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/signout-callback-oidc", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/tap-auth/", StringComparison.OrdinalIgnoreCase);

                if (isCallback)
                {
                    await nxt();
                    return;
                }

                if (!(ctx.User.Identity?.IsAuthenticated ?? false))
                {
                    // Round-trip the original URL so the user lands back where they started.
                    var returnTo = ctx.Request.GetEncodedUrl();
                    await ctx.ChallengeAsync(
                        OpenIdConnectDefaults.AuthenticationScheme,
                        new AuthenticationProperties { RedirectUri = returnTo });
                    return;
                }
                await nxt();
            });

            // Sign-out endpoint: clears the cookie and triggers the OIDC end-session flow.
            // GET /tap-auth/signout?post=<url>
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Path.Equals("/tap-auth/signout", StringComparison.OrdinalIgnoreCase))
                {
                    var post = ctx.Request.Query["post"].ToString();
                    if (string.IsNullOrEmpty(post)) post = "/";
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
                        new AuthenticationProperties { RedirectUri = post });
                    return;
                }
                await nxt();
            });
        }
        return app;
    }
}
