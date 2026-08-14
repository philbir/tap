# Tap Workspace Format

> Status: **draft v0**. Subject to change until v1.0. Every file Tap stores in your repo is plain Markdown with YAML frontmatter, so the format is reviewable as a normal git diff.

A Tap **workspace** is a directory that contains a `.tap/` subfolder. Everything inside `.tap/` is meant to be checked into version control. Nothing in `.tap/` ever contains a secret value — only references to a secret provider.

This document is the authoritative spec for the on-disk format. The Tap parser (`Tap.Workspace`) and renderer (`Tap.Workspace.Rendering`) implement exactly what's described here. If parser and spec disagree, the spec is the bug report.

---

## 1. Design goals

1. **Git-native.** Every artifact is plain text. Renames work via `git mv`. Diffs are readable.
2. **Composable.** A runnable request is the composition of *workspace + collection (+ stage) + auth + environment + request*. No single file is the whole story.
3. **Readable on GitHub.** Frontmatter is the structured part; the body is human prose. A request file is a documentation page that happens to be executable.
4. **Secret-safe by construction.** A literal secret cannot occur in a workspace file. Frontmatter values that look like `${{provider:path}}` are *references* and are never resolved at parse time.
5. **One format, a handful of shapes.** Every file is `Markdown + YAML frontmatter`. The `kind` field plus the filename suffix tell the parser what shape to expect.
6. **No required tooling to read.** Any text editor or any Markdown renderer can display a workspace. Tap adds editing, validation, execution, and live preview on top.

---

## 2. File kinds

| Kind | Filename suffix | Purpose |
|---|---|---|
| `request` | `*.req.md` | A single HTTP request template. |
| `auth` | `*.auth.md` | A reusable authentication profile (bearer, basic, OAuth2, custom). Lives either in `auth/` (workspace-scoped) or inside a collection (collection-scoped) — see §8.0. |
| `env` | `*.env.md` | A named environment (set of variables and secret bindings). |
| `collection` | `_collection.md` *(at `collections/<slug>/`)* | A top-level group of requests. Owns the base URL, optional named stages, default auth, default headers, plus collection-scoped variables and tags. |
| `flow` | `*.flow.md` | An ordered sequence of requests where each step can extract values from its response for the steps after it. Lives in `tests/` — see §10. |
| `test` | `*.test.md` | A test set: named checks, each running one request or one flow, with set-scoped variables. Lives in `tests/` — see §11. |
| `workspace` | `tap.md` *(at workspace root)* | Workspace-level config: name, default env, registered secret providers. |

Sub-directories inside a collection are pure grouping for the explorer tree — they carry no metadata, no variables, and no inherited defaults. Every request below a collection inherits its baseUrl, stages, default auth, default headers, and variables; variable sharing across a group of requests lives on `_collection.md`.

An `*.auth.md` placed inside a collection is *owned* by it: the profile's fields resolve against that collection's variables and its active stage. That's the only kind of file below a collection that inherits anything besides a request.

The filename suffix is canonical. The `kind:` frontmatter field is required and must match the suffix. A mismatch is a hard parse error.

### 2.1 Suggested directory layout

```
my-service/
├── src/                                 ← your code
└── .tap/
    ├── tap.md                           ← kind: workspace
    ├── schemas/                         ← JSON Schemas, auto-generated
    │   ├── request.schema.json
    │   ├── auth.schema.json
    │   ├── env.schema.json
    │   └── workspace.schema.json
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

Two distinct interpolations exist, and they mean different things:

- `{{name}}` — a **variable reference**. Resolved from the merged variable scope (§7). Resolved during render. Always returns a string.
- `${{scheme:path}}` — a **secret reference**. Resolved by the matching `ISecretProvider` (§8). Resolved at execute time only. Never visible to the UI in cleartext. May only appear in frontmatter fields that the spec marks as "secret-bearing".

A literal `{` followed by `{` that you do not want interpolated is escaped as `\{{`.

---

## 4. `workspace` — `tap.md`

The single file at the workspace root. Created on `tap init`.

### 4.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"workspace"` | yes | |
| `name` | string | yes | |
| `id` | uuid | yes | |
| `defaultEnv` | path | no | Relative path to the `.env.md` file used when none is specified at execute time. |
| `providers` | array of provider configs | no | Registers the secret providers available in this workspace. See §8.1. |
| `vars` | map<string,string\|var-spec> | no | Workspace-level variables (lowest precedence). |

### 4.2 Example

```markdown
---
kind: workspace
id: 0192-3a4c-bb71-7c1d-9e8f0a1b2c3d
name: acme-billing
defaultEnv: environments/local.env.md
providers:
  # `env` is always registered — gated by host's TAP_VARS_ALLOWED / TAP_SECRETS_ALLOWED.
  - scheme: keychain
    service: tap.acme-billing
  - scheme: azkv
    vaultUrl: https://acme-prod.vault.azure.net
  - scheme: age
    keyFile: ${{env:TAP_AGE_KEY_FILE}}
    file: secrets.age
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
| `auth` | path \| id-ref \| `"none"` | no | Overrides the containing collection's `defaultAuth`. `"none"` opts out entirely. |
| `protocol` | enum: `http` \| `websocket` | no | Wire protocol. Default `http`. `websocket` drives baseUrl scheme normalization (http→ws, https→wss) and switches the executor to a WebSocket transport. See §5.4. |
| `vars` | map<string, var-spec> | no | Request-scoped variables (highest precedence except for explicit per-run overrides). |
| `tags` | string[] | no | |
| `assertions` | array of assertions | no | Declared expectations about the response, evaluated after every run. See §5.5. |

`var-spec` may be a literal string (becomes the default value) or an object:

```yaml
vars:
  customer.email:
    description: Email address used for signup
    required: true
    example: jane@example.com
  customer.name: Jane Doe
```

### 5.2 Body

The body is CommonMark. **Exactly one** fenced code block tagged `http` carries the request template. All other content is documentation and ignored at execute time.

The `http` block follows the [VS Code REST Client / JetBrains HTTP Client](https://www.jetbrains.com/help/idea/exploring-http-syntax.html) syntax, with two extensions:

1. `{{var}}` and `${{secret}}` interpolation per §3.2.
2. The request line's URL may be a bare path (`/v1/customers`) — Tap prepends the containing collection's `baseUrl` (or, when a stage is active, the stage override). If the URL is not absolute and the collection has no baseUrl, the parser rejects the file.

If multiple `http` blocks are present, the parser fails with `E_MULTIPLE_REQUEST_BLOCKS`. If zero are present and the request file isn't explicitly marked `skip: true`, the parser fails with `E_NO_REQUEST_BLOCK`.

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
| `defaultHeaders` | map<string,string> | no | Merged under request-specific headers. |
| `vars` | map<string, var-spec> | no | Collection-scoped variables. Cascade tier between workspace and stage. |
| `stages` | sequence of stage | no | Named per-stage overrides (e.g. `dev`/`staging`/`prod`). Each stage may override `baseUrl`, `defaultAuth`, and `vars`. |
| `defaultStage` | string | no | Stage to preselect in the editor. |
| `tags` | string[] | no | |

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

A named environment. Activated by Tap at execute time; provides values for `{{var}}` references and the bindings for `${{secret}}` references that this environment uses.

### 7.1 Frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"env"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `vars` | map<string, string \| number \| boolean \| secret-ref> | no | |
| `defaultVariableProvider` | string | no | Provider (or alias) that bare `{{name}}` tokens hit first — and that receives un-targeted variable writes — while this env is active. Overrides the workspace/system default. |
| `providerAliases` | map<string, string> | no | Alias → provider-name bindings. Requests use a stable prefix (`{{kv:secret}}`); each env points the alias at its own provider (`kv: kv-dev` vs `kv: kv-prod`). |
| `strictVariables` | boolean | no | With a `defaultVariableProvider` set: bare `{{name}}` lookups that miss it fail instead of falling through to other providers. Recommended for one-vault-per-environment setups. |

The value of any `vars` entry may be a literal **or** a secret reference (`${{scheme:path}}`). Tap delays resolution until execute time and never logs the resolved value.

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
  STRIPE_KEY: ${{azkv:billing-prod/stripe-live-key}}
  customer.email: noreply+prod@acme.example
---

# Production

Live Stripe. Destructive runs require approval from @billing. Audit log:
every secret resolution from this env emits an entry tagged `env=prod`.
```

### 7.3 Variable scope and precedence

When Tap renders a request, it merges variable scopes in this order (later overrides earlier):

1. `tap.md` `vars`
2. Owning `_collection.md` `vars` (the collection the request lives under)
3. Active collection stage's `vars`
4. Active `env.md` `vars`
5. Request file `vars`
6. Per-run overrides (CLI `--var foo=bar`, UI form input)

Resolution is single-pass — a variable may not reference another variable that depends on it. Tap detects cycles at render time and fails with `E_VAR_CYCLE`.

---

## 8. `auth` — `*.auth.md`

A reusable authentication profile. Used by requests via the `auth:` frontmatter field, or applied as an API default.

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
are cached **per stage**, so `dev` and `prod` never hand each other a token; clearing a
profile's token clears every stage at once. A workspace-scoped profile has no stage, so its
cache behaves exactly as it always has.

Nothing else changes with scope: a request in collection A may reference a profile owned by
collection B (it just resolves against B's variables, not A's), and a collection-scoped
profile still shows up in Studio's Auth tab alongside the shared ones.

### 8.1 Common frontmatter

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `"auth"` | yes | |
| `name` | string | no | |
| `id` | uuid | no | |
| `type` | enum | yes | `none` \| `basic` \| `bearer` \| `apiKey` \| `oauth2` \| `aws-sigv4` \| `custom`. |

Type-specific fields below. Any field marked **(secret-bearing)** may contain a `${{...}}` reference.

### 8.2 `bearer`

| Field | Type | Required |
|---|---|---|
| `token` | string (secret-bearing) | yes |

### 8.3 `basic`

| Field | Type | Required |
|---|---|---|
| `username` | string | yes |
| `password` | string (secret-bearing) | yes |

### 8.4 `apiKey`

| Field | Type | Required |
|---|---|---|
| `in` | enum: `header` \| `query` \| `cookie` | yes |
| `name` | string | yes — the header/query/cookie name |
| `value` | string (secret-bearing) | yes |

### 8.5 `oauth2`

| Field | Type | Required | Notes |
|---|---|---|---|
| `flow` | enum: `authorization_code` \| `client_credentials` \| `device_code` \| `password` | yes | |
| `authorizeUrl` | string | conditional | Required for `authorization_code`. |
| `tokenUrl` | string | yes | |
| `clientId` | string (secret-bearing) | yes | |
| `clientSecret` | string (secret-bearing) | conditional | Not required for public clients (PKCE-only). |
| `scopes` | string[] | no | |
| `audience` | string | no | |
| `redirectUri` | string | no | Default: `http://localhost:7878/callback` (Tap's loopback). |
| `tokenCache` | string | no | Default: `keychain`. The provider used to store the obtained tokens. |

Acquired access tokens, refresh tokens, and expiry timestamps live in the **token cache provider** (default the OS keychain). They are never written to a workspace file.

### 8.6 `aws-sigv4`

| Field | Type | Required |
|---|---|---|
| `region` | string | yes |
| `service` | string | yes |
| `accessKeyId` | string (secret-bearing) | yes |
| `secretAccessKey` | string (secret-bearing) | yes |
| `sessionToken` | string (secret-bearing) | no |

### 8.7 `custom`

| Field | Type | Required | Notes |
|---|---|---|---|
| `headers` | map<string, string (secret-bearing)> | no | Headers injected as-is. |
| `query` | map<string, string (secret-bearing)> | no | |

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

## 12. Secret providers

A **secret provider** resolves `${{scheme:path}}` references at execute time. Providers are registered in the workspace's `tap.md` `providers:` array (§4.2). Each provider is identified by its `scheme`; references and providers are bound by exact scheme match.

### 12.1 Built-in providers (v0)

| Scheme | Source | Reference example |
|---|---|---|
| `env` | Process environment variable (gated by the host allowlist — see below) | `${{env:STRIPE_KEY}}` |
| `keychain` | OS keychain (macOS Keychain / Windows Credential Manager / Linux libsecret) | `${{keychain:acme/stripe-key}}` |
| `age` | A workspace file encrypted with [age](https://age-encryption.org); decrypted in-memory using a key from another provider | `${{age:stripe/secret-key}}` |
| `azkv` | Azure Key Vault | `${{azkv:billing-prod/stripe-live-key}}` |
| `1p` | 1Password CLI (`op read`) | `${{1p:Personal/Stripe/api-key}}` |

#### `env` allowlist

The `env` provider is the only one whose reachable surface is bounded by the **host process**
rather than the workspace file — anything the Tap process can see in its own environment is
fair game otherwise, and the workspace is shared / checked in. Two process env vars gate
access; both take a comma-separated list of glob patterns where `*` is the only wildcard:

| Host env var | Effect |
|---|---|
| `TAP_VARS_ALLOWED` | Names whose values surface as plain System variables in the cascade (visible in the UI). Usable as `{{NAME}}` directly. |
| `TAP_SECRETS_ALLOWED` | Names whose values stay masked everywhere in the UI but can be resolved via `${{env:NAME}}` at execute time. |

Example:

```shell
export TAP_VARS_ALLOWED="VITE_*,ASPNETCORE_ENVIRONMENT,DEMO_API_URL"
export TAP_SECRETS_ALLOWED="DEMO_*_TOKEN,AZURE_*"
```

If neither variable is set, the `env` provider denies every reference and the System scope is
empty — deny-by-default is the safe default. References to names that don't match either
pattern list fail with `E_SECRET_RESOLUTION_FAILED`.

### 12.2 Resolution rules

- A reference resolves to a string. Non-string secret values are an error.
- Tap caches resolutions in memory for the duration of a render. Two references to the same secret within one execute call hit the provider once.
- A reference whose scheme is not registered in `providers:` fails with `E_UNKNOWN_SECRET_SCHEME`.
- Failed resolution (provider down, ref missing) produces `E_SECRET_RESOLUTION_FAILED`; Tap surfaces which ref failed but never the partial value.
- Resolved values are redacted from execution history. Only the ref text is recorded.

### 12.3 Adding a custom provider

Tap will support out-of-tree providers via a plugin model (post-v0). For v0 the built-in set is the supported surface.

---

## 13. Rendering: from files to a ResolvedRequest

`Tap.Workspace.Rendering.WorkspaceRenderer.RenderAsync(requestRef, envRef, overrides)` produces a `ResolvedRequest`:

```
ResolvedRequest {
  method:    "POST"
  url:       "https://api.stripe.com/v1/customers"
  headers:   { "Content-Type": "application/x-www-form-urlencoded",
               "Stripe-Version": "2025-04-30",
               "Accept": "application/json",
               "Authorization": "Bearer sk_live_***" }
  body:      "email=jane%40example.com&name=Jane%20Doe"
  metadata:  { sourceFile, sourceLine, envId, secretsUsed: ["azkv:billing-prod/stripe-live-key"] }
}
```

The render pipeline:

1. Load the request file; parse frontmatter + body.
2. Find the owning collection by walking the request path (`collections/<slug>/...`); resolve the active stage, the request-or-stage-or-collection `auth:` ref, and inherited default headers.
3. Load the active `env.md`. Build the merged variable scope (§7.3).
4. Expand `{{var}}` in the URL line, header lines, and body of the fenced `http` block. Reject on unknown var.
5. Apply auth: bearer/basic/apikey inject headers/cookies/query; oauth2 obtains a token via the token cache (refreshing if needed); aws-sigv4 signs the canonical request. The profile's own fields expand against *its* scope (§8.0) — the owning collection's, which is not necessarily the request's.
6. Resolve `${{secret}}` references inline (last step — keeps secrets out of stages 1–4).
7. Return the resolved request. The caller (executor, CLI `render`, diff viewer) consumes it.

Step 6 is auditable: the renderer emits an `ISecretResolutionTrace` listing which refs were resolved, in which provider, at what timestamp.

Executors may add their own defaults on top of the resolved headers. Tap Studio stamps `User-Agent: tap-studio/<version>` when the rendered headers don't already carry one (case-insensitive) — a `User-Agent` set on the request, the collection's `defaultHeaders`, or the auth profile always wins.

---

## 14. References between files

Two ways to point from one file to another:

1. **Relative path** (recommended for v0): `auth: ../../auth/stripe-bearer.auth.md`, or `auth: stripe-oauth.auth.md` for a sibling inside the same collection. Survives `git mv` provided both files move together. Clearer in diffs.
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
| `E_FRONTMATTER_MALFORMED_YAML` | Frontmatter is not valid YAML 1.2. |
| `E_KIND_MISMATCH` | `kind:` does not match the filename suffix. |
| `E_KIND_MISSING` | `kind:` field absent. |
| `E_UNKNOWN_FIELD` | Frontmatter contains an unrecognized field for that kind (warning, not error, in v0). |
| `E_NO_REQUEST_BLOCK` | A `request` file has no fenced `http` block. |
| `E_MULTIPLE_REQUEST_BLOCKS` | A `request` file has more than one fenced `http` block. |
| `E_DANGLING_REF` | A `path` or `id:` reference does not resolve. |
| `E_VAR_UNKNOWN` | A `{{var}}` interpolation references a var not in scope. |
| `E_VAR_CYCLE` | Variable scope contains a reference cycle. |
| `E_UNKNOWN_SECRET_SCHEME` | A `${{scheme:...}}` reference uses a scheme not registered. |
| `E_SECRET_RESOLUTION_FAILED` | A registered provider rejected the reference. |
| `E_AUTH_TYPE_INVALID` | Auth `type:` is not a recognized value. |
| `E_HTTP_BLOCK_SYNTAX` | The fenced `http` block fails to parse as VS Code REST Client syntax. |
| `E_ASSERT_INVALID` | An entry under `assertions:` is not a usable (extractor, matcher) pair — see §5.5. |
| `E_FLOW_INVALID` | A `steps:` entry is malformed — no `request:`, two extraction sources, an unknown key. See §10. |
| `E_TEST_INVALID` | A `tests:` entry is malformed — neither or both of `request:`/`flow:`, an unknown key. See §11. |
| `E_WORKSPACE_LOAD_FAILED` | The workspace root could not be read at all. The workspace loads empty carrying this error rather than failing the host. |
| `E_WORKSPACE_SCAN_TRUNCATED` | The folder walk hit its budget (20s / 25 000 folders) and the workspace is only partially loaded — the root is far too broad. Walks skip `node_modules`, `.git`, `.hg`, `.svn`, `.venv`, `__pycache__`, `bin`, `obj`, `target`, and never follow symlinked directories. |

---

## 17. Out of scope for v0

The following are deliberate omissions, slated for later versions:

- Assertions on responses: now a first-class request field via `assertions:` (§5.5). Still
  out of scope — collection-level default assertions and assertions on WebSocket frames.
- Request chaining: now a first-class kind via `*.flow.md` (§10), grouped into test sets by
  `*.test.md` (§11). Still out of scope — parallel execution, data-driven tests (one test × N
  rows of variables), extracting a value back into a variable provider, per-step retry /
  wait-for polling, and a headless runner that turns a test set into a CI report.
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
$ tap render collections/customer/create.req.md --env environments/prod.env.md \
    --var customer.email=jane@example.com --var customer.name="Jane Doe"
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

With the audit trace:

```
secret  azkv:billing-prod/stripe-live-key  → resolved in 142ms
var     baseUrl                            ← collections/customer/_collection.md
var     customer.email                     ← CLI --var
var     customer.name                      ← CLI --var
```

This is the contract the rest of Tap (executor, diff viewer, capture-promotion flow, satellite) is built against.
