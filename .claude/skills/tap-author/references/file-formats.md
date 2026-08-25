# Tap file formats — full frontmatter spec + templates

Every file: YAML frontmatter fenced by `---`, then a CommonMark body (documentation,
except the one `http` block in requests). UTF-8, LF. Universal fields on every kind:

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | enum | yes | `workspace` · `collection` · `request` · `auth` · `env` · `flow` · `test` |
| `id` | uuid | no | Stable identity; assigned (UUIDv7) by the Studio on first save. Omit when authoring. |
| `name` | string | no | Display name; defaults to the filename stem. CLI/MCP targets resolve by it. |
| `tags` | string[] | no | Free-form labels; `test --tag` selects by them. |

Cross-file refs (`auth:`, `request:`, `flow:`) are relative paths from the referencing
file (preferred), or `id:<uuid>` refs. `E_DANGLING_REF` when the target doesn't exist.

All filenames end in `.tap`. Workspaces authored before 0.7.0 use the same formats with a
`.md` extension (and a manifest named `tap.md`); those still load, with a
`W_LEGACY_EXTENSION` warning, until 0.8.0. `tap-studio migrate` converts a workspace,
renaming files and rewriting refs together.

---

## `workspace` — `workspace.tap` (workspace root marker)

| Field | Type | Notes |
|---|---|---|
| `defaultEnv` | path | Env used when a run doesn't pass `--env`. |
| `vars` | map<string, var-spec> | Lowest cascade tier. |
| `variableProviders` | array | Declares named providers: `{ name, type, settings? }`. Types: `env` (host env vars, gated by `TAP_VARS_ALLOWED`/`TAP_SECRETS_ALLOWED`), `file` (workspace-local store, secrets encrypted), `azkv` (Azure Key Vault; settings `vaultName`, `tenantId`, `prefix`, `filter` — a regex limiting which secret names the provider exposes), `1p` (1Password CLI). A host-level `system` provider also exists (`~/.tap/system.json`). |
| `defaultVariableProvider` | string | Provider bare `{{name}}` tokens hit first after the cascade. |
| `response` | map | Body caps: `maxBytes` (default `2mb`) is how much of a response is delivered inline and seen by body assertions; `maxRetainedBytes` (default `64mb`) is how much the Studio holds back for "Show all" / a full download. Sizes are bytes or `kb`/`mb`/`gb` (1024-based). |
| `history` | bool \| map | Record exchanges to `.tap-history/`. Off by default. See **History** below. Workspace tier is the weakest; a collection or request overrides it per key. Only this scope may set `orphanRetentionDays`. |

```markdown
---
kind: workspace
name: acme-billing
defaultEnv: environments/local.env.tap
defaultVariableProvider: file
variableProviders:
- name: env
  type: env
- name: file
  type: file
- name: kv-prod
  type: azkv
  settings:
    vaultName: acme-prod
    tenantId: 00000000-0000-0000-0000-000000000000
response:
  maxBytes: 8mb
  maxRetainedBytes: 256mb
vars:
  app.userAgent: acme-tap/1.0
---

# Acme Billing

Workspace docs for humans.
```

## `collection` — `collections/<slug>/_collection.tap`

| Field | Type | Notes |
|---|---|---|
| `baseUrl` | string | May contain `{{vars}}`. Scheme optional — bare `host:port` renders `http://` (`ws://` for websocket requests). Required if any member request uses a relative URL. |
| `defaultAuth` | path \| id-ref | Inherited by member requests without their own `auth:`. |
| `defaultHeaders` | map<string,string> | Merged under request-specific headers. |
| `transport` | mapping | `ignoreTlsErrors: true` and/or `timeoutMs: <n>` — inherited by member requests. |
| `vars` | map<string, var-spec> | Collection tier of the cascade. |
| `agent` | bool \| mapping | `agent: false` (or `agent: { enabled: false }`) fences the collection off from agent surfaces. Default enabled; omit unless opting out. |
| `history` | bool \| map | Recording policy for every request in the collection. Overrides the workspace per key; a request overrides it in turn. See **History** below. |

```markdown
---
kind: collection
name: Stripe
baseUrl: '{{API_URL}}'
defaultAuth: stripe-oauth.auth.tap
defaultHeaders:
  Accept: application/json
vars:
  API_URL: https://api.stripe.test
  IDP_URL: https://idp.stripe.test
---

# Stripe
```

Per-target overrides — `dev` / `uat` / `prod` of this one API — are **environments assigned to
the collection**, not a `stages:` block (removed in 0.7.0; a collection still carrying one
fails to parse):

```markdown
---
kind: env
name: prod
collections:
- collection: stripe
  baseUrl: https://api.stripe.com
vars:
  IDP_URL: https://idp.stripe.com
---
```

## `request` — `*.req.tap`

| Field | Type | Notes |
|---|---|---|
| `auth` | path \| id-ref | Overrides the collection's `defaultAuth`; point at a `type: none` profile to opt out of an inherited default. |
| `protocol` | `http` \| `websocket` | Default `http`. `websocket`: scheme normalizes to ws/wss, body (if any) is sent as the first text frame. WS requests run from the editor/send only — not from flows/test sets. |
| `vars` | map<string, var-spec> | Request tier (top file tier). |
| `transport` | mapping | `ignoreTlsErrors`, `timeoutMs` — overrides collection transport. |
| `history` | bool \| map | Recording policy for this request alone. Strongest tier. See **History** below. |
| `assertions` | array | See references/assertions.md. |

Body: **exactly one** fenced ```` ```http ```` block (REST-Client syntax): request line
(`METHOD url`, url may be relative → joined onto the collection baseUrl), then header
lines, then a blank line, then the body. `{{var}}` interpolates in all three.

```markdown
---
kind: request
name: Create customer
auth: ../../auth/stripe-bearer.auth.tap
tags: [customer, write]
vars:
  customer.email:
    description: Email used for signup
    required: true
  customer.name: Jane Doe
assertions:
- status: 2xx
- jsonpath: $.id
  matches: ^cus_
---

# Create customer

Why this request exists, for the next human.

```http
POST /v1/customers
Content-Type: application/x-www-form-urlencoded

email={{customer.email}}&name={{customer.name}}
```
```

**var-spec**: a literal string (the default value) or an object:
`{ default: <string>, secret: <bool>, description: <string>, required: <bool>, example: <string> }`.
`secret: true` masks the value everywhere.

## `auth` — `*.auth.tap`

Location decides variable scope: `auth/**` sees workspace+env vars; inside a collection it
also sees that collection's vars (that's how `{{IDP_URL}}` re-points per environment). The
profile's own location decides this, not the caller's — a request borrowing a profile from
another collection resolves it in that collection's scope.
Common fields: `kind: auth`, `name`, `type` (required), `tags`. Type-specific fields —
any field marked *(s)* may reference a secret via `{{provider:name}}`:

| `type` | Fields | Behavior |
|---|---|---|
| `none` | — | Explicit opt-out profile. |
| `bearer` | `token` *(s)* | `Authorization: Bearer <token>`. |
| `basic` | `username`, `password` *(s)* | Basic auth header. |
| `apiKey` | `in` (`header`\|`query`\|`cookie`), `apiKeyName`, `apiKeyValue` *(s)* | Injects the key where `in` says (only `header` is applied today). Don't use `name`/`value` — `name` is the profile's display name. |
| `oauth2` | `flow` (`authorization_code` \| `authorization_code_pkce` \| `client_credentials` \| `device_code` \| `password`), `tokenUrl`, `clientId` *(s)*, `clientSecret?` *(s)*, `authorizeUrl` (auth-code flows), `scopes[]`, `audience?` (no `redirectUri` — the runtime derives it and ignores the field) | Token minted at run time and cached per environment — never written to files. Interactive flows (PKCE/device) need a human: Studio UI, or CLI `--use-cached-tokens` after the user signed in. `client_credentials`/`password` work headlessly. |
| `azure-cli` | `scope` (v2) or `resource` (v1) | Shells out to `az account get-access-token`; requires prior `az login`. |
| `jwt` | `algorithm` (HS256/RS256/RS512/…), `key` *(s)*, claim fields | Renderer mints and signs the JWT itself. |
| `github` | `mode` (`gh-cli` \| `pat`), for `pat`: `token` *(s)* | `gh-cli` shells out to `gh auth token`; adds GitHub API headers automatically. |
| `aws-sigv4` | `region`, `service`, `accessKeyId` *(s)*, `secretAccessKey` *(s)*, `sessionToken?` *(s)* | SigV4-signs the request. |
| `custom` | `headers` map *(s)*, `query` map *(s)* | Raw injection. Last resort. |

```markdown
---
kind: auth
name: Stripe OAuth
type: oauth2
flow: client_credentials
tokenUrl: '{{IDP_URL}}/connect/token'
clientId: '{{STRIPE_CLIENT_ID}}'
clientSecret: '{{kv-prod:stripe-client-secret}}'
scopes: [api]
---
```

## `env` — `*.env.tap`

The single mechanism for "the same requests, pointed somewhere else". **Global** with no
`collections:` key (selectable anywhere — `environments/dev.env.tap`); **assigned** with one
(offered only in those collections, and applied only to them — put it beside
`_collection.tap`). The assigned form is what a collection `stage` was.

| Field | Type | Notes |
|---|---|---|
| `collections` | sequence | Collections to assign this env to. Each entry is a **slug** (not a path — that's a parse error), or a mapping with `collection` plus that collection's overrides. Absent/empty = global. One entry per slug. |
| `collections[].baseUrl` | string | Replaces *that* collection's `baseUrl` while active. Per-assignment because one env points each collection somewhere different. |
| `collections[].defaultAuth` | path \| id-ref | Replaces *that* collection's `defaultAuth` while active; resolved relative to this env file. A request's own `auth:` still wins. |
| `vars` | map<string, var-spec> | Env tier — above collection, below request. |
| `defaultVariableProvider` | string | Provider bare tokens hit first while this env is active. |
| `providerAliases` | map<string,string> | Stable alias → provider name (`kv: kv-dev` here, `kv: kv-prod` in prod.env.tap) so requests keep one spelling `{{kv:secret}}`. |
| `strictVariables` | bool | With a default provider set: bare-token misses fail instead of falling through. |

```markdown
---
kind: env
name: Production
vars:
  user.email: noreply+prod@acme.example
providerAliases:
  kv: kv-prod
strictVariables: true
---
```

## `flow` — `tests/*.flow.tap`

Ordered steps; each sends one referenced request and can bind response values for later
steps. Frontmatter: `vars` (flow tier of run overrides), `steps` (sequence), `tags`.

Step fields: `request` (path, required — steps never inline requests), `name?`,
`vars?` (templates, expanded against the run bag — this is how `id: '{{orderId}}'` reads
an earlier step), `extract?` (see assertions reference), `assertions?` (extra checks on
this step's response), `continueOnFailure?` (default false — a failed step skips the
rest), `skip?`.

```markdown
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

## `test` — `tests/*.test.tap`

Frontmatter: `vars` (set tier — the topmost file tier of run overrides), `onFailure`
(`continue` default \| `stop`), `tests` (sequence), `tags`.

Each test names exactly one of `request:` or `flow:`, plus `name?`, `vars?` (templates),
`assertions?` (checked against the response — for a flow, against the **last** step's),
`skip?`. The referenced request's own assertions also run; a contradiction is reported
as a failure, not resolved silently. A run caps at 500 requests.

```markdown
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

## History

`history:` records each exchange to `.tap-history/` at the workspace root — one folder per
request id, one JSON file per exchange. Declarable on `workspace.tap`, `_collection.tap`, and a
single request, merged **per key** with the nearest scope winning.

```yaml
history: true              # shorthand for { enabled: true }

history:
  enabled: true
  maxEntries: 25           # per request, oldest pruned
  encrypt: false
  maxBodyBytes: 256kb      # same size grammar as `response:`
  orphanRetentionDays: 30  # workspace.tap only
```

Two things to know when authoring:

- **It needs an `id:`.** History is keyed by the request's stable id so a rename doesn't orphan
  it. A request with no `id:` is not recorded. The Studio assigns one on save; a hand-written
  file needs one written in.
- **Redacted, or encrypted — never both off.** With `encrypt: false` (default) credential
  headers are masked and every resolved secret is replaced by value. With `encrypt: true` the
  entry keeps what actually went on the wire and the file is sealed with the machine key; if no
  key can be obtained, nothing is written rather than being stored in the clear.

The folder writes its own `.gitignore`, so recorded traffic stays out of commits. Only the
Studio's interactive Send records — `tap-studio send`, MCP tools, and test runs do not.

## Run-override precedence (top tier of the cascade, later wins)

test-set `vars` → test-entry `vars` → flow `vars` → values bound by `extract:` as the
run progresses → the step's own `vars` → CLI `--var`. Extraction outranking file tiers
is the point: step 2 must see step 1's output; pin a value by not extracting over it.

---

## `.http` — the portable request format

Visual Studio scaffolds one of these into every new ASP.NET Core project, and REST Client,
JetBrains, httpyac, and Kulala all read the same dialect. Tap loads them as first-class
requests — same engine, same auth, same assertions. **Tap never reformats a `.http` file**, so
edit them as raw text; there is no spec/emitter round-trip for this kind.

One file holds several requests, separated by `###`. Each is addressed by a *fragment path*:

```
collections/demo/orders.http#get-order
```

That fragment is the canonical identity and is what a `*.flow.tap` step or `*.test.tap` entry
references. If the file holds exactly one request, the bare path works too.

Naming precedence: `# @name` > the `###` title > a slug from method + last path segment > ordinal.

```http
# File-level directives apply to every request below.
# @tap-collection billing
# @tap-assert status == 200

@apiVersion = v1

### Get order
# @name get-order
# @tap-assert body $.id exists
GET /{{apiVersion}}/orders/{{orderId}}
Accept: application/json

### Create order
# @tap-assert header location exists
POST /{{apiVersion}}/orders
Content-Type: application/json

{"sku":"ABC","requestId":"{{$guid}}"}
```

### Directives

| Directive | Effect |
|---|---|
| `# @tap-collection <slug>` | Inherit a collection's baseUrl/headers/auth and its scoped environments from anywhere in the repo. Files already under `collections/<slug>/` don't need it. |
| `# @tap-auth <path\|id:uuid>` | Same as the `auth:` frontmatter key. |
| `# @tap-assert <expr>` | One assertion; repeatable. |
| `# @tap-secret <var>[, ...]` | Mark file variables secret so their values are redacted. |
| `# @tap-protocol websocket` | Same as `protocol:`. |
| `# @tap-tag a, b` | Same as `tags:`. |

Above the first request = file-wide default. Inside a request = override, except assertions,
tags, and secrets, which accumulate. Unknown `@tap-*` keys warn.

### One-line assertions

`.http` has no YAML, so assertions use an expression spelling of the same model — identical
`AssertSpec`, identical validation, identical reporting.

```
# @tap-assert status == 200
# @tap-assert status 2xx
# @tap-assert header content-type contains application/json
# @tap-assert header etag                 # no operator means exists
# @tap-assert body $.id exists
# @tap-assert $.items count 3
# @tap-assert duration < 2000
```

`<extractor> [selector] [operator] [value]`; a bare value means equals, nothing means exists.
Extractors: `status`, `duration`, `header <name>`, `body`, `body <$.jsonpath>` (or a bare
`$.jsonpath`), `xpath <expr>`.

### Staying portable

A `.http` file is expected to run in Visual Studio and REST Client too, where nothing knows about
collections — so `GET /orders` is not a URL there. Write the portable form instead:

```
@baseUrl = http://localhost:5000

### Ping
GET {{baseUrl}}/
```

Outside Tap, `@baseUrl` answers. Inside Tap, a file's own variables are the **weakest** tier of
the cascade, so the collection's baseUrl (and the active environment's override, and any env
redefining the name) overrides it — which is also what keeps the environment picker meaningful.
`{{baseUrl}}` is built in: Tap binds it to the env-resolved base URL whenever no other scope
defines the name, so it works even in a file that never declared one.

A relative request line still works and still inherits the collection's baseUrl — prefer the
portable form when the file is shared with other tools; either is fine otherwise.

### Other file syntax

- `@name = value` declares a file variable, visible to every request in the file regardless of
  declaration order. Sits at the portable tier — below every workspace scope, see above.
- `# @timeout 30` (seconds) sets the transport timeout; `< ./file` includes a body.
- `{{$guid}}`, `{{$uuid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt [min max]}}` are
  generated at render time. Each resolves once per render, so the same token in a header and the
  body is one value. These work in `*.req.tap` too.
- Constructs from other tools (JetBrains `{% %}` scripts, httpyac `??`, `run`/`import`, `>>`
  redirects, request chaining `{{x.response...}}`) are skipped with a warning naming the Tap
  equivalent. For chaining, that is a flow.
