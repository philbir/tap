# Assertions and extractions — full grammar

## Assertions (`assertions:` on requests, flow steps, and test entries)

Each entry is one **extractor** (what to read from the response) + at most one
**matcher** (how to compare it) + optional modifiers. Assertions annotate the result —
they never change what is sent and never abort the exchange. A request that is
*supposed* to 404 asserts `status: 404` and passes.

```yaml
assertions:
- status: 2xx                       # scalar on an argument-less extractor = equals
- header: etag                      # argument extractor alone = exists
- header: content-type
  contains: application/json        # selector in the value slot, matcher as sibling
- jsonpath: $.order.customer.email
  equals: '{{user.email}}'          # expected side expands through the same cascade
- jsonpath: $.order.lines
  count: 3
- jsonpath: $.error
  exists: false
- xpath: /order/total
  gt: 100
- regex: '"id":\s*"ord-\d+"'        # shorthand for body + matches
- duration:
    lt: 800                         # argument-less extractor: matcher nests in its slot
- name: order id
  jsonpath: $.order.id
  matches: ^ord-\d+$
  skip: true
```

### Extractors — exactly one per entry

| Key | Argument | Reads |
|---|---|---|
| `status` | — | Status code, as a number. `2xx`/`20x` class patterns work (only here). |
| `duration` | — | Total elapsed ms, including redirects. |
| `header` | header name | First value; name match case-insensitive. |
| `body` | — | Decoded response body as text. |
| `jsonpath` | RFC 9535 expression | Nodelist from the JSON-parsed body. |
| `xpath` | XPath 1.0 expression | Result over the XML-parsed body. |
| `regex` | .NET pattern | Shorthand — normalizes to `body` + `matches`. |

### Matchers — at most one per entry

| Matcher | Applies to | Notes |
|---|---|---|
| `equals` / `notEquals` | anything | Type-coerced: both sides numeric → numeric compare; both boolean → boolean; else text. |
| `contains` / `notContains` | text, nodelist | Substring; membership over a multi-node result. |
| `startsWith` / `endsWith` | text | |
| `matches` / `notMatches` | text | .NET regex, 2 s timeout. |
| `lt` / `lte` / `gt` / `gte` | numbers | Fails with an explanation if either side isn't numeric. |
| `between` | numbers | Inclusive: `between: [200, 299]`. |
| `in` | anything | Membership: `in: [200, 201, 204]`. |
| `exists` | `header`, `jsonpath`, `xpath` | `true` (default) or `false`. |
| `count` | `jsonpath`, `xpath` | Matched-node count; valid on an empty result. |
| `length` | all but `status`/`duration` | Chars for text, elements for a JSON array, properties for an object, else node count. |
| `type` | `jsonpath` | `string` · `number` · `boolean` · `object` · `array` · `null`. |

### Modifiers

| Key | Meaning |
|---|---|
| `name` | Display label; generated from the assertion when absent. |
| `skip` | Listed but not evaluated; counts as neither passed nor failed. |
| `ignoreCase` | Case-insensitive string comparison. |

### Semantics you must know when authoring

- **Expected values are strings** (a `{{var}}` can only expand to one); coercion handles
  numbers/booleans. When the expected side resolves through something secret, the report
  masks it as `***`.
- **JSONPath cardinality.** Zero nodes: only `exists: false` / `count: 0` pass. One node:
  the matcher applies to its value (a JSON string compares as its contents). Several
  nodes: only `count`, `length`, `contains`, `notContains` work — the rest fail with
  *matched N nodes* rather than silently picking the first.
- **A wrong body type is a failed assertion, not an error** — `jsonpath` on non-JSON,
  a bad selector, an invalid regex each fail that one assertion with an explanation.
- **Truncated bodies** (capture stops at 2 MiB): body-family assertions fail with *body
  truncated* rather than passing on a prefix.
- **SSE**: body-family assertions run against the captured stream text once it ends.
- **WebSocket** requests keep their assertions but report them skipped (frame assertions
  aren't modelled) — and WS requests can't run inside flows/test sets at all.

## Extractions (`extract:` on flow steps only)

Bind response values into the run bag for later steps. Each entry: `var` (the name to
bind) + exactly one source — the same vocabulary as assertion extractors:

| Key | Argument | Binds |
|---|---|---|
| `status` / `duration` / `header` / `body` / `xpath` | as above | that value, as text |
| `jsonpath` | RFC 9535 | the matched node as text (a JSON string binds its contents) |
| `regex` | .NET pattern | a capture group of the first match |

| Modifier | Meaning |
|---|---|
| `group` | `regex` only — capture group to bind. Default 1 (0 when the pattern has no groups). |
| `default` | Bound when the source matches nothing, instead of failing the step. |
| `required` | `false` — bind nothing and carry on when the source matches nothing. |

```yaml
extract:
- var: orderId
  jsonpath: $.order.id
- var: token
  regex: 'session=([^;]+)'
- var: page
  header: x-page
  default: '1'
```

Unlike assertions, **a missing extraction fails the step** (the next step depends on the
value) — `default:` and `required: false` are the two ways to declare it optional. A
JSONPath matching several nodes is an error, not a first-node pick. Bound values are
run-scoped; nothing is written back to files or providers. A bound name shadows any
file-scope variable of the same name for the rest of the run.
