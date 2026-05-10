# Tap Architecture

Tap has two user-facing entry points and one shared inspector runtime:

```mermaid
flowchart LR
    CLI["tap CLI"]
    Aspire["Aspire AppHost"]
    Server["Tap.Server\nshared inspector host"]
    UI["React inspector UI"]
    Upstream["Upstream app"]
    Cloudflare["Cloudflare edge"]
    Cloudflared["cloudflared"]
    TSEdge["Tailscale Funnel"]
    Tailscaled["tailscaled"]

    CLI --> Server
    Aspire --> Hosting["Tap.Hosting"]
    Hosting --> Server
    CLI --> Cloudflared
    Hosting --> Cloudflared
    Cloudflare --> Cloudflared
    Cloudflared --> Server
    CLI --> Tailscaled
    Hosting --> Tailscaled
    TSEdge --> Tailscaled
    Tailscaled --> Server
    Server --> Upstream
    Server --> UI
```

The CLI is the direct path for local, ad hoc work: point Tap at an upstream URL and optionally add a tunnel. Aspire is the modeled path: declare inspectors and tunnels beside your app resources and let the AppHost resolve ports, hostnames, and DNS before startup. Tap supports two tunnel providers — **Cloudflare** (named tunnels with optional API/DNS management) and **Tailscale Funnel** (one upstream per tailnet node, ports 443/8443/10000).

## High-Level Flows

### CLI

```mermaid
flowchart TD
    User["Developer terminal"]
    Run["tap run http://localhost:3000 --quick"]
    Options["Build TapInspectorOptions\nfrom flags, env, tap.config"]
    Tunnel["Provision/start tunnel\nCloudflare or Tailscale"]
    Server["Tap.Server\nproxy :4444, UI :4445"]
    App["Local upstream\nhttp://localhost:3000"]

    User --> Run --> Options --> Server
    Options --> Tunnel --> Server
    Server --> App
```

The CLI owns process orchestration. It can install `cloudflared`, provision Cloudflare tunnels, start `cloudflared`, configure Tailscale `serve` / `funnel`, run ephemeral `tailscaled` processes or the `tailscale/tailscale` Docker image, print the public URL, and then run the shared inspector server until Ctrl+C.

### Aspire

```mermaid
flowchart TD
    AppHost["DistributedApplication AppHost"]
    Hosting["Tap.Hosting extensions"]
    Hook["CloudflaredLifecycleHook\nBeforeStartAsync"]
    Inspector["Tap.Server project resource"]
    Tunnel["cloudflared executable resource"]
    Api["Aspire upstream resource"]

    AppHost --> Hosting
    Hosting --> Inspector
    Hosting --> Tunnel
    Hosting --> Api
    Hook --> Tunnel
    Hook --> Inspector
    Tunnel --> Inspector
    Inspector --> Api
```

Aspire owns resource modeling. `Tap.Hosting` registers project and executable resources, the lifecycle hook resolves the final tunnel state, and `Tap.Server` receives its configuration through environment variables.

### Runtime Request Path

```mermaid
sequenceDiagram
    participant Client
    participant CF as Cloudflare
    participant CFD as cloudflared
    participant Proxy as Tap proxy port
    participant Store as Request store
    participant App as Upstream app
    participant UI as Inspector UI

    Client->>CF: HTTP request
    CF->>CFD: tunnel connection
    CFD->>Proxy: localhost proxy port
    Proxy->>Store: capture request
    Proxy->>App: forward via YARP
    App-->>Proxy: response
    Proxy->>Store: capture response
    Store-->>UI: SSE event
    Proxy-->>Client: response
```

The proxy branch captures traffic before and after forwarding. The UI branch is local control-plane traffic: request history, replay, ingress, tunnel details, and the static React app.

## Components

```mermaid
flowchart TB
    subgraph Entry["Entry points"]
        CLI["Tap.Cli"]
        Aspire["Tap.Hosting"]
    end

    subgraph Core["Shared core"]
        Auth["Tap.Core.Auth"]
        CFAPI["Tap.Core.Cloudflare"]
        CFDCore["Tap.Core.Cloudflared"]
    end

    subgraph Runtime["Tap.Server"]
        Host["TapInspectorHost"]
        Capture["CaptureMiddleware"]
        YARP["YARP reverse proxy"]
        Store["InMemoryRequestStore"]
        API["REST + SSE API"]
        StaticUI["Bundled React UI"]
    end

    CLI --> Core
    Aspire --> Core
    CLI --> Runtime
    Aspire --> Runtime
    Host --> Capture --> YARP
    Capture --> Store
    API --> Store
    StaticUI --> API
    Auth --> Host
```

### Tap.Cli

`Tap.Cli` is a command-line wrapper around the same server runtime used by Aspire.

| Command | Purpose |
|---|---|
| `tap run <upstream>` | Start a local inspector and forward traffic to an upstream URL. |
| `tap run <upstream> --quick` | Start a TryCloudflare quick tunnel. |
| `tap run <upstream> --token <token>` | Use a Cloudflare connector token. |
| `tap run <upstream> --api-managed <name>` | Look up or create an API-managed tunnel. |
| `tap run <upstream> --dynamic <zone>` | Mint a fresh hostname under a Cloudflare zone. |
| `tap run <upstream> --tailscale [--tailscale-port 8443]` | Route through Tailscale (system tailscaled, **tailnet-only via `tailscale serve`** — pair with `--tailscale-public` for `funnel`). |
| `tap run <upstream> --tailscale --tailscale-public` | Switch to public `tailscale funnel`; pair with auth flags. |
| `tap run <upstream> --tailscale --tailscale-authkey <key>` | Ephemeral mode: CLI spawns a userspace tailscaled, authenticates with the key, and tears it down on shutdown. |
| `tap run <upstream> --tailscale --docker` | Ephemeral mode via the `tailscale/tailscale` Docker image (same `--docker` flag also drives `cloudflare/cloudflared` when not in Tailscale mode). |
| `tap run --name <profile>` | Run a saved profile. Tailscale profiles can use system mode, ephemeral process mode from an auth key, or ephemeral Docker mode via `--docker`. |
| `tap install-cloudflared` | Install `cloudflared` with the host package manager. |

The CLI reads command flags first, then environment variables (`TAP_UPSTREAM`, `CLOUDFLARE_TUNNEL_TOKEN`, `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `TAILSCALE_AUTHKEY`, `TAILSCALE_LOGIN_SERVER`), then optional `tap.config` defaults.

### Tap.Hosting

`Tap.Hosting` lives in the `Aspire.Hosting` namespace so consumer AppHosts can use Tap as normal Aspire extension methods.

| Extension | Purpose |
|---|---|
| `AddTap<TTapServer>()` | Register `Tap.Server` as an Aspire project with proxy and UI endpoints. |
| `AddTapContainer(...)` | Same Tap, hosted as a Docker container. |
| `WithTap(tap)` | Route a resource through a tap (and its tunnel, if attached). Dispatches by tunnel resource type to the right provider attach path. |
| `tap.WithTunnel(name, configure)` | Attach a Cloudflare tunnel as a child of the tap. |
| `tap.WithQuickTunnel()` | Attach a TryCloudflare quick tunnel wired to the tap's proxy port. |
| `tap.WithTailscaleServe(name?, configure?)` | Attach a Tailscale `serve` (tailnet-only — the safe default) as a child of the tap. |
| `tap.WithTailscaleFunnel(name?, configure?)` | Attach a Tailscale `funnel` (publicly exposed — opt-in, pair with auth) as a child of the tap. |
| `WithExistingTunnel(token)` | Run cloudflared against a tunnel created in the Cloudflare dashboard. |
| `WithApiManagedTunnel(...)` | Create or reuse a named tunnel using the Cloudflare API. |
| `WithDynamicHostname(...)` | Mint hostnames and DNS CNAMEs before startup. |
| `WithSystemDaemon()` / `WithEphemeralDaemon(authKey)` | Toggle Tailscale daemon mode: reuse the host's tailscaled, or spawn a userspace one per session. |
| `WithFunnelPort(port)` | Pick the Tailscale Funnel public port (443 default, 8443, 10000). |
| `AddCloudflaredTunnel()` / `AddTailscaleFunnel()` | Low-level escape hatches: register the tunnel resource directly without binding it to a tap. |

The provider-agnostic shape lives in `Tap.Hosting/Tunnels/`: `TapTunnelResource` (abstract base), `TapTunnelAnnotation` (sibling of `CloudflareTunnelAnnotation`), and `TapTunnelIngress`. `CloudflaredTunnelResource` and `TailscaleFunnelResource` both inherit `TapTunnelResource`, so `TapHandle.AttachedTunnel` is typed as the base and `WithTap<T>` runtime-dispatches via `is` checks.

The generic `TTapServer` is the project metadata type emitted by Aspire's AppHost source generator, usually `Projects.Tap_Server`. Because that type exists only in the consumer's AppHost project, the consumer project must reference `Tap.Server` directly.

### CloudflaredLifecycleHook

In Aspire, tunnel and DNS state has to exist before `cloudflared` starts. `CloudflaredLifecycleHook` runs in `BeforeStartAsync` and handles that setup.

| Responsibility | Why it happens before start |
|---|---|
| Verify or install `cloudflared` | Fail early when the tunnel binary is unavailable. |
| Look up or create API-managed tunnels | Local ingress config needs a tunnel id and credentials file. |
| Write credentials JSON | `cloudflared tunnel --config ... run <id>` consumes this file. |
| Resolve zones | Dynamic hostnames and DNS CNAMEs need the correct Cloudflare zone id. |
| Mint dynamic hostnames | Aspire annotations and inspector ingress entries need final public hostnames. |
| Ensure DNS CNAMEs | Public hostnames must point to `<tunnelId>.cfargotunnel.com`. |
| Back-fill annotations | Aspire URL chips and the inspector UI should show final tunnel details. |
| Watch quick tunnel logs | TryCloudflare URLs only appear after cloudflared emits them. |

For API-managed tunnels, existing tunnels are reused only when Tap can find a matching local credentials file. Cloudflare does not reveal an existing tunnel secret through the API, so Tap avoids destructive rotation and throws a clear setup error instead.

### TailscaleLifecycleHook

The Tailscale provider has its own lifecycle hook with disjoint responsibilities — both hooks scan their own resource types and don't touch each other's state.

| Responsibility | Why it happens before start |
|---|---|
| Verify the `tailscale` CLI is on PATH | Fail early when the CLI is missing. |
| In ephemeral mode, register a `TailscaledDaemonResource` (sibling resource) | The userspace daemon needs to show up in the Aspire dashboard with its own logs and shutdown ordering rather than as a leaked side-process. |
| Generate a per-resource bootstrapper script under `Path.GetTempPath()` | The script runs `tailscale up` (ephemeral), waits for the node to come Online, pre-warms the cert, configures the funnel, and stays alive. |
| Watch the bootstrapper's stdout for `TAP_TAILSCALE_HOSTNAME=...` | The MagicDNS name is only known after `tailscaled` reports Online; back-fill it into `TapTunnelAnnotation.Hostname` and the tap's "proxy" URL chip via `PublishUpdateAsync`. |

The bootstrapper is a bash script on macOS/Linux and a PowerShell script on Windows. Windows ephemeral process mode is intentionally not supported in v1 (the GUI service model gets in the way) — pair the auth key with Docker mode on Windows. All three Unix bootstrapper variants (system, ephemeral process, Docker) install an EXIT trap that removes the path-specific rule (`tailscale serve|funnel --https=$PORT --set-path=/ off`) so other manual rules on that port survive shutdown.

In Docker mode (`AddTailscaleFunnel(hostMode: TailscaleHostMode.Docker)` or `tap.WithTailscaleServe(..., hostMode: ...)`), the same companion `TailscaledDaemonResource` is registered but its command is `docker` and its args are `run --rm --name <id> -e TS_AUTHKEY=... -e TS_USERSPACE=true tailscale/tailscale:latest` (Linux gets `--add-host=host.docker.internal:host-gateway` automatically). The bootstrapper drives `tailscale` via `docker exec <container> tailscale ...` rather than bind-mounting the LocalAPI socket — bind-mounted unix sockets don't survive macOS Docker Desktop's VM boundary. It skips the `tailscale up` step (the container's entrypoint runs it from `TS_AUTHKEY`) and rewrites `localhost:<port>` → `host.docker.internal:<port>` so the funnel target is reachable from inside the container. Cleanup is the same: Aspire kills the `docker run` process, `--rm` removes the container.

### Tap.Server

`Tap.Server` builds one ASP.NET Core application bound to two ports.

```mermaid
flowchart LR
    Request["Incoming HTTP"]
    Branch{"Local port"}
    Proxy["Proxy port\nCaptureMiddleware + YARP"]
    UI["UI port\nStatic files + /api/*"]
    Upstream["Upstream"]
    Store["In-memory store"]
    Browser["Browser UI"]

    Request --> Branch
    Branch --> Proxy --> Upstream
    Proxy --> Store
    Branch --> UI --> Browser
    UI --> Store
```

`TapInspectorHost.Build()` registers the capture store, replay client, optional Cloudflare client, YARP routes, proxy branch, and UI branch. Both CLI and Aspire create `TapInspectorOptions` and pass them into this host.

## Capture Model

`CaptureMiddleware` sits before YARP in the proxy branch.

For each request it records:

| Field group | Examples |
|---|---|
| Identity | sequence, id, timestamp, method, host, path, remote IP. |
| Request | headers, content type, body size, captured text body when supported. |
| Response | status, headers, content type, body size, captured text or image body when supported. |
| Timing | duration in milliseconds. |
| Errors | proxy exception message when forwarding fails. |

Bodies are capped at 1 MB. Text-like content types are captured as UTF-8 strings; image responses are stored as base64 for UI rendering; unsupported or oversized bodies are marked truncated.

`InMemoryRequestStore` keeps the newest 200 records and fans out new records to `/api/stream` subscribers over server-sent events.

## Configuration Shape

Both entry points converge on `TapInspectorOptions`.

| Option | CLI source | Aspire source |
|---|---|---|
| Proxy port | `--proxy-port`, default `4444` | `Inspector__ProxyPort`, default `5199` |
| UI port | `--ui-port`, default `4445` | `Inspector__UiPort`, default `5198` |
| Ingress | upstream argument / `TAP_UPSTREAM` / `tap.config` | resolved Aspire resource endpoint |
| Tunnel mode | `--quick`, `--token`, `--api-managed`, `--dynamic` | tunnel extension methods |
| Cloudflare credentials | CLI flags or env vars | .NET configuration / user-secrets |
| Tailscale auth key / login server | `--tailscale-authkey`, `TAILSCALE_AUTHKEY`, `--tailscale-login-server`, `TAILSCALE_LOGIN_SERVER`, profile | .NET configuration / user-secrets passed to `WithEphemeralDaemon(...)` |
| Auth | CLI auth flags | `Inspector:Auth` configuration |

The server itself does not care whether options came from a terminal command or an Aspire AppHost.

## Auth Boundary

The inspector UI port is local control-plane traffic and is not gated by Tap auth. The proxy port can enforce `Inspector:Auth` checks before requests reach the upstream:

| Mechanism | Source |
|---|---|
| Static header | Configured request header and value. |
| CIDR allowlist | `CF-Connecting-IP`, then `X-Forwarded-For`, then remote IP. |
| Country allowlist | `CF-IPCountry` header. |
| OIDC | Cookie plus OpenID Connect code flow. |

Enabled mechanisms are combined: a request must satisfy every configured check.

## Design Constraints

Tap intentionally keeps runtime state simple:

| Constraint | Current choice |
|---|---|
| Request history | In-memory, newest 200 records. |
| Body capture | 1 MB maximum per request or response body. |
| Persistence | None for captured traffic. |
| Transport | HTTP reverse proxy via YARP. |
| Live updates | Server-sent events. |
| Cloudflare setup | CLI provisioning or Aspire lifecycle provisioning. |

That makes Tap easy to run from a terminal and easy to model inside an Aspire AppHost.
