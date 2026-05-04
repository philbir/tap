using System.Net;
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
    ILogger<TapAuthMiddleware> logger)
{
    private readonly System.Net.IPNetwork[]? _cidrs = ParseCidrs(options.AllowedCidrs);
    private readonly HashSet<string>? _countries = options.AllowedCountries is { Count: > 0 }
        ? new HashSet<string>(options.AllowedCountries.Select(c => c.Trim().ToUpperInvariant()))
        : null;

    public async Task InvokeAsync(HttpContext ctx)
    {
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
            var country = ctx.Request.Headers["CF-IPCountry"].ToString().Trim().ToUpperInvariant();
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
        var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrEmpty(cf) && IPAddress.TryParse(cf, out var ip)) return ip;

        var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out ip)) return ip;
        }
        return ctx.Connection.RemoteIpAddress;
    }

    private static bool ConstantTimeEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
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
