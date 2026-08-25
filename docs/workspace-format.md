# Tap Workspace Format

> Status: **draft v0**. Subject to change until v1.0. Every file Tap stores in your repo is plain Markdown with YAML frontmatter, so the format is reviewable as a normal git diff.

A Tap **workspace** is a folder holding a `workspace.tap` manifest plus the typed files described
below. Everything in it is meant to be checked into version control. Nothing in it ever
contains a secret value — secrets live in **variable providers** (§12); workspace files carry
only `{{provider:name}}` references, `secret: true` flags, or the file provider's encrypted
envelopes.

This document is the authoritative spec for the on-disk format. The Tap parser (`Tap.Workspace`) and renderer (`Tap.Workspace.Rendering`) implement exactly what's described here. If parser and spec disagree, the spec is the bug report.

---

## 1. Design goals

1. **Git-native.** Every artifact is plain text. Renames work via `git mv`. Diffs are readable.
2. **Composable.** A runnable request is the composition of *workspace + collection + auth + environment + request*. No single file is the whole story.
3. **Readable on GitHub.** Frontmatter is the structured part; the body is human prose. A request file is a documentation page that happens to be executable.
4. **Secret-safe by construction.** A literal secret never needs to occur in a workspace file. A field that needs one references a provider (`{{kv-prod:stripe-live-key}}`), or a variable declared `secret: true`; values resolve at render time, are traced by name only, and are redacted from anything echoed back out (§12.3, §13).
5. **One format, a handful of shapes.** Every file is `Markdown + YAML frontmatter`. The `kind` field plus the filename suffix tell the parser what shape to expect.
6. **No required tooling to read.** Any text editor or any Markdown renderer can display a workspace. Tap adds editing, validation, execution, and live preview on top.

---

## 2. File kinds

| Kind | Filename suffix | Purpose |
|---|---|---|
| `request` | `*.req.tap` | A single HTTP request template. |
| `auth` | `*.auth.tap` | A reusable authentication profile (bearer, oauth2, azure-cli, github, …). Lives either in `auth/` (workspace-scoped) or inside a collection (collection-scoped) — see §8.0. |
| `env` | `*.env.tap` | A named environment (variables plus provider bindings). |
| `collection` | `_collection.tap` *(at `collections/<slug>/`)* | A top-level group of requests. Owns the base URL, default auth, default headers, plus collection-scoped variables and tags. |
| `flow` | `*.flow.tap` | An ordered sequence of requests where each step can extract values from its response for the steps after it. Lives in `tests/` — see §10. |
| `test` | `*.test.tap` | A test set: named checks, each running one request or one flow, with set-scoped variables. Lives in `tests/` — see §11. |
| `workspace` | `workspace.tap` *(at workspace root)* | Workspace-level config: name, default env, registered variable providers. |

Sub-directories inside a collection are pure grouping for the explorer tree — they carry no metadata, no variables, and no inherited defaults. Every request below a collection inherits its baseUrl, default auth, default headers, and variables; variable sharing across a group of requests lives on `_collection.tap`.

An `*.auth.tap` placed inside a collection is *owned* by it: the profile's fields resolve against that collection's variables and its active environment. That's the only kind of file below a collection that inherits anything besides a request.

The filename suffix is canonical. The `kind:` frontmatter field is required and must match the suffix. A mismatch is a hard parse error.

### 2.0 The `.md` → `.tap` rename

Before 0.7.0 every workspace file ended in `.md` — `orders.req.md`, `_collection.md`, and a manifest called `tap.md`. Nothing about the format changed in the rename: same YAML frontmatter, same markdown body, same fenced `http` blocks, same kind-by-suffix rule. Only the trailing extension moved, so that a Tap file is recognizable as one rather than reading as documentation.

Both families load for the whole 0.7.x line:

| | Canonical (0.7.0+) | Legacy (still read) |
|---|---|---|
| Manifest | `workspace.tap` | `tap.md` |
| Collection | `_collection.tap` | `_collection.md` |
| Everything else | `*.req.tap`, `*.auth.tap`, `*.env.tap`, `*.flow.tap`, `*.test.tap` | `*.req.md`, … |

- **Reading** accepts either. A legacy file loads exactly as before and reports a `W_LEGACY_EXTENSION` warning, which does *not* fail `tap-studio lint`.
- **Writing** is always canonical. New files get `.tap`; an existing legacy file is saved back in place rather than renamed, because renaming it behind your back would orphan every reference pointing at it.
- **Migrating** is one command: `tap-studio migrate` (`--dry-run` to preview). It renames the files *and* rewrites the refs, which matters because a ref is a literal relative path carrying an extension — `auth: ../../auth/admin.auth.md` breaks the moment its target is renamed. Refs written as `id:` are unaffected.

Support for the legacy family is scheduled for removal in 0.8.0.

### 2.1 Suggested directory layout

```
my-service/
├── src/                                 ← your code
└── tap/                                 ← the workspace root — any folder you point Tap at
    ├── workspace.tap                           ← kind: workspace
    ├── auth/                            ← workspace-scoped profiles, shared by every collection
    │   ├── stripe-bearer.auth.tap
    │   └── corp-oidc.auth.tap
    ├── environments/
    │   ├── local.env.tap
    │   ├── staging.env.tap
    │   └── prod.env.tap
    ├── tests/                           ← test sets and flows
    │   ├── billing.test.tap
    │   └── checkout.flow.tap
    └── collections/
        └── stripe/
            ├── _collection.tap        ← kind: collection (owns baseUrl, default auth/headers)
            ├── stripe-live.env.tap    ← env assigned to this collection (collections: [stripe])
            ├── stripe-oauth.auth.tap  ← collection-scoped profile (sees this collection's vars)
            ├── create-customer.req.tap
            ├── get-customer.req.tap
            └── refunds/              ← pure-grouping sub-folder
                ├── issue.req.tap
                └── list.req.tap
```

The workspace root is simply the folder you open in Studio or pass to the CLI
(`--workspace`, defaulting to the nearest ancestor of the working directory that contains
`workspace.tap`). The loader walks that folder for the known suffixes — dotfolders included, so the
older `.tap/` sub-folder layout keeps loading — while skipping package/VCS caches
(`node_modules`, `.git`, `bin`, `obj`, …) and capping single files at 8 MiB. Tap also keeps
a housekeeping `.tap/` directory under the root for the file provider's variable store
(§12.1); that directory is data, not workspace files. A collection imported from an OpenAPI
description also carries `_openapi.lock.json`, which records the source document and the hashes
re-sync compares against — likewise data, and likewise never loaded as a workspace file.

The four top-level directories (`auth/`, `environments/`, `tests/`, `collections/`) are
structural: `auth/`, `environments/`, and `tests/` hold flat lists of typed files;
`collections/` hosts one sub-directory per collection. Inside each collection, nested
directories are freeform grouping with no metadata — variable sharing across a group of
requests lives on `_collection.tap`.

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

**A declared variable may hold a token of its own.** When `{{name}}` hits a variable a
`vars:` block declared, that variable's value is expanded in turn — which is what makes
`default: '{{file:stripe.key}}'` (§12.6) resolve to the value in the provider instead of
going out as the literal token. The chain is followed as far as it goes, each level starting
from the author's template, and a chain that closes on itself fails with `E_VAR_CYCLE` naming
every variable in the loop.

**Nothing else is re-scanned.** A value a provider returned, a value a flow step bound with
`extract:` (§10.3), and a per-run override (`--var`, the Studio's input form) are all emitted
verbatim, however token-shaped they look. That line is the whole point: re-scanning a value
that arrived in a response would let the upstream choose which secret the next request
carries.

There is **no separate secret syntax**. Whether a resolution is secret comes from the source:
a provider marks its values (Key Vault values are always secret; the env provider follows the
host allowlists), and a cascade variable is secret when its declaration says `secret: true`
(§5.1). Secret or not, the token is spelled the same way.

A literal `{` followed by `{` that you do not want interpolated is escaped as `\{{`.

---

## 4. `workspace` — `workspace.tap`

The single file at the workspace root. Created on `tap init`.

### 4.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"workspace"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `defaultEnv` | path | no | Workspace-root-relative path to the `.env.tap` file used when none is specified at execute time. |
| `variableProviders` | array of provider configs | no | Registers the named variable providers available in this workspace. See below and §12. |
| `defaultVariableProvider` | string | no | Provider that bare `{{name}}` tokens hit first after the cascade (and that receives un-targeted variable writes). An active env's own `defaultVariableProvider` overrides it. Legacy key `defaultProvider` is also read. |
| `vars` | map<string, var-spec> | no | Workspace-level variables (lowest precedence). Same var-spec shape as §5.1. |
| `response` | map | no | Response body caps — see below. |
| `history` | bool \| map | no | Whether exchanges are recorded to `.tap-history/` — see §4.3. Weakest tier; a collection or request overrides it per key. |

Each `variableProviders:` entry declares `name` (the `{{name:…}}` prefix), `type` (one of
the built-in types — §12.1), and optionally `settings`. Settings may sit under an explicit
`settings:` mapping or inline at the entry root; unknown scalar keys fall into the settings
bag either way. Provider names must match `[A-Za-z][A-Za-z0-9_-]*` — anything else is
rejected with `E_PROVIDER_CONFIG_INVALID` (file-backed providers combine the name into a
path, so separators or `..` would escape the workspace). The legacy `providers:` key is
still honored so older workspaces keep loading.

`response:` caps how much of a response body Tap keeps, and takes two optional sizes:

| Key | Default | Meaning |
|---|---|---|
| `maxBytes` | `2mb` | How much of the body is delivered inline — the Studio's body pane, a test run's captured body, the CLI's `--json` document — and evaluated by body assertions. Past it the body on screen is a prefix and body assertions report "not evaluated" rather than matching one. |
| `maxRetainedBytes` | `64mb` | How much the Studio holds back on disk so the truncation banner can offer **Show all** and a complete **Download**, without re-sending the request. Never delivered unless asked for. Raised to `maxBytes` if set below it; both are clamped to 1 GB. |

Sizes are a plain byte count or a number with a `kb` / `mb` / `gb` suffix (case-insensitive,
1024-based; `2mb`, `2 MB` and `2m` are the same). Anything else is rejected with
`E_UNKNOWN_FIELD` rather than falling back to the default — a typo'd cap that reads as "no
cap configured" is the surprise the field exists to remove. The retained copy lives in a temp
directory for the last few responses and is deleted when the Studio exits; it is a
convenience for the response you are looking at, not history.

Providers can also be registered at **system scope** (the host's settings store) — those are
available to every workspace, and a workspace provider with the same name shadows the system
one. The built-in `system` provider (§12.1) is always registered by the host.

### 4.2 Example

```markdown
---
kind: workspace
id: 0192-3a4c-bb71-7c1d-9e8f0a1b2c3d
name: acme-billing
defaultEnv: environments/local.env.tap
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
response:
  maxBytes: 8mb          # show more of a big report inline
  maxRetainedBytes: 256mb # …and keep enough of it to download whole
vars:
  app.userAgent: tap/0.5
---

# Acme Billing

API workspace for the billing service. Owned by @platform. Production access
runs through `prod.env.tap` and requires Azure Key Vault membership in the
`billing-eng` group.
```

### 4.3 `history:` — recording what you actually ran

`history:` turns on a durable record of the exchanges Tap runs. It is declarable at three
scopes — `workspace.tap`, `_collection.tap`, and a single `*.req.tap` — and merged **per key**,
nearest wins:

```yaml
history: true              # shorthand for { enabled: true }

history:                   # or the long form; every key is optional
  enabled: true
  maxEntries: 25           # per request, oldest pruned
  encrypt: false
  maxBodyBytes: 256kb      # same size grammar as `response:`
  orphanRetentionDays: 30  # workspace.tap only
```

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Record exchanges for this scope. Off unless something says otherwise — recording every response a workspace produces is a decision about someone's disk. |
| `maxEntries` | `25` | How many entries survive per request. Clamped to 1000. |
| `encrypt` | `false` | Encrypt each entry at rest, and store it **unredacted** — see below. |
| `maxBodyBytes` | `256kb` | Response body kept per entry. Far below `response.maxBytes` on purpose: history grows unattended. Clamped to 64 MB. |
| `orphanRetentionDays` | `30` | How long the history of a *deleted* request is kept. `workspace.tap` only — by the time a folder is orphaned, the collection and request that would configure it are what no longer exist. Rejected elsewhere with `E_UNKNOWN_FIELD`. |

A nearer scope overrides only the keys it names, so a collection can switch recording on for
everything under it while one noisy request sets `history: { enabled: false }` or a smaller
`maxEntries` without restating the rest.

**Where it goes.** One folder, `.tap-history/` at the workspace root, with a folder per request
id and one file per exchange named by UTC timestamp. The folder writes its own `.gitignore`
containing `*` on first use, so recorded traffic never reaches a commit because somebody forgot
a line in the repo's ignore file.

**Secrets.** An entry is *either* redacted and plaintext *or* unredacted and encrypted; there is
no third combination. With `encrypt: false` (the default), credential headers are masked by name
and every resolved secret is replaced by value wherever it landed — URL, body, response, and
assertion output — by the same redactor the CLI's `--json` and the MCP results use. With
`encrypt: true` the entry keeps what actually went on the wire and the whole document is sealed
in the AES-256-GCM envelope (§12.5) under the machine key. **If that key cannot be obtained, the
entry is not written at all** — never downgraded to plaintext, because encryption is what
licenses storing the secrets in the first place.

**Identity.** History is keyed by the request's `id:` (§3.1), so renaming or moving a request
keeps its history attached. A request with no id is not recorded; the Studio assigns one when it
saves, so in practice this only affects files it has never written. History whose request has
been deleted becomes an *orphan*: it stays readable (each entry records the request's name and
path as of recording), is marked as such in the timeline, is swept after
`orphanRetentionDays`, and re-links by itself if a file with that id comes back.

Today only the Studio's interactive **Send** records. `tap-studio send`, the MCP tools, and test
runs do not.

---

## 5. `request` — `*.req.tap`

A single executable request. The most-edited file kind.

### 5.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"request"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `auth` | path \| id-ref | no | Overrides the containing collection's `defaultAuth`. To opt a request out of an inherited default entirely, point it at a profile with `type: none` (§8.1). |
| `protocol` | enum: `http` \| `websocket` | no | Wire protocol. Default `http`. `websocket` drives baseUrl scheme normalization (http→ws, https→wss) and switches the executor to a WebSocket transport. See §5.4. |
| `history` | bool \| map | no | Recording policy for this request. Overrides its collection and the manifest per key — see §4.3. |
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
from anything echoed to agents (§13). Marking a variable secret in the Studio also moves its
value into a provider and leaves a reference behind, so the flag and the file agree about what
is in the repository — see §12.6.

### 5.2 Body

The body is CommonMark. **Exactly one** fenced code block tagged `http` carries the request template. All other content is documentation and ignored at execute time.

The `http` block follows the [VS Code REST Client / JetBrains HTTP Client](https://www.jetbrains.com/help/idea/exploring-http-syntax.html) syntax, with three extensions:

1. `{{var}}` and `{{provider:name}}` interpolation per §3.2 — in the request line, headers, and body.
2. The request line's URL may be a bare path (`/v1/customers`) — Tap prepends the containing collection's `baseUrl` (or, when the active environment declares one, the environment's override). If the URL is not absolute and neither answers, the render fails with `E_HTTP_BLOCK_SYNTAX`.
3. A body that is exactly one line of the form `< ./relative/path` is a **file reference**: the executor loads the file's bytes (resolved relative to the request file, clamped inside the workspace) and sends them as the body. The literal `< …` text is kept for display so captures show what was referenced.

If multiple `http` blocks are present, the parser fails with `E_MULTIPLE_REQUEST_BLOCKS`. If zero are present, the parser fails with `E_NO_REQUEST_BLOCK`.

### 5.3 Example

```markdown
---
kind: request
id: 0192-3a4d-7000-7b91-a0c1d2e3f405
name: Create customer
auth: ../../auth/stripe-bearer.auth.tap
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
- **Truncated bodies.** Response capture stops at the workspace's `response.maxBytes`
  (§4.1, 2 MiB by default). Past that, body/`jsonpath`/`xpath`/`regex` assertions fail with
  *body truncated* rather than matching a prefix and claiming a pass the full response might
  not have earned. Raising the cap is what makes them evaluate; the separately retained copy
  behind **Show all** / **Download** is for reading, not for asserting against.
- **Streams.** For `text/event-stream`, status/header/duration assertions behave normally
  and body-family assertions run against the captured stream text once it ends.
- **WebSocket** requests (§5.4) parse and keep their assertions but report them as skipped —
  frame assertions are not modelled yet.
- Errors in an `assertions:` block are reported as `E_ASSERT_INVALID`, naming the offending
  entry by position.

---

## 6. `collection` — `_collection.tap`

A top-level group of requests, owning the base URL, default auth, default headers, plus collection-scoped variables and tags. Lives at `collections/<slug>/_collection.tap`.

Per-target overrides — the `stages:` block that existed through 0.6.x — are **environments assigned to this collection** (§7), each assignment carrying the base URL and default auth it points this collection at. A collection file that still carries `stages:` or `defaultStage:` fails to parse with `E_UNKNOWN_FIELD`.

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
| `vars` | map<string, var-spec> | no | Collection-scoped variables. Cascade tier between workspace and env. |
| `tags` | string[] | no | |
| `history` | bool \| map | no | Recording policy inherited by every request in this collection. Overrides the manifest per key; a request overrides it in turn — see §4.3. |
| `agent` | bool \| mapping | no | Agent-surface policy. `agent: false` (or `agent: { enabled: false }`) fences the collection off from AI agents: its requests disappear from agent discovery, and the MCP tools and `tap-studio call` refuse to describe, send, or call into it (`E_AGENT_ACCESS_DISABLED`). The Studio UI, `send`, and `test` are unaffected — this is policy for agents, not a sandbox. Absent means enabled. The mapping form is reserved for finer-grained controls later. |

### 6.2 Example

```markdown
---
kind: collection
id: 0192-3a4d-9000-7a01-1234-5678-9abc-def0
name: Stripe
baseUrl: https://api.stripe.com
defaultAuth: ../../auth/stripe-bearer.auth.tap
defaultHeaders:
  Stripe-Version: "2025-04-30"
  Accept: application/json
---

# Stripe

[Public docs](https://stripe.com/docs/api). Every request below this collection
inherits the baseUrl, default auth, and default headers automatically.

`stripe-test.env.tap` next door scopes itself to this collection and swaps the
default auth for the test key — selecting it in the environment picker moves
every request below without touching this file.
```

```markdown
---
kind: env
name: Stripe test mode
collections:
- collection: stripe
  defaultAuth: ../../auth/stripe-test-bearer.auth.tap
---
```

---

## 7. `env` — `*.env.tap`

A named environment — the single mechanism for "the same requests, pointed somewhere else".
Activated by Tap at execute time; supplies the env tier of the variable cascade, binds
provider prefixes for the duration of the run, and may override the owning collection's
`baseUrl` and `defaultAuth`.

An environment is one of two kinds:

- **Global** — no `collections:` key. Selectable anywhere, in the header's workspace-wide
  environment switcher. This is the right shape for a `dev` / `prod` pair every collection
  shares.
- **Assigned** — a non-empty `collections:` list. Offered only while a request from one of
  those collections is in front of you, and applied only to them; carried into any other
  collection (by a test set that spans several, say) it silently drops out rather than
  contributing another collection's values. This is what a collection `stage` was.

Each assignment may carry a `baseUrl` and a `defaultAuth` — the two things a stage could do
that an env could not. **They belong to the assignment, not to the environment**, because
they are only ever true of one collection: an `uat` assigned to both `orders` and `billing`
points each at a different host. A global environment therefore has no base URL at all, which
is right — "the dev environment" is not a statement about one collection's address.

An assignment with no overrides is written as a bare slug:

```yaml
collections:
- billing                                  # offered here; contributes variables only
- collection: orders                       # …and moves this one
  baseUrl: https://orders-uat.acme.test
  defaultAuth: ../../auth/uat.auth.tap
```

The file may live anywhere — `environments/` for the global ones, beside `_collection.tap`
for one belonging to a single collection, so deleting the collection takes it along.
Location does not decide scope; `collections:` does.

### 7.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"env"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `collections` | sequence | no | Collections this env is assigned to. Each entry is a **slug** (not a path), or a mapping with `collection` plus that collection's overrides. Absent or empty = global. A slug may appear once. |
| `collections[].baseUrl` | string | no | Replaces *that* collection's `baseUrl` while this env is active. May contain `{{vars}}`. |
| `collections[].defaultAuth` | path \| id-ref | no | Replaces *that* collection's `defaultAuth` while this env is active. Resolved relative to **this env file**, since that is where the ref is written. A request that pins its own `auth:` still wins. |
| `vars` | map<string, var-spec> | no | Env-tier variables. Same var-spec shape as §5.1, including `secret: true`. |
| `defaultVariableProvider` | string | no | Provider (or alias) that bare `{{name}}` tokens hit first — and that receives un-targeted variable writes — while this env is active. Overrides the workspace/system default. |
| `providerAliases` | map<string, string> | no | Alias → provider-name bindings. Requests use a stable prefix (`{{kv:secret}}`); each env points the alias at its own provider (`kv: kv-dev` vs `kv: kv-prod`). |
| `strictVariables` | boolean | no | With a `defaultVariableProvider` set: bare `{{name}}` lookups that miss it fail instead of falling through to other providers. Recommended for one-vault-per-environment setups. |

A `vars` value is a literal, a var-spec object, or a reference: a `{{provider:name}}` token
written inside a var's value resolves when the variable is read (§3.2), so
`apiToken: { default: '{{kv:client-secret}}', secret: true }` puts the vault behind a name
requests can spell plainly. Binding the prefix with `providerAliases` does the same job one
level up, so the same spelling reaches a different vault per environment.

The provider-binding fields make the one-vault-per-environment pattern work: declare
`kv-dev` and `kv-prod` once (in `workspace.tap` or the system settings), then have
`dev.env.tap` bind `kv: kv-dev` and `prod.env.tap` bind `kv: kv-prod`. Requests keep a
single spelling — `{{kv:clientSecret}}` — and switching the environment switches the
vault. Explicit `{{provider:name}}` tokens never fall through; `strictVariables`
extends the same guarantee to bare tokens.

### 7.2 Example

```markdown
---
kind: env
id: 0192-3a4d-c000-7e1f-...
name: Production
collections:
- collection: stripe
  baseUrl: https://api.stripe.com
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

0. **Portable** — a `.http` file's own `@name = value` lines (§13.5.5). Only present for that
   file kind, and deliberately the weakest tier.
1. `workspace.tap` `vars`
2. Owning `_collection.tap` `vars` (the collection the request lives under)
3. Active `*.env.tap` `vars`
4. Request file `vars`
5. Per-run overrides (CLI `--var foo=bar`, UI form input; flow/test-set tiers per §10.5)

An **assigned** env only contributes tier 3 for the collections it names. For any other
collection it is as if no env were selected at all, and the rendered request reports
`envPath: null`.

A later scope redefining a name also redefines its sensitivity — an env that overrides a
secret with a literal test value is no longer holding a secret.

**`{{baseUrl}}` is built in.** Before the request block is expanded, Tap binds `baseUrl` to the
owning collection's base URL — the active environment's override for *that* collection when its
assignment declares one — already expanded and with any trailing `/` trimmed. It binds only when no scope from tier 1 upward defines the name, so
declaring your own `baseUrl` var keeps working unchanged. Writing `GET {{baseUrl}}/orders` is
therefore equivalent to `GET /orders` inside Tap, and unlike the relative form it also resolves in
tools that know nothing about collections (§13.5.5).

The merged cascade wins over providers for bare `{{name}}` tokens; `{{provider:name}}`
bypasses it. A value that a `vars:` block declared may itself reference another variable and
is expanded when it is read (§3.2); a value that arrived at runtime never is. A reference
chain that closes on itself fails with `E_VAR_CYCLE`.

---

## 8. `auth` — `*.auth.tap`

A reusable authentication profile. Used by requests via the `auth:` frontmatter field, or applied as a collection default.

### 8.0 Where a profile lives — workspace vs collection scope

An auth file may sit in either of two places, and the choice decides which variables its
fields can reference:

| Location | Scope | Cascade its fields resolve against |
|---|---|---|
| `auth/**/*.auth.tap` | **workspace** — shared by every collection | workspace < env |
| `collections/<slug>/**/*.auth.tap` | **collection** — owned by that collection | workspace < collection < env |

Both are referenced the same way, by relative path from the referencing file:

```yaml
# a request inside collections/stripe/, pointing at a workspace profile
auth: ../../auth/stripe-bearer.auth.tap

# the same request pointing at its own collection's profile
auth: stripe-oauth.auth.tap
```

Pick collection scope when the profile's token URL, client id, audience, or credentials are
already expressed as collection (or environment) variables:

```yaml
# collections/stripe/_collection.tap
kind: collection
name: Stripe
baseUrl: '{{API_URL}}'
defaultAuth: stripe-oauth.auth.tap
vars:
  API_URL: https://api.stripe.test
  IDP_URL: https://idp.stripe.test
```

```yaml
# collections/stripe/prod.env.tap
kind: env
name: prod
collections:
- collection: stripe
vars:
  API_URL: https://api.stripe.com
  IDP_URL: https://idp.stripe.com
```

```yaml
# collections/stripe/stripe-oauth.auth.tap
kind: auth
name: Stripe OAuth
type: oauth2
flow: client_credentials
tokenUrl: '{{IDP_URL}}/connect/token'   # resolves per environment
clientId: '{{STRIPE_CLIENT_ID}}'
```

Selecting the `prod` environment repoints `tokenUrl` without touching the profile. Runtime
tokens are cached **per environment**, so `dev` and `prod` never hand each other a token;
clearing a profile's token clears every environment at once.

A profile's environment is decided by the profile's *own* collection, not the caller's: a
request in collection A that borrows a profile from collection B resolves that profile
against B's scope, so a scoped env belonging to A never leaks across.

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
keyed per profile + env (§8.0). They are never written to a workspace file.

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

## 9. `collection` — `_collection.tap`

A **collection** is the top-level grouping for requests. Each collection lives at
`collections/<slug>/_collection.tap`; the slug is the directory name and serves as the
collection's id-on-disk. Nested directories below the collection are pure grouping
(no metadata, no inheritance) — every request, no matter how deeply nested, belongs
to exactly one collection.

### 9.1 Frontmatter

See §6 — collections own the base URL, default headers, default auth, vars, and tags. This section is retained for navigation only; the canonical schema lives in §6.

---

## 10. `flow` — `*.flow.tap`

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
  request: ../collections/demo/create-order.req.tap
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
  request: ../collections/demo/get-order.req.tap
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

## 11. `test` — `*.test.tap`

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
  request: ../collections/demo/create-order.req.tap
  vars:
    item: nope
  assertions:
  - status: 404
- name: Full checkout
  flow: ./checkout.flow.tap
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
`{{provider:name}}` tokens resolve against (§3.2). Providers are declared in `workspace.tap`'s
`variableProviders:` array (§4.1) or at system scope in the host's settings; a workspace
provider shadows a same-named system one. Each provider reports per-value sensitivity
(`IsSecret`), which drives masking and redaction everywhere a value could be echoed.

### 12.1 Built-in provider types (v0)

| Type | Source | Mode | Settings |
|---|---|---|---|
| `env` | Process environment variables, gated by the **host allowlists** (below) | read | none — the gate deliberately lives on the host, not in files |
| `file` | A YAML store per provider at `<workspace>/.vars/<name>.yml`; `secret: true` values are encrypted at rest (AES-256-GCM, key derived from the machine's encryption key — §12.4) | read/write | none |
| `azkv` | Azure Key Vault via `DefaultAzureCredential` (picks up `az login`, managed identity, …). Every value is secret. | read | `vaultName` (required), `tenantId`, `prefix`, `filter` (below) |
| `1p` | 1Password via the `op` CLI (desktop-app / biometric auth on the host) | read | `mode`: `environment` (default; a 1Password Environment's variables), `item` (`vault` + `item` — one item's fields), or `vault` (`vault` — one variable per item) |
| `aspire` | A resource's allocated URL, read from the standard `services__<resource>__<scheme>__<index>` environment variables | read | none — always registered (§12.2) |
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

#### `azkv` name filter

A team vault usually holds far more than one workspace needs. `filter` narrows the provider to
the slice that does:

```yaml
- name: kv-billing
  type: azkv
  settings:
    vaultName: acme-prod
    filter: '^billing-'
```

The value is a .NET regular expression matched against the name **as tokens spell it** — after
any `prefix` has been stripped — and it is **unanchored**, so `billing` matches anywhere in the
name while `^billing-` matches only at the start. An unparseable pattern is
`E_PROVIDER_CONFIG_INVALID`.

It is a scope, not a display filter. A name outside it is absent from listings *and* from the
Studio's Browse drawer, resolves as a miss (so `{{kv-billing:payments-key}}` fails as unknown
rather than reaching past the filter), and refuses writes — storing a secret the provider would
then hide is worse than being told no.

### 12.2 The `aspire` provider and the CI story

`{{aspire:orders-api}}` resolves to that resource's allocated URL, so a collection can say:

```yaml
baseUrl: '{{aspire:orders-api}}'
```

and hit whatever port the AppHost handed out this run. Unlike the other types it needs no
declaration — it is always registered, because it only resolves names the environment already
advertises and returns nothing otherwise. A workspace or system provider that claims the name
`aspire` still shadows it.

It reads Aspire's **standard service-discovery convention**, not an Aspire API:
`services__<resource>__<scheme>__<index>`, preferring `https` over `http` and the lowest index.
That is the whole point — the same workspace runs unchanged in three places:

```bash
# Under an AppHost: injected automatically for every resource you WithReference().
# From the CLI on your machine: same, if you run inside the AppHost's environment.
# In CI, with no Aspire at all — just export the variable yourself:
export services__orders-api__https__0=https://staging.example.com
tap-studio test "Orders smoke"
```

Values are never marked secret: an allocated URL is not a credential, and masking it would
redact the field you most need to read in a failing report. A missing resource fails with
`E_PROVIDER_RESOLUTION_FAILED` naming the exact environment variable to set.

### 12.3 Resolution rules

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

### 12.4 Adding a custom provider

Tap will support out-of-tree providers via a plugin model (post-v0). For v0 the built-in set is the supported surface.

### 12.5 The encryption key

Anything Tap encrypts at rest is keyed to **one passphrase per machine**, resolved in this
order:

| Source | Notes |
|---|---|
| `TAP_ENCRYPTION_KEY` (process env) | Wins outright. How CI supplies the key. |
| `<system-dir>/encryption.key` | `$TAP_SYSTEM_DIR`, else `~/.tap`. One line, written owner-only. |

There is deliberately no third source. In particular the key is **not** a provider setting and
cannot be declared in `workspace.tap` — a passphrase stored beside the ciphertext it unlocks
travels with it into Git, and is not encryption. The PBKDF2 salt is still per provider
(`tap-file-provider:<name>`), so one machine key yields a distinct derived key per store.

The key is needed only for `secret: true` values, and it provisions itself: **storing a secret on
a machine with no key generates `<system-dir>/encryption.key` first**, so nothing has to be set up
before the first secret. A machine with no key reads and writes plain values normally.

Generation happens on the write path only. A secret *read* with no key still fails with
`E_PROVIDER_DECRYPT_FAILED` naming both sources — the ciphertext on disk was written under a key
that is gone, and minting a fresh one would answer that with a key which still cannot read it.
A secret write fails with `E_PROVIDER_CONFIG_INVALID` only when no key can be created either:
`TAP_ENCRYPTION_KEY` is exported empty, or the system directory is not writable.

`TAP_ENCRYPTION_KEY` suppresses generation entirely. The variable already answers the question,
and a key file it shadows is one nothing reads.

```shell
tap-studio key status      # is there a key, and where did it come from
tap-studio key init        # generate <system-dir>/encryption.key now (refuses to overwrite)
```

`key init` is optional — it exists to provision and back the key up *before* there is anything to
lose, and for `--force` rotation. Studio offers the same step inline. Back the file up either way:
it is the only thing that can decrypt what it encrypted, and no endpoint or command will print it
back to you.

### 12.6 Marking a variable secret

`secret: true` on a cascade variable used to say "mask this" while the value sat in the file in
clear text — the mark and the file disagreed. Marking a variable secret in Studio now moves the
value: it is written to a writable provider, and the file keeps a reference in its place.

```yaml
vars:
  stripe.key:
    default: '{{file:stripe.key}}'   # the value lives in the `file` provider
    secret: true
```

Nothing downstream changes — resolving that token is ordinary variable resolution (§3.2), so
the request renders the same value it always did, from somewhere that isn't the repository.
Hand-authored files can of course write the reference directly; the flag and the reference are
independent, and `secret: true` on a literal still only masks.

A reference resolves wherever a `vars:` block can be written — the workspace manifest, a
collection, an env, a request, a `.http` file's `@name = value` lines, and a test
set's or flow's own variables. What it resolves *to* is redacted on the strength of the
provider's own mark, so the value in the store is the one that says `secret: true`.

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
    sourceRequestPath, envPath, resolvedBaseUrl,
    variablesUsed: [ { provider, name, isSecret } ]   ← names only, never values
  }
}
```

The render pipeline:

1. Load the request file; locate the owning collection by walking the request path
   (`collections/<slug>/…`); drop the selected environment if it is scoped away from that
   collection (§7); resolve the auth ref — the request's `auth:`, else the env's assignment to
   this collection, else the collection's own; merge transport settings (request over collection).
   `metadata.envPath` reports the env that actually applied, which is what the executor keys
   its token lookup on.
2. Build the merged variable cascade (§7.3), tracking which names are secret and which came
   out of a `vars:` block — the latter decides whose value may itself carry a token (§3.2).
   Template-valued overrides (a flow step's `vars:`) are expanded against the cascade in
   order, each seeing the ones before it.
3. Expand `{{…}}` tokens in the fenced `http` block — cascade first, then providers (§3.2) —
   and parse it into method / URL / headers / body.
4. If the URL is relative, expand the env-or-collection `baseUrl`, normalize its scheme
   (bare `host:port` gains `http://`, or `ws://` for websocket requests; `http(s)` is
   rewritten to `ws(s)` for websocket), and join. Failing that, `E_HTTP_BLOCK_SYNTAX`.
5. Merge headers: collection `defaultHeaders` (each value expanded) under the block's
   headers, under auth-derived headers (§8's inline family). Every source is interpolated
   exactly once — a resolved value is never re-scanned (§3.2) — and the assembled request
   line and headers are rejected if anything smuggled in a line break.
6. Render the request's assertions against the same cascade; an expected value that pulled
   in anything secret is flagged so reports mask it as `***`.
7. Build the redactor from every secret value the registry resolved, every secret cascade
   value that won its name, and the auth-derived header names.

The executor then adds what only it can know: for runtime-token profiles (oauth2, azure-cli,
jwt, github past PAT) it stamps `Authorization: Bearer …` from the token store — scoped per
profile + env, and the minted token joins the redaction set; a `< ./file` body
reference is loaded from disk (workspace-scoped); and Tap Studio stamps
`User-Agent: tap-studio/<version>` when the rendered headers don't already carry one
(case-insensitive) — a `User-Agent` set on the request, the collection's `defaultHeaders`,
or the auth profile always wins.

Every echo of a rendered request to an agent surface (CLI `--json`, MCP results) passes
through the redactor; `metadata.variablesUsed` is the audit trail of which providers and
names were consulted.

---

## 13.5 `.http` files — the portable request format

Tap reads `.http` files as a first-class request source. This is the format Visual Studio
scaffolds into every new ASP.NET Core project, and the one VS Code REST Client, JetBrains,
httpyac, and Kulala share. A `.http` file in a workspace is loaded, rendered, authenticated,
asserted, and executed by exactly the same engine as a `*.req.tap` — it is not an import step,
and Tap never rewrites the file.

### 13.5.1 One file, several requests

Requests are separated by `###`. Each becomes its own request, addressed by a **fragment path**:

```
collections/demo/orders.http#get-order
```

The fragment is the canonical identity — it stays stable when requests are added or removed
above it, which an ordinal would not. It is also what a `*.flow.tap` step or `*.test.tap` entry
references. Where a file holds exactly one request, the bare file path resolves too.

A request's name comes from the first of: a `# @name` directive, the `###` separator's title, a
slug derived from the method and last path segment (`GET /api/v1/orders` → `get-orders`), then
its ordinal.

### 13.5.2 What is supported

| Construct | Behaviour |
|---|---|
| `### Title` | Request separator; the title names the request. |
| `METHOD URL [HTTP/1.1]` | Request line. Indented continuation lines starting `?` or `&` fold into the URL. |
| Headers, blank line, body | As in a `*.req.tap` fenced `http` block — the same parser reads both. |
| `# comment` / `// comment` | Both accepted. |
| `@name = value` | File variable, visible to every request in the file (declaration order need not precede use). Sits at the **portable** tier — the weakest in the cascade, so every workspace scope overrides it (§13.5.5). |
| `< ./file` | Body include, same semantics as in a `*.req.tap`. |
| `# @name x` | Names the request. |
| `# @timeout 30` | Seconds → the request's transport timeout. |
| `{{var}}`, `{{provider:name}}` | Identical to Tap's own interpolation. |
| `{{$guid}}` `{{$uuid}}` `{{$timestamp}}` `{{$isoTimestamp}}` `{{$randomInt [min max]}}` | Generated at render time. A token resolves once per render, so the same `{{$guid}}` in a header and the body is one value — useful as a correlation id. Available in `*.req.tap` too. |

Constructs belonging to a specific tool are **recognized, skipped, and reported as warnings**
(`W_HTTP_UNSUPPORTED_CONSTRUCT`) naming the Tap equivalent — JetBrains `< {% %}` / `> {% %}`
scripts, httpyac `??` assertions, `run` / `import`, `>>` response redirects, and request
chaining (`{{login.response.body.$.id}}`, which a flow does instead). A malformed request drops
only itself; the rest of the file still loads.

### 13.5.3 Tap directives

Tap's own features ride in comments, so a file carrying them still opens and sends normally in
every other tool:

| Directive | Effect |
|---|---|
| `# @tap-collection <slug>` | Attach to a collection from anywhere in the repo — inherits its baseUrl, default headers, default auth, and scoped environments. Files under `collections/<slug>/` inherit by location and need no directive. |
| `# @tap-auth <path\|id:uuid>` | Same semantics as a request's `auth:` frontmatter key. |
| `# @tap-assert <expression>` | One assertion, in the one-line form below. Repeatable. |
| `# @tap-secret <var>[, <var>]` | Marks file variables secret, so their values are redacted everywhere Tap reports a request. |
| `# @tap-protocol websocket` | Same as the `protocol:` frontmatter key. |
| `# @tap-tag a, b` | Same as `tags:`. |

Directives above the first request are **file-wide defaults**. The same directive inside a
request's comment block overrides the file default — except assertions, tags, and secrets, which
accumulate, since a file-level assertion applying to every request is the reason to write one.

An unknown `# @tap-*` key warns rather than being silently inert: a typo'd `@tap-asert` would
otherwise leave you believing an assertion is running.

### 13.5.4 The one-line assertion form

`.http` files have no YAML, so `# @tap-assert` uses an expression spelling of the same model
described in §5.5. It produces the identical `AssertSpec` and is validated by the identical
rules, so an assertion means and reports the same thing in either file kind.

```
# @tap-assert status == 200
# @tap-assert status 2xx
# @tap-assert header content-type contains application/json
# @tap-assert header etag                     # no operator → exists
# @tap-assert body $.id exists
# @tap-assert $.items count 3
# @tap-assert duration < 2000
```

Shape is `<extractor> [selector] [operator] [value]`, with the same sugar as the YAML form: a
bare value means `equals`, and nothing at all means `exists`. Extractors are `status`,
`duration`, `header <name>`, `body`, `body <$.jsonpath>` (or a bare `$.jsonpath`), and
`xpath <expr>`. Operators accept symbol or word spellings — `==` `=` `equals` `is`, `!=`,
`contains`, `not-contains`, `starts-with`, `ends-with`, `matches`, `<` `<=` `>` `>=`, `exists`,
`count`, `length`, `type`, `in`, `between`.

### 13.5.5 Running the same file inside and outside Tap

A `.http` file is expected to keep working in Visual Studio and REST Client, where nothing knows
about collections. That rules out the relative request line: `GET /orders` only resolves because
Tap prepends the collection's base URL, and elsewhere it is not a URL at all.

The portable spelling is a file variable plus the built-in `{{baseUrl}}` (§7.3):

```
@baseUrl = http://localhost:5000

### Ping
# @tap-assert status 2xx
GET {{baseUrl}}/
Accept: application/json
```

Outside Tap, `@baseUrl` is the only definition there is, so the request goes to
`http://localhost:5000/`. Inside Tap, the file's own variables are the **weakest** tier of the
cascade, so the collection's base URL — and the active environment's override, and any `env` that redefines the
name — wins instead. One file, both meanings, no edit in between.

That inversion is specific to `.http` files. A `*.req.tap`'s `vars:` are authored for Tap and stay
at their usual tier, above the collection.

Two consequences worth knowing:

- Selecting an environment moves a portable request, exactly as it moves a `*.req.tap` one. Had
  the file's own `@baseUrl` won, the environment picker would have silently done nothing.
- A relative request line still works and still inherits the collection's base URL. The portable
  form is an option, not a requirement — and a file that will only ever run inside Tap can use
  either.

### 13.5.6 Editing

Tap never reformats a `.http` file. There is no canonical `.http` emitter: the file on disk stays
the source of truth in its own format, and Studio edits it as raw source. In Studio the file has
its own editor — the requests it holds, each sendable on its own, above the raw text — reachable
from **Edit…** on the file's row in the explorer. Send runs the text currently on screen, saved or
not, so iterating does not require a save between edits.

---

## 14. References between files

Two ways to point from one file to another:

1. **Relative path** (recommended for v0): `auth: ../../auth/stripe-bearer.auth.tap`, or `auth: stripe-oauth.auth.tap` for a sibling inside the same collection. Paths resolve relative to the file that declares them and never escape the workspace root. Survives `git mv` provided both files move together. Clearer in diffs.
2. **Id reference**: `auth: id:0192-3a4d-9000-...`. Tap maintains an index built from `id:` fields. Survives rename without coordinated moves but requires the index to be up-to-date.

The parser accepts both, normalizes internally to a canonical `WorkspaceRef`. Tap's writer always emits relative paths.

---

## 15. Versioning, IDs, and stability

- A new file with no `id:` gets a UUIDv7 assigned by the writer on first save. The Studio does
  this for every kind it writes, so a file it created can be referenced by id, and its recorded
  history (§4.3) survives a rename.
- The id is the durable identity. Renaming the file preserves the id.
- Cross-file `id:` references resolve through the workspace index; if an id has no owner, references to it produce `E_DANGLING_REF`.
- The format version is implicit in this document. Files do not carry a version field in v0; a future breaking change will introduce a `tapFormat: "1.x"` field in `workspace.tap`.

---

## 16. Parse errors (canonical)

| Code | Meaning |
|---|---|
| `E_FRONTMATTER_MISSING` | File has no `---` fenced frontmatter block. |
| `E_FRONTMATTER_MALFORMED_YAML` | Frontmatter is not valid YAML 1.2 (also reported for unreadable or oversized files). |
| `E_KIND_MISMATCH` | `kind:` does not match the filename suffix, or the filename matches no known suffix. |
| `E_KIND_MISSING` | `kind:` field absent. |
| `E_EXTENSION_COLLISION` | The same logical file exists under both extension families (`orders.req.md` beside `orders.req.tap`) — what a half-finished migration leaves behind. The canonical file wins; the legacy one is not loaded. |
| `W_LEGACY_EXTENSION` | *Warning.* The file loaded but uses the pre-0.7.0 `.md` extension (§2.0). Run `tap-studio migrate`. Does not fail `lint`. |
| `E_UNKNOWN_FIELD` | A frontmatter field is unrecognized or malformed for that kind (bad `protocol:`, a removed `stages:`/`defaultStage:` on a collection, a path where `collections:` wants a slug, the same collection assigned twice, invalid `agent:` shape, duplicate path/id in the index, …). |
| `E_NO_REQUEST_BLOCK` | A `request` file has no fenced `http` block. |
| `E_MULTIPLE_REQUEST_BLOCKS` | A `request` file has more than one fenced `http` block. |
| `E_DANGLING_REF` | A `path` or `id:` reference does not resolve. |
| `E_VAR_UNKNOWN` | A `{{name}}` token is neither in the cascade nor resolvable by any provider. |
| `E_VAR_CYCLE` | A declared variable resolves through itself — `a: '{{b}}'`, `b: '{{a}}'` (§3.2). The message names every variable in the loop. |
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
- Request chaining: now a first-class kind via `*.flow.tap` (§10), grouped into test sets by
  `*.test.tap` (§11). Still out of scope — parallel execution, data-driven tests (one test × N
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
$ tap-studio send collections/stripe/create-customer.req.tap \
    --env environments/prod.env.tap \
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
var     baseUrl                       ← collections/stripe/_collection.tap
var     customer.email                ← --var
var     customer.name                 ← --var
secret  kv-prod : stripe-live-key     (isSecret — value never recorded)
```

This is the contract the rest of Tap (executor, diff viewer, capture-promotion flow, satellite) is built against.
