// Sample AppHost for Tap. One Sample.Api upstream, one tap per scenario,
// each tunnel nested as a child of its tap in the Aspire dashboard.
//
//   api  ──────────────────────────────────  Sample.Api (single upstream)
//   tap-standalone           5298 / 5299
//   tap-quick                5306 / 5307
//     └── tap-quick-tunnel                   (cloudflared, TryCloudflare quick tunnel)
//   tap-existing             5300 / 5301
//     └── tap-existing-tunnel                (cloudflared --token)
//   tap-managed              5302 / 5303
//     └── tap-managed-tunnel                 (cloudflared, API-managed tunnel)
//   tap-dynamic              5304 / 5305
//     └── tap-dynamic-tunnel                 (cloudflared, dynamic hostname)
//
// Configure via user-secrets (project-scoped):
//   dotnet user-secrets set Cloudflare:TunnelToken "<token>"        --project samples/Sample.AppHost
//   dotnet user-secrets set Cloudflare:ApiToken    "<api-token>"    --project samples/Sample.AppHost
//   dotnet user-secrets set Cloudflare:AccountId   "<account-id>"   --project samples/Sample.AppHost

var builder = DistributedApplication.CreateBuilder(args);

var zone = builder.Configuration["Cloudflare:Zone"] ?? "p7e.dev";

// One upstream API, shared by every scenario.
var api = builder.AddProject<Projects.Sample_Api>("api");

// 1) Standalone tap — direct: client -> tap:5299 -> api.
var tapStandalone = builder.AddTap<Projects.Tap_Server>(
    name: "tap-standalone", proxyPort: 5299, uiPort: 5298);
api.WithTap(tapStandalone);

// 2) TryCloudflare quick tunnel — no Cloudflare account needed.
//    cloudflared assigns a random *.trycloudflare.com URL on startup; the URL is parsed
//    from cloudflared's logs and surfaced as a clickable link on the tunnel resource.
{
    var tap = builder.AddTap<Projects.Tap_Server>(
            name: "tap-quick", proxyPort: 5307, uiPort: 5306)
        .WithQuickTunnel();

    api.WithTap(tap); // hostname is null — TryCloudflare assigns one at runtime
}

// 3) Existing dashboard-managed tunnel: cloudflared with --token, tap in front.
var tunnelToken = builder.Configuration["Cloudflare:TunnelToken"];
if (!string.IsNullOrWhiteSpace(tunnelToken))
{
    var tap = builder.AddTap<Projects.Tap_Server>(
            name: "tap-existing", proxyPort: 5301, uiPort: 5300)
        .WithTunnel("tap-existing-tunnel", t => t.WithExistingTunnel(tunnelToken));

    api.WithTap(tap, builder.Configuration["Cloudflare:Hostnames:Token"] ?? $"existing-tap.{zone}");
}

// 4) API-managed tunnel + 5) dynamic hostname tunnel.
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
    var tapManaged = builder.AddTap<Projects.Tap_Server>(
            name: "tap-managed", proxyPort: 5303, uiPort: 5302)
        .WithTunnel("tap-managed-tunnel", t => t
            .WithApiManagedTunnel(apiToken, accountId, tunnelName: "tap-cf-api"));

    api.WithTap(tapManaged, builder.Configuration["Cloudflare:Hostnames:Managed"] ?? $"managed-tap.{zone}");

    var tapDynamic = builder.AddTap<Projects.Tap_Server>(
            name: "tap-dynamic", proxyPort: 5305, uiPort: 5304)
        .WithTunnel("tap-dynamic-tunnel", t => t
            .WithApiManagedTunnel(apiToken, accountId, tunnelName: "tap-cf-dyn")
            .WithDynamicHostname(zone, prefix: "api-", suffix: "-tap"));

    api.WithTap(tapDynamic); // hostname allocated at startup
}

builder.Build().Run();
