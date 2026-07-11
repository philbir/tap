<div align="center">
  <p>
    <img src="assets/tap-logo.svg" alt="Tap" width="150">
  </p>

  <picture>
    <source srcset="assets/tap-hero-dark.png" media="(prefers-color-scheme: dark)">
    <img src="assets/tap-hero.png" alt="Tap tunnel and HTTP inspector illustration" width="620">
  </picture>

  <p><strong>Easy tunneling with an HTTP inspector built in.</strong> Test mobile app hooks, webhook deliveries, auth callbacks, partner integrations, and temporary demos from your local machine without changing the app you are building.</p>

  <p>
    <a href="https://philbir.github.io/tap/"><strong>Landing page and docs</strong></a>
  </p>

  <p>
    <a href="https://philbir.github.io/tap/"><img alt="Docs" src="https://img.shields.io/badge/docs-GitHub%20Pages-14945f"></a>
    <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet">
    <img alt="Aspire" src="https://img.shields.io/badge/Aspire-ready-7b2ff7">
    <img alt="Cloudflare Tunnel" src="https://img.shields.io/badge/Cloudflare-Tunnel-f38020?logo=cloudflare">
    <img alt="Tailscale Funnel" src="https://img.shields.io/badge/Tailscale-Funnel-5e64f4?logo=tailscale">
    <img alt="UI" src="https://img.shields.io/badge/UI-React%2019-14945f?logo=react">
  </p>
</div>

---

Tap is for the local-development moment when you need a real public URL and a clear view of what hit it. Mobile app development hooks, webhook deliveries, third-party OAuth redirects, auth provider callbacks, partner integrations, and "can you hit my laptop for a minute?" demos all need the same thing: a tunnel that is quick to bring up and a request log that tells you what actually happened.

Tap gives you both. Run it directly from the `tap` CLI when you want an ad hoc tunnel for one upstream, or add it to a .NET Aspire AppHost when tunnel wiring should live beside the rest of your distributed app.

Quick tunnels are free with TryCloudflare and do not need a Cloudflare account. If you want stable hostnames, use a free Cloudflare account with a domain you control; a `.dev` domain is a nice fit for developer projects and is usually inexpensive depending on registrar. Tap itself is meant to feel like tap water: free, useful, and available whenever you need another glass.

> [!IMPORTANT]
> Tap makes local services reachable through public URLs. Treat exposed endpoints as internet-facing. Prefer short-lived TryCloudflare tunnels for quick demos, use Cloudflare Access or Tap's inspector auth options for sensitive services, and never tunnel a privileged local admin endpoint without an explicit access boundary.

> [!WARNING]
> **Public tunnels are scanned within minutes.** The moment a public hostname's TLS cert appears in a CT log (which happens immediately when you bring up Cloudflare Tunnel or Tailscale Funnel), opportunistic scanners hit it looking for admin endpoints, debug routes, and known-CVE banners. **Always pair public tunnels with auth or edge controls.** Tap's auth options (header / CIDR / country / OIDC) gate the proxy port before traffic reaches your upstream; for Cloudflare hostnames, Cloudflare Access and WAF rules are another good outer layer. Those attempts show up directly in the Inspector request log, often seconds after the tunnel is reachable. For Tailscale, prefer `WithTailscaleServe(...)` (tailnet-only) over `WithTailscaleFunnel(...)` (public) unless you actually need internet exposure.

## Run Modes

| | When to use |
|---|---|
| **CLI** | You want to point Tap at an upstream URL now: `tap run http://localhost:3000 --quick`. |
| **Aspire** | You want tunnels and inspectors modeled in your AppHost with generated resource URLs. |
| **Standalone inspector** | You want a local capture proxy without Cloudflare. |
| **Quick tunnel** | You need a throwaway `*.trycloudflare.com` URL with no Cloudflare account or DNS setup. |
| **Existing tunnel** | You already manage a tunnel in the Cloudflare dashboard and want Tap to run `cloudflared --token` against it. |
| **API-managed tunnel** | You want the AppHost to look up or create a named tunnel, write local credentials, and manage DNS. |
| **Dynamic hostname** | You want fresh per-run hostnames such as `api-1a2b3c4d-tap.example.com` for demos or parallel dev loops. |
| **Tailscale Serve (default)** | Tailnet-only: reachable from your other tailnet devices but not the public internet. The safe default for Tailscale. |
| **Tailscale Funnel (public, opt-in)** | Public URL via your tailnet node — pair with auth. |
| **Tailscale (ephemeral)** | AppHost / CLI: spin up a per-session userspace `tailscaled` from an auth key (Process or Docker). Node disappears when the run stops. |

## Use Cases

| Use case | Why Tap helps |
|---|---|
| **Mobile app callbacks** | Point native or emulator builds at a public URL while still serving from localhost. The inspector's **QR tab** (or `http://localhost:<uiPort>/#qr`) lets you scan the public URL straight onto your phone. |
| **Webhook development** | See the raw headers, body, status code, and replay path for every provider delivery. |
| **Auth callbacks** | Test OAuth/OIDC redirect URIs against a real HTTPS hostname. |
| **Streaming protocols** | Tap proxies and live-captures **Server-Sent Events** (`text/event-stream`) and **WebSockets** end-to-end. The inspector UI renders dedicated **SSE** and **WS** tabs with a live frame/event timeline (direction, payload, timestamps) — open while the connection is in flight and watch messages append in real time. |
| **Partner demos** | Share a temporary URL to work running on your machine, then tear it down. |
| **Aspire demos** | Put the same tunnel and inspector wiring in the AppHost so the whole team gets it. |

## Install

Pick whichever fits — all three install the same `tap` CLI.

### .NET global tool

Needs the .NET 10 SDK on PATH. Cross-platform.

```bash
dotnet tool install -g Tap
dotnet tool update    -g Tap
dotnet tool uninstall -g Tap
```

Make sure `~/.dotnet/tools` (Linux/macOS) or `%USERPROFILE%\.dotnet\tools` (Windows) is on your `PATH`.

### Self-contained binary (Linux/macOS)

No .NET install required. Downloads the latest release for your platform, verifies the SHA256 checksum, and writes a launcher to `~/.local/bin/tap`.

```bash
curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh
```

Pin a version with `TAP_VERSION=0.1.0 ...`, override paths with `TAP_INSTALL_DIR` / `TAP_BIN_DIR`. To uninstall: `rm -rf ~/.local/share/tap ~/.local/bin/tap`.

Archives are also available directly from the [GitHub Releases](https://github.com/philbir/tap/releases) page as `tap-<version>-<rid>.tar.gz`, with a `SHA256SUMS` file alongside.

### Windows one-liner

Wraps the .NET global-tool install — needs the .NET 10 SDK on PATH.

```powershell
irm https://raw.githubusercontent.com/philbir/tap/main/install.ps1 | iex
```

Pin a version with `$env:TAP_VERSION = "0.2.3"` before running. To uninstall: `dotnet tool uninstall -g Tap`.

### cloudflared

Cloudflare-tunnel features need [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/) on `PATH`. Install it once with `brew install cloudflared`, `winget install Cloudflare.cloudflared`, or run `tap install-cloudflared` after Tap is installed.

### Tailscale

Tailscale support can use `tailscale serve` for private tailnet-only access or `tailscale funnel` for public internet access. Host-process modes need the [`tailscale`](https://tailscale.com/download) CLI on `PATH`; Docker mode runs the official `tailscale/tailscale` image instead. One-time tailnet setup:

1. Install Tailscale (`brew install tailscale` on macOS, `tailscale.com/download/linux` on Linux, or the GUI installer on Windows) and sign in with `tailscale up` when using system mode.
2. In the [admin console](https://login.tailscale.com/admin/), enable **HTTPS Certificates** under DNS. This is required for both `serve` and `funnel`.
3. For public Funnel only, add a `nodeAttrs` rule to your tailnet ACL granting the `funnel` capability:

```json
{
  "nodeAttrs": [
    { "target": ["*"], "attr": ["funnel"] }
  ]
}
```

Verify with `tailscale status --json | grep -i funnel` — `"funnel"` should appear in your node's `CapMap`. Funnel only listens on ports `443`, `8443`, and `10000`. `tailscale serve` is the Tap default and stays private to your tailnet.

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

Use Tailscale (system tailscaled — requires the Tailscale CLI on PATH and a tailnet you're signed in on):

```bash
# Tailnet-only (safe default — reachable from your other tailnet devices, not the public internet):
tap run http://localhost:3000 --tailscale

# Public Funnel (URL is on the internet — pair with auth):
tap run http://localhost:3000 --tailscale --tailscale-public \
  --auth-header "X-Tap-Key=$TAP_KEY"
```

Or run a per-session userspace `tailscaled` with an auth key (no system Tailscale install needed beyond the CLI):

```bash
export TAILSCALE_AUTHKEY=tskey-...        # or pass --tailscale-authkey
tap run http://localhost:3000 --tailscale
```

The CLI spawns `tailscaled --tun=userspace-networking` under a temp state dir, runs `tailscale up --authkey ...`, configures `tailscale serve` (or `funnel` with `--tailscale-public`), and tears everything down (including the state dir) on Ctrl+C. macOS/Linux only — on Windows pair the auth key with `--docker` to use the `tailscale/tailscale` container.

Don't have a host `tailscaled` binary? Run the official `tailscale/tailscale` Docker image instead — works on any host with Docker, including macOS where the GUI Tailscale client doesn't expose `tailscaled`:

```bash
export TAILSCALE_AUTHKEY=tskey-...
tap run http://localhost:3000 --tailscale --docker
```

The same `--docker` flag controls Cloudflare and Tailscale: with `--tailscale` it runs `tailscale/tailscale`; without, it runs `cloudflare/cloudflared`. For Tailscale, tap starts the container with `TS_USERSPACE=true` and drives funnel config via `docker exec` (bind-mounted unix sockets don't survive macOS Docker Desktop's VM boundary). The container reaches the inspector through Docker's `host.docker.internal` host-gateway alias (auto on Docker Desktop; `--add-host` is added on Linux). Container is `--rm` and force-removed on shutdown.

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

var tap = builder.AddTap<Projects.Tap_Server>();
api.WithTap(tap);

builder.Build().Run();
```

Open <http://localhost:5198> for the inspector UI. Send traffic through <http://localhost:5199> and Tap records the request, response, headers, status, timing, and supported bodies before forwarding to the upstream service. WebSocket upgrades and Server-Sent Events are forwarded through the same proxy port; their frames/events stream live in the inspector's **WS** and **SSE** tabs.

### Aspire: quick public tunnel

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>(
        name: "tap-quick",
        proxyPort: 5307,
        uiPort: 5306)
    .WithQuickTunnel();

api.WithTap(tap);

builder.Build().Run();
```

`cloudflared` assigns a random TryCloudflare URL at startup. Tap watches the tunnel logs, surfaces the public URL on the tap, and routes Cloudflare traffic through the tap before it reaches `api`.

### Aspire: existing Cloudflare tunnel

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t =>
        t.WithExistingTunnel(builder.Configuration["Cloudflare:TunnelToken"]));

api.WithTap(tap, "api-local.example.com");

builder.Build().Run();
```

Configure the token with user-secrets:

```bash
dotnet user-secrets set Cloudflare:TunnelToken "<token>" \
  --project samples/Sample.AppHost
```

`WithExistingTunnel` expects a Cloudflare Tunnel you have already created. Create the tunnel in the Cloudflare dashboard first, copy its connector token, and pass that token to Tap. Tap will run `cloudflared tunnel run --token ...`; it will not create or reconfigure that dashboard-managed tunnel.

### Aspire: Tailscale (private by default)

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

// Tailnet-only — reachable only from other devices on your tailnet (the safe default).
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-serve", t => t.WithSystemDaemon());
api.WithTap(tap);

builder.Build().Run();
```

For a public URL on the internet (pair with auth!):

```csharp
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t.WithSystemDaemon())
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!);
api.WithTap(tap);
```

For a per-session userspace daemon (clean tailnet membership, throw-away node):

```csharp
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(8443));   // 443 (default), 8443, or 10000
api.WithTap(tap);
```

Or run the userspace daemon in Docker (`tailscale/tailscale` image — useful on macOS where the GUI client doesn't expose a `tailscaled` binary):

```csharp
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!),
        hostMode: TailscaleHostMode.Docker);
api.WithTap(tap);
```

In Docker mode the funnel target is auto-rewritten from `localhost:<port>` to `host.docker.internal:<port>` so the container can reach the inspector on the host. The companion `tailscaled` Aspire resource shows up in the dashboard as a `docker run` child of the funnel resource — its logs are the container's logs, and Aspire's shutdown kills the docker process which `--rm`s the container.

Funnel exposes one URL per tailnet node, so each `WithTailscaleFunnel(...)` is bound to exactly one upstream — register multiple funnels for multiple upstreams. Tap shells out to `tailscale funnel` and parses MagicDNS for the public URL; the `TailscaleLifecycleHook` provisions everything before the funnel resource starts and removes only the path-specific rule on shutdown so other funnel/serve rules survive.

### Aspire: API-managed tunnel and DNS

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t => t
        .WithApiManagedTunnel(
            builder.Configuration["Cloudflare:ApiToken"]!,
            builder.Configuration["Cloudflare:AccountId"]!,
            tunnelName: "tap-dev")
        .WithDynamicHostname("example.com", prefix: "api-", suffix: "-tap"));

api.WithTap(tap);

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
| `--docker` | Run the active provider in Docker. With `--tailscale`: `tailscale/tailscale` (ephemeral, userspace networking). Without: `cloudflare/cloudflared`. |
| `--auto-install` | Install `cloudflared` if missing. |
| `--tailscale` | Route through Tailscale (system `tailscaled` by default — tailnet-only via `tailscale serve`; pair with `--tailscale-public` for `tailscale funnel`). |
| `--tailscale-public` | Switch from `serve` (tailnet-only, default) to `funnel` (public internet). Pair with auth flags. |
| `--tailscale-port` | Funnel/serve port. Allowed: `443` (default), `8443`, `10000`. |
| `--tailscale-authkey` | Auth key. Switches to ephemeral mode — the CLI spawns a userspace `tailscaled` per session and joins the tailnet with the key. Env: `TAILSCALE_AUTHKEY`. |
| `--tailscale-system` | Force system mode even when an auth key is present (CLI flag, env, or profile). Use when `TAILSCALE_AUTHKEY` is exported globally but you want one run on the host's existing node. |
| `--tailscale-login-server` | Override Tailscale coordination server (Headscale, etc.). Env: `TAILSCALE_LOGIN_SERVER`. |
| `--config` | Load defaults from a JSON `tap.config` file. |

Useful environment variables:

| Variable | Purpose |
|---|---|
| `TAP_UPSTREAM` | Upstream URL when omitted from the command line. |
| `CLOUDFLARE_TUNNEL_TOKEN` | Token tunnel connector token. |
| `CLOUDFLARE_API_TOKEN` | API-managed tunnel token. |
| `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account id. |
| `TAILSCALE_AUTHKEY` | Tailscale auth key — picked up by `--tailscale` to enable ephemeral mode. |
| `TAILSCALE_LOGIN_SERVER` | Override Tailscale coordination server (Headscale, etc.). |

## Tailscale Setup

> [!CAUTION]
> Default to **`tailscale serve`** (tailnet-only). Only switch to **`tailscale funnel`** (public) when you actually need internet exposure, and always pair public tunnels with auth — opportunistic scanners hit new public hostnames within minutes.

System mode (CLI + AppHost):

1. Install Tailscale and run `tailscale up` so the node is authenticated.
2. Enable **HTTPS Certificates** in the admin console (one-time per tailnet — needed for both `serve` and `funnel`).
3. For Funnel only: grant the `funnel` capability via tailnet ACL `nodeAttrs` (see the install section above). `serve` mode doesn't need this.

Ephemeral mode (CLI + AppHost):

1. Generate a reusable auth key in the admin console under **Settings → Keys** and apply tags that grant the `funnel` capability.
2. CLI: pass `--tailscale-authkey`, set `TAILSCALE_AUTHKEY`, or save the key in a profile. Tap spawns `tailscaled --tun=userspace-networking` for the run, then tears it down on Ctrl+C.
3. AppHost: stash it in user-secrets with `dotnet user-secrets set Tailscale:AuthKey "tskey-..." --project samples/Sample.AppHost`, then use `WithEphemeralDaemon(authKey)`.
4. Windows ephemeral process mode is not supported; pair the auth key with `--docker` in the CLI or `hostMode: TailscaleHostMode.Docker` in Aspire.

The inspector dialog (Tunnel chip in the Inspector header) shows live daemon state — backend state, MagicDNS name, tailnet, version — and a table of every active `tailscale funnel` / `serve` rule on the node.

## Cloudflare Setup

For token mode:

1. In Cloudflare Zero Trust, create a Cloudflare Tunnel.
2. Copy the `cloudflared tunnel run --token ...` connector command.
3. Use only the token value with `tap run --token` or `WithExistingTunnel(...)`.
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
var tap = builder.AddTap<Projects.Tap_Server>()
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!)
    .WithIpAllowList("203.0.113.0/24")
    .WithCountryAllowList("CH")
    .WithOidcAuth(
        authority: builder.Configuration["Auth:Authority"]!,
        clientId: builder.Configuration["Auth:ClientId"]!,
        clientSecret: builder.Configuration["Auth:ClientSecret"]);

api.WithTap(tap);
```

Enabled checks are combined. If header auth, CIDR allowlist, country allowlist, and OIDC are all configured, every request must satisfy every configured check.

## Packages

| Package | Purpose |
|---|---|
| `Tap.Hosting` | Aspire AppHost extensions: `AddTap`, `AddTapContainer`, `WithTap`, `tap.WithTunnel`, `tap.WithQuickTunnel`, `tap.WithTailscaleServe` (tailnet-only, default), `tap.WithTailscaleFunnel` (public, opt-in), `WithExistingTunnel`, `WithApiManagedTunnel`, `WithDynamicHostname`, `WithSystemDaemon`/`WithEphemeralDaemon`/`WithFunnelPort`. |
| `Tap.Server` | ASP.NET Core capture server: YARP reverse proxy, capture middleware, WebSocket-terminating proxy, SSE event parser, REST API, `/api/stream` push channel, and bundled React UI with live **WS** and **SSE** message timelines. |
| `Tap.Cli` | Local command host that reuses the same inspector server code. Tailscale system-mode profiles run from the CLI; ephemeral mode requires the AppHost. |

Both entry points use the same `Tap.Server` host internally. The CLI builds `TapInspectorOptions` from command-line flags, environment variables, and optional `tap.config`; Aspire writes the same options through project environment variables.

Consumer AppHost projects must reference both `Tap.Hosting` and `Tap.Server`. `Tap.Server` supplies the generated `Projects.Tap_Server` metadata type used by `AddTap<TTapServer>()`; `Tap.Hosting` should be referenced with `IsAspireProjectResource="false"` because it is a library, not a launchable resource.

```xml
<ProjectReference Include="..\..\src\backend\Tap.Hosting\Tap.Hosting.csproj"
                  IsAspireProjectResource="false" />
<ProjectReference Include="..\..\src\backend\Tap.Server\Tap.Server.csproj" />
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

### AppHost Tailscale settings

| Key | Purpose |
|---|---|
| `Tailscale:AuthKey` | Auth key used by `WithEphemeralDaemon(authKey)` to spawn a userspace `tailscaled` per AppHost run. Reusable keys are recommended. |
| `Tailscale:UseSystem` | Sample AppHost only: set to `true` to enable the system-daemon Tailscale scenario. |
| `Tailscale:UseDocker` | Sample AppHost only: set to `true` (with `Tailscale:AuthKey`) to enable the Tailscale + Docker scenario. |

The sample AppHost can be filtered by provider:

```bash
dotnet run --project samples/Sample.AppHost                          # all scenarios (default)
dotnet run --project samples/Sample.AppHost -- --scenarios tailscale  # standalone + ts-* only
dotnet run --project samples/Sample.AppHost -- --scenarios cloudflare # standalone + cf-* only
```

### Inspector server settings

Tap.Hosting writes these for you when running under Aspire. The CLI maps its flags to the same server options.

| Variable | Purpose |
|---|---|
| `Inspector__ProxyPort` | Port that receives proxied app traffic. Default `5199`. |
| `Inspector__UiPort` | Port for the local inspector UI and API. Default `5198`. |
| `Inspector__Mode` | `standalone` or `tunnel`. |
| `Inspector__Provider` | `cloudflare` or `tailscale`. Gates provider-specific UI panes and API endpoints. |
| `Inspector__Ingress` | JSON array of `{ hostname, upstream, tunnelMode, tunnelName, publicUrl }`. |
| `Inspector__Tunnel__*` | Optional tunnel context surfaced by `/api/tunnel/details`. |
| `Inspector__Tunnel__SocketPath` | Tailscale daemon socket path (set automatically in ephemeral mode). |
| `Inspector__Auth__*` | Optional proxy-side auth gate: header, CIDR, country, and OIDC settings. |

## Development

```bash
dotnet restore Tap.slnx
dotnet build Tap.slnx
dotnet run --project samples/Sample.AppHost
```

UI source lives in `src/ui-inspector/` and is built into `src/backend/Tap.Server/wwwroot/` during server builds:

```bash
cd src/ui-inspector
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
Internet -> Cloudflare      -> cloudflared -> Tap proxy port -> upstream app
         -> Tailscale Funnel -> tailscaled  -> Tap proxy port -> upstream app
                                            \-> Tap UI port -> inspector UI/API
```

The proxy branch captures request and response data, stores the latest records in a bounded in-memory ring, and publishes new records over server-sent events. WebSocket upgrade requests are intercepted by the capture middleware and re-originated against the upstream so that every text and binary frame can be recorded in both directions; the inspector renders them in a dedicated **WS** tab alongside the existing **SSE** view. The UI branch serves the React inspector and exposes REST endpoints for request history, replay, ingress, and tunnel details. With Cloudflare credentials configured the UI can show and update tunnel ingress rules; with Tailscale it shows live daemon state and active funnel/serve rules read from `tailscale status --json` and `tailscale serve status --json`.

For the deeper technical background, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Layout

```text
assets/                       README logo and hero assets
docs/                         Technical documentation
src/backend/Tap.Core/         Shared auth and Cloudflare/cloudflared primitives
src/backend/Tap.Hosting/      Aspire integration and lifecycle hook
src/backend/Tap.Server/       Capture server, YARP proxy, SSE API, bundled UI host
src/backend/Tap.Cli/          CLI host for the inspector server
src/backend/Tap.Studio/       Studio workbench backend (REST + SSE)
src/backend/Tap.Workspace/    Workspace + spec model shared by Studio
src/ui-inspector/             Vite + React inspector source
src/ui-studio/                Vite + React Studio workbench source
src/desktop/  Tauri desktop shell for Studio
samples/                      Sample AppHosts and upstream APIs
```

## License

TBD.
