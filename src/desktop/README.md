# Tap Studio Desktop

Tauri 2 shell that wraps the `Tap.Studio` ASP.NET sidecar. The Rust shell spawns
the sidecar on launch, parses a one-line JSON handshake from its stdout to learn
the bound URL, then navigates the webview to `http://127.0.0.1:<port>`. The SPA
already lives inside the sidecar's `wwwroot` (built by `BuildStudioUi` in
`src/backend/Tap.Studio/Tap.Studio.csproj`), so the webview is single-origin with the
API and `fetch('/api/...')` Just Works without any URL plumbing.

## Layout

```
src/desktop/
├── package.json            # @tauri-apps/cli wrapper scripts
├── src/
│   ├── index.html          # splash shown until the sidecar reports ready
│   └── splash.js           # renders startup phases + the failure card
└── src-tauri/
    ├── Cargo.toml
    ├── tauri.conf.json     # bundle, externalBin (sidecar), deep-link config
    ├── build.rs
    ├── capabilities/
    │   ├── default.json    # granted to the sidecar's http origin
    │   └── splash.json     # local-only: retry / reveal-log commands
    ├── icons/              # see "Icons" below
    ├── binaries/           # generated — sidecar dropped here per host triple
    └── src/
        ├── main.rs
        └── lib.rs          # sidecar spawn + handshake + deep-link forwarder
```

## Dev modes

The shell talks to a backend chosen at launch via the `STUDIO_DESKTOP_URL` env var:

- **Packaged / standalone** (`STUDIO_DESKTOP_URL` unset) — spawns the bundled
  self-contained Tap.Studio sidecar and points the webview at its
  `studio.ready` URL. This is what a shipped `.app`/`.msi` does.
- **Aspire dev** (`STUDIO_DESKTOP_URL` set) — skips the sidecar and points the
  webview straight at the URL, reusing an already-running backend.

The `Studio.AppHost` can launch the shell for you so `aspire run` brings up the
whole desktop loop (demo-api + studio-api + studio-ui + the native window). It's
off by default; enable with `RunDesktop=true`:

```bash
cd samples && RunDesktop=true aspire run
```

That adds a `studio-desktop` resource which runs `yarn --cwd src/desktop dev`
with `STUDIO_DESKTOP_URL` set to the studio-ui endpoint — so you get Vite hot
reload and the same Aspire-managed backend the browser dev loop uses. No
`compile-server` needed in this mode.

## Build

Prereqs: Rust toolchain (`rustup default stable`), Node 22 + yarn 4 (`corepack enable`),
.NET 10 SDK (matches `global.json`).

```bash
# from repo root — convenience wrapper for local dev
scripts/build-desktop.sh           # publish sidecar + yarn tauri build
scripts/build-desktop.sh --dev     # publish sidecar + yarn tauri dev
```

CI uses the underlying Node scripts directly so a Windows runner can drive the
whole pipeline without bash:

```bash
node src/desktop/scripts/compile-server.mjs <triple>
yarn --cwd src/desktop tauri build --target <triple> --bundles ...
```

Supported triples: `aarch64-apple-darwin`, `x86_64-apple-darwin`,
`x86_64-unknown-linux-gnu`, `aarch64-unknown-linux-gnu`, `x86_64-pc-windows-msvc`.
`compile-server.mjs` maps each to the matching .NET RID, calls
`dotnet publish` (single-file, self-contained, no compression — see csproj
comment for why), and stages the binary + `wwwroot/` into `src-tauri/binaries/`.

Output bundles land under `src/desktop/src-tauri/target/<triple>/release/bundle/`.

## Icons

Tauri expects `icons/{32x32,128x128,128x128@2x}.png`, `icons/icon.icns` (macOS),
and `icons/icon.ico` (Windows). Generate them once from the existing source:

```bash
cd src/desktop
yarn tauri icon ../../assets/tap-studio-icon.svg
```

The generated files are committed (icons are stable across builds).

## OAuth callback (deep link)

The shell registers the `tap-studio://` URL scheme via the deep-link plugin and
sets `TAP_STUDIO_DESKTOP=1` on the sidecar. With that env var set,
`AuthRunner.ResolveCallbackUri()` returns `tap-studio://callback` instead of the
ephemeral `http://localhost:<random>/api/auth/callback`, so IdPs can register a
stable redirect URI.

When the OS routes a `tap-studio://callback?...` URL back to the desktop app,
the shell's deep-link handler forwards the query string to
`<api>/api/auth/callback?...` via the webview's `fetch`, which runs same-origin
with the sidecar and the existing flow completes.

## Release pipeline

`.github/workflows/desktop.yml` mirrors the [mango](https://github.com/philbir/mango)
pattern: tag-triggered matrix (`macos-14` arm64, `ubuntu-22.04` x86_64,
`windows-latest` x86_64), tauri-action handles bundling + code-signing + the
GitHub Release, then a `publish-updater` job composes `latest.json` from the
per-platform `.sig` files and uploads it for the auto-updater client.

To cut a release: tag the repo `0.2.0` (no `v` prefix) and push — the workflow
does the rest.

### One-time setup

Before the first signed/notarized release, populate the following repo secrets
(Settings → Secrets and variables → Actions):

**macOS — Developer ID Application signing**

| Secret                       | How to get it                                           |
| ---------------------------- | ------------------------------------------------------- |
| `APPLE_CERTIFICATE`          | `base64 -i developer-id.p12`                            |
| `APPLE_CERTIFICATE_PASSWORD` | password used when exporting the .p12                   |
| `APPLE_SIGNING_IDENTITY`     | `Developer ID Application: <Name> (<TEAMID>)`           |
| `APPLE_TEAM_ID`              | 10-char team identifier                                 |

**macOS — App Store Connect API key for notarization** (preferred over Apple ID + app-specific password)

| Secret                  | How to get it                                                       |
| ----------------------- | ------------------------------------------------------------------- |
| `APPLE_API_ISSUER`      | App Store Connect → Users and Access → Keys → Issuer ID             |
| `APPLE_API_KEY`         | key ID for the AuthKey                                              |
| `APPLE_API_KEY_BASE64`  | `base64 -i AuthKey_<key>.p8` — workflow decodes to `~/.private_keys/AuthKey.p8` |

**Updater signing** — required for auto-updates to work; without it the updater client rejects every artifact.

The public key is already committed in `src-tauri/tauri.conf.json` (`plugins.updater.pubkey`). Only the matching private key needs to be added as a repo secret:

| Secret                       | Value                                                    |
| ---------------------------- | -------------------------------------------------------- |
| `TAURI_SIGNING_PRIVATE_KEY`  | contents of the generated `tap-studio-updater.key` file  |
| `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` | the key's passphrase — omit if generated without one |

To rotate the keypair, regenerate and replace both the committed pubkey and the secret:

```bash
npx @tauri-apps/cli signer generate -w ~/.tauri/tap-studio.key
# Public key → src-tauri/tauri.conf.json plugins.updater.pubkey
# Private key (contents of ~/.tauri/tap-studio.key) → TAURI_SIGNING_PRIVATE_KEY secret
```

Until the secrets are populated the workflow still produces ad-hoc-signed
bundles — users get a Gatekeeper prompt on first launch on macOS but the app
runs.

## Sidecar handshake protocol

With `TAP_STUDIO_EMIT_READY=1` the sidecar narrates its startup on stdout as
newline-delimited JSON — one event per line, every one carrying `event` and
`elapsedMs` (see `Tap.Studio/StartupSignal.cs`):

```json
{"event":"studio.progress","phase":"host.building","message":"Configuring the Studio host","elapsedMs":0}
{"event":"studio.progress","phase":"workspace.loading","message":"Loading workspace /Users/me/apis","elapsedMs":94}
{"event":"studio.progress","phase":"workspace.loaded","message":"Loaded 37 workspace file(s) in 62 ms","elapsedMs":156}
{"event":"studio.progress","phase":"server.starting","message":"Binding to localhost:0","elapsedMs":162}
{"event":"studio.ready","url":"http://127.0.0.1:54123","pid":42,"elapsedMs":175}
```

`studio.ready` is the one that matters: the shell navigates the webview at `url`.
The rest exist so a launch that *doesn't* get there can say how far it got —
`studio.error` reports a failure the sidecar could describe itself. Lines that
don't parse as an event are logged verbatim, so adding a new event is backward
compatible with an older shell.

## Startup, when it goes wrong

The shell used to sit on "Spawning sidecar" forever if the handshake never
arrived. It now tracks startup as phases and shows the current one:

- **Timeout** — no `studio.ready` within 45s (`TAP_STUDIO_STARTUP_TIMEOUT_SECS`
  overrides; `0` waits forever) swaps the spinner for a failure card naming the
  last phase reached. The sidecar is *not* killed — a genuinely slow first scan
  still finishes and navigates, and the card disappears when it does.
- **Sidecar exits early** — reported immediately with its exit code/signal and
  the tail of its output, instead of waiting out the timeout.
- **PATH probe** — `resolve_user_path` runs the user's *interactive* login shell
  to recover a real `PATH` for `az`/`gh`/`op`. That is arbitrary user code, so it
  runs under a 5s deadline; past it the launch continues with the inherited PATH
  and says so in the log.
- **Session log** — everything above plus all sidecar output goes to
  `<app log dir>/studio.log` (macOS: `~/Library/Logs/dev.philbir.tap-studio/`),
  with the previous session kept as `studio.prev.log` and a 4 MB ceiling. The
  failure card shows the path, can reveal it in the file manager, and can copy a
  diagnostics blob.
- **Try again** respawns the sidecar without restarting the app.

The failure card lives in `src/index.html` + `src/splash.js`; the Rust side pushes
status into it with `window.eval` (the page has no bundler, and the payload is a
serde-produced JSON literal, never hand-quoted text). Its two commands
(`studio_retry`, `studio_open_log`) are granted in `capabilities/splash.json`,
which — unlike `default.json` — is local-context only, so the sidecar-origin SPA
cannot fork extra backends.

To exercise the failure path locally:

```bash
TAP_STUDIO_STARTUP_TIMEOUT_SECS=1 yarn --cwd src/desktop tauri dev
```
