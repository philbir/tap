using System.Collections.Concurrent;
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
        services.AddSingleton<TapAuthFailureTracker>();

        if (options.JwtConfigured)
        {
            services.AddSingleton(new JwtTokenValidator(options.Jwt!));
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
    private readonly TapAuthFailureTracker? _failures = services.GetService<TapAuthFailureTracker>();

    /// <summary>
    /// Key under which the pre-<c>UseForwardedHeaders</c> transport peer is stashed. That middleware
    /// rewrites <c>Connection.RemoteIpAddress</c> in place, so "did this really arrive over the
    /// tunnel's loopback hop?" can only be answered from the address we saw before the rewrite.
    /// </summary>
    internal const string TransportPeerKey = "tap.auth.transport-peer";

    public async Task InvokeAsync(HttpContext ctx)
    {
        // CORS preflights are answered here instead of being forwarded. A browser strips
        // Authorization from a preflight, so gating it would 401 and the real request would never
        // fire ("Failed to fetch" on the client) — but letting an unauthenticated OPTIONS reach the
        // upstream runs its handler code for free, which is the thing this gate exists to prevent.
        if (IsCorsPreflight(ctx.Request))
        {
            WritePreflightResponse(ctx);
            return;
        }

        // Header check — short-circuit BEFORE the request reaches OIDC, since machine-to-machine
        // calls won't have a browser session.
        if (options.Header is { } h)
        {
            if (string.IsNullOrEmpty(h.Value))
            {
                // A blank credential is a broken gate, not an exemption: refuse rather than proxy.
                await Reject(ctx, StatusCodes.Status503ServiceUnavailable,
                    $"Header auth for '{h.Name}' is configured without a value; refusing to proxy.");
                return;
            }
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
            var country = ReadTrustedCountry(ctx);
            if (country is null)
            {
                // No trustworthy country header means we cannot evaluate the allowlist at all.
                // Failing open here would silently disable the check on every non-Cloudflare
                // deployment, so refuse and say why.
                await Reject(ctx, 403,
                    "Country allowlist is configured but no trusted CF-IPCountry header is present " +
                    "(this check only works behind a Cloudflare tunnel).");
                return;
            }
            if (!_countries.Contains(country))
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

        // Feed the rate limiter: a client that keeps failing gets a much tighter bucket than
        // ordinary proxy traffic on the next request.
        if (status is 401 or 403)
        {
            _failures?.RecordFailure(RateLimitPartitionKey(ctx, options));
        }

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

    private IPAddress? ResolveClientIp(HttpContext ctx) => ResolveClientIp(ctx, options);

    /// <summary>
    /// The client address the allowlists are evaluated against. <c>CF-Connecting-IP</c> is only
    /// believed when Cloudflare is the configured provider AND the request really did arrive over
    /// the tunnel's loopback hop; otherwise anyone — including a local process, or a remote client
    /// behind a fronting proxy that doesn't rewrite the peer — could name its own source address.
    /// Everything else defers to whatever <c>UseForwardedHeaders</c> already resolved: it consumes
    /// the rightmost <c>X-Forwarded-For</c> entry from a known loopback proxy, which is the only
    /// entry a client cannot append to.
    /// </summary>
    private static IPAddress? ResolveClientIp(HttpContext ctx, TapAuthOptions authOptions)
    {
        if (TrustsForwardedCloudflareHeaders(ctx, authOptions))
        {
            var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            if (!string.IsNullOrEmpty(cf) && IPAddress.TryParse(cf, out var ip)) return ip;
        }
        return ctx.Connection.RemoteIpAddress;
    }

    /// <summary>
    /// Best-effort per-client key for rate-limit partitioning. It runs <em>before</em>
    /// <c>UseForwardedHeaders</c>, so it has to read the forwarded headers itself and mirrors that
    /// middleware's <c>ForwardLimit = 1</c> by taking the rightmost <c>X-Forwarded-For</c> entry.
    /// Never an authorization input — the worst a wrong key can do is put a caller in the wrong bucket.
    /// </summary>
    public static string RateLimitPartitionKey(HttpContext ctx, TapAuthOptions authOptions)
    {
        var peer = TransportPeer(ctx);
        if (IsTrustedForwarder(peer))
        {
            if (authOptions.TrustsCloudflareHeaders)
            {
                var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString().Trim();
                if (cf.Length > 0) return cf;
            }

            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (fwd.Length > 0)
            {
                var last = fwd.AsSpan()[(fwd.LastIndexOf(',') + 1)..].Trim();
                if (last.Length > 0) return last.ToString();
            }
        }
        return peer?.ToString() ?? "unknown";
    }

    /// <summary>
    /// The country Cloudflare reported, or null when there is nothing trustworthy to read.
    /// </summary>
    private static string? ReadTrustedCountry(HttpContext ctx, TapAuthOptions authOptions)
    {
        if (!TrustsForwardedCloudflareHeaders(ctx, authOptions)) return null;
        var country = ctx.Request.Headers["CF-IPCountry"].ToString().Trim().ToUpperInvariant();
        return country.Length == 0 ? null : country;
    }

    private string? ReadTrustedCountry(HttpContext ctx) => ReadTrustedCountry(ctx, options);

    private static bool TrustsForwardedCloudflareHeaders(HttpContext ctx, TapAuthOptions authOptions) =>
        authOptions.TrustsCloudflareHeaders && IsTrustedForwarder(TransportPeer(ctx));

    /// <summary>The peer we saw on the socket, before <c>UseForwardedHeaders</c> rewrote it.</summary>
    private static IPAddress? TransportPeer(HttpContext ctx) =>
        ctx.Items.TryGetValue(TransportPeerKey, out var stashed) && stashed is IPAddress peer
            ? peer
            : ctx.Connection.RemoteIpAddress;

    private static bool IsTrustedForwarder(IPAddress? remoteIp)
    {
        if (remoteIp is null) return false;
        if (IPAddress.IsLoopback(remoteIp)) return true;
        return remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4());
    }

    private static bool IsCorsPreflight(HttpRequest request) =>
        HttpMethods.IsOptions(request.Method) &&
        request.Headers.ContainsKey("Origin") &&
        request.Headers.ContainsKey("Access-Control-Request-Method");

    /// <summary>
    /// Answers a preflight without consulting the upstream. Tap echoes what the browser asked for
    /// so an authenticated cross-origin call still works, but never grants
    /// <c>Access-Control-Allow-Credentials</c>: the actual request is still gated, and its response
    /// headers still come from the upstream, so nothing here makes a response readable that wasn't.
    /// </summary>
    private static void WritePreflightResponse(HttpContext ctx)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        response.StatusCode = StatusCodes.Status204NoContent;
        response.Headers["Access-Control-Allow-Origin"] = request.Headers.Origin;
        response.Headers["Access-Control-Allow-Methods"] = request.Headers["Access-Control-Request-Method"];
        var requestedHeaders = request.Headers["Access-Control-Request-Headers"];
        if (requestedHeaders.Count > 0)
        {
            response.Headers["Access-Control-Allow-Headers"] = requestedHeaders;
        }
        response.Headers["Access-Control-Max-Age"] = "600";
        response.Headers.Vary = "Origin";
        response.Headers.CacheControl = "no-store";
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
    // Pinned so a token can never pick its own verification algorithm — most importantly not
    // "none", and not an HMAC verified against a public JWKS key.
    private static readonly string[] SymmetricAlgorithms =
    [
        SecurityAlgorithms.HmacSha256, SecurityAlgorithms.HmacSha384, SecurityAlgorithms.HmacSha512,
    ];

    private static readonly string[] AsymmetricAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly JwtAuthOptions _options;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SymmetricSecurityKey? _symmetricKey;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public JwtTokenValidator(JwtAuthOptions options)
    {
        _options = options;

        if (string.IsNullOrWhiteSpace(options.Audience) && !options.AllowAnyAudience)
        {
            throw new InvalidOperationException(
                "JwtAuthOptions requires an Audience, or AllowAnyAudience = true to say explicitly that " +
                "any audience is acceptable. Inferring 'no audience configured' as 'don't check the " +
                "audience' is how a token minted for a different application ends up passing this gate.");
        }

        if (!string.IsNullOrWhiteSpace(options.SecretKey))
        {
            _symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey));
        }
        else if (!string.IsNullOrWhiteSpace(options.WellKnownEndpoint))
        {
            if (string.IsNullOrWhiteSpace(options.Issuer))
            {
                throw new InvalidOperationException(
                    "JwtAuthOptions.WellKnownEndpoint requires an Issuer. A multi-tenant JWKS (for " +
                    "example login.microsoftonline.com/common) signs tokens for every tenant, so " +
                    "without an issuer check any of them would validate against this gate.");
            }

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
            // The JWKS branch cannot get here without an Issuer (the constructor refuses). With a
            // symmetric secret the key itself pins the issuer, so an explicit one stays optional.
            ValidateIssuer = !string.IsNullOrWhiteSpace(_options.Issuer),
            ValidIssuer = _options.Issuer,
            ValidateAudience = !_options.AllowAnyAudience,
            ValidAudience = _options.Audience,
        };

        if (_symmetricKey is not null)
        {
            parameters.IssuerSigningKey = _symmetricKey;
            parameters.ValidAlgorithms = SymmetricAlgorithms;
        }
        else if (_configManager is not null)
        {
            var config = await _configManager.GetConfigurationAsync(ct);
            parameters.IssuerSigningKeys = config.SigningKeys;
            parameters.ValidAlgorithms = AsymmetricAlgorithms;
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

/// <summary>
/// Counts recent auth failures per client so the rate limiter can drop a caller that is guessing
/// the API key into a much tighter bucket than ordinary proxy traffic. In-process and approximate:
/// this is a speed bump in front of a dev machine, not an account-lockout system.
/// </summary>
public sealed class TapAuthFailureTracker
{
    /// <summary>Failures inside <see cref="Window"/> after which a client is treated as suspect.</summary>
    public const int Threshold = 5;

    /// <summary>How long failures are remembered for.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _failures = new();

    public void RecordFailure(string client)
    {
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(
            client,
            _ => (1, now),
            (_, existing) => now - existing.Since > Window ? (1, now) : (existing.Count + 1, existing.Since));
        Prune(now);
    }

    public bool IsSuspect(string client) =>
        _failures.TryGetValue(client, out var entry) &&
        entry.Count >= Threshold &&
        DateTimeOffset.UtcNow - entry.Since <= Window;

    // Only sweeps once the map is big enough to be worth sweeping — a busy proxy would otherwise
    // pay for an enumeration on every rejected request.
    private void Prune(DateTimeOffset now)
    {
        if (_failures.Count < 512) return;
        foreach (var (client, entry) in _failures)
        {
            if (now - entry.Since > Window) _failures.TryRemove(client, out _);
        }
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
    /// <summary>The one <c>/tap-auth/*</c> route Tap serves itself; everything else under that prefix is upstream.</summary>
    private const string SignOutPath = "/tap-auth/signout";

    public static IApplicationBuilder UseTapAuth(this IApplicationBuilder app, TapAuthOptions options)
    {
        if (!options.AnyConfigured) return app;

        // UseForwardedHeaders overwrites Connection.RemoteIpAddress, so stash the real transport
        // peer first: "did this arrive over the tunnel's loopback hop?" is unanswerable afterwards,
        // and that question is what decides whether the CF-* headers may be believed.
        app.Use(async (ctx, nxt) =>
        {
            ctx.Items[TapAuthMiddleware.TransportPeerKey] = ctx.Connection.RemoteIpAddress;
            await nxt();
        });

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
                // Exact matches only. A StartsWith exemption also unlocks "/signin-oidc/anything"
                // and the whole "/tap-auth/" prefix, and the catch-all YARP route behind this
                // middleware would happily proxy those to the upstream unauthenticated.
                var isExempt =
                    path.Equals(callback, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/signout-oidc", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/signout-callback-oidc", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(SignOutPath, StringComparison.OrdinalIgnoreCase);

                if (isExempt)
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
                        new AuthenticationProperties { RedirectUri = SafeRedirect(ctx, returnTo) });
                    return;
                }
                await nxt();
            });

            // Sign-out endpoint: clears the cookie and triggers the OIDC end-session flow.
            // GET /tap-auth/signout?post=<url>
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Path.Equals(SignOutPath, StringComparison.OrdinalIgnoreCase))
                {
                    var post = SafeRedirect(ctx, ctx.Request.Query["post"].ToString());
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

    /// <summary>
    /// Clamps a redirect target to this origin. Sign-out takes the destination from the query
    /// string, which makes the gate an open redirect — and a convincing one, because the hop starts
    /// on the tunnel's own hostname — unless the value is a local path or names this exact origin.
    /// </summary>
    private static string SafeRedirect(HttpContext ctx, string? target)
    {
        if (string.IsNullOrEmpty(target)) return "/";

        // "//evil.example" and "/\evil.example" are protocol-relative: they look local and aren't.
        if (target[0] == '/')
        {
            return target.Length == 1 || (target[1] != '/' && target[1] != '\\') ? target : "/";
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps) &&
            string.Equals(absolute.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(absolute.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
                ? target
                : "/";
    }
}
