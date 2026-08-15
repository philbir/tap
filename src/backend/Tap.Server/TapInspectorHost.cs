using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Tap.Core.Auth;
using Yarp.ReverseProxy.Configuration;

namespace Tap.Server;

/// <summary>
/// Builds a fully-configured inspector <see cref="WebApplication"/>. Reused by both the
/// standalone <c>Tap.Server</c> entry point and the <c>tap</c> CLI tool.
/// </summary>
public sealed class TapInspectorOptions
{
    public required int ProxyPort { get; init; }
    public required int UiPort { get; init; }

    /// <summary>
    /// Address the capture proxy binds to. Loopback by default: cloudflared and tailscaled both
    /// connect over localhost, so a wildcard bind only ever adds LAN reachability — and a plain
    /// <c>tap run</c> has no tunnel and no auth in front of it. Container hosting sets
    /// <c>Inspector:ProxyHost</c> to <c>0.0.0.0</c> explicitly, because there the wildcard is the
    /// only way the published port reaches the process.
    /// </summary>
    public string ProxyHost { get; init; } = "localhost";
    public string UiHost { get; init; } = "localhost";

    /// <summary>
    /// Host header values the UI (control-plane) port will answer to, on top of the loopback
    /// literals and <see cref="UiHost"/>. Bound from <c>Inspector:UiAllowedHosts</c> as a
    /// comma-separated list. Only needed when the UI is bound to a wildcard address.
    /// </summary>
    public string[] UiAllowedHosts { get; init; } = [];
    public required InspectorIngressEntry[] Ingress { get; init; }
    public string Mode { get; init; } = "standalone";
    public TapAuthOptions? Auth { get; init; }

    /// <summary>"cloudflare" | "tailscale" | null. Gates provider-specific tunnel endpoints.</summary>
    public string? Provider { get; init; }

    // Optional per-inspector tunnel context — used to surface tunnel info via /api/tunnel/details.
    public string? TunnelMode { get; init; }
    public string? TunnelName { get; init; }
    public string? TunnelResourceName { get; init; }
    public string? TunnelPublicUrl { get; init; }
    public string? TunnelAccountId { get; init; }
    public string? TunnelTunnelId { get; init; }
    public string? TunnelApiToken { get; init; }

    /// <summary>Tailscale daemon socket path (ephemeral mode); empty/null = system default.</summary>
    public string? TunnelSocketPath { get; init; }

    /// <summary>
    /// When true, ASP.NET Core's framework log categories (Microsoft.*, System.*) are
    /// pinned to Warning so the CLI can render its own request log on top.
    /// </summary>
    public bool Quiet { get; init; }
}

public static class TapInspectorHost
{
    public static TapInspectorOptions OptionsFromConfiguration(IConfiguration config)
    {
        var ingressJson = config["Inspector:Ingress"];
        var ingress = string.IsNullOrWhiteSpace(ingressJson)
            ? Array.Empty<InspectorIngressEntry>()
            : JsonSerializer.Deserialize(ingressJson, InspectorIngressJsonContext.Default.InspectorIngressEntryArray) ?? [];

        var auth = new TapAuthOptions();
        config.GetSection("Inspector:Auth").Bind(auth);

        // A header credential that binds to an empty string — an unset $TAP_KEY, a config key that
        // isn't there — used to register the gate and then wave every request through. Refuse to
        // start instead: a tunnel that claims to be protected has to actually be protected.
        if (auth.Header is { } header && string.IsNullOrEmpty(header.Value))
        {
            throw new InvalidOperationException(
                $"Inspector:Auth:Header is configured for '{header.Name}' but its Value is empty. " +
                "Set Inspector__Auth__Header__Value, or remove the header auth configuration.");
        }

        return new TapInspectorOptions
        {
            ProxyPort = config.GetValue<int>("Inspector:ProxyPort"),
            UiPort = config.GetValue<int>("Inspector:UiPort"),
            ProxyHost = config["Inspector:ProxyHost"] ?? "localhost",
            UiHost = config["Inspector:UiHost"] ?? "localhost",
            UiAllowedHosts = (config["Inspector:UiAllowedHosts"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Ingress = ingress,
            Mode = config["Inspector:Mode"] ?? "standalone",
            Auth = auth.AnyConfigured ? auth : null,
            Provider = config["Inspector:Provider"],
            TunnelMode = config["Inspector:Tunnel:Mode"],
            TunnelName = config["Inspector:Tunnel:Name"],
            TunnelResourceName = config["Inspector:Tunnel:ResourceName"],
            TunnelPublicUrl = config["Inspector:Tunnel:PublicUrl"],
            TunnelAccountId = config["Inspector:Tunnel:AccountId"],
            TunnelTunnelId = config["Inspector:Tunnel:TunnelId"],
            TunnelApiToken = config["Inspector:Tunnel:ApiToken"],
            TunnelSocketPath = config["Inspector:Tunnel:SocketPath"],
        };
    }

    public static WebApplication Build(string[] args, TapInspectorOptions options)
    {
        if (options.ProxyPort <= 0 || options.UiPort <= 0)
        {
            throw new InvalidOperationException("Inspector ProxyPort and UiPort must be > 0.");
        }

        // Point WebRoot at Tap.Server's wwwroot (bundled UI) regardless of who's hosting us.
        // Use AppContext.BaseDirectory so this works in single-file publish too.
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Directory.Exists(wwwroot) ? wwwroot : null,
        });
        builder.WebHost.UseUrls(
            $"http://{options.ProxyHost}:{options.ProxyPort}",
            $"http://{options.UiHost}:{options.UiPort}");

        // The inspector is a transparent proxy — body-size policy belongs to the upstream API,
        // not to Tap. Kestrel's default 30 MB cap would otherwise reject large uploads (videos,
        // datasets, etc.) with 413 before they ever reach the upstream.
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);

        if (options.Quiet)
        {
            // Suppress per-request and lifetime chatter — the CLI renders its own log.
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("System", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
            builder.Logging.AddFilter("Yarp", LogLevel.Warning);

            // Compact single-line colored output for anything that does make it through
            // (auth rejections, unexpected errors), so it sits well next to the Spectre table.
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new TapCompactConsoleLoggerProvider());
        }
        builder.Logging.AddFilter("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Error);

        var (routes, clusters) = BuildYarpConfig(options.Ingress);

        builder.Services.AddSingleton<InMemoryRequestStore>();
        builder.Services.AddSingleton<IRequestStore>(sp => sp.GetRequiredService<InMemoryRequestStore>());
        builder.Services.AddSingleton(options.Ingress);

        builder.Services.AddHttpClient("replay").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });

        var cloudflareOptions = new CloudflareOptions
        {
            ApiToken = options.TunnelApiToken ?? builder.Configuration["Cloudflare:ApiToken"],
            AccountId = options.TunnelAccountId ?? builder.Configuration["Cloudflare:AccountId"],
            TunnelId = options.TunnelTunnelId ?? builder.Configuration["Cloudflare:TunnelId"],
        };
        builder.Services.AddSingleton(cloudflareOptions);
        builder.Services.AddHttpClient<CloudflareClient>();

        builder.Services.AddSingleton(new TailscaleOptions { SocketPath = options.TunnelSocketPath });
        builder.Services.AddSingleton<TailscaleClient>();

        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

        if (options.Auth is { } auth)
        {
            // The CF-* forwarded headers are only believable when Cloudflare is actually the thing
            // in front of us, so the gate is told which provider it is sitting behind.
            auth.Provider = options.Provider;
            builder.Services.AddTapAuth(auth);
            AddAuthRateLimiter(builder.Services, auth);
        }

        var app = builder.Build();

        if (!options.Quiet)
        {
            // Reported from the checks that will actually run, never from "an Auth object exists" —
            // a gate that skips every check must not announce itself as enforced.
            IReadOnlyList<string> enforced = options.Auth?.EnforcedChecks ?? [];
            app.Logger.LogInformation(
                "HTTP Inspector starting. Proxy: http://{ProxyHost}:{ProxyPort}, UI: http://{UiHost}:{UiPort}. {Ingress} ingress entr{Suffix}.{Auth}",
                options.ProxyHost, options.ProxyPort, options.UiHost, options.UiPort, options.Ingress.Length,
                options.Ingress.Length == 1 ? "y" : "ies",
                enforced.Count > 0 ? $" Auth: enforced ({string.Join(", ", enforced)})." : "");
        }

        // Proxy branch: tunnel/captured traffic, with optional auth in front.
        app.MapWhen(ctx => ctx.Connection.LocalPort == options.ProxyPort, proxy =>
        {
            // WebSockets: must come before CaptureMiddleware so ctx.WebSockets.IsWebSocketRequest
            // resolves correctly when we intercept the upgrade.
            proxy.UseWebSockets();
            proxy.Use(ServeErrorPageAssets);
            if (options.Auth is not null)
            {
                // Ahead of the gate: without it a static header key on a public tunnel is open to
                // unlimited online guessing. Only on this branch — the UI branch is loopback-only.
                proxy.UseRateLimiter();
                proxy.UseTapAuth(options.Auth);
            }
            proxy.UseMiddleware<CaptureMiddleware>();
            proxy.UseRouting();
            proxy.Use(next =>
            {
                var logger = proxy.ApplicationServices.GetRequiredService<ILogger<UpstreamErrorPageMiddleware>>();
                return new UpstreamErrorPageMiddleware(next, options.Ingress, logger).InvokeAsync;
            });
            proxy.UseEndpoints(ep => ep.MapReverseProxy());
        });

        // UI branch — local control plane. Binding to loopback keeps other machines out, but
        // it is NOT an authorization boundary against the developer's own browser: Kestrel
        // answers whatever Host header arrives, so a page on attacker.example whose DNS flips
        // to 127.0.0.1 becomes same-origin with this port and can read every response
        // (captured Authorization headers, tunnel profiles). Pinning the acceptable Host
        // values is what closes that; the origin guard behind it is defence in depth.
        var (uiHosts, allowAnyUiHost) = BuildUiHostAllowlist(options);
        if (allowAnyUiHost)
        {
            app.Logger.LogWarning(
                "Inspector UI is bound to a wildcard host ('{UiHost}') with no Inspector:UiAllowedHosts set, so the " +
                "control plane accepts any Host header and cannot be defended against DNS rebinding. " +
                "Set Inspector:UiAllowedHosts to the hostname(s) you actually browse it on.",
                options.UiHost);
        }

        app.MapWhen(ctx => ctx.Connection.LocalPort == options.UiPort, ui =>
        {
            ui.Use(HostAllowlist(uiHosts, allowAnyUiHost));
            ui.Use(RejectCrossOriginRequests);
            ui.UseDefaultFiles();
            ui.UseStaticFiles();
            ui.UseRouting();
            ui.UseEndpoints(ep => MapUiEndpoints(ep, options, cloudflareOptions));
        });

        return app;
    }

    /// <summary>
    /// Per-client-IP throttling for the auth-gated proxy branch. Tap is a transparent proxy, so the
    /// normal bucket is deliberately wide enough that real traffic never notices it; a client that
    /// has recently failed the gate (<see cref="TapAuthFailureTracker"/>) drops into a bucket that
    /// makes guessing a key pointless. The partition key is best-effort — see
    /// <see cref="TapAuthMiddleware.RateLimitPartitionKey"/> — because this runs before
    /// UseForwardedHeaders; behind a tunnel that hides the client address every request shares one
    /// partition, which is why the normal limit is a ceiling rather than a per-user quota.
    /// </summary>
    private static void AddAuthRateLimiter(IServiceCollection services, TapAuthOptions auth)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var client = TapAuthMiddleware.RateLimitPartitionKey(ctx, auth);
                var suspect = ctx.RequestServices.GetService<TapAuthFailureTracker>()?.IsSuspect(client) == true;

                return suspect
                    ? RateLimitPartition.GetFixedWindowLimiter($"suspect:{client}", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetFixedWindowLimiter($"client:{client}", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 1200,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        });
            });
        });
    }

    private static async Task ServeErrorPageAssets(HttpContext ctx, RequestDelegate next)
    {
        var assetName = ctx.Request.Path.Value switch
        {
            "/tap-error-broken.png" => "tap-error-broken.png",
            "/tap-error-denied.png" => "tap-error-denied.png",
            _ => null
        };

        if (assetName is null)
        {
            await next(ctx);
            return;
        }

        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var webRoot = !string.IsNullOrEmpty(env.WebRootPath)
            ? env.WebRootPath
            : Path.Combine(env.ContentRootPath, "wwwroot");
        var path = Path.Combine(webRoot, assetName);

        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "wwwroot", assetName);
        }

        if (!File.Exists(path))
        {
            await next(ctx);
            return;
        }

        ctx.Response.ContentType = "image/png";
        ctx.Response.Headers.CacheControl = "public, max-age=3600";
        await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
    }

    private static readonly string[] LoopbackHostNames = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// The Host values the control plane answers to. Wildcard binds ("0.0.0.0", "*", "+", "::")
    /// have no single correct hostname, so unless the operator names one via
    /// <c>Inspector:UiAllowedHosts</c> we keep accepting any Host (container/LAN hosting still
    /// works) and warn at startup instead of silently breaking it.
    /// </summary>
    private static (HashSet<string> Hosts, bool AllowAny) BuildUiHostAllowlist(TapInspectorOptions options)
    {
        var hosts = new HashSet<string>(LoopbackHostNames, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in options.UiAllowedHosts)
        {
            hosts.Add(NormalizeHost(extra));
        }

        var isWildcard = options.UiHost is "0.0.0.0" or "*" or "+" or "::" or "[::]";
        if (!isWildcard)
        {
            hosts.Add(NormalizeHost(options.UiHost));
        }

        return (hosts, isWildcard && options.UiAllowedHosts.Length == 0);
    }

    /// <summary>Strips the brackets Kestrel keeps around an IPv6 literal so "[::1]" matches "::1".</summary>
    private static string NormalizeHost(string host) => host.Trim().Trim('[', ']');

    private static Func<HttpContext, RequestDelegate, Task> HostAllowlist(HashSet<string> allowed, bool allowAny) =>
        async (ctx, next) =>
        {
            if (allowAny || allowed.Contains(NormalizeHost(ctx.Request.Host.Host)))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsync(
                "Tap inspector control plane: this Host is not allowed. Browse it on localhost, " +
                "or list the hostname in Inspector:UiAllowedHosts.");
        };

    /// <summary>
    /// Keeps foreign origins off the control plane. Two layers:
    /// <list type="bullet">
    /// <item><c>Sec-Fetch-Site</c> — sent by every current browser and not forgeable from
    /// script, so it covers GET as well. That matters because the endpoints worth stealing
    /// (<c>/api/requests</c>, <c>/api/profiles</c>, <c>/api/stream</c>) are all reads; the old
    /// guard exempted every safe method. Top-level navigations to non-API paths stay allowed —
    /// following a link to the UI is legitimate and the response is not script-readable.</item>
    /// <item><c>Origin</c>/<c>Referer</c> — the original check, still applied to unsafe methods.
    /// It compares against <c>ctx.Request.Host</c>, which is only trustworthy because
    /// <see cref="HostAllowlist"/> has already pinned that value.</item>
    /// </list>
    /// Non-browser clients (curl, the CLI) send neither header and are unaffected.
    /// </summary>
    private static async Task RejectCrossOriginRequests(HttpContext ctx, RequestDelegate next)
    {
        if (string.Equals(ctx.Request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase)
            && !IsTopLevelDocumentNavigation(ctx.Request))
        {
            await RejectCrossOrigin(ctx);
            return;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) ||
            HttpMethods.IsHead(ctx.Request.Method) ||
            HttpMethods.IsOptions(ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        var origin = ctx.Request.Headers.Origin.ToString();
        var referer = ctx.Request.Headers.Referer.ToString();
        if ((string.IsNullOrEmpty(origin) || IsSameOrigin(origin, ctx.Request.Host)) &&
            (string.IsNullOrEmpty(referer) || IsSameOrigin(referer, ctx.Request.Host)))
        {
            await next(ctx);
            return;
        }

        await RejectCrossOrigin(ctx);
    }

    private static bool IsTopLevelDocumentNavigation(HttpRequest req) =>
        !req.Path.StartsWithSegments("/api") &&
        string.Equals(req.Headers["Sec-Fetch-Mode"].ToString(), "navigate", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(req.Headers["Sec-Fetch-Dest"].ToString(), "document", StringComparison.OrdinalIgnoreCase);

    private static async Task RejectCrossOrigin(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync("Cross-origin control-plane requests are not allowed.");
    }

    private static bool IsSameOrigin(string value, HostString host)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == (host.Port ?? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));
    }

    private static void MapUiEndpoints(IEndpointRouteBuilder ep, TapInspectorOptions options, CloudflareOptions cloudflareOptions)
    {
        var ingress = options.Ingress;
        var proxyPort = options.ProxyPort;

        ep.MapGet("/api/requests", (IRequestStore store) =>
            Results.Json(store.GetAll(), RequestRecordJsonContext.Default.ListRequestRecord));

        ep.MapDelete("/api/requests", (IRequestStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        ep.MapGet("/api/ingress",
            () => Results.Json(ingress, InspectorIngressJsonContext.Default.InspectorIngressEntryArray));

        ep.MapGet("/api/config", () => Results.Json(
            new InspectorConfig(proxyPort, ingress, cloudflareOptions.IsConfigured ? "cloudflare-api" : "token", options.Mode)
            {
                Provider = options.Provider,
            },
            InspectorConfigJsonContext.Default.InspectorConfig));

        ep.MapGet("/api/tunnel/details", async (CloudflareClient cf, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(options.TunnelMode))
            {
                return Results.NotFound(new { error = "No tunnel attached to this inspector." });
            }

            // Tailscale: return the basic Cloudflare-shaped context (so the existing UI keeps working)
            // — the rich Tailscale-specific snapshot is served separately at /api/tunnel/tailscale/status.
            if (!string.Equals(options.Provider, "cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new TunnelContext(
                    Mode: options.TunnelMode!,
                    Name: options.TunnelName ?? options.TunnelResourceName ?? "tunnel",
                    ResourceName: options.TunnelResourceName ?? "",
                    PublicUrl: options.TunnelPublicUrl,
                    AccountId: null,
                    TunnelId: null,
                    DashboardUrl: null,
                    ApiResolved: false,
                    Status: null,
                    CreatedAt: null,
                    Connections: null,
                    Error: null
                ), InspectorConfigJsonContext.Default.TunnelContext);
            }

            var dashboard = !string.IsNullOrEmpty(options.TunnelAccountId) && !string.IsNullOrEmpty(options.TunnelTunnelId)
                ? $"https://dash.cloudflare.com/{options.TunnelAccountId}/one/networks/tunnels/cfd_tunnel/{options.TunnelTunnelId}/edit"
                : null;

            string? status = null, createdAt = null;
            int? connections = null;
            string? error = null;
            var apiResolved = false;

            if (cloudflareOptions.IsConfigured && !string.IsNullOrEmpty(options.TunnelAccountId) && !string.IsNullOrEmpty(options.TunnelTunnelId))
            {
                try
                {
                    var details = await cf.GetTunnelAsync(options.TunnelAccountId!, options.TunnelTunnelId!, ct);
                    if (details is not null)
                    {
                        status = details.Status;
                        createdAt = details.CreatedAt;
                        connections = details.Connections?.Length;
                        apiResolved = true;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            }

            return Results.Json(new TunnelContext(
                Mode: options.TunnelMode!,
                Name: options.TunnelName ?? options.TunnelResourceName ?? "tunnel",
                ResourceName: options.TunnelResourceName ?? "",
                PublicUrl: options.TunnelPublicUrl,
                AccountId: options.TunnelAccountId,
                TunnelId: options.TunnelTunnelId,
                DashboardUrl: dashboard,
                ApiResolved: apiResolved,
                Status: status,
                CreatedAt: createdAt,
                Connections: connections,
                Error: error
            ), InspectorConfigJsonContext.Default.TunnelContext);
        });

        // Tailscale-only: live daemon status + active funnel/serve rules.
        ep.MapGet("/api/tunnel/tailscale/status", async (TailscaleClient ts, CancellationToken ct) =>
        {
            if (!string.Equals(options.Provider, "tailscale", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"Provider '{options.Provider ?? "none"}' is not Tailscale." });
            }
            var snap = await ts.GetSnapshotAsync(ct);
            return Results.Json(snap, TailscaleJsonContext.Default.TailscaleSnapshot);
        });

        // Cloudflare-only: read or modify named-tunnel ingress rules.
        // For other providers (Tailscale Funnel) ingress mutation is meaningless — one node = one URL.
        ep.MapGet("/api/tunnel/ingress", async (CloudflareClient cf, CancellationToken ct) =>
        {
            if (!string.Equals(options.Provider, "cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"Provider '{options.Provider ?? "none"}' does not support hostname-based ingress." });
            }
            if (!cloudflareOptions.IsConfigured) return Results.BadRequest(new { error = "Cloudflare API not configured." });
            try
            {
                var rules = await cf.GetIngressAsync(ct);
                var results = rules
                    .Where(r => !string.IsNullOrEmpty(r.Hostname))
                    .Select(r => new HostnameResult(r.Hostname!, r.Service))
                    .ToArray();
                return Results.Json(results, InspectorConfigJsonContext.Default.HostnameResultArray);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        ep.MapPost("/api/tunnel/ingress", async (UpsertHostnameRequest body, CloudflareClient cf, CancellationToken ct) =>
        {
            if (!string.Equals(options.Provider, "cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"Provider '{options.Provider ?? "none"}' does not support hostname-based ingress." });
            }
            if (!cloudflareOptions.IsConfigured) return Results.BadRequest(new { error = "Cloudflare API not configured." });
            if (string.IsNullOrWhiteSpace(body.Hostname)) return Results.BadRequest(new { error = "hostname is required" });
            try
            {
                var service = $"http://localhost:{proxyPort}";
                var rules = await cf.UpsertIngressAsync(body.Hostname, service, ct);
                var results = rules
                    .Where(r => !string.IsNullOrEmpty(r.Hostname))
                    .Select(r => new HostnameResult(r.Hostname!, r.Service))
                    .ToArray();
                return Results.Json(results, InspectorConfigJsonContext.Default.HostnameResultArray);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        ep.MapGet("/api/stream", async (HttpContext ctx, IRequestStore store, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            await foreach (var ev in store.Stream(ct))
            {
                switch (ev)
                {
                    case RecordEvent re:
                    {
                        var json = JsonSerializer.Serialize(re.Record, RequestRecordJsonContext.Default.RequestRecord);
                        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                        break;
                    }
                    case SseStreamEvent se:
                    {
                        var envelope = new SseEventEnvelope(se.RecordId, se.Sequence, se.Event);
                        var json = JsonSerializer.Serialize(envelope, RequestRecordJsonContext.Default.SseEventEnvelope);
                        await ctx.Response.WriteAsync($"event: sse\ndata: {json}\n\n", ct);
                        break;
                    }
                    case WebSocketStreamEvent we:
                    {
                        var envelope = new WebSocketMessageEnvelope(we.RecordId, we.Sequence, we.Message);
                        var json = JsonSerializer.Serialize(envelope, RequestRecordJsonContext.Default.WebSocketMessageEnvelope);
                        await ctx.Response.WriteAsync($"event: ws\ndata: {json}\n\n", ct);
                        break;
                    }
                }
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        ep.MapPost("/api/requests/{id:guid}/replay", async (
            Guid id, IRequestStore store, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var record = store.GetAll().FirstOrDefault(r => r.Id == id);
            if (record is null) return Results.NotFound();

            if (!TryCreateHttpMethod(record.Method, out var replayMethod))
            {
                return Results.Json(new ReplayResponse(false, Error: "Invalid method."),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
            }

            using var req = new HttpRequestMessage(replayMethod,
                $"http://localhost:{proxyPort}{record.Path}");
            req.Headers.Host = record.Host;

            if (!string.IsNullOrEmpty(record.RequestBody))
            {
                if (!TryCreateReplayContent(record.RequestBody, record.RequestContentType, out var content, out var error))
                {
                    return Results.Json(new ReplayResponse(false, Error: error),
                        RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
                }
                req.Content = content;
            }

            foreach (var (key, value) in record.RequestHeaders)
            {
                if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (!req.Headers.TryAddWithoutValidation(key, value) && req.Content is not null)
                {
                    req.Content.Headers.TryAddWithoutValidation(key, value);
                }
            }

            try
            {
                var client = httpClientFactory.CreateClient("replay");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return Results.Json(new ReplayResponse(true, Status: (int)resp.StatusCode),
                    RequestRecordJsonContext.Default.ReplayResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new ReplayResponse(false, Error: ex.Message),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 500);
            }
        });

        // Edit-and-replay: a fully editable request payload, fired either through the
        // local proxy (path-mode → captured by the inspector) or against an absolute URL
        // (url-mode → off-proxy, useful for replaying against staging/prod for compare).
        ep.MapPost("/api/replay", async (HttpContext httpCtx, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            ReplayRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(httpCtx.Request.Body,
                    RequestRecordJsonContext.Default.ReplayRequest, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(new ReplayResponse(false, Error: $"Invalid JSON: {ex.Message}"),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
            }
            if (body is null || string.IsNullOrWhiteSpace(body.Method))
            {
                return Results.Json(new ReplayResponse(false, Error: "Missing method."),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
            }

            string targetUrl;
            string? hostHeader = null;
            if (!string.IsNullOrWhiteSpace(body.Url))
            {
                if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var parsed) ||
                    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    return Results.Json(new ReplayResponse(false, Error: "Url must be absolute http(s)."),
                        RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
                }
                targetUrl = parsed.ToString();
            }
            else if (!string.IsNullOrWhiteSpace(body.Path))
            {
                var path = body.Path.StartsWith('/') ? body.Path : "/" + body.Path;
                targetUrl = $"http://localhost:{proxyPort}{path}";
                hostHeader = body.Host;
            }
            else
            {
                return Results.Json(new ReplayResponse(false, Error: "Either path or url is required."),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
            }

            if (!TryCreateHttpMethod(body.Method, out var method))
            {
                return Results.Json(new ReplayResponse(false, Error: "Invalid method."),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
            }

            using var req = new HttpRequestMessage(method, targetUrl);
            if (!string.IsNullOrEmpty(hostHeader)) req.Headers.Host = hostHeader;

            if (!string.IsNullOrEmpty(body.Body))
            {
                if (!TryCreateReplayContent(body.Body, body.ContentType, out var content, out var error))
                {
                    return Results.Json(new ReplayResponse(false, Error: error),
                        RequestRecordJsonContext.Default.ReplayResponse, statusCode: 400);
                }
                req.Content = content;
            }

            if (body.Headers is not null)
            {
                foreach (var (key, value) in body.Headers)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!req.Headers.TryAddWithoutValidation(key, value) && req.Content is not null)
                    {
                        req.Content.Headers.TryAddWithoutValidation(key, value);
                    }
                }
            }

            try
            {
                var client = httpClientFactory.CreateClient("replay");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return Results.Json(new ReplayResponse(true, Status: (int)resp.StatusCode),
                    RequestRecordJsonContext.Default.ReplayResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new ReplayResponse(false, Error: ex.Message),
                    RequestRecordJsonContext.Default.ReplayResponse, statusCode: 500);
            }
        });

        ep.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        ProfileEndpoints.Map(ep);

        ep.MapFallbackToFile("index.html");
    }

    private static bool TryCreateHttpMethod(string value, out HttpMethod method)
    {
        method = HttpMethod.Get;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (ch <= 32 || ch >= 127 || "()<>@,;:\\\"/[]?={} \t".Contains(ch, StringComparison.Ordinal))
            {
                return false;
            }
        }

        method = new HttpMethod(trimmed.ToUpperInvariant());
        return true;
    }

    private static bool TryCreateReplayContent(
        string body,
        string? contentType,
        out StringContent content,
        out string? error)
    {
        content = new StringContent(body, Encoding.UTF8);
        error = null;

        if (string.IsNullOrWhiteSpace(contentType))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return true;
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            content.Dispose();
            content = null!;
            error = "Invalid content type.";
            return false;
        }

        content.Headers.ContentType = parsed;
        return true;
    }

    private static (RouteConfig[] routes, ClusterConfig[] clusters) BuildYarpConfig(IReadOnlyList<InspectorIngressEntry> ingress)
    {
        var routes = new RouteConfig[ingress.Count];
        var clusters = new ClusterConfig[ingress.Count];

        for (var i = 0; i < ingress.Count; i++)
        {
            var entry = ingress[i];
            var clusterId = $"cluster-{i}";
            routes[i] = new RouteConfig
            {
                RouteId = $"route-{i}",
                ClusterId = clusterId,
                Match = string.IsNullOrEmpty(entry.Hostname)
                    ? new RouteMatch { Path = "/{**catchall}" }
                    : new RouteMatch { Hosts = [entry.Hostname], Path = "/{**catchall}" },
            };
            clusters[i] = new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    [$"dest-{i}"] = new DestinationConfig { Address = entry.Upstream },
                },
            };
        }

        return (routes, clusters);
    }
}

internal sealed class TapCompactConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => TapCompactConsoleLogger.Instance;
    public void Dispose() { }
}

internal sealed class TapCompactConsoleLogger : ILogger
{
    public static readonly TapCompactConsoleLogger Instance = new();
    private static readonly object ConsoleLock = new();

    private TapCompactConsoleLogger() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        var icon = logLevel switch
        {
            LogLevel.Critical => "🚨",
            LogLevel.Error => "❌",
            LogLevel.Warning => "⚠️",
            LogLevel.Information => "ℹ️",
            _ => "•"
        };

        lock (ConsoleLock)
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {icon}  {message}");
            if (exception is not null)
            {
                Console.WriteLine(exception);
            }
        }
    }
}
