using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    public string ProxyHost { get; init; } = "0.0.0.0";
    public string UiHost { get; init; } = "localhost";
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

        return new TapInspectorOptions
        {
            ProxyPort = config.GetValue<int>("Inspector:ProxyPort"),
            UiPort = config.GetValue<int>("Inspector:UiPort"),
            ProxyHost = config["Inspector:ProxyHost"] ?? "0.0.0.0",
            UiHost = config["Inspector:UiHost"] ?? "localhost",
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

        if (options.Auth is not null)
        {
            builder.Services.AddTapAuth(options.Auth);
        }

        var app = builder.Build();

        if (!options.Quiet)
        {
            app.Logger.LogInformation(
                "HTTP Inspector starting. Proxy: http://{ProxyHost}:{ProxyPort}, UI: http://{UiHost}:{UiPort}. {Ingress} ingress entr{Suffix}.{Auth}",
                options.ProxyHost, options.ProxyPort, options.UiHost, options.UiPort, options.Ingress.Length,
                options.Ingress.Length == 1 ? "y" : "ies",
                options.Auth is not null ? " Auth: enforced." : "");
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

        // UI branch — local control plane. It binds to loopback by default and rejects
        // cross-origin unsafe browser requests.
        app.MapWhen(ctx => ctx.Connection.LocalPort == options.UiPort, ui =>
        {
            ui.Use(RejectCrossOriginUnsafeRequests);
            ui.UseDefaultFiles();
            ui.UseStaticFiles();
            ui.UseRouting();
            ui.UseEndpoints(ep => MapUiEndpoints(ep, options, cloudflareOptions));
        });

        return app;
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

    private static async Task RejectCrossOriginUnsafeRequests(HttpContext ctx, RequestDelegate next)
    {
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

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
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
