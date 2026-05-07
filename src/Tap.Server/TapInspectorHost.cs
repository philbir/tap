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
    public required InspectorIngressEntry[] Ingress { get; init; }
    public string Mode { get; init; } = "standalone";
    public TapAuthOptions? Auth { get; init; }

    // Optional per-inspector tunnel context — used to surface tunnel info via /api/tunnel/details.
    public string? TunnelMode { get; init; }
    public string? TunnelName { get; init; }
    public string? TunnelResourceName { get; init; }
    public string? TunnelPublicUrl { get; init; }
    public string? TunnelAccountId { get; init; }
    public string? TunnelTunnelId { get; init; }
    public string? TunnelApiToken { get; init; }

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
            Ingress = ingress,
            Mode = config["Inspector:Mode"] ?? "standalone",
            Auth = auth.AnyConfigured ? auth : null,
            TunnelMode = config["Inspector:Tunnel:Mode"],
            TunnelName = config["Inspector:Tunnel:Name"],
            TunnelResourceName = config["Inspector:Tunnel:ResourceName"],
            TunnelPublicUrl = config["Inspector:Tunnel:PublicUrl"],
            TunnelAccountId = config["Inspector:Tunnel:AccountId"],
            TunnelTunnelId = config["Inspector:Tunnel:TunnelId"],
            TunnelApiToken = config["Inspector:Tunnel:ApiToken"],
        };
    }

    public static WebApplication Build(string[] args, TapInspectorOptions options)
    {
        if (options.ProxyPort <= 0 || options.UiPort <= 0)
        {
            throw new InvalidOperationException("Inspector ProxyPort and UiPort must be > 0.");
        }

        // Point WebRoot at Tap.Server's wwwroot (bundled UI) regardless of who's hosting us.
        var serverDir = Path.GetDirectoryName(typeof(TapInspectorHost).Assembly.Location)!;
        var wwwroot = Path.Combine(serverDir, "wwwroot");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Directory.Exists(wwwroot) ? wwwroot : null,
        });
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.ProxyPort}", $"http://0.0.0.0:{options.UiPort}");

        if (options.Quiet)
        {
            // Suppress per-request and lifetime chatter — the CLI renders its own log.
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("System", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
            builder.Logging.AddFilter("Yarp", LogLevel.Warning);
        }

        var (routes, clusters) = BuildYarpConfig(options.Ingress);

        builder.Services.AddSingleton<InMemoryRequestStore>();
        builder.Services.AddSingleton<IRequestStore>(sp => sp.GetRequiredService<InMemoryRequestStore>());

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

        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        if (options.Auth is not null)
        {
            builder.Services.AddTapAuth(options.Auth);
        }

        var app = builder.Build();

        if (!options.Quiet)
        {
            app.Logger.LogInformation(
                "HTTP Inspector starting. Proxy: {Proxy}, UI: {Ui}. {Ingress} ingress entr{Suffix}.{Auth}",
                options.ProxyPort, options.UiPort, options.Ingress.Length,
                options.Ingress.Length == 1 ? "y" : "ies",
                options.Auth is not null ? " Auth: enforced." : "");
        }

        // Proxy branch: tunnel/captured traffic, with optional auth in front.
        app.MapWhen(ctx => ctx.Connection.LocalPort == options.ProxyPort, proxy =>
        {
            if (options.Auth is not null)
            {
                proxy.UseTapAuth(options.Auth);
            }
            proxy.UseMiddleware<CaptureMiddleware>();
            proxy.UseRouting();
            proxy.UseEndpoints(ep => ep.MapReverseProxy());
        });

        // UI branch — never gated by auth (it's the local control plane).
        app.MapWhen(ctx => ctx.Connection.LocalPort == options.UiPort, ui =>
        {
            ui.UseCors();
            ui.UseDefaultFiles();
            ui.UseStaticFiles();
            ui.UseRouting();
            ui.UseEndpoints(ep => MapUiEndpoints(ep, options, cloudflareOptions));
        });

        return app;
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
            new InspectorConfig(proxyPort, ingress, cloudflareOptions.IsConfigured ? "cloudflare-api" : "token", options.Mode),
            InspectorConfigJsonContext.Default.InspectorConfig));

        ep.MapGet("/api/tunnel/details", async (CloudflareClient cf, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(options.TunnelMode))
            {
                return Results.NotFound(new { error = "No tunnel attached to this inspector." });
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

        ep.MapGet("/api/tunnel/ingress", async (CloudflareClient cf, CancellationToken ct) =>
        {
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
                }
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        ep.MapPost("/api/requests/{id:guid}/replay", async (
            Guid id, IRequestStore store, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var record = store.GetAll().FirstOrDefault(r => r.Id == id);
            if (record is null) return Results.NotFound();

            using var req = new HttpRequestMessage(new HttpMethod(record.Method),
                $"http://localhost:{proxyPort}{record.Path}");
            req.Headers.Host = record.Host;

            if (!string.IsNullOrEmpty(record.RequestBody))
            {
                var mediaType = record.RequestContentType?.Split(';')[0].Trim() ?? "application/octet-stream";
                req.Content = new StringContent(record.RequestBody, System.Text.Encoding.UTF8, mediaType);
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
                return Results.Json(new { replayed = true, status = (int)resp.StatusCode });
            }
            catch (Exception ex)
            {
                return Results.Json(new { replayed = false, error = ex.Message }, statusCode: 500);
            }
        });

        ep.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        ProfileEndpoints.Map(ep);

        ep.MapFallbackToFile("index.html");
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
