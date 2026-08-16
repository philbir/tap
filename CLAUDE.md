# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Developer flow

- **Frontend changes (`src/ui-inspector/`, `src/ui-studio/`)**: always verify in a browser before reporting done. Use the Claude Preview / Chrome MCP to load the dev server, exercise the changed feature, and check the console for errors. Type-check + build passing is not enough — the UI must actually render and behave correctly.
- **Backend changes (`Tap.Hosting`, `Tap.Server`, `Tap.Studio`, AppHost)**: use Aspire exclusively for application execution and debugging. A user-started AppHost is usually already running: inspect `aspire ps --non-interactive`, reuse it, and only start a new AppHost when none is running. Rebuild and restart only the affected resource with `aspire resource <resource-name> rebuild --apphost <apphost.csproj> --non-interactive`; do not use `dotnet build`, `dotnet run`, or manual process management for application iteration.

## Build, run, test

- SDK is pinned in `global.json` to .NET 10 (`10.0.201`). Targets `net10.0` everywhere.
- `TreatWarningsAsErrors` is enabled globally (`Directory.Build.props`) — warnings break the build.
- Solution file is `Tap.slnx` (modern XML format). Use `dotnet restore Tap.slnx` only for an explicit restore; do not use `dotnet build` to run or debug the application.
- Only when no AppHost is already running, start the app with `aspire start --non-interactive --apphost <apphost.csproj>`.
- Tests live in `src/backend/Tap.Tests` (xunit v3). Run them with `dotnet test src/backend/Tap.Tests/Tap.Tests.csproj -p:SkipStudioUiBuild=true` (the skip flag avoids a full Vite build on every run). They cover the workspace parser/emitter round-trips (requests, assertions, flows, test sets), the assertion evaluator, and the response-value extractor — all pure functions, no AppHost needed.
- The `tap-studio` CLI lives in `src/backend/Tap.Studio.Cli` and ships as the `Tap.Studio.Cli` dotnet tool. Run it from source with `dotnet run --project src/backend/Tap.Studio.Cli -- test <name> --workspace samples/sample-workspace`; pack it with `dotnet pack src/backend/Tap.Studio.Cli -c Release`. It never needs the AppHost — it talks to the upstream directly, not to `studio-api`. Besides `test`/`send`/`lint`/`vars`/`migrate` it carries the agent surface: `list [kind]`, `describe <request>`, `call <METHOD> <url> -c <collection>` (dynamic request; relative URLs only unless `--allow-any-url`), each with `--json` (redacted, stdout-only document; progress/errors on stderr), and `mcp` — a stdio MCP server (official `ModelContextProtocol` SDK) exposing the same surface as tools (`workspace_inventory`, `describe_request`, `send_request`, `call_request`, `run_test`; workspace re-read per call, logs on stderr). `.mcp.json` registers it against the sample workspace; the `.claude/skills/tap-studio` skill documents the discover → describe → call loop, and `.claude/skills/tap-author` (self-contained, shippable to consumer repos) teaches agents to author workspace assets — full per-kind frontmatter spec + assertion grammar in its `references/`, kept in sync with `docs/workspace-format.md` and the parser.
- After a backend change, use `aspire resource <resource-name> rebuild --apphost <apphost.csproj> --non-interactive`; this rebuilds and restarts the resource.
- **Workspace files end in `.tap`** (`workspace.tap`, `_collection.tap`, `*.req.tap`, `*.auth.tap`, `*.env.tap`, `*.flow.tap`, `*.test.tap`). The pre-0.7.0 `.md` family still loads through 0.7.x, warning `W_LEGACY_EXTENSION` per file; removal is targeted at 0.8.0. `KindResolver` (`src/backend/Tap.Workspace/Parsing/`) is the single suffix table for both families — add nothing extension-shaped anywhere else. Reads accept either family; writes are always canonical, except that saving a file that already exists as `.md` lands in place rather than renaming it (`LoadedWorkspace.ResolveWritePath`). `tap-studio migrate` converts a workspace, renaming files *and* rewriting refs — refs are literal relative paths carrying an extension, so the two halves cannot be separated. In the UI, all suffix handling goes through `src/ui-studio/src/shell/tapFiles.ts`.
- `samples/aspire.config.json` points at `Studio.AppHost`, so `aspire start --non-interactive` from inside `samples/` selects it automatically. Pass `--apphost Sample.AppHost/Sample.AppHost.csproj` for the tunnel scenarios.
- `cloudflared` must be on PATH at AppHost start time (`brew install cloudflared` / `winget install Cloudflare.cloudflared`). The lifecycle hook shells out and fails fast if it's missing.

### UI

- **Two UIs.** `src/ui-inspector/` is the inspector (vanilla CSS modules + base-ui primitives, embedded in Tap.Server). `src/ui-studio/` is the workbench (Mantine v9.2.1 + Tabler icons + Zustand). They share zero runtime code.
- Both are yarn 4.9.1 (Berry) — use `yarn`, not `npm`. Scripts: `yarn dev` / `yarn build` / `yarn preview`. Inspector dev port is 5197; Studio dev port is 5297.
- `Tap.Server.csproj` has a `BuildTapUi` MSBuild target that runs `yarn install` + `yarn build` and copies `src/ui-inspector/dist/**` into `src/backend/Tap.Server/wwwroot/` on every server build. Set `-p:SkipTapUiBuild=true` to skip when iterating on C# only.
- For inspector hot-reload against a running AppHost: `cd src/ui-inspector && yarn dev`. Vite proxies `/api` → `VITE_INSPECTOR_API_URL` (default `http://localhost:5198`).
- For Studio hot-reload: `cd src/ui-studio && yarn dev` against the Studio.AppHost (`VITE_STUDIO_API_URL`, default `http://localhost:5298`).
- `src/backend/Tap.Server/wwwroot/*` is gitignored (regenerated artifact) — never hand-edit it.

### Studio UI conventions (`src/ui-studio/`)

- **Mantine-only**. The component library is `@mantine/core` v9.2.1 plus `@mantine/hooks`, `@mantine/modals`, `@mantine/notifications`, `@mantine/form`. Do not introduce other UI libs. Icons come from `@tabler/icons-react`. The provider stack lives in `src/main.tsx`; theme in `src/theme.ts`.
- **Skill**: `.claude/skills/mantine/SKILL.md` documents the v9 gotchas, our conventions, color tokens, and the per-kind editor recipe. Load it whenever editing `src/ui-studio/`.
- **Live docs MCP**: `.mcp.json` registers Context7 (`@upstash/context7-mcp`). Use `mcp__context7__resolve-library-id` + `get-library-docs` for authoritative v9 prop signatures.
- **State**: Zustand store at `src/store/index.ts`. Global slices (`info / tree / envs / collections / auths / knownWorkspaces / tabs / activeEnvByRoot / generation`) live there; editor-local form state stays in `useState`. UI state (open tabs + per-workspace active env) is persisted to localStorage via `persist` middleware.
- **Editor pattern**: every editor maintains a typed `spec` + `savedSpec` and PUTs to `/api/{kind}/spec` on save. The server (in `src/backend/Tap.Studio/Specs/`) is the sole producer of canonical YAML — clients never assemble YAML strings. Dirty = `JSON.stringify(spec) !== JSON.stringify(savedSpec)`. Source tab is read-only.
- **No CSS modules in editors.** Spacing via Mantine props (`mb="md"`, `gap="xs"`), layout via `<Stack>` / `<Group>` / `<SimpleGrid>`. The one exception is `VariableInput.module.css` (painted-overlay token highlighter).

## Architecture

Two product families share one repo. **Inspector** (`Tap.Hosting` + `Tap.Server` + the `Tap`
CLI) watches traffic arriving at your machine; **Studio** (`Tap.Workspace` + `Tap.Execution` +
`Tap.Studio` + the `Tap.Studio.Cli` tool) is where you author and send it. They share nothing
at runtime.

### Studio layering — the rule that matters

```
Tap.Workspace    parse · render · assert · extract     ← pure, no I/O beyond the loader
     ▲
Tap.Execution    send · auth · run flows & test sets   ← no ASP.NET Core, no Git, no UI
     ▲                        ▲
Tap.Studio              Tap.Studio.Cli
  REST + SSE + React UI    `tap-studio` dotnet tool (CI)
```

`Tap.Execution` is the engine **both** front ends run on, so a verdict from CI and a verdict
from the UI are the same computation. Two consequences to respect when editing:

- **Nothing may flow downhill.** The engine must not reference `Tap.Studio` — if a piece of
  execution logic needs something from the host, it goes behind `IWorkspaceHost` /
  `IAuthTokenSource` (`Tap.Execution/Workspace/`). `WorkspaceService` implements the former.
- **`Tap.Execution/Contracts/` is a shared public API.** The Studio serializes those records
  straight onto its SSE stream and the CLI renders them to JUnit; changing one is a breaking
  change to both. Studio-only wire shapes stay in `Tap.Studio/Contracts/Dtos.cs`.
- **The agent surface lives in `Tap.Execution/Agent/`** (backs the agent-facing CLI/MCP
  front doors): `WorkspaceInventory` (discovery read-model, auth profiles as name + type
  only), `TargetResolver` (path/name/stem → file), `AgentJson` (the one JSON dialect every
  agent surface emits), `DynamicRequestFactory` (ad-hoc requests synthesized into a
  collection; relative URLs only unless `AllowAnyUrl` — call `EnsureCollectionScoped` on the
  rendered result, since a variable can expand to an absolute URL and skip the baseUrl
  join). Any echo of a rendered request to an agent (CLI `--json`, MCP results) must go
  through `ResolvedRequest.Redactor` (`SecretRedactor`, built during render; the pipeline
  extends it with minted tokens) — never serialize `ResolvedRequest.Headers` raw.
- **`Tap.Studio.Mcp` is the shared MCP tool layer** (`TapStudioTools` +
  `IMcpWorkspaceProvider`), served twice: `tap-studio mcp` hosts it over stdio (workspace
  loaded per call, headless auth), and `Tap.Studio` maps it at `/mcp` over the live
  `WorkspaceService` (streamable HTTP; token source is the user's interactive cache, so
  PKCE-authed requests work without any credential leaving the Studio process). Tool
  contracts must not fork: change them in `Tap.Studio.Mcp`, never per host.

The rest of the repo:

- **`Tap.Hosting`** (library, namespace `Aspire.Hosting`) — extension methods consumers call from their own AppHost. Primary surface: `AddTap<T>`, `AddTapContainer`, `WithTap`, `tap.WithTunnel(name, configure)`, `tap.WithQuickTunnel`, `tap.WithTailscaleFunnel`, `WithExistingTunnel`, `WithApiManagedTunnel`, `WithDynamicHostname`, `WithSystemDaemon`/`WithEphemeralDaemon`/`WithFunnelPort` (Tailscale). Low-level escape hatches still public: `AddCloudflaredTunnel`, `AddTailscaleFunnel`, `WithCloudflareTunnel`, `WithTailscaleFunnel(target, tunnel)`. No runtime; pure AppHost wiring.
- **Tunnel abstraction** lives in `src/backend/Tap.Hosting/Tunnels/` (`TapTunnelResource` base, `TapTunnelAnnotation`, `TapTunnelIngress`). `CloudflaredTunnelResource` (multi-host) and `TailscaleFunnelResource` (single endpoint) both inherit `TapTunnelResource`. `TapHandle.AttachedTunnel` is the base type; `WithTap<T>` dispatches to provider-specific attach via `is`-check on the runtime type.
- **`Tap.Server`** (`Microsoft.NET.Sdk.Web`) — standalone ASP.NET Core app: YARP reverse proxy + capture middleware + SSE feed + bundled React UI in `wwwroot`. Reads its config from `Inspector:*` and `Cloudflare:*` env vars set by the AppHost.
- **`ui/`** — Vite + React 19 + TypeScript source for the inspector UI. Built into `Tap.Server/wwwroot` at build time; not a separate runtime artifact.
- **`samples/Sample.AppHost`** — exercises eight scenarios in parallel: standalone, Cloudflare quick / existing-dashboard / API-managed / dynamic-hostname tunnels, and Tailscale Serve in three flavors (system daemon, ephemeral userspace process, ephemeral Docker container). Each scenario is gated on the relevant user-secrets being present (`Cloudflare:*` for CF modes; `Tailscale:UseSystem=true` for system-Tailscale; `Tailscale:AuthKey` for ephemeral; pair with `Tailscale:UseDocker=true` for the container variant). `samples/Sample.Api` is the trivial upstream. Filter providers via `--scenarios cloudflare|tailscale|all` (default all).

### How a request flows

1. Internet → Cloudflare → cloudflared (existing-tunnel mode: dashboard ingress; local-ingress mode: a `config.yml` written to temp by `CloudflaredExtensions.WriteConfigYamlAsync`).
2. cloudflared → `localhost:<InspectorProxyPort>` (when a tap is attached; otherwise straight to the upstream's allocated localhost port).
3. `Tap.Server` listens on **two ports** distinguished by `app.MapWhen(ctx.Connection.LocalPort == ...)` in `Program.cs`:
   - `proxyPort` branch: `app.UseWebSockets()` then `CaptureMiddleware` records request/response into `InMemoryRequestStore` (200-record ring, 1MB body cap, image bodies stored as base64) then YARP forwards to the upstream picked by Host header. `text/event-stream` responses are detected mid-write and parsed into SSE events. WebSocket upgrade requests are intercepted before YARP — `WebSocketProxy` accepts the upgrade, opens a matching `ClientWebSocket` to the upstream (resolved from the ingress array by Host header), and pumps frames in both directions while recording each completed text/binary/close message as a `WebSocketMessage`.
   - `uiPort` branch: REST under `/api/*` (list/clear records, replay, ingress, tunnel ingress) + an SSE stream at `/api/stream` (default `data:` events for record snapshots, named `event: sse` for SSE frames, named `event: ws` for WebSocket frames) + static UI fallback to `index.html`.
4. UI subscribes to `/api/stream` (live tail) and renders detail panes — `RequestDetail` shows a **WS** tab for WebSocket records (frame timeline with direction filter) alongside the existing **SSE** tab.

### Two important Aspire-specific gotchas

- **Generic `TTapServer` parameter**: `AddTap<T>` takes a generic project-metadata type (typically `Projects.Tap_Server`). That generated type is emitted by the `Aspire.AppHost.Sdk` source generator **only in the consumer's AppHost csproj**, so the library cannot reference it directly. The consumer's AppHost csproj must therefore add a `ProjectReference` to `Tap.Server` itself (see `samples/Sample.AppHost/Sample.AppHost.csproj`). The `Tap.Hosting` reference in that same csproj uses `IsAspireProjectResource="false"` because it's a library, not a launchable project.
- **`CloudflaredLifecycleHook` runs in `BeforeStartAsync`** and is responsible for: verifying the `cloudflared` binary, calling the Cloudflare API to look up / create named tunnels, writing credentials JSON to temp, resolving zone IDs by walking up labels of the FQDN, minting dynamic hostnames, ensuring DNS CNAMEs to `<tunnelId>.cfargotunnel.com`, and back-filling generated hostnames into `CloudflareTunnelAnnotation` and the tap's ingress entries. Anything that needs a hostname before cloudflared launches belongs here, not in the extension methods.
- **`TailscaleLifecycleHook` runs in `BeforeStartAsync`** for any `TailscaleFunnelResource`. It generates a per-resource bash bootstrapper script (`tap-ts-<name>-<guid>.sh` under `Path.GetTempPath()`) — three variants: system, ephemeral process, and ephemeral Docker. Each variant waits for the daemon (host process socket OR `docker exec`), runs `tailscale up` if needed (ephemeral process only — Docker's container entrypoint does that from `TS_AUTHKEY`), pre-warms the cert, then runs `tailscale serve` (default, tailnet-only) or `tailscale funnel` (when `PublicExpose=true`). The script doesn't grep for the funnel cap — it lets `tailscale` itself report cap/ACL/HTTPS errors via stderr, which is more precise than any string-match. Each script then `tail -f /dev/null`s to keep the resource alive (`sleep infinity` is GNU-only and rejected by macOS BSD `sleep`). On exit, an EXIT trap removes the path-specific rule (`tailscale serve --set-path=/ off`) so manual rules on the same port survive. The bootstrapper echoes `TAP_TAILSCALE_HOSTNAME=<dns>` once MagicDNS resolves; a C# log watcher picks that up and back-fills `TailscaleFunnelResource.MagicDnsName`, the `TapTunnelAnnotation.Hostname`, and the tap's "proxy" URL chip via `PublishUpdateAsync`. Ephemeral mode also registers a separate `TailscaledDaemonResource` (parented to the funnel resource) so the daemon shows up in the Aspire dashboard with its own logs and shutdown ordering. Windows + ephemeral process mode is unsupported (the GUI service model gets in the way) — pair an auth key with Docker mode on Windows. The host CLI check is skipped entirely when every Tailscale tunnel is in Docker mode (no host `tailscale` binary is needed in that case).

### Configuration touch-points

- AppHost reads `Cloudflare:TunnelToken`, `Cloudflare:ApiToken`, `Cloudflare:AccountId`, `Cloudflare:Zone`, `Cloudflare:Hostnames:*`, and `Tailscale:AuthKey` from `IConfiguration`. The sample expects these via `dotnet user-secrets` (UserSecretsId `tap-sample-apphost`) — see header comment in `samples/Sample.AppHost/Program.cs`.
- `Tap.Server` consumes `Inspector:ProxyPort`, `Inspector:UiPort`, `Inspector:Mode` (`standalone`|`tunnel`), `Inspector:Provider` (`cloudflare`|`tailscale`|unset — gates provider-specific endpoints), `Inspector:Ingress` (JSON array of `{hostname, upstream}`), `Inspector:UiAllowedHosts` (comma-separated extra `Host` values accepted on the UI port — only needed when `Inspector:UiHost` is a wildcard; loopback and `UiHost` are always allowed), and optional `Cloudflare:ApiToken`/`AccountId`/`TunnelId` (used by `/api/tunnel/ingress` to mutate Cloudflare ingress rules; that endpoint returns 404 for non-Cloudflare providers). The AppHost serializes the ingress array via `WithEnvironment(ctx => ...)` so allocated upstream ports are resolved at startup, not registration.
- Default ports: proxy `5199`, UI `5198` (constants in `TapExtensions`). The sample overrides to `5299/5298` to avoid collisions if a real consumer also runs.

### Source-generated JSON

Both `Tap.Server` and `Tap.Hosting/Cloudflared/CloudflareApi.cs` use `JsonSerializerContext` partial classes (`InspectorIngressJsonContext`, `RequestRecordJsonContext`, `InspectorConfigJsonContext`, `CloudflareApiJson`). When adding a new serialized DTO, add a `[JsonSerializable(...)]` attribute to the relevant context — don't introduce reflection-based serialization.

### Central package management

`Directory.Packages.props` enables `ManagePackageVersionsCentrally`. Bump versions there, never in individual csproj files. `$(AspireVersion)` and `$(AspNetCoreVersion)` are the two version variables.
