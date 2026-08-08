# Tap Studio

> The HTTP workbench: compose requests, authenticate against real identity providers, execute
> them, and keep the whole thing in your repo as reviewable Markdown.

Tap Studio is the second half of Tap. Where the [Inspector](inspector.md) watches traffic that
someone *else* sends to your machine, Studio is where **you** author and send the traffic —
a request composer with first-class authentication, an AI assistant, and a workspace that is
plain text in your git repo.

- On-disk format: [workspace-format.md](workspace-format.md)
- Backend: `src/backend/Tap.Studio` (ASP.NET Core, REST + SSE)
- Workspace model: `src/backend/Tap.Workspace`
- UI: `src/ui-studio` (React 19 + Mantine v9)
- Desktop shell: `src/desktop` (Tauri 2) — see [src/desktop/README.md](../src/desktop/README.md)

---

## Contents

- [Why another HTTP client](#why-another-http-client)
- [Install and run](#install-and-run)
- [The workspace](#the-workspace)
- [Composing a request](#composing-a-request)
- [Authentication](#authentication)
- [Variables, environments, and secrets](#variables-environments-and-secrets)
- [AI assistant](#ai-assistant)
- [Git](#git)
- [Importing from Postman](#importing-from-postman)
- [Desktop app](#desktop-app)
- [Configuration](#configuration)

---

## Why another HTTP client

Three things Studio does differently:

1. **Your workspace is your repo.** Every request, collection, auth profile, and environment is
   a Markdown file with YAML frontmatter, checked in beside the code it calls. Renames are
   `git mv`. Reviews are ordinary diffs. No proprietary export format, no sync service.
2. **Authentication is a first-class flow, not a header you paste.** Studio runs OAuth 2.0 /
   OIDC (authorization code + PKCE, client credentials, ROPC, device code), Microsoft Entra,
   Azure CLI (direct + on-behalf-of), GitHub (PAT / `gh` CLI / GitHub App / OAuth App),
   AWS SigV4, and self-signed JWTs. Tokens land in your OS state folder, never in the repo.
3. **Secrets never enter the workspace.** Values come from variable providers at execute time.
   A workspace file can only ever contain a *reference*.

![Tap Studio request composer](../assets/screenshots/studio-request.png)

---

## Install and run

### Desktop app (recommended)

Studio ships as a native desktop app — a [Tauri 2](https://tauri.app) shell wrapping the
self-contained `Tap.Studio` sidecar. Download the bundle for your platform from
[GitHub Releases](https://github.com/philbir/tap/releases):

| Platform | Artifact |
|---|---|
| macOS (Apple Silicon / Intel) | `.dmg` / `.app` |
| Windows | `.msi` and NSIS `.exe` setup |
| Linux | `.deb` |

The app self-updates: each release publishes a signed `latest.json` the updater client polls.

### From source

```bash
# whole dev loop: demo API + Studio backend + Vite UI (+ optional desktop shell)
cd samples
aspire run
```

`samples/aspire.config.json` points at `Studio.AppHost`, which starts:

| Resource | What it is |
|---|---|
| `demo-api` | `samples/Demo.Api` — the canonical upstream. Every verb, every content type, plus SSE, WebSockets, GraphQL, and an OpenIddict OAuth2/OIDC server. |
| `studio-api` | `Tap.Studio` — REST + SSE backend. |
| `studio-ui` | Vite dev server for `src/ui-studio` (port 5297). |
| `studio-desktop` | Optional Tauri window over `studio-ui`. Off by default; `RunDesktop=true aspire run`. |

Point it at your own repo instead of the bundled sample workspace:

```bash
STUDIO_WORKSPACE=/path/to/your/repo aspire run
```

To build the desktop bundle locally:

```bash
scripts/build-desktop.sh          # publish sidecar + tauri build
scripts/build-desktop.sh --dev    # publish sidecar + tauri dev
```

---

## The workspace

A workspace is a directory containing a `tap.md` manifest. Everything in it is meant to be
committed. Five file kinds, all Markdown + YAML frontmatter:

| Kind | File | Owns |
|---|---|---|
| `workspace` | `tap.md` | Name, default environment, variable providers, workspace-wide vars. |
| `collection` | `collections/<slug>/_collection.md` | Base URL, named stages, default auth, default headers, collection vars. |
| `request` | `*.req.md` | One HTTP (or WebSocket) call, as a fenced `http` block. |
| `auth` | `auth/*.auth.md` or `collections/<slug>/*.auth.md` | A reusable authentication profile — shared workspace-wide, or owned by one collection. |
| `env` | `environments/*.env.md` | A named set of variables. |

```
my-service/
├── src/                          ← your code
└── .tap/
    ├── tap.md
    ├── auth/
    │   ├── stripe-bearer.auth.md
    │   └── corp-entra.auth.md
    ├── environments/
    │   ├── local.env.md
    │   └── prod.env.md
    └── collections/
        └── stripe/
            ├── _collection.md    ← baseUrl, stages, default auth/headers
            ├── stripe-oauth.auth.md  ← auth owned by this collection
            ├── create-customer.req.md
            └── refunds/          ← plain grouping folder, no metadata
                └── issue.req.md
```

A runnable request is the *composition* of workspace + collection (+ stage) + auth +
environment + request. Sub-folders inside a collection are pure grouping — they carry no
metadata and no inheritance.

The [workspace format spec](workspace-format.md) is the authoritative reference for every
frontmatter field, the variable cascade, and the canonical parse-error codes.

Studio is the sole producer of the YAML: editors PUT a typed spec to `/api/{kind}/spec` and
the server re-emits the file. The **Source** tab shows the resulting Markdown read-only, so
what you review in git is exactly what the editor wrote.

---

## Composing a request

The request editor is one URL bar plus seven tabs:

| Tab | What it holds |
|---|---|
| **Params** | Query string, edited as key/value rows. Kept in sync with the URL bar. |
| **Headers** | Request headers, with name completion for the common ones. |
| **Body** | `None` / `Form` / `Multipart` / `Raw` / `Binary` / `GraphQL`. Raw bodies get JSON / Text / XML syntax modes and a Format button; multipart supports multiple files. |
| **Auth** | Which auth profile applies — inherited from the collection, overridden here, or `none`. |
| **Variables** | Request-scoped variables with descriptions, defaults, and `required` flags. |
| **Meta** | Name, tags, protocol (`http` / `websocket`). |
| **Docs** | Markdown documentation rendered under the request. This is the request file's body. |
| **Source** | The generated `*.req.md`, read-only. |

The URL bar splits the collection's base URL from the path, so a request stores
`/v1/customers` and picks up `https://api.stripe.com` (or the active stage's override) at
render time. `{{variable}}` tokens are highlighted inline wherever they appear.

**Executing.** Send runs the composed request and opens the response panel: status, duration,
size, and content type in the header, with tabs for **Body** (syntax-highlighted, with image
and binary previews), **Headers**, **Request** (exactly what went on the wire), **Flow** (the
auth + variable steps taken to get there), and **Secrets** (which secret references were
resolved — names and providers, never values).

Requests that don't define a `User-Agent` — on the request, the collection's
`defaultHeaders`, or the auth profile — go out as `tap-studio/<version>`. Set the header
anywhere in that chain to override it; the **Request** tab always shows the one that was sent.

**Streaming.** `text/event-stream` responses stream in live. Requests marked
`protocol: websocket` open a real WebSocket — the body, if any, is sent as the first text
frame and inbound frames append as they arrive.

**GraphQL.** The GraphQL body mode fetches the endpoint's schema, so queries, mutations, and
variables get completion and validation against the live server.

---

## Authentication

Studio's auth profiles are reusable files (`*.auth.md`) referenced by requests or set as a
collection default. Creating one starts in a three-step wizard — pick a scope + template,
fill the required fields, review — so you don't have to know the underlying field names.

![The auth-flow catalog](../assets/screenshots/studio-auth-wizard.png)

### Workspace or collection scope

The **Scope** picker in the create dialog decides where the profile is written, and that
decides which variables it can use:

- **Workspace** (`auth/`) — shared by every collection, resolves workspace + environment
  variables. The right choice for a credential several APIs share.
- **A collection** (`collections/<slug>/`) — owned by that collection, and additionally
  resolves its variables and the selected stage's. Use it when the token URL, client id, or
  audience already lives on the collection: switching stage (`dev` → `prod`) repoints the
  profile with no edit. Tokens are cached per stage, so the two never mix.

Collection-scoped profiles appear in the **Requests** tree under their collection, next to
the requests that use them, and in the **Auth** tab grouped under the collection name.
Every auth picker (request `auth:`, collection/stage `defaultAuth`) groups its options the
same way, so it's always clear which scope a profile came from.

| Template | What it does |
|---|---|
| **GitHub Personal Access Token** | Classic or fine-grained PAT against the GitHub REST/GraphQL API. |
| **GitHub CLI** | Reuses whichever account `gh auth login` is signed in as on this machine. |
| **GitHub App** | Mints an RS256 JWT from the App private key, exchanges it for an installation token. |
| **GitHub OAuth App** | Interactive sign-in against github.com, for tools acting as a user. |
| **Bearer token** | Static `Authorization: Bearer …`. |
| **Basic auth** | Username + password, base64 into `Authorization`. |
| **API key** | A fixed header (e.g. `X-API-Key`) on every request. |
| **OAuth 2.0 / OIDC** | Generic OAuth — endpoints by hand or via `.well-known` discovery. |
| **Microsoft Entra (Azure AD)** | Pre-fills the AAD v2 authority; you supply tenant + client id. |
| **Azure CLI** | Shells out to `az account get-access-token`; `direct` or on-behalf-of. |
| **Signed JWT** | Mints a self-signed JWT per call, for service-to-service patterns. |
| **AWS Signature V4** | Signs requests with AWS access keys. |
| **Custom headers** | Free-form header/cookie injection. |

### OAuth 2.0 grants

`OAuth 2.0 / OIDC` and `Microsoft Entra` support:

| Grant | Notes |
|---|---|
| `authorization_code` (with PKCE) | Opens your browser, catches the callback, exchanges the code. |
| `client_credentials` | Straight machine-to-machine exchange. |
| `password` (ROPC) | Username + password against the token endpoint. |
| `device_code` | For inputs you can't type into. |

Turn on **Use Discovery** and Studio fetches `/.well-known/openid-configuration` from the
authority to fill in the token, authorize, and device endpoints.

![OAuth 2.0 authorization code + PKCE profile](../assets/screenshots/studio-auth-oauth2.png)

### Tokens and redirect URIs

- **Tokens are never written to the workspace.** Access tokens, refresh tokens, and expiry live
  in `~/.tap/auth-tokens.json`, keyed by workspace root + profile path, tightened to `0600`
  where the OS supports it. Refresh happens automatically; explicit logout removes the entry.
- **The redirect URI is owned by the runtime, not the file.** Studio derives it from its own
  base URL and shows it read-only — register that value with your identity provider. In the
  desktop app it is the stable deep link `tap-studio://callback` instead of an ephemeral
  loopback port.
- **You choose the browser.** Interactive flows let you pick which installed browser (and
  profile) handles the sign-in, so a work tenant doesn't land in your personal session.

**Try it** in the auth editor runs the flow on its own and shows you the token that comes back
— you can prove a profile works before wiring it to a request.

---

## Variables, environments, and secrets

A token is either `{{name}}` — resolved from the scope cascade first, then across variable
providers in registration order — or `{{provider:name}}`, resolved only against that provider.
Write `\{{` for a literal brace pair.

The cascade, lowest precedence first:

1. `tap.md` `vars`
2. the owning `_collection.md` `vars`
3. the active collection **stage**
4. the active `*.env.md`
5. the request's own `vars`
6. per-run overrides

Providers are declared in `tap.md` under `variableProviders` (a workspace provider shadows a
same-named system one):

| Type | Source |
|---|---|
| `env` | Process environment, gated by two allowlists — `TAP_VARS_ALLOWED` (names whose values may be *shown*) and `TAP_SECRETS_ALLOWED` (names that stay masked but resolve at execute time). Both take comma-separated globs; unset means deny-everything. |
| `file` | `.tap/.vars/<provider>.yml` in the workspace. Values marked `secret: true` are encrypted at rest with AES-256-GCM under a passphrase-derived key. |
| `azkv` | Azure Key Vault, via `DefaultAzureCredential`. |
| `1password` | 1Password, via the local [`op` CLI](https://www.1password.dev/cli), which must be installed. **Read-only** — secrets are created and rotated in 1Password itself, where the audit trail and sharing rules live. `mode` picks one of three shapes: **`environment`** — a [1Password Environment](https://www.1password.dev/environments)'s variables become the provider's (needs the beta CLI, 2.38.2-beta.01+); **`item`** — one item's *fields* become the variables, named after the field labels; **`vault`** — every item in a vault becomes a variable named after its title, valued by its `field` (default: the item's password/credential). Reads use whatever session `op` already has (desktop app integration, `op signin`) — set `serviceAccountToken` only for headless hosts. The settings form switches modes, browses your vaults, and auto-detects the `op` binary. |
| `system` | Variables stored in Studio's own `system.json`, edited from Settings. Machine-local, outside the repo. |

Every execution records which variables and secrets were touched — provider, name, and whether
it was secret — so the **Secrets** tab can show what a request depends on without ever
surfacing a value.

---

## AI assistant

Studio can hand the request you're editing to an AI coding CLI you already have installed and
let it propose changes.

![The AI assistant proposing a request edit](../assets/screenshots/studio-assistant.png)

- **Providers:** GitHub Copilot CLI (`copilot`) or Claude Code (`claude`). Studio spawns your
  installed binary per request rather than bundling an SDK, so there is no extra native
  dependency and your existing CLI authentication is reused as-is. Pick the provider and the
  model in Settings; `/api/ai/status` reports what was detected and what still needs setup.
- **It sees your workspace, not just your prompt.** The system prompt carries the current
  request spec, the owning collection (base URL, default auth, shared headers, stages), the
  available auth profiles, the environment names, and the variable catalog with scopes and
  secret flags — so it references things that actually exist instead of inventing them.
- **It proposes, you apply.** The assistant never writes files. It returns a structured request
  spec that the UI previews and applies to the editor as an *unsaved* change; you review the
  diff and press Save. Re-apply is one click if you want it back.
- **It writes the docs too.** New or meaningfully changed requests come back with Markdown
  documentation for the request body, and it is told to refine existing notes rather than
  overwrite them.
- **It won't inline secrets.** The prompt requires `{{variable}}` references for anything
  sensitive.

---

## Git

Studio has a built-in git view because the workspace *is* a git repo: branch, changed files,
staging, diff, and commit.

![A request edit as an ordinary git diff](../assets/screenshots/studio-git.png)

Because requests are Markdown, a change to a request is a change to a couple of lines of text
— which is what makes review, blame, cherry-pick, and revert work the way they do for code.

---

## Importing from Postman

`POST /api/collections/import/postman` (Create → Collection → *Import from Postman* in the UI)
converts a Postman collection export into a Tap collection: one `_collection.md` plus a
`*.req.md` per request, with folders preserved as grouping directories.

---

## Desktop app

The Tauri shell spawns the `Tap.Studio` sidecar, reads a one-line JSON handshake from its
stdout to learn the bound URL, and points the webview at it. The SPA is served by the sidecar
itself, so the UI and the API are same-origin and `fetch('/api/...')` needs no plumbing.

Two things the desktop build changes:

- **OAuth callbacks** use the registered `tap-studio://` URL scheme, so identity providers get
  a stable redirect URI to register instead of a random loopback port.
- **Updates** are signed and delivered through Tauri's updater.

Full build, signing, and release details: [src/desktop/README.md](../src/desktop/README.md).

---

## Configuration

| Key | Purpose |
|---|---|
| `Studio:WorkspaceRoot` | Workspace to open on boot. The active workspace is then remembered in `workspaces.json`. |
| `Studio:Port` / `Studio:Host` | Where Kestrel binds. Default `localhost:5298`. |
| `Studio:StatePath` | State database path. Default `<system dir>/state.db`. |
| `Studio:VariableProviders:[]` | System-level variable providers, same shape as workspace ones (`name`, `type`, `mode`, `settings`). |
| `TAP_SYSTEM_DIR` | Overrides the state folder (default `~/.tap`) — holds `state.db`, `workspaces.json`, `system.json`, and `auth-tokens.json`. |
| `TAP_VARS_ALLOWED` | Comma-separated globs of environment variable names the `env` provider may expose in cleartext. |
| `TAP_SECRETS_ALLOWED` | Comma-separated globs of environment variable names the `env` provider may resolve while keeping them masked. |
| `TAP_STUDIO_DESKTOP` | Set by the desktop shell; switches the OAuth callback to `tap-studio://callback`. |
| `TAP_STUDIO_EMIT_READY` | Makes the sidecar print its `studio.ready` handshake line on stdout. |
