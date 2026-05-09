# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build, run, test

- SDK is pinned in `global.json` to .NET 10 (`10.0.201`). Targets `net10.0` everywhere.
- `TreatWarningsAsErrors` is enabled globally (`Directory.Build.props`) — warnings break the build.
- Solution file is `Tap.slnx` (modern XML format). Use `dotnet build Tap.slnx` / `dotnet restore Tap.slnx`.
- Run the full demo: `dotnet run --project samples/Sample.AppHost`. There is no test project yet.
- Aspire CLI: `samples/aspire.config.json` points at `Sample.AppHost`, so `aspire run` from inside `samples/` works.
- `cloudflared` must be on PATH at AppHost start time (`brew install cloudflared` / `winget install Cloudflare.cloudflared`). The lifecycle hook shells out and fails fast if it's missing.

### UI

- `ui/` is yarn 4.9.1 (Berry) — use `yarn`, not `npm`. Scripts: `yarn dev` (Vite, port 5197), `yarn build` (`tsc -b && vite build`), `yarn preview`.
- `Tap.Server.csproj` has a `BuildTapUi` MSBuild target that runs `yarn install` + `yarn build` and copies `ui/dist/**` into `src/Tap.Server/wwwroot/` on every server build. Set `-p:SkipTapUiBuild=true` to skip when iterating on C# only.
- For UI hot-reload against a running AppHost: `cd ui && yarn dev`. Vite proxies `/api` → `VITE_INSPECTOR_API_URL` (default `http://localhost:5198`); set that env var to whatever UI port the AppHost allocated.
- `src/Tap.Server/wwwroot/*` is gitignored (regenerated artifact) — never hand-edit it.

## Architecture

Tap is two NuGet-style packages plus a sample, glued together by Aspire:

- **`Tap.Hosting`** (library, namespace `Aspire.Hosting`) — extension methods consumers call from their own AppHost. Primary surface: `AddTap<T>`, `AddTapContainer`, `WithTap`, `tap.WithTunnel(name, configure)`, `tap.WithQuickTunnel`, `tap.WithTailscaleFunnel`, `WithExistingTunnel`, `WithApiManagedTunnel`, `WithDynamicHostname`, `WithSystemDaemon`/`WithEphemeralDaemon`/`WithFunnelPort` (Tailscale). Low-level escape hatches still public: `AddCloudflaredTunnel`, `AddTailscaleFunnel`, `WithCloudflareTunnel`, `WithTailscaleFunnel(target, tunnel)`. No runtime; pure AppHost wiring.
- **Tunnel abstraction** lives in `src/Tap.Hosting/Tunnels/` (`TapTunnelResource` base, `TapTunnelAnnotation`, `TapTunnelIngress`). `CloudflaredTunnelResource` (multi-host) and `TailscaleFunnelResource` (single endpoint) both inherit `TapTunnelResource`. `TapHandle.AttachedTunnel` is the base type; `WithTap<T>` dispatches to provider-specific attach via `is`-check on the runtime type.
- **`Tap.Server`** (`Microsoft.NET.Sdk.Web`) — standalone ASP.NET Core app: YARP reverse proxy + capture middleware + SSE feed + bundled React UI in `wwwroot`. Reads its config from `Inspector:*` and `Cloudflare:*` env vars set by the AppHost.
- **`ui/`** — Vite + React 19 + TypeScript source for the inspector UI. Built into `Tap.Server/wwwroot` at build time; not a separate runtime artifact.
- **`samples/Sample.AppHost`** — exercises eight scenarios in parallel: standalone, Cloudflare quick / existing-dashboard / API-managed / dynamic-hostname tunnels, and Tailscale Serve in three flavors (system daemon, ephemeral userspace process, ephemeral Docker container). Each scenario is gated on the relevant user-secrets being present (`Cloudflare:*` for CF modes; `Tailscale:UseSystem=true` for system-Tailscale; `Tailscale:AuthKey` for ephemeral; pair with `Tailscale:UseDocker=true` for the container variant). `samples/Sample.Api` is the trivial upstream. Filter providers via `--scenarios cloudflare|tailscale|all` (default all).

### How a request flows

1. Internet → Cloudflare → cloudflared (existing-tunnel mode: dashboard ingress; local-ingress mode: a `config.yml` written to temp by `CloudflaredExtensions.WriteConfigYamlAsync`).
2. cloudflared → `localhost:<InspectorProxyPort>` (when a tap is attached; otherwise straight to the upstream's allocated localhost port).
3. `Tap.Server` listens on **two ports** distinguished by `app.MapWhen(ctx.Connection.LocalPort == ...)` in `Program.cs`:
   - `proxyPort` branch: `CaptureMiddleware` records request/response into `InMemoryRequestStore` (200-record ring, 1MB body cap, image bodies stored as base64) then YARP forwards to the upstream picked by Host header.
   - `uiPort` branch: REST under `/api/*` (list/clear records, replay, ingress, tunnel ingress) + an SSE stream at `/api/stream` + static UI fallback to `index.html`.
4. UI subscribes to `/api/stream` (live tail) and renders detail panes.

### Two important Aspire-specific gotchas

- **Generic `TTapServer` parameter**: `AddTap<T>` takes a generic project-metadata type (typically `Projects.Tap_Server`). That generated type is emitted by the `Aspire.AppHost.Sdk` source generator **only in the consumer's AppHost csproj**, so the library cannot reference it directly. The consumer's AppHost csproj must therefore add a `ProjectReference` to `Tap.Server` itself (see `samples/Sample.AppHost/Sample.AppHost.csproj`). The `Tap.Hosting` reference in that same csproj uses `IsAspireProjectResource="false"` because it's a library, not a launchable project.
- **`CloudflaredLifecycleHook` runs in `BeforeStartAsync`** and is responsible for: verifying the `cloudflared` binary, calling the Cloudflare API to look up / create named tunnels, writing credentials JSON to temp, resolving zone IDs by walking up labels of the FQDN, minting dynamic hostnames, ensuring DNS CNAMEs to `<tunnelId>.cfargotunnel.com`, and back-filling generated hostnames into `CloudflareTunnelAnnotation` and the tap's ingress entries. Anything that needs a hostname before cloudflared launches belongs here, not in the extension methods.
- **`TailscaleLifecycleHook` runs in `BeforeStartAsync`** for any `TailscaleFunnelResource`. It generates a per-resource bash bootstrapper script (`tap-ts-<name>-<guid>.sh` under `Path.GetTempPath()`) — three variants: system, ephemeral process, and ephemeral Docker. Each variant waits for the daemon (host process socket OR `docker exec`), runs `tailscale up` if needed (ephemeral process only — Docker's container entrypoint does that from `TS_AUTHKEY`), pre-warms the cert, then runs `tailscale serve` (default, tailnet-only) or `tailscale funnel` (when `PublicExpose=true`). The script doesn't grep for the funnel cap — it lets `tailscale` itself report cap/ACL/HTTPS errors via stderr, which is more precise than any string-match. Each script then `tail -f /dev/null`s to keep the resource alive (`sleep infinity` is GNU-only and rejected by macOS BSD `sleep`). On exit, an EXIT trap removes the path-specific rule (`tailscale serve --set-path=/ off`) so manual rules on the same port survive. The bootstrapper echoes `TAP_TAILSCALE_HOSTNAME=<dns>` once MagicDNS resolves; a C# log watcher picks that up and back-fills `TailscaleFunnelResource.MagicDnsName`, the `TapTunnelAnnotation.Hostname`, and the tap's "proxy" URL chip via `PublishUpdateAsync`. Ephemeral mode also registers a separate `TailscaledDaemonResource` (parented to the funnel resource) so the daemon shows up in the Aspire dashboard with its own logs and shutdown ordering. Windows + ephemeral process mode is unsupported (the GUI service model gets in the way) — pair an auth key with Docker mode on Windows. The host CLI check is skipped entirely when every Tailscale tunnel is in Docker mode (no host `tailscale` binary is needed in that case).

### Configuration touch-points

- AppHost reads `Cloudflare:TunnelToken`, `Cloudflare:ApiToken`, `Cloudflare:AccountId`, `Cloudflare:Zone`, `Cloudflare:Hostnames:*`, and `Tailscale:AuthKey` from `IConfiguration`. The sample expects these via `dotnet user-secrets` (UserSecretsId `tap-sample-apphost`) — see header comment in `samples/Sample.AppHost/Program.cs`.
- `Tap.Server` consumes `Inspector:ProxyPort`, `Inspector:UiPort`, `Inspector:Mode` (`standalone`|`tunnel`), `Inspector:Provider` (`cloudflare`|`tailscale`|unset — gates provider-specific endpoints), `Inspector:Ingress` (JSON array of `{hostname, upstream}`), and optional `Cloudflare:ApiToken`/`AccountId`/`TunnelId` (used by `/api/tunnel/ingress` to mutate Cloudflare ingress rules; that endpoint returns 404 for non-Cloudflare providers). The AppHost serializes the ingress array via `WithEnvironment(ctx => ...)` so allocated upstream ports are resolved at startup, not registration.
- Default ports: proxy `5199`, UI `5198` (constants in `TapExtensions`). The sample overrides to `5299/5298` to avoid collisions if a real consumer also runs.

### Source-generated JSON

Both `Tap.Server` and `Tap.Hosting/Cloudflared/CloudflareApi.cs` use `JsonSerializerContext` partial classes (`InspectorIngressJsonContext`, `RequestRecordJsonContext`, `InspectorConfigJsonContext`, `CloudflareApiJson`). When adding a new serialized DTO, add a `[JsonSerializable(...)]` attribute to the relevant context — don't introduce reflection-based serialization.

### Central package management

`Directory.Packages.props` enables `ManagePackageVersionsCentrally`. Bump versions there, never in individual csproj files. `$(AspireVersion)` and `$(AspNetCoreVersion)` are the two version variables.
