# Test sets & flows — design & implementation plan

Status: **implemented**. The normative spec lives in
[workspace-format.md §10 / §11](workspace-format.md); the user-facing walkthrough in
[studio.md](studio.md) — read those first. This file is the design record: rationale,
alternatives, and the deferred work at the end.

Three things landed differently from the plan below. `steps:` / `tests:` are **not** required —
the editor writes a file's frontmatter before its first entry exists, and refusing to load that
file would mean a freshly created test set could never be reopened; malformed *entries* are
still rejected. A malformed assertion inside a step keeps its own `E_ASSERT_INVALID` rather
than being reclassified, with the step's position prefixed to the message. And a test entry's
`vars:` sit below extraction in the run bag, not above it — see §1.4.

Assertions ([assertions-plan.md](assertions-plan.md)) answered *"did this one response look
right?"*. Testing answers the two questions above it: *"do these requests still pass?"* and
*"does this multi-step exchange still work end to end?"*. Everything here composes existing
parts — the renderer's variable cascade, the assertion evaluator, the execute pipeline — so a
future `tap test` CLI reuses the same engine.

---

## 1. Two kinds, not one

A **flow** and a **test set** are different things and get different files:

| Kind | File | Answers | Shape |
|---|---|---|---|
| `flow` | `*.flow.md` | "does this sequence still work?" | Ordered **steps**. Each step runs one request and can **extract** values from its response into variables the later steps read. |
| `test` | `*.test.md` | "do these still pass?" | A **test set**: set-scoped variables plus a list of **tests**, each of which runs *either* one request *or* one flow, with its own variables and extra assertions. |

The alternative — a single kind with a flat step list — collapses the two: a suite of
independent checks and a dependent chain need opposite failure semantics (keep going vs. stop),
and a flow is worth reusing from more than one test set. Two files also keep each editor
honest: the flow editor is a *composer* (what feeds what), the test-set editor is a *runner*
(what to check, with which variables).

A flow is useful on its own — a login-then-call sequence you run by hand — so it is runnable
directly, not only through a test set.

### 1.1 `flow` — `*.flow.md`

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

- `request:` is a ref — a path relative to the flow file, or `id:<uuid>` — resolved the same
  way `auth:` is on a request. A step never inlines a request: requests stay the single
  definition of what goes on the wire, and a step is composition only.
- `vars:` on a step are **templates**, expanded once against the run bag before they become
  overrides. That is what makes `id: '{{orderId}}'` read step 1's output.
- `extract:` binds response values to names in the run bag. One entry, one variable.
- `assertions:` is the §5.5 grammar verbatim, evaluated against that step's response *in
  addition to* whatever the request file declares.
- A failed step stops the flow, because everything after it was going to run against a state
  that never happened. `continueOnFailure: true` opts a step out; `skip: true` parks one.

### 1.2 `test` — `*.test.md`

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

- Each entry names exactly one of `request:` or `flow:`.
- `vars:` at the set level are the "overwrite at last" tier — above env and request scope,
  below only the entry's own `vars:`.
- `assertions:` on a `request:` entry check that request's response; on a `flow:` entry they
  check the **last step's** response, which is the one a caller of the flow would see.
- `onFailure: continue` (the default) runs every test regardless — independent checks. `stop`
  aborts the set at the first failure, for a set whose entries build on each other.

### 1.3 Extraction

An extract entry is a `var` plus exactly one source, reusing the assertion extractor vocabulary
so there is one thing to learn:

| Key | Argument | Binds |
|---|---|---|
| `status` | — | the status code |
| `duration` | — | elapsed milliseconds |
| `header` | header name | that header's first value |
| `body` | — | the whole decoded body |
| `jsonpath` | RFC 9535 expression | the matched node (as text; a JSON string binds its contents) |
| `xpath` | XPath 1.0 expression | the matched node's value |
| `regex` | .NET pattern | a capture group of the first match — `group:` selects it, default 1 (or 0 when the pattern has no groups) |

Modifiers: `default:` (used when the source matched nothing, instead of failing the step) and
`required: false` (bind nothing and carry on). A multi-node JSONPath is an error, not a silent
first-node pick — same rule assertions use.

Extraction failure is a **step failure**, unlike an assertion, which only annotates: a missing
`orderId` means the next step is going to send `{{orderId}}` literally or fail to render, and
saying so at the extraction is a better error than either.

### 1.4 Variable precedence

The renderer's cascade is unchanged (workspace < collection < stage < env < request < **overrides**).
A run supplies the overrides, built in this order — later wins:

1. test-set `vars`
2. the test entry's `vars` (expanded against the bag first)
3. flow `vars`
4. values bound by `extract:` as the run progresses
5. the step's own `vars` (expanded against the bag first)

Extraction beating the static tiers is the point of a flow: step 2 must see step 1's output.
An author who wants a fixed value simply doesn't extract over it. Step vars win last so a
single step can pin a value without touching the rest of the run.

The entry tier sits *below* extraction rather than above: a flow whose bound id could be
overridden by whichever set happened to call it is no longer a flow. For a request entry —
which extracts nothing — the entry tier is effectively the top, which is what "a test set's
variables overwrite last" means in practice.

Bound values are **run-scoped** — nothing is written back to a file or to a provider.

---

## 2. Architecture decisions

| Decision | Choice | Why |
|---|---|---|
| Location | `tests/` at the workspace root, beside `collections/` / `environments/` / `auth/` | A flow spans collections, so it can't be owned by one. Files parse from anywhere; new ones default here. |
| Engine location | `Tap.Studio/Testing/` | Unlike the assertion evaluator it needs the HTTP executor and the workspace service, which are Studio-side. The *pure* halves (extraction, bag composition) live in `Tap.Workspace` so the future CLI keeps them. |
| Extraction | Refactor `AssertEvaluator`'s private extractor into a public `ResponseReader` | The two must never disagree about what `$.order.id` means. One implementation, two callers. |
| Step execution | Reuse `WorkspaceService.RenderAsync` + `HttpExecutionHelpers` | A step must behave exactly like a Send of the same request: same auth injection, same redirect policy, same body cap, same assertion evaluation. |
| Result transport | SSE (`POST /api/tests/run`), one event per step | A ten-step flow against a slow API is the normal case; a spinner that sits there for 30 s is not. Same event-stream shape as `/api/execute/stream`. |
| Persistence of results | None | Matches assertions — ephemeral per-run. A results store is its own feature. |
| Assertion merging | Request's own assertions first, then the step's | Both are real expectations; a step that adds `status: 404` to a request asserting `status: 2xx` is a contradiction the author should see, not something to resolve silently. |

---

## 3. Phases

0. **Spec** — this file + workspace-format §2/§9/§10.
1. **Model, parse, emit** — new kinds through parser and emitter, byte-stable round-trip.
2. **Extraction engine** — public `ResponseReader`, regex capture groups, unit tests.
3. **Run engine + endpoints** — CRUD, SSE run, `AssertRunner` reuse.
4. **Studio UI** — Testing sidebar tab, flow composer, test-set editor, run panel.
5. **Tests, samples, docs.**

---

## 4. Deferred

- **Parallel test execution** — the set-level model allows it (independent entries), but
  ordering makes failures far easier to read while the feature is new.
- **`tap test` CLI + JUnit output** — the engine split above is what makes it a small job.
- **Data-driven tests** — one entry × N rows of variables. Wants a table editor and a
  results matrix; worth doing only once the single-row case is proven.
- **Extract into a provider** — writing a bound value back to a variable provider so it
  outlives the run. Deliberately out: a test run that mutates workspace state is a surprise.
- **Flow-level retry / wait-for** — polling a status endpoint until it flips. Needs a
  step-level loop construct.
