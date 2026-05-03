using System.Text.Json;
using Tap.Server;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

var proxyPort = builder.Configuration.GetValue<int>("Inspector:ProxyPort");
var uiPort = builder.Configuration.GetValue<int>("Inspector:UiPort");
var ingressJson = builder.Configuration["Inspector:Ingress"];
var mode = builder.Configuration["Inspector:Mode"] ?? "standalone";

if (proxyPort <= 0 || uiPort <= 0)
{
    throw new InvalidOperationException(
        "Inspector:ProxyPort and Inspector:UiPort must be set (via AppHost WithHttpInspector()).");
}

var ingress = string.IsNullOrWhiteSpace(ingressJson)
    ? Array.Empty<InspectorIngressEntry>()
    : JsonSerializer.Deserialize(ingressJson, InspectorIngressJsonContext.Default.InspectorIngressEntryArray) ?? [];

var (routes, clusters) = BuildYarpConfig(ingress);

builder.Services.AddSingleton<InMemoryRequestStore>();
builder.Services.AddSingleton<IRequestStore>(sp => sp.GetRequiredService<InMemoryRequestStore>());

builder.Services.AddHttpClient("replay").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
});

var cloudflareOptions = new CloudflareOptions
{
    ApiToken = builder.Configuration["Cloudflare:ApiToken"],
    AccountId = builder.Configuration["Cloudflare:AccountId"],
    TunnelId = builder.Configuration["Cloudflare:TunnelId"],
};
builder.Services.AddSingleton(cloudflareOptions);
builder.Services.AddHttpClient<CloudflareClient>();

builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.Logger.LogInformation(
    "HTTP Inspector starting. Proxy port: {Proxy}, UI port: {Ui}. {Ingress} ingress entr{Suffix}.",
    proxyPort, uiPort, ingress.Length, ingress.Length == 1 ? "y" : "ies");

// Proxy branch: only the tunnel traffic, captured via middleware then forwarded by YARP.
app.MapWhen(ctx => ctx.Connection.LocalPort == proxyPort, proxy =>
{
    proxy.UseMiddleware<CaptureMiddleware>();
    proxy.UseRouting();
    proxy.UseEndpoints(ep => ep.MapReverseProxy());
});

// UI branch: JSON API + SSE stream + static UI (from wwwroot). The Vite dev server can
// also be run against this API for hot-reload during development.
app.MapWhen(ctx => ctx.Connection.LocalPort == uiPort, ui =>
{
    ui.UseCors();
    ui.UseDefaultFiles();
    ui.UseStaticFiles();
    ui.UseRouting();
    ui.UseEndpoints(ep =>
    {
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
            new InspectorConfig(proxyPort, ingress, cloudflareOptions.IsConfigured ? "cloudflare-api" : "token", mode),
            InspectorConfigJsonContext.Default.InspectorConfig));

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
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        ep.MapPost("/api/tunnel/ingress", async (
            UpsertHostnameRequest body,
            CloudflareClient cf,
            CancellationToken ct) =>
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
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        ep.MapGet("/api/stream", async (HttpContext ctx, IRequestStore store, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // Initial comment + flush so EventSource fires `onopen` immediately and any
            // proxy (Vite, Cloudflare, etc.) commits the response headers downstream.
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            await foreach (var record in store.Stream(ct))
            {
                var json = JsonSerializer.Serialize(record, RequestRecordJsonContext.Default.RequestRecord);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        ep.MapPost("/api/requests/{id:guid}/replay", async (
            Guid id,
            IRequestStore store,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            var record = store.GetAll().FirstOrDefault(r => r.Id == id);
            if (record is null)
            {
                return Results.NotFound();
            }

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

        ep.MapFallbackToFile("index.html");
    });
});

app.Run();

static (RouteConfig[] routes, ClusterConfig[] clusters) BuildYarpConfig(IReadOnlyList<InspectorIngressEntry> ingress)
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
            // Match by Host when a hostname is provided; otherwise any Host lands here
            // (standalone mode with a single upstream is the common case).
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
