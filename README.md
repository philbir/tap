<div align="center">
  <p>
    <img src="assets/tap-logo.svg" alt="Tap" width="150">
  </p>

  <picture>
    <source srcset="assets/tap-hero-dark.png" media="(prefers-color-scheme: dark)">
    <img src="assets/tap-hero.png" alt="Tap tunnel and HTTP inspector illustration" width="620">
  </picture>

  <h1>Tap</h1>
  <p><strong>Easy tunneling with an HTTP inspector built in.</strong> Test mobile app hooks, webhook deliveries, auth callbacks, partner integrations, and temporary demos from your local machine without changing the app you are building.</p>

  <p>
    <a href="https://philbir.github.io/tap/"><strong>Landing page and docs</strong></a>
  </p>

  <p>
    <a href="https://philbir.github.io/tap/"><img alt="Docs" src="https://img.shields.io/badge/docs-GitHub%20Pages-14945f"></a>
    <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet">
    <img alt="Aspire" src="https://img.shields.io/badge/Aspire-ready-7b2ff7">
    <img alt="Cloudflare Tunnel" src="https://img.shields.io/badge/Cloudflare-Tunnel-f38020?logo=cloudflare">
    <img alt="UI" src="https://img.shields.io/badge/UI-React%2019-14945f?logo=react">
  </p>
</div>

---

Tap is for the local-development moment when you need a real public URL and a clear view of what hit it. Mobile app development hooks, webhook deliveries, third-party OAuth redirects, auth provider callbacks, partner integrations, and "can you hit my laptop for a minute?" demos all need the same thing: a tunnel that is quick to bring up and a request log that tells you what actually happened.

Tap gives you both. Run it directly from the `tap` CLI when you want an ad hoc tunnel for one upstream, or add it to a .NET Aspire AppHost when tunnel wiring should live beside the rest of your distributed app.

Quick tunnels are free with TryCloudflare and do not need a Cloudflare account. If you want stable hostnames, use a free Cloudflare account with a domain you control; a `.dev` domain is a nice fit for developer projects and is usually inexpensive depending on registrar. Tap itself is meant to feel like tap water: free, useful, and available whenever you need another glass.

> [!IMPORTANT]
> Tap makes local services reachable through public URLs. Treat exposed endpoints as internet-facing. Prefer short-lived TryCloudflare tunnels for quick demos, use Cloudflare Access or Tap's inspector auth options for sensitive services, and never tunnel a privileged local admin endpoint without an explicit access boundary.

## Run Modes

| | When to use |
|---|---|
| **CLI** | You want to point Tap at an upstream URL now: `tap run http://localhost:3000 --quick`. |
| **Aspire** | You want tunnels and inspectors modeled in your AppHost with generated resource URLs. |
| **Standalone inspector** | You want a local capture proxy without Cloudflare. |
| **Quick tunnel** | You need a throwaway `*.trycloudflare.com` URL with no Cloudflare account or DNS setup. |
| **Token tunnel** | You already manage a tunnel in the Cloudflare dashboard and want Tap to run `cloudflared --token`. |
| **API-managed tunnel** | You want the AppHost to look up or create a named tunnel, write local credentials, and manage DNS. |
| **Dynamic hostname** | You want fresh per-run hostnames such as `api-1a2b3c4d-tap.example.com` for demos or parallel dev loops. |

## Use Cases

| Use case | Why Tap helps |
|---|---|
| **Mobile app callbacks** | Point native or emulator builds at a public URL while still serving from localhost. |
| **Webhook development** | See the raw headers, body, status code, and replay path for every provider delivery. |
| **Auth callbacks** | Test OAuth/OIDC redirect URIs against a real HTTPS hostname. |
| **Partner demos** | Share a temporary URL to work running on your machine, then tear it down. |
| **Aspire demos** | Put the same tunnel and inspector wiring in the AppHost so the whole team gets it. |

## Quick Start

### CLI

```bash
tap run http://localhost:3000
```

That starts the inspector with a local proxy on <http://localhost:4444> and the UI on <http://localhost:4445>.

Add a quick TryCloudflare tunnel:

```bash
tap run http://localhost:3000 --quick
```

Use an existing dashboard-managed tunnel token:

```bash
tap run http://localhost:3000 \
  --token "$CLOUDFLARE_TUNNEL_TOKEN" \
  --hostname api-local.example.com
```

Use Cloudflare API-managed DNS and a fresh dynamic hostname:

```bash
tap run http://localhost:3000 \
  --api-token "$CLOUDFLARE_API_TOKEN" \
  --account "$CLOUDFLARE_ACCOUNT_ID" \
  --api-managed tap-cli \
  --dynamic example.com
```

If `cloudflared` is not installed, run:

```bash
tap install-cloudflared
```

CLI options can also come from environment variables and an optional `tap.config` file. Command-line flags win, then environment variables, then config file defaults.

```json
{
  "upstream": "http://localhost:3000"
}
```

### Aspire: standalone inspection

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var inspector = builder.AddHttpInspector<Projects.Tap_Server>();
api.WithInspector(inspector);

builder.Build().Run();
```

Open <http://localhost:5198> for the inspector UI. Send traffic through <http://localhost:5199> and Tap records the request, response, headers, status, timing, and supported bodies before forwarding to the upstream service.

### Aspire: quick public tunnel

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var inspector = builder.AddHttpInspector<Projects.Tap_Server>(
    name: "inspector-quick",
    proxyPort: 5307,
    uiPort: 5306);

var tunnel = builder.AddCloudflaredTunnel("cf-quick")
    .WithQuickTunnel("http://localhost:5307")
    .WithHttpInspector(inspector)
    .WithParentRelationship(inspector.Project);

api.WithCloudflareTunnel(tunnel);

builder.Build().Run();
```

`cloudflared` assigns a random TryCloudflare URL at startup. Tap watches the tunnel logs, surfaces the public URL in Aspire, and routes Cloudflare traffic through the inspector before it reaches `api`.

### Aspire: Cloudflare token tunnel

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tunnel = builder.AddCloudflaredTunnel("cf-token")
    .WithToken(builder.Configuration["Cloudflare:TunnelToken"])
    .WithHttpInspector<Projects.Tap_Server>();

api.WithCloudflareTunnel(tunnel, "api-local.example.com");

builder.Build().Run();
```

Configure the token with user-secrets:

```bash
dotnet user-secrets set Cloudflare:TunnelToken "<token>" \
  --project samples/Sample.AppHost
```

Token mode expects an existing Cloudflare Tunnel. Create the tunnel in the Cloudflare dashboard first, copy its connector token, and pass that token to Tap. Tap will run `cloudflared tunnel run --token ...`; it will not create or reconfigure that dashboard-managed tunnel.

### Aspire: API-managed tunnel and DNS

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tunnel = builder.AddCloudflaredTunnel("cf-api")
    .WithApiManagedTunnel(
        builder.Configuration["Cloudflare:ApiToken"]!,
        builder.Configuration["Cloudflare:AccountId"]!,
        tunnelName: "tap-dev")
    .WithDynamicHostname("example.com", prefix: "api-", suffix: "-tap")
    .WithHttpInspector<Projects.Tap_Server>();

api.WithCloudflareTunnel(tunnel);

builder.Build().Run();
```

The lifecycle hook runs before `cloudflared` starts. It looks up or creates the named tunnel, writes a temporary credentials file, resolves the Cloudflare zone, mints hostnames when needed, ensures CNAME records, and then starts `cloudflared` with a local ingress config.

## CLI Reference

| Option | Purpose |
|---|---|
| `<upstream>` | Target URL to inspect, for example `http://localhost:3000`. |
| `--proxy-port` | Captured traffic port. Default `4444`. |
| `--ui-port` | Inspector UI/API port. Default `4445`. |
| `--quick` | Start a TryCloudflare quick tunnel. |
| `--token` | Connector token for an existing Cloudflare Tunnel. |
| `--hostname` | Public hostname for token or API-managed mode. |
| `--api-token` | Cloudflare API token for managed tunnel/DNS operations. |
| `--account` | Cloudflare account id. |
| `--api-managed` | Named tunnel to create or reuse. |
| `--dynamic` | Zone where Tap should mint a fresh hostname. |
| `--docker` | Run `cloudflared` through Docker host networking. |
| `--auto-install` | Install `cloudflared` if missing. |
| `--config` | Load defaults from a JSON `tap.config` file. |

Useful environment variables:

| Variable | Purpose |
|---|---|
| `TAP_UPSTREAM` | Upstream URL when omitted from the command line. |
| `CLOUDFLARE_TUNNEL_TOKEN` | Token tunnel connector token. |
| `CLOUDFLARE_API_TOKEN` | API-managed tunnel token. |
| `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account id. |

## Cloudflare Setup

For token mode:

1. In Cloudflare Zero Trust, create a Cloudflare Tunnel.
2. Copy the `cloudflared tunnel run --token ...` connector command.
3. Use only the token value with `tap run --token` or `WithToken(...)`.
4. Route the hostname you pass to Tap to that tunnel in Cloudflare.

For API-managed mode:

1. Create a Cloudflare API token with account-level Cloudflare Tunnel edit permission.
2. Add DNS edit permission for the zone Tap will manage.
3. Provide `Cloudflare:ApiToken` and `Cloudflare:AccountId` through user-secrets, environment variables, or normal .NET configuration.
4. Use `WithApiManagedTunnel(...)`; add `WithDynamicHostname(...)` when Tap should mint hostnames and DNS CNAMEs.

Cloudflare references: [tunnel tokens](https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/) and [API token permissions](https://developers.cloudflare.com/fundamentals/api/reference/permissions/).

## Authentication

Tap auth gates the proxy branch before traffic reaches the upstream. The inspector UI port stays local and is not gated by these checks.

CLI static checks:

```bash
tap run http://localhost:3000 --quick \
  --auth-header "X-Tap-Key=$TAP_KEY" \
  --auth-cidr "203.0.113.0/24" \
  --auth-country "CH"
```

CLI OIDC:

```bash
tap run http://localhost:3000 --quick \
  --auth-oidc-authority "https://issuer.example.com" \
  --auth-oidc-client-id "$OIDC_CLIENT_ID" \
  --auth-oidc-client-secret "$OIDC_CLIENT_SECRET"
```

Aspire auth:

```csharp
var inspector = builder.AddHttpInspector<Projects.Tap_Server>()
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!)
    .WithIpAllowList("203.0.113.0/24")
    .WithCountryAllowList("CH")
    .WithOidcAuth(
        authority: builder.Configuration["Auth:Authority"]!,
        clientId: builder.Configuration["Auth:ClientId"]!,
        clientSecret: builder.Configuration["Auth:ClientSecret"]);

api.WithInspector(inspector);
```

Enabled checks are combined. If header auth, CIDR allowlist, country allowlist, and OIDC are all configured, every request must satisfy every configured check.

## Packages

| Package | Purpose |
|---|---|
| `Tap.Hosting` | Aspire AppHost extensions: `AddCloudflaredTunnel`, `AddHttpInspector`, `WithCloudflareTunnel`, `WithInspector`, `WithHttpInspector`, `WithToken`, `WithApiManagedTunnel`, `WithDynamicHostname`. |
| `Tap.Server` | ASP.NET Core capture server: YARP reverse proxy, capture middleware, REST API, SSE stream, and bundled React UI. |
| `Tap.Cli` | Local command host that reuses the same inspector server code. |

Both entry points use the same `Tap.Server` host internally. The CLI builds `TapInspectorOptions` from command-line flags, environment variables, and optional `tap.config`; Aspire writes the same options through project environment variables.

Consumer AppHost projects must reference both `Tap.Hosting` and `Tap.Server`. `Tap.Server` supplies the generated `Projects.Tap_Server` metadata type used by `AddHttpInspector<TInspectorServer>()`; `Tap.Hosting` should be referenced with `IsAspireProjectResource="false"` because it is a library, not a launchable resource.

```xml
<ProjectReference Include="..\..\src\Tap.Hosting\Tap.Hosting.csproj"
                  IsAspireProjectResource="false" />
<ProjectReference Include="..\..\src\Tap.Server\Tap.Server.csproj" />
```

## Configuration

### AppHost Cloudflare settings

| Key | Purpose |
|---|---|
| `Cloudflare:TunnelToken` | Connector token for dashboard-managed token tunnels. |
| `Cloudflare:ApiToken` | API token for API-managed tunnels, DNS, and tunnel details. |
| `Cloudflare:AccountId` | Cloudflare account id used with `Cloudflare:ApiToken`. |
| `Cloudflare:Zone` | Default zone used by the sample AppHost. |
| `Cloudflare:Hostnames:*` | Optional sample hostnames for token and managed scenarios. |

For API-managed DNS, the Cloudflare token needs tunnel edit permission on the account and DNS edit permission on the relevant zone.

### Inspector server settings

Tap.Hosting writes these for you when running under Aspire. The CLI maps its flags to the same server options.

| Variable | Purpose |
|---|---|
| `Inspector__ProxyPort` | Port that receives proxied app traffic. Default `5199`. |
| `Inspector__UiPort` | Port for the local inspector UI and API. Default `5198`. |
| `Inspector__Mode` | `standalone` or `tunnel`. |
| `Inspector__Ingress` | JSON array of `{ hostname, upstream, tunnelMode, tunnelName, publicUrl }`. |
| `Inspector__Tunnel__*` | Optional tunnel context surfaced by `/api/tunnel/details`. |
| `Inspector__Auth__*` | Optional proxy-side auth gate: header, CIDR, country, and OIDC settings. |

## Development

```bash
dotnet restore Tap.slnx
dotnet build Tap.slnx
dotnet run --project samples/Sample.AppHost
```

UI source lives in `ui/` and is built into `src/Tap.Server/wwwroot/` during server builds:

```bash
cd ui
yarn
yarn dev
yarn build
```

Use `-p:SkipTapUiBuild=true` when iterating on C# only.

### Docs site

```bash
cd docs-site
yarn
yarn build
yarn preview
```

The docs site is a static Vite app configured with `base: "./"` so the built `dist/` directory can be deployed under GitHub Pages project paths.

## Architecture

At runtime Tap splits traffic across two ports:

```text
Internet -> Cloudflare -> cloudflared -> Tap proxy port -> upstream app
                                      \-> Tap UI port -> inspector UI/API
```

The proxy branch captures request and response data, stores the latest records in a bounded in-memory ring, and publishes new records over server-sent events. The UI branch serves the React inspector, exposes REST endpoints for request history, replay, ingress, and tunnel details, and can use Cloudflare API credentials to show or update tunnel ingress rules.

For the deeper technical background, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Layout

```text
assets/              README logo and hero assets
docs/                Technical documentation
src/Tap.Core/        Shared auth and Cloudflare/cloudflared primitives
src/Tap.Hosting/     Aspire integration and lifecycle hook
src/Tap.Server/      Capture server, YARP proxy, SSE API, bundled UI host
src/Tap.Cli/         CLI host for the inspector server
ui/                  Vite + React inspector source
samples/             Sample AppHost and upstream API
```

## License

TBD.
