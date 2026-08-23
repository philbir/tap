---
name: tap-author
description: "Author Tap workspace assets — collections, requests, auth profiles, environments, flows, and test sets — as markdown files, and verify them with the tap-studio CLI, no UI needed. USE WHEN: creating or editing *.req.tap, _collection.tap, *.auth.tap, *.env.tap, *.flow.tap, *.test.tap, or workspace.tap; scaffolding a new workspace or collection; adding assertions, flows, or test coverage; converting curl commands, OpenAPI operations, or Postman collections into Tap requests. DO NOT USE FOR: merely running existing requests or tests (use the tap-studio skill / MCP tools), or driving the Studio web UI. INVOKES: file edits + the tap-studio CLI (dotnet)."
---

# Authoring Tap workspaces

A Tap workspace is a folder of markdown files — every asset is a `.md` file with YAML
frontmatter and a CommonMark body. The files ARE the product: the Studio UI and the CLI
are two views over the same folder, so everything the UI can create, you can create by
writing files. Verify with the CLI after every change; never consider an asset done until
`lint` is clean and a real `send`/`test` proved it.

## Workspace anatomy

```
workspace.tap                                ← kind: workspace — the root marker + manifest
collections/<slug>/_collection.tap     ← kind: collection — baseUrl, default headers/auth, vars
collections/<slug>/**/*.req.tap        ← kind: request — one executable request each
collections/<slug>/**/*.auth.tap      ← collection-scoped auth profile (sees collection vars)
auth/*.auth.tap                        ← workspace-scoped auth profiles (shared, see only workspace+env vars)
environments/*.env.tap                 ← kind: env — named variable sets (local / dev / prod)
tests/*.test.tap                       ← kind: test — named group of checks over requests/flows
tests/*.flow.tap                       ← kind: flow — multi-step sequence with value extraction
```

Nested folders under a collection are pure grouping — every request belongs to exactly
one collection (the nearest `_collection.tap` above it), and inherits its `baseUrl`,
`defaultHeaders`, `defaultAuth`, and `vars`.

## Concepts that decide everything else

**The variable cascade.** `{{name}}` tokens resolve through scopes, later wins:
workspace `workspace.tap` vars → owning collection vars → active env vars → request vars →
per-run overrides (`--var`), plus flow-extracted values in the run bag.
A var declared in any of those scopes may hold a token of its own —
`apiToken: { default: '{{file:api.token}}', secret: true }` resolves through to the provider
— but a var must not resolve through itself (`E_VAR_CYCLE`), and a value that arrived at
runtime (an `extract:`, a `--var`) is never re-scanned. Unknown vars fail the render.

**Token syntax.** `{{name}}` — cascade first, then providers in registration order.
`{{provider:name}}` — that provider only (e.g. `{{env:DEMO_API_URL}}`,
`{{kv:client-secret}}`). Escape a literal with `\{{`. That is the whole syntax — there
is no other interpolation form.

**Secrets never live in files.** A value is secret when its provider says so (Azure Key
Vault values, env vars matched by `TAP_SECRETS_ALLOWED`, encrypted file-provider
entries) or when a var-spec marks it `secret: true`. Secret values are masked in every
output and redacted from agent-facing JSON. When authoring: reference secrets via
`{{provider:name}}` tokens or secret-bearing auth fields — never paste a real token,
password, or key into a workspace file. The `env` provider is deny-by-default: the host
must export `TAP_VARS_ALLOWED` / `TAP_SECRETS_ALLOWED` (comma-separated globs) before
`{{env:NAME}}` resolves.

**Auth scoping.** A profile under `auth/` resolves its fields against workspace+env vars
only; one inside a collection also sees that collection's vars — which is what lets
`tokenUrl: '{{IDP_URL}}/connect/token'` re-point per environment. Runtime tokens (oauth2,
azure-cli) are cached per environment and never written to workspace files. A profile's
scope comes from its own location, so a request borrowing a profile from another collection
never drags its environment across.

**Environments, global and assigned.** An `*.env.tap` with no `collections:` key is global —
selectable anywhere, contributing variables only. One carrying a `collections:` list is
offered in exactly those collections, and **each assignment carries that collection's own
`baseUrl` and `defaultAuth`** — the same `uat` points `orders` and `billing` at different
hosts, so the override cannot live on the environment. An assignment with no overrides is a
bare slug:

```yaml
collections:
- billing                                  # variables only
- collection: orders
  baseUrl: https://orders-uat.acme.test
  defaultAuth: ../../auth/uat.auth.tap     # relative to THIS env file
```

The assigned form is what a collection `stage` used to be: dev/uat/prod of one API. Select
either kind at run time with `-e <name>`; an assigned env used outside its collections
silently drops out rather than contributing the wrong values. `stages:` / `defaultStage:` on
a collection were removed in 0.7.0 and now fail to parse.

**Agent access.** `agent: false` on a `_collection.tap` fences that collection off from
agent surfaces (discovery omits its requests; describe/send/call refuse with
`E_AGENT_ACCESS_DISABLED`). Respect it — ask the user rather than working around it.
You may still *author* files in such a collection; you just can't execute through the
agent surface.

## Starting from an OpenAPI description? Don't hand-author it

If the user has an OpenAPI/Swagger document, Studio imports it — collection, requests, auth
profile, example bodies and all — far more accurately than transcribing operations by hand,
and the result stays *linked* so it can be re-synced when the API changes.

Point them at it rather than writing the files yourself:

> Studio → Create → Collection → **From OpenAPI** (or right-click a collection →
> *Import from OpenAPI…*). File or URL; JSON or YAML; 3.0, 3.1, or Swagger 2.0.

Hand-authoring is the right call for a *single* request someone describes in prose, for an API
with no published description, or when editing what an import already produced. Everything
below still applies to those.

One thing to know if you edit an imported request: the collection carries
`_openapi.lock.json`, and re-sync compares a hash of each generated file against it. Your edits
are safe — re-sync defaults to keeping them and never touches `assertions`, `vars`, `auth` or
`id` — but a file you rewrite will show up as "changed locally", which is exactly what it is.

## The authoring loop

Work in small verified steps. From the workspace root (or pass `--workspace <dir>`):

```bash
tap-studio lint                                  # after EVERY file edit — parse the whole workspace
tap-studio list --json                           # discover what exists (collections, requests, envs, tests, auths)
tap-studio describe "<name-or-path>" --json      # check how your request file reads before running it
tap-studio send "<name-or-path>" --json          # run it: status, assertion verdicts, redacted body preview
tap-studio call GET /probe -c <collection> --json  # probe an endpoint BEFORE writing the file
tap-studio vars --env <env>                      # inspect the resolved cascade (secrets masked)
tap-studio test "<set-or-flow>" --json           # run the suite you authored
```

In this repo run it from source: `dotnet run --project src/backend/Tap.Studio.Cli -- <args>`;
elsewhere it's the `tap-studio` dotnet tool. If the tap-studio MCP tools are connected
(`workspace_inventory`, `describe_request`, `send_request`, `call_request`, `run_test`),
prefer them for the verify steps — same engine, same JSON.

A productive pattern: `call` the endpoint ad-hoc first to learn its real shape, then
write the `.req.tap` with assertions pinned to what you observed, then `send` the saved
file to confirm, then wire it into a test set.

Exit codes (branch on them): `0` pass · `1` test/assertion failed (the run itself was
fine) · `2` usage error · `3` workspace/parse error · `4` auth needed a human ·
`130` cancelled. On exit 4, suggest `--use-cached-tokens` (local dev) or a
non-interactive auth type — never try to obtain credentials yourself.

## CLI quick reference

| Command | Purpose | Key options |
|---|---|---|
| `lint` | Parse every file; report what doesn't load | `-w <dir>` |
| `list [kind]` | Inventory: `requests\|collections\|envs\|tests\|auths` | `--json` |
| `describe <name>` | One request's template surface (nothing rendered) | `--json` |
| `send <name>` | Send a saved request, evaluate assertions | `-e <env>` `--var k=v` `--use-cached-tokens` `--body` `--json` |
| `call <METHOD> <url>` | Ad-hoc request through a collection | `-c <collection>` `-H 'Name: v'` `-d <text\|@file>` `--auth <ref>` `--allow-any-url` `--json` |
| `test <name>` | Run a test set or flow | `--tag` `--filter` `--only` `--fail-fast` `--output junit\|trx\|json\|markdown` `--json` `--list` |
| `vars` | Resolved cascade, secrets masked | `-e <env>` `--request <path>` |
| `mcp` | Serve all of the above as MCP tools over stdio | `-w <dir>` `--use-cached-tokens` |
| `migrate` | Rename legacy `.md` workspace files to `.tap` and rewrite their refs | `--dry-run` `-w <dir>` |

Every target argument accepts a workspace-relative path, the frontmatter `name:`, or the
filename stem. Ambiguity is an error listing candidates — prefer paths in scripts.

## Authoring rules that trip people up

- A request body has **exactly one** fenced ```` ```http ```` block — the request line,
  headers, blank line, body. Everything else in the file is documentation.
- A relative URL on the request line requires the owning collection to have a `baseUrl`.
  Bare `host:port` baseUrls get `http://` (or `ws://` for `protocol: websocket`).
- Paths in `auth:` / `request:` / `flow:` refs are relative to the *referencing file*.
- `id:` is optional — omit it; the Studio assigns a UUIDv7 on first save. Never invent ids.
- Assertions annotate, they never abort: a request that should 404 asserts `status: 404`
  and passes. Extractions (flows) are different — a missing value fails the step.
- Quote YAML values that start with `{{` (`baseUrl: '{{API_URL}}'`) or YAML reads a map.
- One frontmatter per file; empty frontmatter is illegal (`kind:` is the minimum).
- `.http` files are first-class requests too (the format Visual Studio scaffolds). One file
  holds several requests addressed as `orders.http#get-order`; Tap features ride in
  `# @tap-*` comment directives. Never reformat a `.http` file — edit it as raw text.
- Name new files with the `.tap` extensions. Workspaces written before 0.7.0 use `.md`
  (`orders.req.md`, `tap.md`) and still load with a `W_LEGACY_EXTENSION` warning — if you
  see those, offer `tap-studio migrate`; do not rename files by hand, because refs are
  literal paths carrying the extension and would be left dangling.
- After creating files, `lint` first — a workspace parse error makes `test` refuse to run.

## Full reference

- [references/file-formats.md](references/file-formats.md) — complete frontmatter spec +
  template for every kind: workspace, collection, request (incl. WebSocket), auth (all
  types incl. oauth2 / azure-cli / jwt / github), env (incl. provider binding), flow, test.
- [references/assertions.md](references/assertions.md) — the full assertion grammar
  (extractors, matchers, modifiers, semantics) and flow extraction spec.

Load the reference file whenever you author beyond the minimal templates above; do not
guess field names.
