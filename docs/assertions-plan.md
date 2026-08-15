# Request assertions — design & implementation plan

Status: **implemented**. The normative spec now lives in
[workspace-format.md §5.5](workspace-format.md), and the user-facing walkthrough in
[studio.md](studio.md) — read those first; this file is kept as the design record
(rationale, alternatives, and the deferred work listed at the end).

Two things landed differently from the plan below: the test project is
`src/backend/Tap.Tests` rather than `Tap.Workspace.Tests`, because the parse → emit →
parse round-trip has to exercise the Studio emitter; and `POST /api/assertions/evaluate`
reports per-assertion problems as failed rows instead of rejecting the batch, so a
half-typed assertion doesn't blank out the verdicts beside it.

Assertions let a request declare what a passing response looks like — status, headers,
duration, and body content via JSONPath / XPath / text / regex selectors. Results ride
along with every execution (manual Send today, automated collection runs later). The
evaluator is a pure function in `Tap.Workspace`, so a future `tap test` CLI runner and
CI mode reuse it unchanged.

---

## 1. The assertion language

Assertions live in request frontmatter under the already-reserved `assertions:` key —
a YAML list, one assertion per entry. Every entry combines exactly one **extractor**
(what to pull from the response) with one **matcher** (how to compare), plus optional
modifiers. No string micro-syntax to learn; the sugar forms cover the common cases.

```yaml
---
kind: request
name: Create order
assertions:
  # sugar: a scalar on an extractor key means "equals"
  - status: 201
  # status class patterns: 2xx, 20x (x = wildcard digit)
  - status: 2xx
  # header + matcher
  - header: content-type
    contains: application/json
  # JSONPath (RFC 9535) against the JSON body; expected values support {{vars}}
  - jsonpath: $.order.customer.email
    equals: "{{user.email}}"
  - jsonpath: $.order.lines
    count: 3
  - jsonpath: $.error
    exists: false
  # XPath 1.0 against an XML body
  - xpath: /order/total
    gt: 100
  # plain-text body checks
  - body:
    contains: Thank you
  # regex sugar — equivalent to `body:` + `matches:`
  - regex: '"id":\s*"ord-\d+"'
  # timing
  - duration:
    lt: 800
  # modifiers
  - name: order id present
    jsonpath: $.order.id
    matches: '^ord-\d+$'
    skip: true          # temporarily disabled, still shown in UI
---
```

### 1.1 Extractors (exactly one per assertion)

| Key        | Argument             | Extracted value                                          |
|------------|----------------------|----------------------------------------------------------|
| `status`   | —                    | response status code (number)                            |
| `duration` | —                    | total elapsed milliseconds (number, includes redirects)  |
| `header`   | header name          | first value of that response header (case-insensitive name), or *absent* |
| `body`     | —                    | decoded response body text                               |
| `jsonpath` | RFC 9535 expression  | nodelist from the JSON-parsed body                       |
| `xpath`    | XPath 1.0 expression | evaluation result over the XML-parsed body               |
| `regex`    | .NET regex pattern   | **sugar** — normalizes to `body:` + `matches:`           |

### 1.2 Matchers (exactly one; defaults below)

| Matcher                      | Applies to        | Notes                                             |
|------------------------------|-------------------|---------------------------------------------------|
| `equals` / `notEquals`       | any               | smart coercion (§1.4)                             |
| `contains` / `notContains`   | string, nodelist  | substring; for a multi-node JSONPath result: membership |
| `startsWith` / `endsWith`    | string            |                                                   |
| `matches` / `notMatches`     | string            | .NET regex, 2 s match timeout                     |
| `lt` / `lte` / `gt` / `gte`  | number            | fails with a message if either side is not numeric |
| `between`                    | number            | inclusive two-element list: `between: [200, 299]` |
| `in`                         | any               | list membership: `in: [200, 201, 204]`            |
| `exists`                     | header, jsonpath, xpath | `true` (default) or `false`                 |
| `count`                      | jsonpath, xpath   | number of matched nodes                           |
| `length`                     | string, nodelist  | string length or node count                       |
| `type`                       | jsonpath          | `string` \| `number` \| `boolean` \| `object` \| `array` \| `null` |

Defaults when no matcher key is present:
- scalar value on the extractor key → `equals` (for `status`, class patterns like `2xx` are allowed);
- `header:` / `jsonpath:` / `xpath:` alone → `exists: true`.

### 1.3 Modifiers (all optional)

| Key          | Meaning                                                        |
|--------------|----------------------------------------------------------------|
| `name`       | display label; auto-generated when omitted (e.g. `$.order.id matches ^ord-\d+$`) |
| `skip`       | `true` — don't evaluate, report as skipped                     |
| `ignoreCase` | `true` — case-insensitive string comparison for `equals`/`contains`/`startsWith`/`endsWith` |

### 1.4 Semantics (normative)

- **Coercion.** If both actual and expected parse as numbers → numeric compare; both
  as booleans → boolean compare; otherwise ordinal string compare. Variable expansion
  produces strings, so coercion is what makes `equals: "{{expected.total}}"` work
  against a JSON number.
- **JSONPath cardinality.** 0 nodes: only `exists: false` passes, everything else
  fails with `no match for <expr>`. 1 node: the matcher applies to its value. N nodes:
  `count`/`length` compare N, `contains` tests membership, `equals` compares as an
  array when the expected value is a YAML list; other matchers fail with
  `expression matched N nodes`.
- **Wrong body type is a failed assert, not an error.** `jsonpath` on a non-JSON body
  fails with `response body is not valid JSON`; same for `xpath`/XML. A malformed
  selector or regex fails with the parser/regex message. The evaluator never throws.
- **Assertions never fail the exchange.** The request executes normally; results are
  attached to the response. Overall `ok` = every non-skipped assertion passed.
- **Variables.** Expected values, header names, and selector expressions are expanded
  with the same cascade/registry as the http block (workspace < collection < stage <
  env < request < overrides). If a secret variable was used in an expected value, the
  reported `expected` is masked (`***`) in results — mirror the `VariableCompiler`
  trace behavior.
- **Body caps.** Assertions see the same body the UI sees: capped at
  `HttpExecutionHelpers.BodyCap` (2 MiB) and decoded by `TryDecodeBody`. If the body
  was truncated, body/jsonpath/xpath/regex asserts fail with
  `body truncated at 2 MiB — not evaluated` rather than silently matching a prefix.
- **SSE responses** (`text/event-stream`): status/header/duration asserts evaluate
  normally; body-family asserts run against the accumulated captured stream text at
  completion.
- **WebSocket requests** (`protocol: websocket`): out of scope for v1 — parse and
  round-trip the field, but report each assert as skipped with
  `assertions are not supported for websocket requests yet`.
- **Regex** is .NET `System.Text.RegularExpressions` syntax with a 2-second match
  timeout (same guard as `Interpolation.cs`). No `NonBacktracking` (it would reject
  lookarounds/backreferences).

### 1.5 Canonical emit form

The Studio server remains the sole producer of file content. The emitter writes the
sugar form whenever possible (`- status: 200`, `- regex: …`) and the explicit
extractor + matcher form otherwise, in stable key order:
`name`, extractor, matcher, `ignoreCase`, `skip`. Multiline expected values use YAML
block scalars via the existing `QuoteStyleFor` policy so `{{var}}` round-trips
bit-for-bit. Hand-written explicit forms that have a sugar equivalent normalize to the
sugar on the next editor save (same normalization philosophy as the rest of the spec
emitters).

---

## 2. Architecture decisions

| Decision | Choice | Why |
|---|---|---|
| Field name | `assertions:` (file + wire + DTOs); UI tab label "Asserts" | Already reserved in the format doc; older binaries ignore unknown keys, so files stay backward-compatible |
| Engine location | `src/backend/Tap.Workspace/Asserts/` | Pure, testable, reusable by a future `tap test` CLI runner; parsing/rendering already live here |
| JSONPath | **JsonPath.Net** (json-everything) — add to `Directory.Packages.props` (check latest stable) | RFC 9535, works on `System.Text.Json` `JsonNode`, no Newtonsoft; fits the source-generated-STJ convention |
| XPath | `System.Xml.Linq` + `System.Xml.XPath` (BCL) | XPath 1.0 in-box, no dependency |
| Evaluation point | Server-side in Tap.Studio, after body capture, shared by `ExecuteEndpoint` and `ExecuteStreamEndpoint` | Asserts need the same variable registry as the request; results stream to the UI on the `done` event |
| Result transport | Extend `ExecuteStreamDoneDto` / `ExecutionResultDto` with `Assertions` + summary | Fewer moving parts than a new SSE event; UI already keys off `done` |
| Re-evaluate without resending | `POST /api/assertions/evaluate` (asserts + response snapshot + variable context → results) | Enables live pass/fail while authoring asserts against the last response — the core of the authoring experience |
| Persistence of results | None (ephemeral, per-execution) | Matches current design — no history store exists |
| Tests | New `src/backend/Tap.Workspace.Tests` (xunit), first test project in the repo | The evaluator/parser are pure functions; this is the foundation for test automation |

### Evaluator contract

```csharp
// Tap.Workspace/Asserts/
sealed record ResponseSnapshot(
    int Status,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? BodyText,
    bool BodyTruncated,
    double DurationMs);

sealed record AssertResult(
    int Index, string Name, bool Ok, bool Skipped,
    string? Actual, string? Expected, string? Message);

static class AssertEvaluator
{
    static IReadOnlyList<AssertResult> Evaluate(
        IReadOnlyList<ResolvedAssert> asserts, ResponseSnapshot response);
}
```

`ResolvedAssert` is the post-interpolation form (expected values / selectors expanded,
secret-mask flags set). `AssertSpec` is the on-disk form on `RequestFile`.

---

## 3. Phased implementation

Execute phases in order; each ends with a verifiable acceptance gate. Follow the
`transport:` feature (commit `4c72959`) as the end-to-end template for adding a
request-spec field.

### Phase 0 — spec first (docs)

1. `docs/workspace-format.md`: replace the reserved-field row in §5.1 (line ~179)
   with a pointer to a new **§5.5 Assertions** section containing the grammar and
   semantics from §1 of this plan; delete the assertions bullet from §15 (line ~651).

*Gate:* format doc fully specifies the language, including cardinality/coercion rules.

### Phase 1 — model, parse, emit (round-trip)

All in `src/backend/`:

1. `Tap.Workspace/Model/WorkspaceFile.cs` — add `sealed record AssertSpec` (extractor
   kind + argument, matcher kind + expected value(s), `Name`, `Skip`, `IgnoreCase`)
   and `IReadOnlyList<AssertSpec> Assertions` on `RequestFile` (~line 162).
2. `Tap.Workspace/Model/WorkspaceErrorCode.cs` — add `E_ASSERT_INVALID` (zero or ≥2
   extractors, ≥2 matchers, unknown key, non-numeric `between` bounds, etc.).
3. `Tap.Workspace/Parsing/FileParser.cs` — `ParseAsserts` wired into `ParseRequest`
   (~line 69); model on `ParseStages` (~line 97) for the sequence-of-maps shape.
   Sugar handling: scalar on extractor key → equals; `regex:` → body+matches;
   extractor alone → `exists: true`.
4. `Tap.Workspace/Parsing/YamlExt.cs` — add the small readers `ParseAsserts` needs
   (e.g. a `MappingList` helper, scalar-or-list reader for `in`/`between`).
5. `Tap.Studio/Contracts/Dtos.cs` — `AssertSpecDto`, add `Assertions` to
   `RequestSpecDto` (~485) and `RequestDetailDto` (~118); `[JsonSerializable]` for the
   DTO **and** `IReadOnlyList<AssertSpecDto>` in `StudioJson` (~886).
6. `Tap.Studio/Specs/SpecYaml.cs` — `SetAssertions` emit helper implementing §1.5
   canonical form (use `SetMappingList` ~line 95 as the base).
7. `Tap.Studio/Specs/RequestSpecEmitter.cs` — emit `assertions:` after `vars`,
   before `tags` in the frontmatter key order.
8. `Tap.Studio/Endpoints/RequestEndpoints.cs` — map the field in the GET detail DTO
   (~line 31) and accept it on `PUT /api/requests/spec` (~line 63).

*Gate:* a `.req.md` with every §1 example parses without errors and round-trips
byte-stable through parse → emit → parse. Invalid entries produce `E_ASSERT_INVALID`
with a line-anchored message, not an exception.

### Phase 2 — evaluation engine + tests

1. `Directory.Packages.props` — add `<PackageVersion Include="JsonPath.Net" …/>`
   (latest stable); reference from `Tap.Workspace.csproj`.
2. `Tap.Workspace/Asserts/AssertEvaluator.cs` (+ `ResponseSnapshot`, `AssertResult`,
   `ResolvedAssert`) implementing all §1.4 semantics, including auto-naming
   (deterministic, server-side, so the UI never re-derives labels).
3. New test project `src/backend/Tap.Workspace.Tests/` (xunit, net10.0, added to
   `Tap.slnx`). Coverage matrix: every extractor × representative matchers, sugar
   normalization, coercion table, JSONPath cardinality (0/1/N), non-JSON body,
   malformed selector, malformed regex, regex timeout, truncated body, `ignoreCase`,
   `skip`, `between`/`in`, status class patterns, XML/XPath node + attribute + count,
   header absence, auto-name generation, parser error codes. Also round-trip tests
   for Phase 1 (parse → emit → parse equality).
4. Update `CLAUDE.md` / `AGENTS.md` ("no test project yet" is no longer true; add
   `dotnet test` note).

*Gate:* `dotnet test` green; `TreatWarningsAsErrors` clean.

### Phase 3 — execution wiring

1. `Tap.Workspace/Rendering/ResolvedRequest.cs` — add
   `IReadOnlyList<ResolvedAssert> Assertions`.
2. `Tap.Workspace/Rendering/WorkspaceRenderer.cs` — expand expected values, header
   names, and selector expressions per-value with the existing cascade (like
   `defaultHeaders`, ~line 93); flag secret-derived expected values for masking from
   the interpolation trace.
3. `Tap.Studio/Contracts/Dtos.cs` — `AssertResultDto`
   (`index/name/ok/skipped/actual/expected/message`), `AssertSummaryDto`
   (`ok/passed/failed/skipped`); add both to `ExecutionResultDto` (~784) and
   `ExecuteStreamDoneDto` (~858) + `[JsonSerializable]` entries.
4. `Tap.Studio/Endpoints/ExecuteEndpoint.cs` + `ExecuteStreamEndpoint.cs` — after
   body capture, build `ResponseSnapshot` (truncation flag from the body-cap path in
   `HttpExecutionHelpers`), call `AssertEvaluator.Evaluate`, attach results.
   WebSocket branch: emit skipped results per §1.4. Truncate `actual` in results to
   ~1 KB.
5. New `Tap.Studio/Endpoints/AssertEndpoints.cs` —
   `POST /api/assertions/evaluate`: body = assert spec DTOs + response snapshot +
   `VariableContext`; renders the asserts through the same interpolation, evaluates,
   returns results. Register in `StudioHost.cs` (~165).

*Gate:* `curl` against a running Studio: executing a sample request returns assert
results on the `done` SSE event; the evaluate endpoint reproduces the same results
from a pasted snapshot; a draft (unsaved) spec with assertions flows through the
existing draft-spec path automatically.

### Phase 4 — Studio UI (`src/ui-studio/`, Mantine-only, load the mantine skill)

1. `src/api/types.ts` — `AssertSpec`, `AssertResult`, `AssertSummary`; add
   `assertions?` to `RequestSpec` (~126) and `RequestDetail` (~114); results on
   `ExecutionResult` (~748). `src/api/client.ts` — `evaluateAssertions(...)`.
2. `src/editors/RequestEditor.tsx` — new **Asserts** tab between `auth` and
   `transport` (~line 416): `TabCount` badge with the assertion count, plus a
   green/red `TabDot` after a run. Map the field in `specFromDetail` (~981) —
   omit when empty so dirty-tracking stays quiet.
3. New `src/editors/AssertsPanel.tsx` — row editor modeled on `KvTable.tsx`:
   extractor `Select`, selector/argument input, matcher `Select` (filtered to valid
   matchers for the extractor), expected-value `VariableInput` (with `context` +
   `onOpenVariables` so `{{var}}` chips work), `ignoreCase`/`skip` toggles, remove
   button, auto-name preview as `dimmed` text. "Add assertion" appends a
   `status equals 200` starter row.
4. `src/editors/ResponsePanel.tsx` — new **Asserts** result tab (`availableTabs`
   ~92, tab strip ~159, panels ~206): summary line ("3 passed · 1 failed"), then one
   row per result — icon (`IconCheck`/`IconX`/skip), name, and on failure
   expected vs actual + message. Show a compact `2/3` pass badge next to the status
   chip in the response header.
5. **Live re-evaluation** — while the Asserts tab is open and a last response exists,
   debounce-call `evaluateAssertions` on spec change and paint row-level pass/fail
   dots in the editor itself. This is the flagship authoring loop: send once, sculpt
   asserts against the real response with instant feedback, save.
6. Default new-request specs (`shell/Sidebar.tsx` ~232, `editors/CreateNewDialog.tsx`
   ~202, `shell/DuplicateRequestDialog.tsx` ~48) — carry/initialize the field.

*Gate (per CLAUDE.md, must verify in browser):* with the Studio AppHost running and
`yarn dev`, author asserts on a demo request, Send, see pass/fail in ResponsePanel and
the tab dot; edit an expected value and watch live re-eval flip a row without
resending; save, confirm the Source tab shows canonical YAML; reload, confirm
round-trip. Check the console for errors.

### Phase 5 — samples & polish

1. Add assertions to a few sample requests exercising Demo.Api:
   `samples/sample-workspace/collections/demo/methods/01-get.req.md` (status + jsonpath),
   `content/json.req.md` (jsonpath count/type), `content/status-404.req.md`
   (`status: 404` — asserting a "failure" passes), `content/xml`-flavored sample if
   present or add one (xpath), plus one regex example.
2. `docs/studio.md` — short "Assertions" section with a screenshot placeholder.
3. Release notes entry under `docs/release-notes/`.

*Gate:* fresh checkout → AppHost start → sample requests show passing asserts.

### Explicitly out of scope (future work, enabled by this design)

- **Collection-level default assertions** (e.g. every request: `status: 2xx`,
  `duration lt 2000`) — merge in `WorkspaceRenderer` like `defaultHeaders`.
- **"Add assert from response"** — click a node in the ResponsePanel JSON tree →
  pre-filled jsonpath assertion.
- **`tap test` CLI runner** — run a collection headless, evaluate assertions, emit
  JUnit/TAP output; the Tap.Workspace engine is already CLI-ready.
- **WebSocket frame assertions** (`WebSocketExecutor.cs` names these as future work).
- **Assertion history / trend** — needs a results store that doesn't exist yet.

---

## 4. Risks & notes for the implementing agent

- **JsonPath.Net version**: verify the current stable on nuget.org before pinning in
  `Directory.Packages.props`; versions are managed centrally only.
- **`StudioJson` completeness**: every new DTO *and* every `IReadOnlyList<>` wrapper
  needs its own `[JsonSerializable]` — missing entries fail at runtime, not compile
  time.
- **Round-trip byte-stability** is a hard requirement of the spec-emitter design —
  add the parse→emit→parse tests before touching the emitter.
- **Secret masking**: expected values interpolated from secret variables must render
  as `***` in `AssertResultDto.Expected` (reuse the sensitivity flags the
  interpolation trace already carries).
- **Don't evaluate client-side.** The UI never re-implements matcher semantics; it
  only renders server-produced results (single source of truth for future CI parity).
- `TreatWarningsAsErrors` is global; the new test project inherits it.
- Backend iteration per CLAUDE.md: `aspire resource <name> rebuild` against the
  running Studio AppHost — no `dotnet run`.
