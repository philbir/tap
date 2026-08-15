# Tap Workspace Format

> Status: **draft v0**. Subject to change until v1.0. Every file Tap stores in your repo is plain Markdown with YAML frontmatter, so the format is reviewable as a normal git diff.

A Tap **workspace** is a folder holding a `tap.md` manifest plus the typed files described
below. Everything in it is meant to be checked into version control. Nothing in it ever
contains a secret value — secrets live in **variable providers** (§12); workspace files carry
only `{{provider:name}}` references, `secret: true` flags, or the file provider's encrypted
envelopes.

This document is the authoritative spec for the on-disk format. The Tap parser (`Tap.Workspace`) and renderer (`Tap.Workspace.Rendering`) implement exactly what's described here. If parser and spec disagree, the spec is the bug report.

---

## 1. Design goals

1. **Git-native.** Every artifact is plain text. Renames work via `git mv`. Diffs are readable.
2. **Composable.** A runnable request is the composition of *workspace + collection (+ stage) + auth + environment + request*. No single file is the whole story.
3. **Readable on GitHub.** Frontmatter is the structured part; the body is human prose. A request file is a documentation page that happens to be executable.
4. **Secret-safe by construction.** A literal secret never needs to occur in a workspace file. A field that needs one references a provider (`{{kv-prod:stripe-live-key}}`), or a variable declared `secret: true`; values resolve at render time, are traced by name only, and are redacted from anything echoed back out (§12.2, §13).
5. **One format, a handful of shapes.** Every file is `Markdown + YAML frontmatter`. The `kind` field plus the filename suffix tell the parser what shape to expect.
6. **No required tooling to read.** Any text editor or any Markdown renderer can display a workspace. Tap adds editing, validation, execution, and live preview on top.

---

## 2. File kinds

| Kind | Filename suffix | Purpose |
|---|---|---|
| `request` | `*.req.md` | A single HTTP request template. |
| `auth` | `*.auth.md` | A reusable authentication profile (bearer, oauth2, azure-cli, github, …). Lives either in `auth/` (workspace-scoped) or inside a collection (collection-scoped) — see §8.0. |
| `env` | `*.env.md` | A named environment (variables plus provider bindings). |
| `collection` | `_collection.md` *(at `collections/<slug>/`)* | A top-level group of requests. Owns the base URL, optional named stages, default auth, default headers, plus collection-scoped variables and tags. |
| `flow` | `*.flow.md` | An ordered sequence of requests where each step can extract values from its response for the steps after it. Lives in `tests/` — see §10. |
| `test` | `*.test.md` | A test set: named checks, each running one request or one flow, with set-scoped variables. Lives in `tests/` — see §11. |
| `workspace` | `tap.md` *(at workspace root)* | Workspace-level config: name, default env, registered variable providers. |

Sub-directories inside a collection are pure grouping for the explorer tree — they carry no metadata, no variables, and no inherited defaults. Every request below a collection inherits its baseUrl, stages, default auth, default headers, and variables; variable sharing across a group of requests lives on `_collection.md`.

An `*.auth.md` placed inside a collection is *owned* by it: the profile's fields resolve against that collection's variables and its active stage. That's the only kind of file below a collection that inherits anything besides a request.

The filename suffix is canonical. The `kind:` frontmatter field is required and must match the suffix. A mismatch is a hard parse error.

### 2.1 Suggested directory layout

```
my-service/
├── src/                                 ← your code
└── tap/                                 ← the workspace root — any folder you point Tap at
    ├── tap.md                           ← kind: workspace
    ├── auth/                            ← workspace-scoped profiles, shared by every collection
    │   ├── stripe-bearer.auth.md
    │   └── corp-oidc.auth.md
    ├── environments/
    │   ├── local.env.md
    │   ├── staging.env.md
    │   └── prod.env.md
    ├── tests/                           ← test sets and flows
    │   ├── billing.test.md
    │   └── checkout.flow.md
    └── collections/
        └── stripe/
            ├── _collection.md        ← kind: collection (owns baseUrl, default auth/headers, stages)
            ├── stripe-oauth.auth.md  ← collection-scoped profile (sees this collection's vars/stages)
            ├── create-customer.req.md
            ├── get-customer.req.md
            └── refunds/              ← pure-grouping sub-folder
                ├── issue.req.md
                └── list.req.md
```

The workspace root is simply the folder you open in Studio or pass to the CLI
(`--workspace`, defaulting to the nearest ancestor of the working directory that contains
`tap.md`). The loader walks that folder for the known suffixes — dotfolders included, so the
older `.tap/` sub-folder layout keeps loading — while skipping package/VCS caches
(`node_modules`, `.git`, `bin`, `obj`, …) and capping single files at 8 MiB. Tap also keeps
a housekeeping `.tap/` directory under the root for the file provider's variable store
(§12.1); that directory is data, not workspace files.

The four top-level directories (`auth/`, `environments/`, `tests/`, `collections/`) are
structural: `auth/`, `environments/`, and `tests/` hold flat lists of typed files;
`collections/` hosts one sub-directory per collection. Inside each collection, nested
directories are freeform grouping with no metadata — variable sharing across a group of
requests lives on `_collection.md`.

Auth profiles are the one kind that can live in either place. Put a profile in `auth/`
when several collections share it; put it inside a collection when its endpoints or
credentials come from that collection's variables. See §8.0.

---

## 3. Common file shape

Every Tap file is:

```
---
<yaml frontmatter>
---

<markdown body>
```

- **Frontmatter** is YAML 1.2, fenced by `---` lines. Required for all Tap files. Empty frontmatter (`---\n---`) is illegal — at minimum `kind:` must be present.
- **Body** is CommonMark. Bodies are documentation for humans except in `request` files, where one fenced `http` block carries the executable template (§5).
- Encoding is UTF-8, LF line endings.
- Line length unconstrained; Tap's writer wraps prose at 100 columns by convention but never alters fenced blocks.

### 3.1 Universal frontmatter fields

These appear on every kind:

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | enum | yes | One of `request`, `auth`, `env`, `collection`, `flow`, `test`, `workspace`. |
| `id` | string | no | Stable identifier. Auto-generated as a [UUIDv7](https://uuid7.com) on first save if omitted. Used for cross-file refs that survive renames. |
| `name` | string | no | Display name. Defaults to the filename stem if omitted. |
| `tags` | string[] | no | Free-form labels for filtering. |

### 3.2 Variable interpolation syntax

One interpolation syntax, two token forms:

- `{{name}}` — an **unprefixed reference**. Resolved first against the merged variable
  cascade (§7.3); on a miss, walked across the registered variable providers (§12) in
  registration order — first non-null wins. An active env's `defaultVariableProvider` is
  consulted ahead of the rest, and `strictVariables` stops the fall-through (§7.1).
- `{{provider:name}}` — an **explicit provider reference**. Resolved only against that
  provider (or against whatever an env alias re-points the prefix at — §7.1). Bypasses the
  cascade. Fails with `E_UNKNOWN_PROVIDER` if no such provider is registered, and with
  `E_PROVIDER_RESOLUTION_FAILED` if the provider can't produce the name.

The provider prefix has the same shape as a provider name: a letter followed by
letters/digits/`_`/`-`. Whitespace inside the braces is tolerated (`{{ name }}`).

Expansion is **single-pass**: a resolved value is emitted verbatim and never re-scanned, so
a value that happens to contain `{{…}}` cannot trigger another round of lookups (that would
be second-order injection straight out of a workspace file). The corollary: a token written
*inside a variable's value* does not expand when that variable is referenced — put provider
tokens directly in the template or auth field that needs them.

There is **no separate secret syntax**. Whether a resolution is secret comes from the source:
a provider marks its values (Key Vault values are always secret; the env provider follows the
host allowlists), and a cascade variable is secret when its declaration says `secret: true`
(§5.1). Secret or not, the token is spelled the same way.

A literal `{` followed by `{` that you do not want interpolated is escaped as `\{{`.

---

## 4. `workspace` — `tap.md`

The single file at the workspace root. Created on `tap init`.

### 4.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"workspace"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `defaultEnv` | path | no | Workspace-root-relative path to the `.env.md` file used when none is specified at execute time. |
| `variableProviders` | array of provider configs | no | Registers the named variable providers available in this workspace. See below and §12. |
| `defaultVariableProvider` | string | no | Provider that bare `{{name}}` tokens hit first after the cascade (and that receives un-targeted variable writes). An active env's own `defaultVariableProvider` overrides it. Legacy key `defaultProvider` is also read. |
| `vars` | map<string, var-spec> | no | Workspace-level variables (lowest precedence). Same var-spec shape as §5.1. |

Each `variableProviders:` entry declares `name` (the `{{name:…}}` prefix), `type` (one of
the built-in types — §12.1), and optionally `settings`. Settings may sit under an explicit
`settings:` mapping or inline at the entry root; unknown scalar keys fall into the settings
bag either way. Provider names must match `[A-Za-z][A-Za-z0-9_-]*` — anything else is
rejected with `E_PROVIDER_CONFIG_INVALID` (file-backed providers combine the name into a
path, so separators or `..` would escape the workspace). The legacy `providers:` key is
still honored so older workspaces keep loading.

Providers can also be registered at **system scope** (the host's settings store) — those are
available to every workspace, and a workspace provider with the same name shadows the system
one. The built-in `system` provider (§12.1) is always registered by the host.

### 4.2 Example

```markdown
---
kind: workspace
id: 0192-3a4c-bb71-7c1d-9e8f0a1b2c3d
name: acme-billing
defaultEnv: environments/local.env.md
defaultVariableProvider: file
variableProviders:
- name: env      # host env vars, gated by TAP_VARS_ALLOWED / TAP_SECRETS_ALLOWED (§12.1)
  type: env
- name: file     # workspace-local store; secrets encrypted at rest
  type: file
- name: kv-dev
  type: azkv
  settings:
    vaultName: acme-dev
    tenantId: 00000000-0000-0000-0000-000000000000
- name: kv-prod
  type: azkv
  settings:
    vaultName: acme-prod
    tenantId: 00000000-0000-0000-0000-000000000000
vars:
  app.userAgent: tap/0.5
---

# Acme Billing

API workspace for the billing service. Owned by @platform. Production access
runs through `prod.env.md` and requires Azure Key Vault membership in the
`billing-eng` group.
```

---

## 5. `request` — `*.req.md`

A single executable request. The most-edited file kind.

### 5.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"request"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `auth` | path \| id-ref | no | Overrides the containing collection's `defaultAuth`. To opt a request out of an inherited default entirely, point it at a profile with `type: none` (§8.1). |
| `protocol` | enum: `http` \| `websocket` | no | Wire protocol. Default `http`. `websocket` drives baseUrl scheme normalization (http→ws, https→wss) and switches the executor to a WebSocket transport. See §5.4. |
| `transport` | mapping | no | `ignoreTlsErrors: <bool>` and/or `timeoutMs: <int ≥ 0>`. Each unset key falls back to the collection's `transport` (§6.1). |
| `vars` | map<string, var-spec> | no | Request-scoped variables (highest precedence except for explicit per-run overrides). |
| `tags` | string[] | no | |
| `assertions` | array of assertions | no | Declared expectations about the response, evaluated after every run. See §5.5. |

`var-spec` may be a literal string (becomes the default value) or an object with any of
`default`, `description`, `required`, `example`, and `secret`:

```yaml
vars:
  customer.email:
    description: Email address used for signup
    required: true
    example: jane@example.com
  api.key:
    default: dev-key-123
    secret: true          # masked in the UI and in echoed output; resolves normally on the wire
  customer.name: Jane Doe
```

`secret: true` is the only secret marker — the value still renders into the request like any
other variable, but the Studio masks it everywhere it's displayed and the renderer redacts it
from anything echoed to agents (§13).

### 5.2 Body

The body is CommonMark. **Exactly one** fenced code block tagged `http` carries the request template. All other content is documentation and ignored at execute time.

The `http` block follows the [VS Code REST Client / JetBrains HTTP Client](https://www.jetbrains.com/help/idea/exploring-http-syntax.html) syntax, with three extensions:

1. `{{var}}` and `{{provider:name}}` interpolation per §3.2 — in the request line, headers, and body.
2. The request line's URL may be a bare path (`/v1/customers`) — Tap prepends the containing collection's `baseUrl` (or, when a stage is active, the stage override). If the URL is not absolute and the collection has no baseUrl, the render fails with `E_HTTP_BLOCK_SYNTAX`.
3. A body that is exactly one line of the form `< ./relative/path` is a **file reference**: the executor loads the file's bytes (resolved relative to the request file, clamped inside the workspace) and sends them as the body. The literal `< …` text is kept for display so captures show what was referenced.

If multiple `http` blocks are present, the parser fails with `E_MULTIPLE_REQUEST_BLOCKS`. If zero are present, the parser fails with `E_NO_REQUEST_BLOCK`.

### 5.3 Example

```markdown
---
kind: request
id: 0192-3a4d-7000-7b91-a0c1d2e3f405
name: Create customer
auth: ../../auth/stripe-bearer.auth.md
tags: [customer, write]
vars:
  customer.email:
    description: Email address
    required: true
  customer.name:
    description: Display name
---

# Create customer

Creates a Stripe customer. Called during signup. Idempotent on `email` thanks
to the upstream's idempotency-key middleware.

## Request

```http
POST /v1/customers
Content-Type: application/x-www-form-urlencoded

email={{customer.email}}&name={{customer.name}}
```

## Notes

When run against `prod`, coordinate with @billing — see runbook B-14.
```

### 5.4 WebSocket requests

Setting `protocol: websocket` flips two behaviors:

1. **URL scheme normalization.** The renderer rewrites the effective scheme so the executor opens a WebSocket. Specifically: scheme-less `host:port` (or `//host:port`) baseUrls pick up `ws://`; `http://X` is rewritten to `ws://X`; `https://X` becomes `wss://X`. An explicit `ws://`/`wss://` on the baseUrl or request line is left alone.
2. **Transport.** The executor opens a `ClientWebSocket` against the resolved URL. The HTTP method (conventionally `GET`) and any custom headers ride the upgrade handshake — `Connection`, `Upgrade`, `Host`, and `Sec-WebSocket-*` are managed by the client and dropped if present.

If the request carries a body, it is sent as the **first text frame** after the upgrade. Subsequent inbound frames stream back to the caller (`event: ws` on `/api/execute/stream`). To just listen, omit the body.

Example:

```markdown
---
kind: request
name: Heartbeat
protocol: websocket
---

```http
GET /demo/stream/ws?interval=1000

hello
```
```

With `baseUrl: "{{DEMO_API_URL}}"` on the collection and `DEMO_API_URL=localhost:5298`, this resolves to `ws://localhost:5298/demo/stream/ws?interval=1000`. The same collection + var also serve plain HTTP requests off `http://localhost:5298`.

### 5.5 Assertions

`assertions:` declares what a passing response looks like. Every entry is an **extractor**
(what to read out of the response) paired with a **matcher** (how to compare it), plus
optional modifiers:

```yaml
assertions:
- status: 2xx
- header: content-type
  contains: application/json
- jsonpath: $.order.customer.email
  equals: '{{user.email}}'
- jsonpath: $.order.lines
  count: 3
- jsonpath: $.error
  exists: false
- xpath: /order/total
  gt: 100
- regex: '"id":\s*"ord-\d+"'
- duration:
    lt: 800
- name: order id
  jsonpath: $.order.id
  matches: ^ord-\d+$
  skip: true
```

Assertions never change what is sent and never fail the exchange — they annotate the
result. A request that is *supposed* to 404 asserts `status: 404` and passes.

#### Extractors — exactly one per entry

| Key | Argument | Reads |
|---|---|---|
| `status` | — | Response status code, as a number. |
| `duration` | — | Total elapsed milliseconds, including redirects. |
| `header` | header name | First value of that header. Name match is case-insensitive. |
| `body` | — | The decoded response body, as text. |
| `jsonpath` | [RFC 9535](https://www.rfc-editor.org/rfc/rfc9535) expression | Nodelist from the JSON-parsed body. |
| `xpath` | XPath 1.0 expression | Result of evaluating it over the XML-parsed body. |
| `regex` | .NET regex pattern | **Shorthand** — normalizes to `body` + `matches`. |

#### Matchers — at most one per entry

| Matcher | Applies to | Notes |
|---|---|---|
| `equals` / `notEquals` | anything | Type-coerced — see below. |
| `contains` / `notContains` | text, nodelist | Substring; over a multi-node result, membership. |
| `startsWith` / `endsWith` | text | |
| `matches` / `notMatches` | text | .NET regex, 2 s match timeout. |
| `lt` / `lte` / `gt` / `gte` | numbers | Fails with an explanation if either side isn't numeric. |
| `between` | numbers | Inclusive, two bounds: `between: [200, 299]`. |
| `in` | anything | Membership: `in: [200, 201, 204]`. |
| `exists` | `header`, `jsonpath`, `xpath` | `true` (the default) or `false`. |
| `count` | `jsonpath`, `xpath` | Number of matched nodes. Valid on an empty result. |
| `length` | everything but `status`/`duration` | Characters for text, elements for a JSON array, properties for an object, otherwise node count. |
| `type` | `jsonpath` | `string` \| `number` \| `boolean` \| `object` \| `array` \| `null`. |

Three shorthands cover the common cases:

- a scalar on an argument-less extractor means `equals` — `- status: 200`;
- an argument-taking extractor alone means `exists` — `- header: etag`;
- `regex:` means `body` + `matches`.

Because `header`, `jsonpath`, and `xpath` need the key's value slot for their selector,
their matcher goes alongside as a sibling key. The others have that slot free, so the
matcher goes into it (`- duration:` then an indented `lt: 800`). Both layouts parse
either way — the difference is only indentation, and it is invisible in an editor.

#### Modifiers

| Key | Meaning |
|---|---|
| `name` | Display label. Generated from the assertion when absent. |
| `skip` | `true` — listed but not evaluated; counts as neither passed nor failed. |
| `ignoreCase` | `true` — case-insensitive string comparison. |

#### Semantics

- **Coercion.** Expected values are always strings — a `{{var}}` can only expand to one.
  So if both sides parse as numbers they compare as numbers (`equals: '129.50'` matches
  the JSON number `129.5`); if both parse as booleans, as booleans; otherwise as text.
- **Status classes.** For `status` only, an expected value of `2xx` / `20x` matches with
  `x` as a wildcard digit. Everywhere else `x` is just a letter.
- **JSONPath cardinality.** Zero nodes: only `exists: false` and `count: 0` pass; anything
  else fails with *did not match anything*. One node: the matcher applies to its value, and
  a JSON string compares as its contents rather than its quoted literal. Several nodes:
  `count`, `length`, `contains`, and `notContains` work; the rest fail with
  *matched N nodes*, rather than silently picking the first.
- **A wrong body type is a failed assertion, not an error.** `jsonpath` against a non-JSON
  body, a malformed selector, an invalid regex, a regex that times out — each fails that
  one assertion with an explanation. The evaluator never throws.
- **Variables.** Selectors and expected values expand through the same cascade as the
  request itself (§3), so `equals: '{{user.email}}'` compares against the very value the
  request was built with. When an expected value resolves through something marked secret,
  the reported expectation is masked as `***`.
- **Truncated bodies.** Response capture stops at 2 MiB. Past that, body/`jsonpath`/
  `xpath`/`regex` assertions fail with *body truncated* rather than matching a prefix and
  claiming a pass the full response might not have earned.
- **Streams.** For `text/event-stream`, status/header/duration assertions behave normally
  and body-family assertions run against the captured stream text once it ends.
- **WebSocket** requests (§5.4) parse and keep their assertions but report them as skipped —
  frame assertions are not modelled yet.
- Errors in an `assertions:` block are reported as `E_ASSERT_INVALID`, naming the offending
  entry by position.

---

## 6. `collection` — `_collection.md`

A top-level group of requests, owning the base URL, optional named stages, default auth, default headers, plus collection-scoped variables and tags. Lives at `collections/<slug>/_collection.md`.

### 6.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"collection"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `baseUrl` | string | no | May contain `{{vars}}`. Scheme is optional — bare `host:port` is rendered with `http://` for normal requests and `ws://` for `protocol: websocket` requests. Required if any request inside writes a relative URL. |
| `defaultAuth` | path \| id-ref | no | Auth profile inherited by every request in the collection that doesn't pin its own `auth:`. |
| `defaultHeaders` | map<string,string> | no | Merged under request-specific headers. Values may contain `{{vars}}`. |
| `transport` | mapping | no | `ignoreTlsErrors: <bool>` and/or `timeoutMs: <int ≥ 0>` — inherited by member requests; a request's own `transport` overrides per key. |
| `vars` | map<string, var-spec> | no | Collection-scoped variables. Cascade tier between workspace and stage. |
| `stages` | sequence of stage | no | Named per-stage overrides (e.g. `dev`/`staging`/`prod`). Each stage requires a `name` (unique within the collection, case-insensitive) and may override `baseUrl`, `defaultAuth`, and `vars`. |
| `defaultStage` | string | no | Stage to preselect when no explicit stage is passed. Must name a defined stage — anything else is a parse error. |
| `tags` | string[] | no | |
| `agent` | bool \| mapping | no | Agent-surface policy. `agent: false` (or `agent: { enabled: false }`) fences the collection off from AI agents: its requests disappear from agent discovery, and the MCP tools and `tap-studio call` refuse to describe, send, or call into it (`E_AGENT_ACCESS_DISABLED`). The Studio UI, `send`, and `test` are unaffected — this is policy for agents, not a sandbox. Absent means enabled. The mapping form is reserved for finer-grained controls later. |

### 6.2 Example

```markdown
---
kind: collection
id: 0192-3a4d-9000-7a01-1234-5678-9abc-def0
name: Stripe
baseUrl: https://api.stripe.com
defaultAuth: ../../auth/stripe-bearer.auth.md
defaultHeaders:
  Stripe-Version: "2025-04-30"
  Accept: application/json
stages:
- name: live
- name: test
  baseUrl: https://api.stripe.com
  defaultAuth: ../../auth/stripe-test-bearer.auth.md
defaultStage: live
---

# Stripe

[Public docs](https://stripe.com/docs/api). Every request below this collection
inherits the baseUrl, default auth, and default headers automatically.
```

---

## 7. `env` — `*.env.md`

A named environment. Activated by Tap at execute time; supplies the env tier of the variable
cascade and binds provider prefixes for the duration of the run.

### 7.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"env"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `vars` | map<string, var-spec> | no | Env-tier variables. Same var-spec shape as §5.1, including `secret: true`. |
| `defaultVariableProvider` | string | no | Provider (or alias) that bare `{{name}}` tokens hit first — and that receives un-targeted variable writes — while this env is active. Overrides the workspace/system default. |
| `providerAliases` | map<string, string> | no | Alias → provider-name bindings. Requests use a stable prefix (`{{kv:secret}}`); each env points the alias at its own provider (`kv: kv-dev` vs `kv: kv-prod`). |
| `strictVariables` | boolean | no | With a `defaultVariableProvider` set: bare `{{name}}` lookups that miss it fail instead of falling through to other providers. Recommended for one-vault-per-environment setups. |

A `vars` value is a literal (or a var-spec object). Because expansion is single-pass (§3.2),
a `{{provider:name}}` token written inside a var's value is **not** re-expanded when the
variable is referenced — to pull a provider value into a request, write the provider token
directly where it's needed, or bind the prefix with `providerAliases` so one spelling works
across environments.

The provider-binding fields make the one-vault-per-environment pattern work: declare
`kv-dev` and `kv-prod` once (in `tap.md` or the system settings), then have
`dev.env.md` bind `kv: kv-dev` and `prod.env.md` bind `kv: kv-prod`. Requests keep a
single spelling — `{{kv:clientSecret}}` — and switching the environment switches the
vault. Explicit `{{provider:name}}` tokens never fall through; `strictVariables`
extends the same guarantee to bare tokens.

### 7.2 Example

```markdown
---
kind: env
id: 0192-3a4d-c000-7e1f-...
name: Production
vars:
  api.baseUrl: https://api.stripe.com
  customer.email: noreply+prod@acme.example
defaultVariableProvider: kv
providerAliases:
  kv: kv-prod
strictVariables: true
---

# Production

Live Stripe. Destructive runs require approval from @billing. Requests reference
secrets as `{{kv:stripe-live-key}}`; with this env active, `kv` resolves against
the `kv-prod` vault, and every resolution is traced by name (never by value).
```

### 7.3 Variable scope and precedence

When Tap renders a request, it merges variable scopes in this order (later overrides earlier):

1. `tap.md` `vars`
2. Owning `_collection.md` `vars` (the collection the request lives under)
3. Active collection stage's `vars`
4. Active `env.md` `vars`
5. Request file `vars`
6. Per-run overrides (CLI `--var foo=bar`, UI form input; flow/test-set tiers per §10.5)

A later scope redefining a name also redefines its sensitivity — an env that overrides a
secret with a literal test value is no longer holding a secret.

The merged cascade wins over providers for bare `{{name}}` tokens; `{{provider:name}}`
bypasses it. Resolution is **single-pass**: values are substituted verbatim and never
re-scanned, so a variable cannot reference another variable (and reference cycles cannot
occur — the `E_VAR_CYCLE` code is reserved).

---

## 8. `auth` — `*.auth.md`

A reusable authentication profile. Used by requests via the `auth:` frontmatter field, or applied as a collection default.

### 8.0 Where a profile lives — workspace vs collection scope

An auth file may sit in either of two places, and the choice decides which variables its
fields can reference:

| Location | Scope | Cascade its fields resolve against |
|---|---|---|
| `auth/**/*.auth.md` | **workspace** — shared by every collection | workspace < env |
| `collections/<slug>/**/*.auth.md` | **collection** — owned by that collection | workspace < collection < stage < env |

Both are referenced the same way, by relative path from the referencing file:

```yaml
# a request inside collections/stripe/, pointing at a workspace profile
auth: ../../auth/stripe-bearer.auth.md

# the same request pointing at its own collection's profile
auth: stripe-oauth.auth.md
```

Pick collection scope when the profile's token URL, client id, audience, or credentials are
already expressed as collection (or stage) variables:

```yaml
# collections/stripe/_collection.md
kind: collection
name: Stripe
baseUrl: '{{API_URL}}'
defaultAuth: stripe-oauth.auth.md
vars:
  API_URL: https://api.stripe.test
  IDP_URL: https://idp.stripe.test
stages:
- name: dev
- name: prod
  vars:
    API_URL: https://api.stripe.com
    IDP_URL: https://idp.stripe.com
```

```yaml
# collections/stripe/stripe-oauth.auth.md
kind: auth
name: Stripe OAuth
type: oauth2
flow: client_credentials
tokenUrl: '{{IDP_URL}}/connect/token'   # resolves per stage
clientId: '{{STRIPE_CLIENT_ID}}'
```

Selecting the `prod` stage repoints `tokenUrl` without touching the profile. Runtime tokens
are cached **per stage and per environment**, so `dev` and `prod` never hand each other a
token; clearing a profile's token clears every stage/env combination at once. A
workspace-scoped profile has no stage, so its cache key carries only the env.

Nothing else changes with scope: a request in collection A may reference a profile owned by
collection B (it just resolves against B's variables, not A's), and a collection-scoped
profile still shows up in Studio's Auth tab alongside the shared ones.

### 8.1 Common frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"auth"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `type` | enum | yes | `none` \| `basic` \| `bearer` \| `apiKey` \| `oauth2` \| `azure-cli` \| `jwt` \| `github` \| `aws-sigv4` \| `custom`. |

Any type-specific field may contain `{{var}}` / `{{provider:name}}` tokens; they expand
against the profile's own scope (§8.0) when the profile is used. Fields that carry
credentials should reference a provider token rather than a literal.

The types split into two families:

- **Inline** — `none`, `basic`, `bearer`, `apiKey`, `custom`, and `github` in `pat` mode.
  The renderer derives the headers directly from the profile's fields; no runtime exchange.
  A `type: none` profile is the explicit opt-out: point a request's `auth:` at one to
  suppress an inherited collection default.
- **Runtime-token** — `oauth2`, `azure-cli`, `jwt`, and `github` in any mode past `pat`.
  Executing the profile mints a token (§8.5–§8.8) which is cached in the host's token store
  — never in a workspace file — and stamped as `Authorization: Bearer …` at execute time.
  Interactive flows (authorization-code, device-code) need a human present: the Studio UI
  runs them; the CLI reuses the Studio's cached tokens.

### 8.2 `bearer`

| Field | Type | Required |
|---|---|---|
| `token` | string | yes |

Injects `Authorization: Bearer <token>`.

### 8.3 `basic`

| Field | Type | Required |
|---|---|---|
| `username` | string | yes |
| `password` | string | yes |

### 8.4 `apiKey`

| Field | Type | Required | Notes |
|---|---|---|---|
| `in` | enum: `header` \| `query` \| `cookie` | yes | Only `header` is applied today — `query` and `cookie` parse but are not yet injected. |
| `apiKeyName` | string | yes | The header name. (The universal `name:` field is the profile's *display* name and is never read as the key name.) |
| `apiKeyValue` | string | yes | Legacy key `value` is also read. |

### 8.5 `oauth2`

| Field | Type | Required | Notes |
|---|---|---|---|
| `flow` | enum | no | `authorization_code` \| `authorization_code_pkce` *(default)* \| `client_credentials` \| `device_code` (alias `device`) \| `password` (aliases `resource_owner`, `ropc`). Legacy key `grantType` is also read. |
| `useDiscovery` | bool | no | With `authority`: fill missing endpoints from the OIDC discovery document. |
| `authority` | string | conditional | Issuer base URL. Required when `useDiscovery` is set. |
| `authorizeUrl` | string | conditional | Required for the authorization-code flows unless discovered. |
| `tokenUrl` | string | conditional | Required for every flow except `device_code` unless discovered. |
| `deviceAuthorizationUrl` | string | conditional | `device_code` only, unless discovered. |
| `clientId` | string | yes | |
| `clientSecret` | string | no | Not needed for public clients (PKCE-only). |
| `scopes` | string[] | no | |
| `audience` | string | no | |
| `username` / `password` | string | conditional | `password` flow only. |

The redirect URI is **owned by the runtime**, not the profile: the Studio derives it from its
own base URL on every run (ports move between boots), shows the live value read-only so you
know what to register with the identity provider, and ignores any `redirectUri` written into
the file.

Acquired access tokens, refresh tokens, and expiry timestamps live in the host's token store,
keyed per profile + stage + env (§8.0). They are never written to a workspace file.

### 8.6 `azure-cli`

Shells out to `az account get-access-token`; requires a prior `az login`. Dispatches on
`flow`:

**`flow: direct`** *(default)*:

| Field | Type | Required | Notes |
|---|---|---|---|
| `scope` | string | one of | v2 scope (e.g. `https://graph.microsoft.com/.default`). |
| `resource` | string | one of | v1 resource URI — `az` accepts either. |
| `tenant` | string | no | Alias `tenantId`. |
| `subscription` | string | no | |

**`flow: on_behalf_of`** *(alias `obo`)* — chains the az step with an AAD JWT-bearer
exchange: `az` mints a user token for the middle-tier API, then Tap posts it to the token
endpoint with `requested_token_use=on_behalf_of` to obtain a downstream token.

| Field | Type | Required | Notes |
|---|---|---|---|
| `userScope` / `userResource` | string | one of | What the az step requests (the middle-tier API). |
| `clientId` | string | yes | The middle-tier app performing the exchange. |
| `clientSecret` | string | no | |
| `scopes` | string[] | no | Downstream API scopes. |
| `tenant` | string | conditional | Alias `tenantId`. With no `tokenUrl`, the AAD v2 endpoint is derived from it. |
| `tokenUrl` | string | conditional | Required unless `tenant` is set. |

### 8.7 `jwt`

The renderer mints and signs a JWT itself and uses it as the Bearer token.

| Field | Type | Required | Notes |
|---|---|---|---|
| `algorithm` | string | no | Default `HS256`. Supported: `HS256/384/512` (key = the field's UTF-8 bytes), `RS256/384/512`, `ES256/384/512`, `PS256/384/512` (key = PEM-encoded private key). |
| `key` | string | yes | HMAC secret or PEM private key. Reference a provider token rather than pasting key material. |
| `keyId` | string | no | Alias `kid`; emitted into the JWT header. |
| `expiresIn` | int (seconds) | no | Default `3600`. |
| `payload` | string (JSON) | no | Claims object. `iss`/`exp`/`iat`/`jti`/`sub`/`aud` are auto-filled; anything in `payload` overrides them. |

Top-level `issuer` / `audience` / `subject` keys from older files are still read, but the
Studio rewrites them into `payload` on save — claims are payload, not frontmatter.

### 8.8 `github`

Dispatches on `mode`; adds the standard GitHub API headers automatically.

| `mode` | Fields | Behavior |
|---|---|---|
| `pat` *(default)* | `token` | Static — the renderer stamps `Authorization: Bearer <token>` directly. |
| `gh-cli` (aliases `ghcli`, `cli`) | — | Shells out to `gh auth token`; requires a prior `gh auth login`. |
| `app` | `appId`, `installationId`, `privateKey` (PEM) | Mints a short-lived RS256 App JWT, exchanges it for an installation token (`ghs_*`), honors GitHub's `expires_at`. |
| `oauth` | `clientId`, `clientSecret`, `scopes[]` | Authorization-code flow against github.com. |

### 8.9 `aws-sigv4` — reserved

| Field | Type | Required |
|---|---|---|
| `region` | string | yes |
| `service` | string | yes |
| `accessKeyId` | string | yes |
| `secretAccessKey` | string | yes |
| `sessionToken` | string | no |

The type and its fields are accepted by the parser and the Studio editor, but **request
signing is not implemented yet** — a request using this profile currently renders without an
Authorization header.

### 8.10 `custom`

| Field | Type | Required | Notes |
|---|---|---|---|
| `headers` | map<string, string> | no | Headers injected as-is (values interpolated per §3.2). |
| `query` | map<string, string> | no | Parsed and round-tripped, but not yet applied to the outgoing request. |

Use sparingly. If you find yourself reaching for `custom`, file an issue — likely there's a first-class type missing.

---

## 9. `collection` — `_collection.md`

A **collection** is the top-level grouping for requests. Each collection lives at
`collections/<slug>/_collection.md`; the slug is the directory name and serves as the
collection's id-on-disk. Nested directories below the collection are pure grouping
(no metadata, no inheritance) — every request, no matter how deeply nested, belongs
to exactly one collection.

### 9.1 Frontmatter

See §6 — collections own the base URL, default headers, default auth, stages, vars, and tags. This section is retained for navigation only; the canonical schema lives in §6.

---

## 10. `flow` — `*.flow.md`

A **flow** runs several requests in order and carries values from one response into the next.
It is the answer to "does this multi-step exchange still work" — create an order, read the id
out of the response, fetch the order back by that id.

Flows live in `tests/` beside test sets (§11). A flow references requests from any collection,
so it isn't owned by one.

### 10.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"flow"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `vars` | map<string, var-spec> | no | Flow-scoped variables. Sit above every file scope in the cascade — see §10.5. |
| `steps` | sequence of step | no | Ordered. Absent or empty is a flow nobody has finished writing — it loads, and running it does nothing. |
| `tags` | string[] | no | |

### 10.2 Steps

| Field | Type | Required | Notes |
|---|---|---|---|
| `request` | path \| id-ref | yes | The request this step sends. Path is relative to the flow file. A step never inlines a request. |
| `name` | string | no | Display label. Defaults to the referenced request's name. |
| `vars` | map<string, string> | no | Per-step overrides. Values are templates, expanded against the run bag *before* the request renders — this is what lets `id: '{{orderId}}'` read an earlier step's output. |
| `extract` | array of extractions | no | Binds response values to variable names for the steps that follow. See §10.3. |
| `assertions` | array of assertions | no | The §5.5 grammar, evaluated against this step's response *in addition to* the ones the request file declares. |
| `continueOnFailure` | bool | no | `true` keeps the flow going after this step fails. Default `false`. |
| `skip` | bool | no | `true` — listed but not run, and it binds nothing. |

```yaml
---
kind: flow
name: Checkout
vars:
  sku: ABC-1
steps:
- name: Create order
  request: ../collections/demo/create-order.req.md
  vars:
    item: '{{sku}}'
  extract:
  - var: orderId
    jsonpath: $.order.id
  - var: etag
    header: etag
  assertions:
  - status: 201
- name: Fetch it back
  request: ../collections/demo/get-order.req.md
  vars:
    id: '{{orderId}}'
  assertions:
  - jsonpath: $.order.id
    equals: '{{orderId}}'
---
```

A failed step stops the flow: everything after it would run against a state that never
happened. `continueOnFailure: true` opts one step out of that.

### 10.3 Extractions

An `extract:` entry is a `var` — the name it binds — plus **exactly one source**. The sources
are the assertion extractors of §5.5, so there is one vocabulary to learn:

| Key | Argument | Binds |
|---|---|---|
| `status` | — | The status code. |
| `duration` | — | Elapsed milliseconds. |
| `header` | header name | That header's first value. |
| `body` | — | The whole decoded body. |
| `jsonpath` | [RFC 9535](https://www.rfc-editor.org/rfc/rfc9535) expression | The matched node as text — a JSON string binds its contents, not its quoted literal. |
| `xpath` | XPath 1.0 expression | The matched node's value. |
| `regex` | .NET pattern | A capture group of the first match. |

| Modifier | Meaning |
|---|---|
| `group` | `regex` only — which capture group to bind. Default 1, or 0 when the pattern declares no groups. |
| `default` | Value bound when the source matches nothing, instead of failing the step. |
| `required` | `false` — bind nothing and carry on when the source matches nothing. |

```yaml
extract:
- var: orderId
  jsonpath: $.order.id
- var: token
  regex: 'session=([^;]+)'
  group: 1
- var: page
  header: x-page
  default: '1'
```

A missing value is a **step failure**, not an annotation — unlike an assertion. The next step
is about to send `{{orderId}}`, and reporting that at the extraction beats reporting it as a
strange URL two steps later. `default:` and `required: false` are the two ways to say the value
is genuinely optional. A JSONPath matching several nodes is an error rather than a silent
first-node pick — the same rule §5.5 applies.

Bound values are **run-scoped**: nothing is written back to a file or a variable provider.

### 10.4 Variable names

A bound name enters the run bag and is read as an ordinary `{{name}}` token by every later
step — in its request's URL, headers, body, assertions, and its own `vars:`. Binding a name
that also exists in a file scope shadows it for the rest of the run.

### 10.5 Precedence

The cascade of §7.3 is unchanged; a run supplies its top tier — the overrides. Within that
tier, later wins:

1. test-set `vars` (§11), when the flow runs inside one
2. the test entry's `vars:` (§11.2), when one named this flow
3. flow `vars`
4. values bound by `extract:` as the run progresses
5. the step's own `vars:`

Entry variables land below extraction on purpose: a flow whose extracted id could be
overridden by the set that called it isn't a flow any more. Pin a value by not extracting
over it.

Extraction beating the static tiers is the point: step 2 has to see step 1's output. An author
who wants a value pinned simply doesn't extract over it.

---

## 11. `test` — `*.test.md`

A **test set** is a named group of checks: set-scoped variables plus a list of tests, each of
which runs either one request or one flow, and passes when nothing it asserts fails.

Test sets live in `tests/` at the workspace root.

### 11.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"test"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `vars` | map<string, var-spec> | no | Set-scoped variables. Override every file scope — see §10.5. |
| `onFailure` | enum: `continue` \| `stop` | no | `continue` (default) runs every test regardless. `stop` aborts the set at the first failure. |
| `tests` | sequence of test | no | Ordered. Absent or empty is an unfinished set — it loads, and running it does nothing. |
| `tags` | string[] | no | |

### 11.2 Tests

Each entry names **exactly one** of `request:` or `flow:`.

| Field | Type | Required | Notes |
|---|---|---|---|
| `request` | path \| id-ref | one of | A request to send. Path is relative to the test file. |
| `flow` | path \| id-ref | one of | A flow (§10) to run. |
| `name` | string | no | Display label. Defaults to the referenced file's name. |
| `vars` | map<string, string> | no | Per-test overrides. Templates, expanded against the run bag first. |
| `assertions` | array of assertions | no | The §5.5 grammar. For a `request:` entry, checked against its response; for a `flow:` entry, against the **last step's** response — the one a caller of the flow sees. |
| `skip` | bool | no | `true` — listed but not run. |

```yaml
---
kind: test
name: Order API
vars:
  customer: cus_demo
onFailure: continue
tests:
- name: Rejects an unknown SKU
  request: ../collections/demo/create-order.req.md
  vars:
    item: nope
  assertions:
  - status: 404
- name: Full checkout
  flow: ./checkout.flow.md
---
```

### 11.3 Semantics

- A test **passes** when every assertion that ran passed and every step completed. Skipped
  assertions count as neither.
- A request's own `assertions:` and the entry's are both evaluated, the request's first. A
  contradiction between them is reported as a failure rather than silently resolved — if a
  request asserts `status: 2xx` and a test asserts `status: 404`, one of them is wrong and
  the author should see which.
- Extractions belong to flows. A `request:` entry has nothing to feed, so it declares none.
- A request that fails to render or never reaches the wire fails its test; its assertions are
  reported as not run.
- Errors in a `tests:` or `steps:` block are reported as `E_TEST_INVALID` (test sets) or
  `E_FLOW_INVALID` (flows), naming the offending entry by position. A malformed *assertion*
  inside one keeps its own `E_ASSERT_INVALID` — the code names the actual defect — with the
  entry's position prefixed to the message.
- **WebSocket requests** (§5.4) can't be run from a flow or a test set yet: frame assertions
  aren't modelled, so a step targeting one fails with an explanation rather than pretending.
- A run stops after 500 requests. A test set that large is really several.

---

## 12. Variable providers

A **variable provider** is a named, typed source of values that `{{name}}` and
`{{provider:name}}` tokens resolve against (§3.2). Providers are declared in `tap.md`'s
`variableProviders:` array (§4.1) or at system scope in the host's settings; a workspace
provider shadows a same-named system one. Each provider reports per-value sensitivity
(`IsSecret`), which drives masking and redaction everywhere a value could be echoed.

### 12.1 Built-in provider types (v0)

| Type | Source | Mode | Settings |
|---|---|---|---|
| `env` | Process environment variables, gated by the **host allowlists** (below) | read | none — the gate deliberately lives on the host, not in files |
| `file` | A YAML store per provider at `<workspace>/.tap/.vars/<name>.yml`; `secret: true` values are encrypted at rest (AES-256-GCM, key derived from a passphrase) | read/write | `encryptionKey` — better supplied via the `TAP_FILE_PROVIDER_KEY` env var (or `TAP_FILE_PROVIDER_KEY_<NAME>`) than committed next to the ciphertext |
| `azkv` | Azure Key Vault via `DefaultAzureCredential` (picks up `az login`, managed identity, …). Every value is secret. | read | `vaultName` (required), `tenantId`, `prefix` |
| `1p` | 1Password via the `op` CLI (desktop-app / biometric auth on the host) | read | `mode`: `environment` (default; a 1Password Environment's variables), `item` (`vault` + `item` — one item's fields), or `vault` (`vault` — one variable per item) |
| `system` | The host's `system.json` settings store — the same file the Settings UI edits. Always registered; no declaration needed. | read/write | — |

#### `env` allowlist

The `env` provider is the only one whose reachable surface is bounded by the **host process**
rather than the workspace file — anything the Tap process can see in its own environment is
fair game otherwise, and the workspace is shared / checked in. Two process env vars gate
access; both take a comma-separated list of glob patterns where `*` is the only wildcard:

| Host env var | Effect |
|---|---|
| `TAP_VARS_ALLOWED` | Names whose values surface as plain variables (visible in the UI). Usable as `{{NAME}}` or `{{env:NAME}}`. |
| `TAP_SECRETS_ALLOWED` | Names whose values resolve normally but stay masked everywhere in the UI and in echoed output. A name on both lists is treated as secret — stricter wins. |

Example:

```shell
export TAP_VARS_ALLOWED="VITE_*,ASPNETCORE_ENVIRONMENT,DEMO_API_URL"
export TAP_SECRETS_ALLOWED="DEMO_*_TOKEN,AZURE_*"
```

If neither variable is set, the `env` provider exposes nothing — deny-by-default is the safe
default. References to names that don't match either pattern list fail as unknown.

### 12.2 Resolution rules

- A resolution produces a string value plus an `IsSecret` flag.
- Resolutions are cached in memory for the duration of one render — two tokens naming the
  same value hit the provider once.
- An explicit `{{provider:name}}` whose provider (or alias target) isn't registered fails
  with `E_UNKNOWN_PROVIDER`. A provider that can't produce the value (CLI missing, vault
  unreachable, name absent) fails with `E_PROVIDER_RESOLUTION_FAILED`.
- Writes route to the env's default provider (or an explicitly named one); routing a write
  to a read-only provider fails with `E_PROVIDER_NOT_WRITABLE`. A file-provider secret that
  can't be decrypted reports `E_PROVIDER_DECRYPT_FAILED`.
- Execution history records **which** provider/name pairs were touched and whether each was
  secret (`variablesUsed`, §13) — never the values. Anything echoed to an agent surface is
  additionally scrubbed by the render's redactor (§13).

### 12.3 Adding a custom provider

Tap will support out-of-tree providers via a plugin model (post-v0). For v0 the built-in set is the supported surface.

---

## 13. Rendering: from files to a ResolvedRequest

`Tap.Workspace.Rendering.WorkspaceRenderer.RenderAsync(request, env, overrides, …)` produces a `ResolvedRequest`:

```
ResolvedRequest {
  method:      "POST"
  url:         "https://api.stripe.com/v1/customers"
  headers:     { "Content-Type": "application/x-www-form-urlencoded",
                 "Stripe-Version": "2025-04-30",
                 "Accept": "application/json",
                 "Authorization": "Bearer sk_live_***" }
  body:        "email=jane%40example.com&name=Jane%20Doe"
  binaryBody:  null                      ← bytes when the body was a `< ./file` reference (§5.2)
  protocol:    http | websocket
  transport:   { ignoreTlsErrors, timeoutMs }
  assertions:  [ …selectors and expected values, already expanded… ]
  redactor:    scrubs this render's secret values from any echoed output
  metadata: {
    sourceRequestPath, envPath, stageName, resolvedBaseUrl,
    variablesUsed: [ { provider, name, isSecret } ]   ← names only, never values
  }
}
```

The render pipeline:

1. Load the request file; locate the owning collection by walking the request path
   (`collections/<slug>/…`); pick the active stage (explicit, else `defaultStage`); resolve
   the auth ref — the request's `auth:`, else the stage's `defaultAuth`, else the
   collection's; merge transport settings (request over collection).
2. Build the merged variable cascade (§7.3), tracking which names are secret. Template-valued
   overrides (a flow step's `vars:`) are expanded against the cascade in order, each seeing
   the ones before it.
3. Expand `{{…}}` tokens in the fenced `http` block — cascade first, then providers (§3.2) —
   and parse it into method / URL / headers / body.
4. If the URL is relative, expand the stage-or-collection `baseUrl`, normalize its scheme
   (bare `host:port` gains `http://`, or `ws://` for websocket requests; `http(s)` is
   rewritten to `ws(s)` for websocket), and join. Failing that, `E_HTTP_BLOCK_SYNTAX`.
5. Merge headers: collection `defaultHeaders` (each value expanded) under the block's
   headers, under auth-derived headers (§8's inline family). Every source is interpolated
   exactly once — an expanded value is never re-scanned (§3.2) — and the assembled request
   line and headers are rejected if anything smuggled in a line break.
6. Render the request's assertions against the same cascade; an expected value that pulled
   in anything secret is flagged so reports mask it as `***`.
7. Build the redactor from every secret value the registry resolved, every secret cascade
   value that won its name, and the auth-derived header names.

The executor then adds what only it can know: for runtime-token profiles (oauth2, azure-cli,
jwt, github past PAT) it stamps `Authorization: Bearer …` from the token store — scoped per
profile + stage + env, and the minted token joins the redaction set; a `< ./file` body
reference is loaded from disk (workspace-scoped); and Tap Studio stamps
`User-Agent: tap-studio/<version>` when the rendered headers don't already carry one
(case-insensitive) — a `User-Agent` set on the request, the collection's `defaultHeaders`,
or the auth profile always wins.

Every echo of a rendered request to an agent surface (CLI `--json`, MCP results) passes
through the redactor; `metadata.variablesUsed` is the audit trail of which providers and
names were consulted.

---

## 14. References between files

Two ways to point from one file to another:

1. **Relative path** (recommended for v0): `auth: ../../auth/stripe-bearer.auth.md`, or `auth: stripe-oauth.auth.md` for a sibling inside the same collection. Paths resolve relative to the file that declares them and never escape the workspace root. Survives `git mv` provided both files move together. Clearer in diffs.
2. **Id reference**: `auth: id:0192-3a4d-9000-...`. Tap maintains an index built from `id:` fields. Survives rename without coordinated moves but requires the index to be up-to-date.

The parser accepts both, normalizes internally to a canonical `WorkspaceRef`. Tap's writer always emits relative paths.

---

## 15. Versioning, IDs, and stability

- A new file with no `id:` gets a UUIDv7 assigned by the writer on first save.
- The id is the durable identity. Renaming the file preserves the id.
- Cross-file `id:` references resolve through the workspace index; if an id has no owner, references to it produce `E_DANGLING_REF`.
- The format version is implicit in this document. Files do not carry a version field in v0; a future breaking change will introduce a `tapFormat: "1.x"` field in `tap.md`.

---

## 16. Parse errors (canonical)

| Code | Meaning |
|---|---|
| `E_FRONTMATTER_MISSING` | File has no `---` fenced frontmatter block. |
| `E_FRONTMATTER_MALFORMED_YAML` | Frontmatter is not valid YAML 1.2 (also reported for unreadable or oversized files). |
| `E_KIND_MISMATCH` | `kind:` does not match the filename suffix, or the filename matches no known suffix. |
| `E_KIND_MISSING` | `kind:` field absent. |
| `E_UNKNOWN_FIELD` | A frontmatter field is unrecognized or malformed for that kind (bad `protocol:`, duplicate stage name, dangling `defaultStage`, invalid `agent:` shape, duplicate path/id in the index, …). |
| `E_NO_REQUEST_BLOCK` | A `request` file has no fenced `http` block. |
| `E_MULTIPLE_REQUEST_BLOCKS` | A `request` file has more than one fenced `http` block. |
| `E_DANGLING_REF` | A `path` or `id:` reference does not resolve. |
| `E_VAR_UNKNOWN` | A `{{name}}` token is neither in the cascade nor resolvable by any provider. |
| `E_VAR_CYCLE` | Reserved. Expansion is single-pass (§3.2), so reference cycles cannot currently occur. |
| `E_UNKNOWN_PROVIDER` | A `{{provider:name}}` token (or the target of an env alias) names a provider that isn't registered. |
| `E_PROVIDER_RESOLUTION_FAILED` | A registered provider failed to produce the value — CLI not installed, vault unreachable, name absent. |
| `E_PROVIDER_CONFIG_INVALID` | A `variableProviders:` entry is unusable — invalid name shape, bad settings. |
| `E_PROVIDER_NOT_WRITABLE` | A variable write was routed to a read-only provider (`env`, `azkv`, `1p`). |
| `E_PROVIDER_DECRYPT_FAILED` | The `file` provider could not decrypt a stored secret (missing or wrong passphrase). |
| `E_AUTH_TYPE_INVALID` | Auth `type:` is missing or not a recognized value. |
| `E_HTTP_BLOCK_SYNTAX` | The fenced `http` block fails to parse; also render-time URL problems — a relative URL with no collection baseUrl, a line break smuggled into the request line or a header, a token scan that timed out. |
| `E_ASSERT_INVALID` | An entry under `assertions:` is not a usable (extractor, matcher) pair — see §5.5. |
| `E_FLOW_INVALID` | A `steps:` entry is malformed — no `request:`, two extraction sources, an unknown key. See §10. |
| `E_TEST_INVALID` | A `tests:` entry is malformed — neither or both of `request:`/`flow:`, an unknown key. See §11. |
| `E_WORKSPACE_LOAD_FAILED` | The workspace root could not be read at all. The workspace loads empty carrying this error rather than failing the host. |
| `E_WORKSPACE_SCAN_TRUNCATED` | The folder walk hit its budget (20s / 25 000 folders) and the workspace is only partially loaded — the root is far too broad. Walks skip `node_modules`, `.git`, `.hg`, `.svn`, `.venv`, `__pycache__`, `bin`, `obj`, `target`, and never follow symlinked directories. |
| `E_DYNAMIC_URL_NOT_COLLECTION_SCOPED` | An agent-supplied dynamic request's URL was (or rendered) absolute without the caller explicitly allowing it — the guard against combining a dynamic URL with inherited auth headers. |
| `E_DYNAMIC_REQUEST_INVALID` | A dynamic request named a missing collection, or its method/URL/headers were malformed. |
| `E_AGENT_ACCESS_DISABLED` | An agent surface tried to use a collection whose `agent:` option disables agent access (§6.1). |

---

## 17. Out of scope for v0

The following are deliberate omissions, slated for later versions:

- Assertions on responses: now a first-class request field via `assertions:` (§5.5). Still
  out of scope — collection-level default assertions and assertions on WebSocket frames.
- Request chaining: now a first-class kind via `*.flow.md` (§10), grouped into test sets by
  `*.test.md` (§11). Still out of scope — parallel execution, data-driven tests (one test × N
  rows of variables), extracting a value back into a variable provider, per-step retry /
  wait-for polling.
- **`aws-sigv4` signing** (§8.9) and **`apiKey` injection into query/cookie** plus
  **`custom` query params** (§8.4, §8.10): the fields parse and round-trip today; the
  wire behavior is not implemented yet.
- **Pre-request and post-response scripts** (planned: a `scripts/` directory with TypeScript modules referenced from request frontmatter).
- **GraphQL request type** (handled today via the standard `application/json` body; a first-class GraphQL kind is a v0.2 candidate).
- **gRPC** as a first-class kind — captured read-only by the Tap tunnel for now.
- **SSE** as a first-class kind — `text/event-stream` responses on regular HTTP requests are already parsed and surfaced; no separate request type is needed.
- WebSocket: now a first-class request via `protocol: websocket` (§5.4). The executor opens the connection, sends the body (if any) as the first frame, and streams inbound frames back.
- **Multi-cursor environments** (overlays, env stacks). Single active env per execution in v0.

---

## 18. Worked example: end-to-end

Given the workspace from §2.1 and the files in §4.2, §5.3, §6.2, §7.2, §8.2:

```
$ tap-studio send collections/stripe/create-customer.req.md \
    --env environments/prod.env.md \
    --var customer.email=jane@example.com --var "customer.name=Jane Doe"
```

The renderer produces:

```http
POST https://api.stripe.com/v1/customers
Stripe-Version: 2025-04-30
Accept: application/json
Authorization: Bearer sk_live_***
Content-Type: application/x-www-form-urlencoded

email=jane%40example.com&name=Jane%20Doe
```

With `metadata.variablesUsed` recording the audit trail:

```
var     baseUrl                       ← collections/stripe/_collection.md
var     customer.email                ← --var
var     customer.name                 ← --var
secret  kv-prod : stripe-live-key     (isSecret — value never recorded)
```

This is the contract the rest of Tap (executor, diff viewer, capture-promotion flow, satellite) is built against.
