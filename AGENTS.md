# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build, run, test

- SDK is pinned in `global.json` to .NET 10 (`10.0.201`). Targets `net10.0` everywhere.
- `TreatWarningsAsErrors` is enabled globally (`Directory.Build.props`) — warnings break the build.
- Solution file is `Tap.slnx` (modern XML format). Use `dotnet restore Tap.slnx` only for an explicit restore; do not use `dotnet build` to run or debug the application.
- Use Aspire exclusively to run and debug application resources. A user-started AppHost is usually already running: check `aspire ps --non-interactive`, reuse that existing AppHost, and only start a new one when none is running. Start an AppHost with `aspire start --non-interactive --apphost <apphost.csproj>`.
- After changing a backend resource, rebuild and restart only that resource with `aspire resource <resource-name> rebuild --apphost <apphost.csproj> --non-interactive`. Use `aspire ps --non-interactive` / `aspire describe --non-interactive` to identify the existing AppHost and resource name first.
- `samples/aspire.config.json` points at `Sample.AppHost`, so `aspire start --non-interactive` from inside `samples/` selects it automatically. There is no test project yet.
- `cloudflared` must be on PATH at AppHost start time (`brew install cloudflared` / `winget install Cloudflare.cloudflared`). The lifecycle hook shells out and fails fast if it's missing.

### UI

- `ui/` is yarn 4.9.1 (Berry) — use `yarn`, not `npm`. Scripts: `yarn dev` (Vite, port 5197), `yarn build` (`tsc -b && vite build`), `yarn preview`.
- `Tap.Server.csproj` has a `BuildTapUi` MSBuild target that runs `yarn install` + `yarn build` and copies `src/ui-inspector/dist/**` into `src/backend/Tap.Server/wwwroot/` on every server build. Set `-p:SkipTapUiBuild=true` to skip when iterating on C# only.
- For UI hot-reload against a running AppHost: `cd src/ui-inspector && yarn dev`. Vite proxies `/api` → `VITE_INSPECTOR_API_URL` (default `http://localhost:5198`); set that env var to whatever UI port the AppHost allocated.
- `src/backend/Tap.Server/wwwroot/*` is gitignored (regenerated artifact) — never hand-edit it.

## Architecture

Tap is two NuGet-style packages plus a sample, glued together by Aspire:

- **`Tap.Hosting`** (library, namespace `Aspire.Hosting`) — extension methods consumers call from their own AppHost. Primary surface: `AddTap<T>`, `AddTapContainer`, `WithTap`, `tap.WithTunnel(name, configure)`, `tap.WithQuickTunnel`, `WithExistingTunnel`, `WithApiManagedTunnel`, `WithDynamicHostname`. Low-level escape hatches still public: `AddCloudflaredTunnel`, `WithCloudflareTunnel`. No runtime; pure AppHost wiring.
- **`Tap.Server`** (`Microsoft.NET.Sdk.Web`) — standalone ASP.NET Core app: YARP reverse proxy + capture middleware + SSE feed + bundled React UI in `wwwroot`. Reads its config from `Inspector:*` and `Cloudflare:*` env vars set by the AppHost.
- **`ui/`** — Vite + React 19 + TypeScript source for the inspector UI. Built into `Tap.Server/wwwroot` at build time; not a separate runtime artifact.
- **`samples/Sample.AppHost`** — exercises five scenarios in parallel (standalone, quick tunnel, existing dashboard tunnel, API-managed tunnel, dynamic-hostname tunnel) gated on which user-secrets are present. `samples/Sample.Api` is the trivial upstream.

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

### Configuration touch-points

- AppHost reads `Cloudflare:TunnelToken`, `Cloudflare:ApiToken`, `Cloudflare:AccountId`, `Cloudflare:Zone`, `Cloudflare:Hostnames:*` from `IConfiguration`. The sample expects these via `dotnet user-secrets` (UserSecretsId `tap-sample-apphost`) — see header comment in `samples/Sample.AppHost/Program.cs`.
- `Tap.Server` consumes `Inspector:ProxyPort`, `Inspector:UiPort`, `Inspector:Mode` (`standalone`|`tunnel`), `Inspector:Ingress` (JSON array of `{hostname, upstream}`), and optional `Cloudflare:ApiToken`/`AccountId`/`TunnelId` (used by `/api/tunnel/ingress` to mutate Cloudflare ingress rules). The AppHost serializes the ingress array via `WithEnvironment(ctx => ...)` so allocated upstream ports are resolved at startup, not registration.
- Default ports: proxy `5199`, UI `5198` (constants in `TapExtensions`). The sample overrides to `5299/5298` to avoid collisions if a real consumer also runs.

### Source-generated JSON

Both `Tap.Server` and `Tap.Hosting/Cloudflared/CloudflareApi.cs` use `JsonSerializerContext` partial classes (`InspectorIngressJsonContext`, `RequestRecordJsonContext`, `InspectorConfigJsonContext`, `CloudflareApiJson`). When adding a new serialized DTO, add a `[JsonSerializable(...)]` attribute to the relevant context — don't introduce reflection-based serialization.

### Central package management

`Directory.Packages.props` enables `ManagePackageVersionsCentrally`. Bump versions there, never in individual csproj files. `$(AspireVersion)` and `$(AspNetCoreVersion)` are the two version variables.
