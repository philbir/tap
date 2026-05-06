// Sample AppHost for Tap. One Sample.Api upstream, one tap per scenario,
// each tunnel nested as a child of its tap in the Aspire dashboard.
// Sample.Client (Vite) runs as a separate resource and probes each tap proxy
// from the browser to exercise endpoints + SSE through the inspector.
//
//   api  ──────────────────────────────────  Sample.Api (single upstream)
//   client                                   Sample.Client (Vite, port 5210)
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

using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

var zone = builder.Configuration["Cloudflare:Zone"] ?? "p7e.dev";

// One upstream API, shared by every scenario.
var api = builder.AddProject<Projects.Sample_Api>("api");

// Collect tap entries to pass to the Sample.Client. Local-only proxies use the
// localhost URL directly (no host filter on the YARP route); tunnel-routed taps
// need the public URL since YARP filters by Host header.
var taps = new List<TapDescriptor>();

// 1) Standalone tap — direct: client -> tap:5299 -> api.
var tapStandalone = builder.AddTap<Projects.Tap_Server>(
    name: "tap-standalone", proxyPort: 5299, uiPort: 5298);
api.WithTap(tapStandalone);
taps.Add(new TapDescriptor("tap-standalone", "standalone", "http://localhost:5299"));

// 2) TryCloudflare quick tunnel — no Cloudflare account needed.
//    cloudflared assigns a random *.trycloudflare.com URL on startup; the URL is parsed
//    from cloudflared's logs and surfaced as a clickable link on the tunnel resource.
{
    var tap = builder.AddTap<Projects.Tap_Server>(
            name: "tap-quick", proxyPort: 5307, uiPort: 5306)
        .WithQuickTunnel();

    api.WithTap(tap); // hostname is null — TryCloudflare assigns one at runtime
    taps.Add(new TapDescriptor("tap-quick", "quick", "http://localhost:5307"));
}

// 3) Existing dashboard-managed tunnel: cloudflared with --token, tap in front.
var tunnelToken = builder.Configuration["Cloudflare:TunnelToken"];
if (!string.IsNullOrWhiteSpace(tunnelToken))
{
    var tap = builder.AddTap<Projects.Tap_Server>(
            name: "tap-existing", proxyPort: 5301, uiPort: 5300)
        .WithTunnel("tap-existing-tunnel", t => t.WithExistingTunnel(tunnelToken));

    var existingHost = builder.Configuration["Cloudflare:Hostnames:Token"] ?? $"existing-tap.{zone}";
    api.WithTap(tap, existingHost);
    taps.Add(new TapDescriptor("tap-existing", "existing", $"https://{existingHost}"));
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

    var managedHost = builder.Configuration["Cloudflare:Hostnames:Managed"] ?? $"managed-tap.{zone}";
    api.WithTap(tapManaged, managedHost);
    taps.Add(new TapDescriptor("tap-managed", "api-managed", $"https://{managedHost}"));

    var tapDynamic = builder.AddTap<Projects.Tap_Server>(
            name: "tap-dynamic", proxyPort: 5305, uiPort: 5304)
        .WithTunnel("tap-dynamic-tunnel", t => t
            .WithApiManagedTunnel(apiToken, accountId, tunnelName: "tap-cf-dyn")
            .WithDynamicHostname(zone, prefix: "api-", suffix: "-tap"));

    api.WithTap(tapDynamic); // hostname allocated at startup
    // The dynamic hostname is minted at runtime; emit a placeholder URL so the
    // selector at least shows the tap. Browser users will need the real public
    // URL (visible in the inspector resource) to exercise tunnel routing.
    taps.Add(new TapDescriptor("tap-dynamic", "dynamic", "https://<minted at startup>"));
}

// Sample.Client — separate Vite resource. Browser-facing UI; not behind a tap.
var clientDir = Path.Combine(builder.AppHostDirectory, "..", "Sample.Client");
var tapsJson = JsonSerializer.Serialize(taps, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
});

builder.AddViteApp("client", clientDir, "dev")
    .WithYarn()
    .WithEnvironment("VITE_TAPS", tapsJson);

builder.Build().Run();

internal sealed record TapDescriptor(string Name, string Mode, string Url);
