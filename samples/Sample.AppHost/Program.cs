// Sample AppHost for Tap. One Sample.Api upstream, one inspector per scenario,
// each tunnel nested as a child of its inspector in the Aspire dashboard.
//
//   api  ──────────────────────────────────  Sample.Api (single upstream)
//   inspector-standalone     5298 / 5299
//   inspector-token          5300 / 5301
//     └── cf-token                          (cloudflared --token)
//   inspector-managed        5302 / 5303
//     └── cf-api                            (cloudflared, API-managed tunnel)
//   inspector-dynamic        5304 / 5305
//     └── cf-dyn                            (cloudflared, dynamic hostname)
//
// Configure via user-secrets (project-scoped):
//   dotnet user-secrets set Cloudflare:TunnelToken "<token>"        --project samples/Sample.AppHost
//   dotnet user-secrets set Cloudflare:ApiToken    "<api-token>"    --project samples/Sample.AppHost
//   dotnet user-secrets set Cloudflare:AccountId   "<account-id>"   --project samples/Sample.AppHost

var builder = DistributedApplication.CreateBuilder(args);

var zone = builder.Configuration["Cloudflare:Zone"] ?? "p7e.dev";

// One upstream API, shared by every scenario.
var api = builder.AddProject<Projects.Sample_Api>("api");

// 1) Standalone inspector — direct: client -> inspector:5299 -> api.
var inspectorStandalone = builder.AddHttpInspector<Projects.Tap_Server>(
    name: "inspector-standalone", proxyPort: 5299, uiPort: 5298);
api.WithInspector(inspectorStandalone);

// 2) TryCloudflare quick tunnel — no Cloudflare account needed.
//    cloudflared assigns a random *.trycloudflare.com URL on startup; the URL is parsed
//    from cloudflared's logs and surfaced as a clickable link on the cf-quick resource.
{
    var inspector = builder.AddHttpInspector<Projects.Tap_Server>(
        name: "inspector-quick", proxyPort: 5307, uiPort: 5306);

    var t = builder.AddCloudflaredTunnel("cf-quick")
        .WithQuickTunnel("http://localhost:5307")
        .WithHttpInspector(inspector)
        .WithParentRelationship(inspector.Resource);

    api.WithCloudflareTunnel(t); // hostname is null — TryCloudflare assigns one at runtime
}

// 3) Token tunnel: cloudflared with --token, inspector in front.
var tunnelToken = builder.Configuration["Cloudflare:TunnelToken"];
if (!string.IsNullOrWhiteSpace(tunnelToken))
{
    var inspector = builder.AddHttpInspector<Projects.Tap_Server>(
        name: "inspector-token", proxyPort: 5301, uiPort: 5300);

    var t = builder.AddCloudflaredTunnel("cf-token")
        .WithToken(tunnelToken)
        .WithHttpInspector(inspector)
        .WithParentRelationship(inspector.Resource);

    api.WithCloudflareTunnel(t, builder.Configuration["Cloudflare:Hostnames:Token"] ?? $"token-tap.{zone}");
}

// 3) API-managed tunnel + 4) dynamic hostname tunnel.
var apiToken = builder.Configuration["Cloudflare:ApiToken"];
var accountId = builder.Configuration["Cloudflare:AccountId"];
if (!string.IsNullOrWhiteSpace(apiToken) && string.IsNullOrWhiteSpace(accountId))
{
    Console.Error.WriteLine(
        "[Sample.AppHost] Cloudflare:ApiToken is set but Cloudflare:AccountId is missing — "
        + "skipping api-managed and dynamic scenarios. "
        + "Run: dotnet user-secrets set Cloudflare:AccountId <id> --project samples/Sample.AppHost");
}
if (!string.IsNullOrWhiteSpace(apiToken) && !string.IsNullOrWhiteSpace(accountId))
{
    var inspectorManaged = builder.AddHttpInspector<Projects.Tap_Server>(
        name: "inspector-managed", proxyPort: 5303, uiPort: 5302);

    var t = builder.AddCloudflaredTunnel("cf-api")
        .WithApiManagedTunnel(apiToken, accountId, tunnelName: "tap-cf-api")
        .WithHttpInspector(inspectorManaged)
        .WithParentRelationship(inspectorManaged.Resource);

    api.WithCloudflareTunnel(t, builder.Configuration["Cloudflare:Hostnames:Managed"] ?? $"managed-tap.{zone}");

    var inspectorDynamic = builder.AddHttpInspector<Projects.Tap_Server>(
        name: "inspector-dynamic", proxyPort: 5305, uiPort: 5304);

    var d = builder.AddCloudflaredTunnel("cf-dyn")
        .WithApiManagedTunnel(apiToken, accountId, tunnelName: "tap-cf-dyn")
        .WithDynamicHostname(zone, prefix: "api-", suffix: "-tap")
        .WithHttpInspector(inspectorDynamic)
        .WithParentRelationship(inspectorDynamic.Resource);

    api.WithCloudflareTunnel(d); // hostname allocated at startup
}

builder.Build().Run();
