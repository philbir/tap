# Tap Studio

![Tap Studio icon](../assets/tap-studio-icon.svg)

> The HTTP request and auth credential crafter: compose, authenticate, execute, and keep the whole
> thing in your repo as reviewable Markdown.

Tap Studio is the outbound half of Tap Platform. Where [Tap Tunnels](inspector.md) routes traffic
to your machine and its built-in Inspector shows what arrived, Studio is where **you** craft and
send the traffic—a request composer with first-class authentication, an AI assistant, and a
workspace that is plain text in your git repo.

![Tap Studio request and credential craft workbench](../assets/tap-studio-workbench-hero.png)

- On-disk format: [workspace-format.md](workspace-format.md)
- Backend: `src/backend/Tap.Studio` (ASP.NET Core, REST + SSE)
- Workspace model: `src/backend/Tap.Workspace`
- UI: `src/ui-studio` (React 19 + Mantine v9)
- Desktop shell: `src/desktop` (Tauri 2) — see [src/desktop/README.md](../src/desktop/README.md)

---

## Contents

- [Why another HTTP client](#why-another-http-client)
- [Install and run](#install-and-run)
- [Run it from your Aspire AppHost](#run-it-from-your-aspire-apphost)
- [The workspace](#the-workspace)
- [Composing a request](#composing-a-request)
- [Assertions](#assertions)
- [Tests and flows](#tests-and-flows)
- [Running tests from CI](#running-tests-from-ci)
- [AI agents](#ai-agents)
- [Authentication](#authentication)
- [Variables, environments, and secrets](#variables-environments-and-secrets)
- [AI assistant](#ai-assistant)
- [Git](#git)
- [Importing from Postman](#importing-from-postman)
- [Importing from OpenAPI](#importing-from-openapi)
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

## Run it from your Aspire AppHost

If your solution already has an Aspire AppHost, Studio can run as a companion resource of it —
started, health-checked, and linked from the dashboard alongside your APIs, pointed at a
workspace folder that lives in the repo.

```csharp
var orders  = builder.AddProject<Projects.Orders_Api>("orders-api");
var billing = builder.AddProject<Projects.Billing_Api>("billing-api");

var studio = builder.AddTapStudio<Projects.Tap_Studio>()
    .WithWorkspaceFolder("tap")   // default; resolved against the AppHost directory
    .WithApi(orders)
    .WithApi(billing);
```

That single call does the following:

- Starts Studio in **aspire mode**: the workspace is pinned to `tap/`, the workspace switcher is
  locked, and the header shows an Aspire badge. Pinning matters — without it Studio would open
  whichever workspace that developer last used on that machine.
- Waits for each API, health-checks Studio at `/health`, and adds it to the dashboard as
  **Tap Studio**.
- Injects the standard service-discovery variables for every `WithApi`, so a collection can say
  `baseUrl: '{{aspire:orders-api}}'` and hit whatever port was allocated this run (§12.2 of
  [workspace-format.md](workspace-format.md)).

`WithApi(api, waitFor: false)` opts out of waiting for a slow-starting API.

### csproj requirements

Two references in the **AppHost** project:

```xml
<!-- Makes Aspire's source generator emit Projects.Tap_Studio. -->
<ProjectReference Include="path/to/Tap.Studio.csproj" />

<!-- The hosting library. IsAspireProjectResource=false because it is a library,
     not a launchable project. -->
<ProjectReference Include="path/to/Tap.Hosting.csproj" IsAspireProjectResource="false" />
```

Building `Tap.Studio` builds its React UI, so this route needs **yarn on PATH**.

### Or run the image instead

`AddTapStudioContainer` runs the published `ghcr.io/philbir/tap-studio` image in place of a
compiled project — no `ProjectReference`, no yarn, nothing to build, and Aspire pulls the image
as part of starting the AppHost:

```csharp
var studio = builder.AddTapStudioContainer()
    .WithWorkspaceFolder("tap")   // bind-mounted into the container at /workspace
    .WithApi(orders);
```

It returns the same `TapStudioHandle`, so `WithApi`, `WithWorkspaceFolder`, `CallbackUrl`, and
the rest behave identically. Three things differ:

- **The workspace is a bind mount.** `WithWorkspaceFolder` points the mount at that folder and
  creates it if it does not exist — Docker would otherwise create it owned by root, and the
  workspace Studio scaffolds into it would not be writable from your editor.
- **State lives in a named volume** (`<name>-state`, mounted at `/state`), so the OAuth token
  cache and `system.json` survive a restart. Pass `persistState: false` to make it ephemeral.
- **A container is not your machine.** The AI assistant cannot spawn the coding CLI you have
  installed, an interactive OAuth flow cannot open your browser, and provider back ends that
  shell out to a local binary (1Password's `op`, the Azure CLI) are not present. Use the project
  route when you want those; use the image for a workspace that authenticates headlessly.

The endpoint is published on loopback only and the Host allowlist is pinned to match; pass
`exposeOnAllInterfaces: true` to widen it deliberately.

The image tag defaults to **the hosting library's own version**, not `latest` — the two are
published from the same git tag, so they always pair. `latest` is only ever tagged for stable
releases, so defaulting to it would leave `AddTapStudioContainer()` unable to pull on a preview.
Pass `tag:` to pin something else. The default pull policy is `Missing`, so a locally-built tag
of the same name is used as-is:

```bash
docker build -t ghcr.io/philbir/tap-studio:local -f src/backend/Tap.Studio/Dockerfile .
StudioContainer=true StudioImageTag=local aspire run   # samples/Studio.AppHost
```

### Seeding an OAuth client

Studio's redirect URI is only known once its port is allocated, so it is exposed as a
`ReferenceExpression`:

```csharp
identity.WithEnvironment("STUDIO_CALLBACK_URL", studio.CallbackUrl);
```

### The same workspace in CI

Nothing about the workspace is Aspire-specific. `{{aspire:orders-api}}` reads the standard
`services__<resource>__<scheme>__<index>` variables, so CI runs it by exporting them:

```bash
export services__orders-api__https__0=https://staging.example.com
tap-studio test "Orders smoke"
```

### While it's running

The workspace switcher is locked and the header shows an **Aspire** badge — the folder is part of
the solution's definition rather than a per-user preference, so changing it means changing
`WithWorkspaceFolder(...)`.

The header also links to the **desktop app**. A Studio started by an AppHost lives in a browser tab
that goes away with `aspire run`; the desktop app opens the same workspace folder without the
AppHost having to be up. Download it from
[the install docs](https://philbir.github.io/tap/#studio-install) or straight from
[GitHub Releases](https://github.com/philbir/tap/releases/latest).

A worked example lives in [`samples/Studio.AppHost`](../samples/Studio.AppHost/Program.cs).

## The workspace

A workspace is a directory containing a `workspace.tap` manifest. Everything in it is meant to be
committed. Seven file kinds, all Markdown + YAML frontmatter:

| Kind | File | Owns |
|---|---|---|
| `workspace` | `workspace.tap` | Name, default environment, variable providers, workspace-wide vars. |
| `collection` | `collections/<slug>/_collection.tap` | Base URL, named stages, default auth, default headers, collection vars. |
| `request` | `*.req.tap` | One HTTP (or WebSocket) call, as a fenced `http` block. |
| `auth` | `auth/*.auth.tap` or `collections/<slug>/*.auth.tap` | A reusable authentication profile — shared workspace-wide, or owned by one collection. |
| `env` | `environments/*.env.tap` | A named set of variables. |
| `flow` | `tests/*.flow.tap` | Requests run in order, passing values from one response to the next. |
| `test` | `tests/*.test.tap` | A set of checks, each running one request or one flow. |

```
my-service/
├── src/                          ← your code
└── .tap/
    ├── workspace.tap
    ├── auth/
    │   ├── stripe-bearer.auth.tap
    │   └── corp-entra.auth.tap
    ├── environments/
    │   ├── local.env.tap
    │   └── prod.env.tap
    ├── tests/
    │   ├── billing.test.tap   ← a set of checks
    │   └── checkout.flow.tap  ← requests in order, values carried across
    └── collections/
        └── stripe/
            ├── _collection.tap    ← baseUrl, stages, default auth/headers
            ├── stripe-oauth.auth.tap  ← auth owned by this collection
            ├── create-customer.req.tap
            └── refunds/          ← plain grouping folder, no metadata
                └── issue.req.tap
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
| **Source** | The generated `*.req.tap`, read-only. |

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

## Assertions

A request can declare what a passing response looks like. The **Asserts** tab holds one row
per expectation, built from dropdowns rather than free text: pick what to read from the
response (status, duration, a header, the body, a JSONPath, an XPath), pick how to compare
it, and give the expected value.

```yaml
assertions:
- status: 2xx
- header: content-type
  contains: application/json
- jsonpath: $.order.id
  matches: ^ord-\d+$
- duration:
    lt: 800
```

Expected values are ordinary Studio inputs, so `{{variables}}` work there too — and they
expand through the same cascade that built the request. `equals: '{{user.email}}'` therefore
reads as *"the response came back carrying whatever we actually sent"*, without hard-coding
the value or duplicating it between the request and its check.

**The authoring loop is the point.** Send once, then open the Asserts tab and shape the rows
against the real response: each verdict re-computes as you type, without sending anything
again. The re-check runs on the server through the very same evaluator a Send uses, so what
you tune against is what a later run — or, eventually, a headless one — will decide.

Assertions never change what is sent and never fail the request. They annotate the result:
the response panel gains an **Asserts** tab with a pass/fail line per row, showing expected
vs actual on the ones that failed. A request that is *supposed* to 404 asserts `status: 404`
and passes.

Two behaviours worth knowing:

- A wrong body type is a failed assertion, not an error — a JSONPath against an HTML body
  fails with *the response body is not valid JSON*, and the run continues.
- When an expected value resolves through a variable marked secret, the reported expectation
  is masked as `***`, so a results pane never becomes somewhere secrets surface.

The full grammar — every extractor, matcher, and modifier, plus the cardinality and coercion
rules — is in [the workspace format spec](workspace-format.md).

---

## Tests and flows

Assertions answer *"did this response look right?"*. The **Testing** tab answers the two
questions above that: *"do these requests still pass?"* and *"does this multi-step exchange
still work end to end?"* — with two file kinds, both living in `tests/`.

![The Testing tab running a test set, with results streaming below](../assets/screenshots/studio-testing.png)

### Flows — requests in order, values carried across

A **flow** (`*.flow.tap`) runs requests in sequence and passes values from one response into
the next. Each step names a request, may override its variables, and may **extract** values
out of its response for the steps below:

```yaml
kind: flow
name: Checkout
steps:
- name: Create the order
  request: ../collections/demo/create-order.req.tap
  extract:
  - var: orderId
    jsonpath: $.order.id
- name: Read it back
  request: ../collections/demo/get-order.req.tap
  vars:
    id: '{{orderId}}'
```

That is the whole mechanism: step 1 binds `orderId`, step 2's variables read it as
`{{orderId}}`. In the editor each step is a card that reads in the order things happen —
which request, what goes in, what comes out, what has to be true — and the extraction row
reads left to right as the sentence it is: **variable ← source (selector)**.

Extract from a JSONPath, an XPath, a header, the status, the duration, the whole body, or a
regex capture group. A value that doesn't turn up **fails the step** rather than quietly
binding nothing — the next request is about to send `{{orderId}}` unexpanded, and saying so
here beats a strange URL two steps later. Use `default:` or the required toggle when a value
is genuinely optional.

**Neither request knows it is in a flow.** They are the same files the Requests tab sends,
with the same assertions; the flow only supplies variables and carries one value across. A
failed step stops the flow, because everything after it would run against a state that never
happened.

### Test sets — a group of checks

A **test set** (`*.test.tap`) is a list of tests, each running either one request or one whole
flow, plus variables that apply to the entire run:

```yaml
kind: test
name: Order API
vars:
  customer: cus_demo
tests:
- name: Rejects an unknown SKU
  request: ../collections/demo/create-order.req.tap
  vars: { item: nope }
  assertions:
  - status: 404
- name: Full checkout
  flow: ./checkout.flow.tap
```

Set variables are the last word on what the requests see — above the environment, above the
request's own — which is what lets one set pin an identity for every check inside it. A test
can add assertions on top of whatever its target already declares; for a flow entry those
check the last step's response, the one a caller of the flow sees.

By default a failing test doesn't stop the others (**Keep going**) — one broken endpoint
shouldn't hide the state of the rest. Switch to **Stop the set** for tests that build on each
other.

### Running

Hit **Run**. Results stream in as they happen rather than appearing all at once at the end,
so a ten-entry set against a slow API reports progress. Each row expands to the request that
ran, every assertion verdict, the values a step bound, and the response body. Failures open
themselves. A single test can be re-run on its own from its row, without re-firing everything
else at the upstream.

Runs use what is on disk, so Run is disabled while there are unsaved edits — the alternative
is a green result for a file that doesn't exist yet.

Nothing is persisted: a run annotates the screen, it doesn't write to your workspace, and an
extracted value lives only for the length of the run.

---

## Running tests from CI

The same runs, headless, through a .NET tool:

```bash
dotnet tool install --global Tap.Studio.Cli
tap-studio test "Demo API smoke"
```

`tap-studio` is a separate package from the `tap` tunnel CLI — different product, different
command, both installable side by side. It carries the execution engine and nothing that
serves a UI, so it stays small enough to install on every runner.

**It is the same engine.** The Studio's API and the CLI both call `Tap.Execution`; a verdict
from a pipeline and a verdict from the Testing tab are the same computation over the same
files, not two implementations that drift apart.

### Commands

| | |
|---|---|
| `tap-studio test <name>` | Run a test set or a flow. |
| `tap-studio send <name>` | Send one request and evaluate its assertions. |
| `tap-studio call <METHOD> <url> -c <collection>` | Send an ad-hoc request through a collection, inheriting its baseUrl, headers, and auth. |
| `tap-studio list [kind]` | List collections, requests, envs, tests, or auth profiles (name + type only). |
| `tap-studio describe <request>` | Show a request's template surface without rendering it. |
| `tap-studio lint` | Parse the workspace and report what doesn't load. |
| `tap-studio vars` | Print the resolved variable cascade, secrets masked. |
| `tap-studio mcp` | Serve the workspace to MCP clients over stdio. |
| `tap-studio agent init --env <claude\|codex\|copilot\|opencode>` | Install the agent skills + MCP registration for an agent environment, per project or per user. |

`test`, `send`, and `call` take `--json` for a secret-redacted machine-readable result on
stdout — the mode built for scripts and AI agents. See [agent-surface.md](agent-surface.md).

`<name>` is a path, the `name:` from the frontmatter, or the filename stem — so the thing you
read off the Testing tab is the thing you can type. `--list` shows what's available. The
workspace is found by walking up from the working directory to the nearest `workspace.tap` — or,
when no ancestor has one, by taking the first workspace beneath the working directory
(shallowest first, then alphabetical, to a bounded depth). Standing at a repo root whose
workspace lives in a subfolder therefore just works; `--workspace` pins it explicitly.

### Selecting what runs

```bash
tap-studio test --tag smoke                  # every test set and flow tagged smoke
tap-studio test --tag smoke --tag graphql    # either tag — repeated tags union
tap-studio test "Order API" --filter refund  # just the tests whose name contains "refund"
tap-studio test "Order API" --only 2         # one entry, by index
```

`--tag` replaces the name argument rather than narrowing it; passing both is an error instead
of a precedence rule to memorise. Repeated tags union, because "run the smoke tests and the
graphql tests" is what the flag reads like.

**A selection that matches nothing is an error (exit 2), never an empty green run.** A
misspelled `--tag` that silently ran zero tests would leave a pipeline passing forever with
nothing in the output to notice it by — so an unmatched tag lists the tags that do exist, and
an unmatched `--filter` says how many tests it looked at.

`--filter` narrows the tests inside a set. It doesn't apply to a flow: its steps are a chain,
and dropping one cuts what a later step depends on, so the CLI says so rather than running a
broken sequence.

### Input variables

```bash
tap-studio test "Order API" --var customer=cus_ci --var-file ci.env
```

`--var` lands in the same tier the UI's per-run overrides use — above every file scope — so it
is the last word on what the requests see. Later `--var-file`s beat earlier ones, and `--var`
beats all of them.

### A pipeline

```yaml
- run: dotnet tool install --global Tap.Studio.Cli
- run: tap-studio test "Demo API smoke" --env ci --output junit --output-file results.xml
  env:
    TAP_SECRETS_ALLOWED: "DEMO_*_TOKEN"
    DEMO_API_TOKEN: ${{ secrets.DEMO_API_TOKEN }}
```

`--output` takes `junit`, `trx`, `json`, or `markdown`.

| Format | For |
|---|---|
| `junit` | Every CI system ingests it without a plugin, so a failed assertion shows up as a failed test in the UI rather than a line in a log. A `--tag` run writes one `<testsuite>` per target inside a single `<testsuites>` — the shape the format was designed for. |
| `trx` | Azure DevOps' native reporting. Several targets merge into one `TestRun`. |
| `json` | Scripting. Always an envelope — `{ ok, passed, failed, skipped, durationMs, runs: [...] }` — whatever the target count, so nothing has to branch on how the run was selected. |
| `markdown` | The places a human reads: a GitHub job summary, a PR comment, an artifact someone opens. |

The Markdown report leads with the verdict, then **failures in full**, then a table per target —
someone opening a summary because a build went red wants the reason on the screen they land on.
Passing targets collapse into a `<details>`; failing ones stay open. When everything passed
there is no failure section at all.

For a GitHub job summary, append rather than write directly, so it can't clobber what another
step put there:

```yaml
- run: |
    tap-studio test --tag smoke --output markdown --output-file summary.md
    cat summary.md >> "$GITHUB_STEP_SUMMARY"
```

This repo runs its own sample set that way on every push — see
[`.github/workflows/workspace-tests.yml`](../.github/workflows/workspace-tests.yml).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything that ran, passed. |
| 1 | A test or assertion failed. |
| 2 | Usage error — unknown or ambiguous name, bad option. |
| 3 | Workspace error — no `workspace.tap`, a file that doesn't parse. |
| 4 | Auth couldn't be acquired without a human. |
| 130 | Cancelled. |

`1` versus everything above it is the distinction worth having: a red build because the API
misbehaved is a different situation from a red build because the runner couldn't do its job,
and one exit code for both makes them indistinguishable from a dashboard.

### Secrets and auth without a browser

Secrets reach a headless run through the same variable providers the UI uses. The `env`
provider is the natural CI path — deny-by-default, with `TAP_SECRETS_ALLOWED` /
`TAP_VARS_ALLOWED` naming what a workspace may read. `azkv` works with workload identity.

Auth profiles split three ways:

| Works headlessly | Doesn't |
|---|---|
| `bearer`, `basic`, `apiKey`, `custom`, `aws-sigv4`, `github` (PAT) — the renderer builds these inline | `oauth2` authorization_code / PKCE / device_code |
| `oauth2` client_credentials and ROPC | `github` oauth |
| `azure-cli`, when `az` is signed in — federated credentials work | |

An interactive grant **fails immediately** with exit 4, naming the profile and the
alternatives. The failure it replaces — a job blocked on a sign-in prompt nobody can see until
the timeout — is far worse than an error message.

The token cache at `~/.tap/auth-tokens.json` that the Studio fills when you complete a sign-in
is **not** consulted unless you pass `--use-cached-tokens`. A CI run that passes because
someone's laptop had a warm token is a test that didn't really run.

---

## AI agents

A workspace is agent-ready out of the box: an AI coding agent can discover what exists,
send saved requests, fire ad-hoc calls through a collection's configured auth, and run
test sets — **without ever holding a credential**. The `--json` mode redacts every
secret from what the agent reads; discovery reports auth profiles as name + type only;
ad-hoc calls are locked to the collection's baseUrl so inherited credentials can't be
sent elsewhere.

The same surface is served as MCP tools: `tap-studio mcp` over stdio, or the running
Studio's `/mcp` endpoint — the latter rides the user's interactively-minted tokens, so
even PKCE-authed requests work for an agent after the user signs in once in the browser.
A collection can opt out of all of it with `agent: false` (the **Agent access** switch
in the collection editor).

The full story — trust model, tool contracts, per-collection control, and the two
shippable Claude Code skills (`tap-studio` for operating a workspace, `tap-author` for
authoring one) — lives in [agent-surface.md](agent-surface.md).

## Authentication

Studio's auth profiles are reusable files (`*.auth.tap`) referenced by requests or set as a
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

1. `workspace.tap` `vars`
2. the owning `_collection.tap` `vars`
3. the active collection **stage**
4. the active `*.env.tap`
5. the request's own `vars`
6. per-run overrides

Providers are declared in `workspace.tap` under `variableProviders` (a workspace provider shadows a
same-named system one):

| Type | Source |
|---|---|
| `env` | Process environment, gated by two allowlists — `TAP_VARS_ALLOWED` (names whose values may be *shown*) and `TAP_SECRETS_ALLOWED` (names that stay masked but resolve at execute time). Both take comma-separated globs; unset means deny-everything. |
| `file` | `.tap/.vars/<provider>.yml` in the workspace. Values marked `secret: true` are encrypted at rest with AES-256-GCM under a key derived from this machine's encryption key (`TAP_ENCRYPTION_KEY`, else `~/.tap/encryption.key`). |
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
converts a Postman collection export into a Tap collection: one `_collection.tap` plus a
`*.req.tap` per request, with folders preserved as grouping directories.

---

## Importing from OpenAPI

Create → Collection → *From OpenAPI*, or right-click a collection → *Import from OpenAPI…*.
Upload a document or fetch a URL; pick the operations; choose a layout. JSON or YAML,
OpenAPI 3.0/3.1, and Swagger 2.0 (converted on read).

Two layouts, chosen per import:

| Layout | Result |
|---|---|
| **One `.req.tap` per operation** | Structured editing, assertions, per-request docs. Grouped into folders by first tag. |
| **One `.http` file per tag** | Portable — opens and sends in Visual Studio, VS Code REST Client, JetBrains, httpyac, Kulala. |

What else comes across: `servers[0]` becomes the collection's `baseUrl` and the rest become
stages; `securitySchemes` becomes an auth profile whose credentials are `{{variables}}` you
fill in once (**nothing secret is ever taken from a document**); a `requestBody` schema becomes
an example body, following `$ref` and preferring the spec's own `example`. Path and query
parameters become declared `vars`, so a request documents its own inputs.

Parsing is always server-side: the browser has no YAML parser, and `$ref` / `allOf` /
Swagger-2.0 normalization must not exist in two places that can disagree. The document is
staged first (`POST /api/openapi/documents` or `.../documents/fetch`) and the import references
it by id, so the operation list you picked from and the document that gets written are provably
the same one.

### Re-syncing when the API changes

Each import records `collections/<slug>/_openapi.lock.json` — the document it came from, plus
two hashes per operation. One says whether *upstream* changed; the other, compared against the
file on disk, says whether *you* changed it. Only when both moved is there a conflict.

Right-click the collection → *Re-sync from OpenAPI…*. It re-fetches the recorded URL, diffs,
and shows one row per operation with a **non-destructive default**:

| Verdict | Default |
|---|---|
| New upstream | Create it |
| Changed, file untouched | Update |
| Changed **and** edited locally | **Keep mine** |
| Gone upstream | Tag `deprecated` — never deleted |
| File missing (renamed or moved away) | Leave alone |

Updating never regenerates a file from scratch. It reads what is on disk, projects it to its
spec, and overwrites only the fields the importer owns — URL, generated headers, example body.
**Assertions, variables, auth, transport and the file's `id` are preserved structurally**: no
code path here assigns them. Headers merge by name and are never removed, so one you added
survives. A `.http` file is edited one `###` section at a time, and untouched sections come back
byte-for-byte.

Tracking survives a rename: `.req.tap` output is anchored by the request's `id:`, not its path,
because moving a file rewrites no refs.

The lock is machine-owned and invisible to the loader (which globs only `*.tap`, `*.md` and
`*.http`). Deleting it loses re-sync and nothing else.

### Suggested values (optional, needs an AI CLI)

*Suggest values with AI* on the Options step proposes a value for each generated variable —
reusing a workspace variable when one already means the same thing (`userName` → `{{user.name}}`),
otherwise realistic sample data. It uses the same local CLI as the request assistant, sees
variable **names, scopes and secret flags but never values**, and proposes only: you apply.
The import is identical without it, which is the default state.

---

## Desktop app

The Tauri shell spawns the `Tap.Studio` sidecar, reads a one-line JSON handshake from its
stdout to learn the bound URL, and points the webview at it. The SPA is served by the sidecar
itself, so the UI and the API are same-origin and `fetch('/api/...')` needs no plumbing.

Three things the desktop build changes:

- **OAuth callbacks** use the registered `tap-studio://` URL scheme, so identity providers get
  a stable redirect URI to register instead of a random loopback port.
- **Updates** are signed and delivered through Tauri's updater.
- **The boot workspace** defaults to `<system dir>/workspace` (created and scaffolded on first
  run) rather than the process's working directory. An app launched from Finder or the Dock
  inherits `/` as its working directory, and treating that as a workspace root means scanning
  the whole disk before the window can open.

If the window is still on the splash after a few seconds it names the phase it is waiting on;
after 45s (`TAP_STUDIO_STARTUP_TIMEOUT_SECS`) it explains itself, with the tail of the
backend's output, the path to `~/Library/Logs/dev.philbir.tap-studio/studio.log`, and a retry
button. The backend keeps starting behind that screen, so a slow first scan still lands.

Full build, signing, and release details: [src/desktop/README.md](../src/desktop/README.md).

---

## Configuration

| Key | Purpose |
|---|---|
| `Studio:WorkspaceRoot` | Workspace to open on boot. The active workspace is then remembered in `workspaces.json`. Defaults to the working directory, or to `<system dir>/workspace` under the desktop shell. The folder walk is bounded (20s / 25 000 folders, skipping `node_modules`, `.git`, `bin`, `obj`, …) — a root that is too broad loads partially and says so instead of hanging. |
| `Studio:Port` / `Studio:Host` | Where Kestrel binds. Default `localhost:5298`. |
| `Studio:StatePath` | State database path. Default `<system dir>/state.db`. |
| `Studio:VariableProviders:[]` | System-level variable providers, same shape as workspace ones (`name`, `type`, `mode`, `settings`). |
| `TAP_SYSTEM_DIR` | Overrides the state folder (default `~/.tap`) — holds `state.db`, `workspaces.json`, `system.json`, and `auth-tokens.json`. |
| `TAP_VARS_ALLOWED` | Comma-separated globs of environment variable names the `env` provider may expose in cleartext. |
| `TAP_SECRETS_ALLOWED` | Comma-separated globs of environment variable names the `env` provider may resolve while keeping them masked. |
| `TAP_STUDIO_DESKTOP` | Set by the desktop shell; switches the OAuth callback to `tap-studio://callback`. |
| `TAP_STUDIO_EMIT_READY` | Makes the sidecar narrate startup on stdout as JSON (`studio.progress` / `studio.error` / `studio.ready`) for the desktop shell to render. |
| `TAP_STUDIO_STARTUP_TIMEOUT_SECS` | Read by the desktop shell: how long to wait for `studio.ready` before the splash explains itself. Default 45; `0` waits forever. |
