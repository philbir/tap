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
                await Reject(ctx, 403, "IP not in allowlist.");
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
        var clientIp = ResolveClientIp(ctx) ?? ctx.Connection.RemoteIpAddress;
        logger.LogWarning("Auth {Status} {Method} {Path} from {Ip}: {Reason}",
            status, ctx.Request.Method, ctx.Request.Path, clientIp, reason);
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.CacheControl = "no-store";

        if (WantsHtml(ctx.Request))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(AuthErrorPage.Render(status), ctx.RequestAborted);
            return;
        }

        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(AuthErrorPage.ResponseText(status), ctx.RequestAborted);
    }

    private static bool WantsHtml(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Accept", out var accept) || accept.Count == 0)
        {
            return false;
        }

        var value = accept.ToString();
        return value.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static System.Net.IPNetwork[]? ParseCidrs(List<string>? cidrs)
    {
        if (cidrs is not { Count: > 0 }) return null;
        return cidrs.Select(c =>
        {
            var trimmed = c.Trim();
            if (!trimmed.Contains('/') && IPAddress.TryParse(trimmed, out var ip))
            {
                var prefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                trimmed = $"{trimmed}/{prefix}";
            }
            return System.Net.IPNetwork.Parse(trimmed);
        }).ToArray();
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

internal static class AuthErrorPage
{
    public static string ResponseText(int statusCode) =>
        statusCode == StatusCodes.Status401Unauthorized
            ? "Authentication required."
            : "Access denied.";

    public static string Render(int statusCode)
    {
        var title = statusCode == StatusCodes.Status401Unauthorized
            ? "Authentication required"
            : "Access denied";
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeResponseText = WebUtility.HtmlEncode(ResponseText(statusCode));

        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{statusCode}} {{safeTitle}} - Tap access denied</title>
  <style>
    :root {
      color-scheme: light;
      --ink: #17142b;
      --muted: #655f83;
      --line: rgba(111, 92, 202, 0.22);
      --panel: rgba(255, 255, 255, 0.78);
      --violet: #7057e9;
      --cyan: #18c5d4;
      --green: #81c783;
      --orange: #ff8a1f;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    * { box-sizing: border-box; }

    body {
      min-height: 100vh;
      margin: 0;
      display: grid;
      place-items: center;
      padding: 32px;
      color: var(--ink);
      background:
        radial-gradient(circle at 12% 18%, rgba(255, 255, 255, 0.95), transparent 22%),
        radial-gradient(circle at 78% 18%, rgba(139, 119, 236, 0.24), transparent 28%),
        radial-gradient(circle at 10% 88%, rgba(112, 190, 133, 0.28), transparent 24%),
        linear-gradient(145deg, #fbf7ff 0%, #f2ecff 46%, #f7fbf5 100%);
    }

    main {
      width: min(1320px, 100%);
      display: grid;
      grid-template-columns: minmax(300px, 0.8fr) minmax(620px, 1.2fr);
      gap: 30px 42px;
      align-items: center;
    }

    .status {
      margin: 0 0 22px;
      color: var(--violet);
      font-size: clamp(92px, 16vw, 178px);
      font-weight: 780;
      letter-spacing: 0;
      line-height: 0.86;
      text-shadow: 0 18px 48px rgba(93, 68, 203, 0.22);
    }

    h1 {
      margin: 0 0 12px;
      font-size: clamp(34px, 5vw, 62px);
      letter-spacing: 0;
      line-height: 1;
    }

    p {
      max-width: 58ch;
      margin: 0;
      color: var(--muted);
      font-size: 18px;
      line-height: 1.55;
    }

    .reason {
      display: inline-block;
      max-width: 100%;
      margin-top: 18px;
      padding: 10px 12px;
      overflow-wrap: anywhere;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.56);
      color: #332b68;
      font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      font-size: 15px;
    }

    .art {
      min-height: 410px;
      padding: 20px;
      border: 1px solid rgba(255, 255, 255, 0.72);
      border-radius: 8px;
      background: var(--panel);
      box-shadow: 0 24px 80px rgba(72, 58, 149, 0.18);
      backdrop-filter: blur(18px);
    }

    .art img {
      display: block;
      width: 100%;
      height: auto;
      border-radius: 6px;
    }

    .meta {
      margin-top: 14px;
      color: var(--muted);
      font-size: 12px;
      text-align: center;
    }

    @media (max-width: 760px) {
      body { padding: 22px; place-items: start center; }
      main { grid-template-columns: 1fr; gap: 28px; }
      .art { min-height: 0; padding: 14px; }
    }
  </style>
</head>
<body>
  <main>
    <section>
      <div class="status">{{statusCode}}</div>
      <h1>{{safeTitle}}</h1>
      <p>Tap received the request, but this tunnel is protected and the request did not pass the configured access checks.</p>
      <div class="reason">{{safeResponseText}}</div>
    </section>
    <section class="art" aria-label="Tap unauthorized request diagram">
      <img src="/tap-error-denied.png" alt="A Tap access gate with a 403 shield blocking incoming requests">
      <div class="meta">Tap auth blocked the request before proxying.</div>
    </section>
  </main>
</body>
</html>
""";
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
