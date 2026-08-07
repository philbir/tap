# Tap Tunnel + Inspector

> Give a local service a real public URL, then see exactly what hit it.

This is the reference for the tunnelling and inspection half of Tap: the `tap` CLI, the Aspire
integration (`Tap.Hosting`), the capture server (`Tap.Server`), and the Cloudflare / Tailscale
providers in front of them. For the request workbench, see [studio.md](studio.md).

- Deep technical background: [ARCHITECTURE.md](ARCHITECTURE.md)
- Getting started, install, and the short version: [README](../README.md)

---

## Contents

- [Run modes](#run-modes)
- [CLI reference](#cli-reference)
- [Cloudflare setup](#cloudflare-setup)
- [Tailscale setup](#tailscale-setup)
- [Proxy authentication](#proxy-authentication)
- [Aspire recipes](#aspire-recipes)
- [Configuration](#configuration)

---

## Run modes

| Mode | When to use |
|---|---|
| **Standalone inspector** | A local capture proxy without any tunnel provider. |
| **Quick tunnel** | A throwaway `*.trycloudflare.com` URL — no Cloudflare account, no DNS setup. |
| **Existing tunnel** | You already manage a tunnel in the Cloudflare dashboard; Tap runs `cloudflared --token` against it. |
| **API-managed tunnel** | Tap looks up or creates a named tunnel, writes local credentials, and manages DNS. |
| **Dynamic hostname** | Fresh per-run hostnames such as `api-1a2b3c4d-tap.example.com`, for demos and parallel dev loops. |
| **Tailscale Serve** (default) | Tailnet-only: reachable from your other tailnet devices, not the public internet. |
| **Tailscale Funnel** (opt-in) | Public URL through your tailnet node — pair with auth. |
| **Tailscale ephemeral** | A per-session userspace `tailscaled` from an auth key, as a host process or a Docker container. The node disappears when the run stops. |

> [!WARNING]
> **Public tunnels are scanned within minutes.** The moment a public hostname's TLS certificate
> appears in a CT log — which happens immediately when Cloudflare Tunnel or Tailscale Funnel
> comes up — opportunistic scanners start probing for admin endpoints, debug routes, and
> known-CVE banners. Always pair public tunnels with [auth](#proxy-authentication) or edge
> controls. Those attempts show up in the Inspector request log, often seconds after the
> tunnel is reachable.

---

## CLI reference

```bash
tap run <upstream> [options]
tap install-cloudflared
```

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
| `--tailscale` | Route through Tailscale (system `tailscaled` by default — tailnet-only via `tailscale serve`). |
| `--tailscale-public` | Switch from `serve` (tailnet-only, default) to `funnel` (public internet). Pair with auth flags. |
| `--tailscale-port` | Funnel/serve port. Allowed: `443` (default), `8443`, `10000`. |
| `--tailscale-authkey` | Auth key. Switches to ephemeral mode — the CLI spawns a userspace `tailscaled` per session. Env: `TAILSCALE_AUTHKEY`. |
| `--tailscale-system` | Force system mode even when an auth key is present. Use when `TAILSCALE_AUTHKEY` is exported globally but you want this run on the host's existing node. |
| `--tailscale-login-server` | Override the Tailscale coordination server (Headscale, etc.). Env: `TAILSCALE_LOGIN_SERVER`. |
| `--auth-header` | Require a header, as `Name=value`. |
| `--auth-cidr` | Allowlist a client IP range. |
| `--auth-country` | Allowlist an ISO country code. |
| `--auth-oidc-authority` / `--auth-oidc-client-id` / `--auth-oidc-client-secret` | Require browser sign-in via OpenID Connect. |
| `--config` | Load defaults from a JSON `tap.config` file. |

Precedence is command-line flags, then environment variables, then `tap.config` defaults.

| Variable | Purpose |
|---|---|
| `TAP_UPSTREAM` | Upstream URL when omitted from the command line. |
| `CLOUDFLARE_TUNNEL_TOKEN` | Token-tunnel connector token. |
| `CLOUDFLARE_API_TOKEN` | API-managed tunnel token. |
| `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account id. |
| `TAILSCALE_AUTHKEY` | Tailscale auth key — picked up by `--tailscale` to enable ephemeral mode. |
| `TAILSCALE_LOGIN_SERVER` | Override the Tailscale coordination server. |

```json
{
  "upstream": "http://localhost:3000"
}
```

---

## Cloudflare setup

Cloudflare features need [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/)
on `PATH`: `brew install cloudflared`, `winget install Cloudflare.cloudflared`, or
`tap install-cloudflared`.

**Token mode** — Tap runs a tunnel you already created:

1. In Cloudflare Zero Trust, create a Cloudflare Tunnel.
2. Copy the `cloudflared tunnel run --token ...` connector command.
3. Pass only the token value to `tap run --token` or `WithExistingTunnel(...)`.
4. Route the hostname you hand to Tap to that tunnel in Cloudflare.

`--token` / `WithExistingTunnel(...)` do **not** create or reconfigure a tunnel.

**API-managed mode** — Tap creates and wires things up for you:

1. Create a Cloudflare API token with account-level Cloudflare Tunnel edit permission.
2. Add DNS edit permission for the zone Tap will manage.
3. Supply `Cloudflare:ApiToken` and `Cloudflare:AccountId` through user-secrets, environment
   variables, or normal .NET configuration.
4. Use `WithApiManagedTunnel(...)`, plus `WithDynamicHostname(...)` when Tap should mint
   hostnames and DNS CNAMEs.

References: [tunnel tokens](https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/) ·
[API token permissions](https://developers.cloudflare.com/fundamentals/api/reference/permissions/)

---

## Tailscale setup

Host-process modes need the [`tailscale`](https://tailscale.com/download) CLI on `PATH`;
Docker mode runs the official `tailscale/tailscale` image instead.

> [!CAUTION]
> Default to **`tailscale serve`** (tailnet-only). Only switch to **`tailscale funnel`**
> (public) when you actually need internet exposure, and always pair public tunnels with auth.

**System mode**

1. Install Tailscale and run `tailscale up` so the node is authenticated.
2. Enable **HTTPS Certificates** in the [admin console](https://login.tailscale.com/admin/)
   under DNS. One-time per tailnet, required for both `serve` and `funnel`.
3. For Funnel only, grant the `funnel` capability via a tailnet ACL `nodeAttrs` rule:

```json
{
  "nodeAttrs": [
    { "target": ["*"], "attr": ["funnel"] }
  ]
}
```

Verify with `tailscale status --json | grep -i funnel` — `"funnel"` should appear in your
node's `CapMap`. Funnel only listens on ports `443`, `8443`, and `10000`.

**Ephemeral mode**

1. Generate a reusable auth key under **Settings → Keys**, with tags that grant the
   capabilities you need.
2. CLI: pass `--tailscale-authkey`, set `TAILSCALE_AUTHKEY`, or save the key in a profile. Tap
   spawns `tailscaled --tun=userspace-networking` for the run and tears it down on Ctrl+C.
3. AppHost: `dotnet user-secrets set Tailscale:AuthKey "tskey-..."`, then
   `WithEphemeralDaemon(authKey)`.
4. Windows ephemeral *process* mode is not supported — pair the auth key with `--docker`
   (CLI) or `hostMode: TailscaleHostMode.Docker` (Aspire).

The Inspector's Tunnel dialog reads live daemon state — backend state, MagicDNS name, tailnet,
IPs, version — plus every active `serve` / `funnel` rule on the node.

---

## Proxy authentication

Auth gates the **proxy** branch before traffic reaches your upstream. The Inspector UI port
stays local and is not gated by these checks.

| Check | Behaviour |
|---|---|
| Header | Require a static API key in a request header. |
| CIDR | Allowlist client IP ranges using `CF-Connecting-IP`, then `X-Forwarded-For`, then the remote IP. |
| Country | Allowlist ISO country codes using Cloudflare's `CF-IPCountry` header. |
| OIDC | Require browser sign-in with a cookie session and the OpenID Connect code flow. |

Enabled checks are **combined** — every request must satisfy every configured check. Machine
clients usually use header auth; browsers use OIDC.

```bash
tap run http://localhost:3000 --quick \
  --auth-header "X-Tap-Key=$TAP_KEY" \
  --auth-cidr "203.0.113.0/24" \
  --auth-country "CH"
```

```bash
tap run http://localhost:3000 --quick \
  --auth-oidc-authority "https://issuer.example.com" \
  --auth-oidc-client-id "$OIDC_CLIENT_ID" \
  --auth-oidc-client-secret "$OIDC_CLIENT_SECRET"
```

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

---

## Aspire recipes

Consumer AppHost projects must reference **both** `Tap.Hosting` and `Tap.Server`. `Tap.Server`
supplies the generated `Projects.Tap_Server` metadata type used by `AddTap<TTapServer>()`;
`Tap.Hosting` is referenced with `IsAspireProjectResource="false"` because it is a library,
not a launchable resource.

```xml
<ProjectReference Include="..\..\src\backend\Tap.Hosting\Tap.Hosting.csproj"
                  IsAspireProjectResource="false" />
<ProjectReference Include="..\..\src\backend\Tap.Server\Tap.Server.csproj" />
```

**Standalone inspector**

```csharp
var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>();
api.WithTap(tap);
```

UI on <http://localhost:5198>, proxy on <http://localhost:5199>.

**Quick public tunnel**

```csharp
var tap = builder.AddTap<Projects.Tap_Server>(
        name: "tap-quick", proxyPort: 5307, uiPort: 5306)
    .WithQuickTunnel();

api.WithTap(tap);
```

**Existing Cloudflare tunnel**

```csharp
var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t =>
        t.WithExistingTunnel(builder.Configuration["Cloudflare:TunnelToken"]));

api.WithTap(tap, "api-local.example.com");
```

**API-managed tunnel and DNS**

```csharp
var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t => t
        .WithApiManagedTunnel(
            builder.Configuration["Cloudflare:ApiToken"]!,
            builder.Configuration["Cloudflare:AccountId"]!,
            tunnelName: "tap-dev")
        .WithDynamicHostname("example.com", prefix: "api-", suffix: "-tap"));

api.WithTap(tap);
```

The lifecycle hook runs before `cloudflared` starts: it looks up or creates the named tunnel,
writes a temporary credentials file, resolves the zone, mints hostnames, ensures CNAME records,
and then launches `cloudflared` with a local ingress config.

**Tailscale — private by default**

```csharp
// Tailnet-only (the safe default).
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-serve", t => t.WithSystemDaemon());
api.WithTap(tap);
```

```csharp
// Public Funnel — pair with auth.
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t.WithSystemDaemon())
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!);
api.WithTap(tap);
```

```csharp
// Per-session userspace daemon.
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(8443));   // 443 (default), 8443, or 10000
api.WithTap(tap);
```

```csharp
// Userspace daemon in Docker — useful on macOS, where the GUI client
// doesn't expose a tailscaled binary.
var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!),
        hostMode: TailscaleHostMode.Docker);
api.WithTap(tap);
```

In Docker mode the funnel target is auto-rewritten from `localhost:<port>` to
`host.docker.internal:<port>`. The companion `tailscaled` resource shows up in the Aspire
dashboard as a child of the funnel resource; its logs are the container's logs.

Funnel exposes one URL per tailnet node, so each `WithTailscaleFunnel(...)` binds to exactly
one upstream — register multiple funnels for multiple upstreams.

**Sample AppHost**

```bash
dotnet run --project samples/Sample.AppHost                            # all scenarios
dotnet run --project samples/Sample.AppHost -- --scenarios tailscale   # standalone + ts-* only
dotnet run --project samples/Sample.AppHost -- --scenarios cloudflare  # standalone + cf-* only
```

---

## Configuration

### AppHost — Cloudflare

| Key | Purpose |
|---|---|
| `Cloudflare:TunnelToken` | Connector token for dashboard-managed token tunnels. |
| `Cloudflare:ApiToken` | API token for API-managed tunnels, DNS, and tunnel details. |
| `Cloudflare:AccountId` | Cloudflare account id used with `Cloudflare:ApiToken`. |
| `Cloudflare:Zone` | Default zone used by the sample AppHost. |
| `Cloudflare:Hostnames:*` | Optional sample hostnames for token and managed scenarios. |

```bash
dotnet user-secrets set Cloudflare:TunnelToken "<connector-token>" --project samples/Sample.AppHost
dotnet user-secrets set Cloudflare:ApiToken    "<api-token>"       --project samples/Sample.AppHost
dotnet user-secrets set Cloudflare:AccountId   "<account-id>"      --project samples/Sample.AppHost
```

### AppHost — Tailscale

| Key | Purpose |
|---|---|
| `Tailscale:AuthKey` | Auth key used by `WithEphemeralDaemon(authKey)`. Reusable keys recommended. |
| `Tailscale:UseSystem` | Sample AppHost only: `true` enables the system-daemon scenario. |
| `Tailscale:UseDocker` | Sample AppHost only: `true` (with an auth key) enables the Docker scenario. |

### Inspector server

`Tap.Hosting` writes these for you under Aspire; the CLI maps its flags to the same options.

| Variable | Purpose |
|---|---|
| `Inspector__ProxyPort` | Port that receives proxied app traffic. Default `5199`. |
| `Inspector__UiPort` | Port for the local Inspector UI and API. Default `5198`. |
| `Inspector__Mode` | `standalone` or `tunnel`. |
| `Inspector__Provider` | `cloudflare` or `tailscale`. Gates provider-specific UI panes and API endpoints. |
| `Inspector__Ingress` | JSON array of `{ hostname, upstream, tunnelMode, tunnelName, publicUrl }`. |
| `Inspector__Tunnel__*` | Optional tunnel context surfaced by `/api/tunnel/details`. |
| `Inspector__Tunnel__SocketPath` | Tailscale daemon socket path (set automatically in ephemeral mode). |
| `Inspector__Auth__*` | Optional proxy-side auth gate: header, CIDR, country, and OIDC settings. |
