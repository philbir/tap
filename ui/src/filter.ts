// Structured filter language for the request list.
//
// Tokens are space-separated and ANDed together. Each token is either a bare
// substring search (matches across method/host/path/status, like before) or a
// `key:value` clause. Prefix any token with `-` to negate it. Quote any token
// with " or ' to allow embedded spaces.
//
// Supported keys:
//
//   method:GET                  exact (case-insensitive); comma-list = OR
//   method:GET,POST
//   status:200                  status:>=400  status:!=204  status:4xx  status:5xx
//   host:api.foo.com            substring, case-insensitive
//   path:/users                 substring on path (incl. query)
//   body:user_id                substring on req+resp bodies
//   header:authorization        header exists
//   header:x-api=secret         header exists AND value contains substring
//   dur:>500                    operator + ms (or e.g. "2s")
//   is:ws | sse | stream        type predicates
//   is:error | live | redirect | ok | replay
//   has:body | error | sse | ws
//
// The parser is deliberately forgiving: unrecognized clauses degrade to a
// plain free-text match against the entire token so the user always sees a
// sensible result while typing.

import type { RequestRecord } from './types'

export type Predicate = (r: RequestRecord) => boolean

export interface ParsedFilter {
  raw: string
  predicate: Predicate
  /** True if at least one clause uses structured syntax (vs. all free-text). */
  structured: boolean
}

const KEYS = new Set([
  'method', 'status', 'host', 'path', 'body', 'header', 'dur', 'is', 'has',
])

function tokenize(input: string): string[] {
  const out: string[] = []
  let cur = ''
  let quote: '"' | "'" | null = null
  for (let i = 0; i < input.length; i++) {
    const c = input[i]
    if (quote) {
      if (c === quote) { quote = null; continue }
      if (c === '\\' && i + 1 < input.length) { cur += input[++i]; continue }
      cur += c
      continue
    }
    if (c === '"' || c === "'") { quote = c; continue }
    if (c === ' ' || c === '\t' || c === '\n') {
      if (cur) { out.push(cur); cur = '' }
      continue
    }
    cur += c
  }
  if (cur) out.push(cur)
  return out
}

function freeMatch(r: RequestRecord, q: string): boolean {
  const ql = q.toLowerCase()
  return (
    r.path.toLowerCase().includes(ql) ||
    r.method.toLowerCase().includes(ql) ||
    r.host.toLowerCase().includes(ql) ||
    String(r.statusCode).includes(q)
  )
}

type NumOp = '=' | '!=' | '>' | '<' | '>=' | '<='

function parseNumOp(v: string): { op: NumOp; rest: string } {
  if (v.startsWith('>=')) return { op: '>=', rest: v.slice(2) }
  if (v.startsWith('<=')) return { op: '<=', rest: v.slice(2) }
  if (v.startsWith('!=')) return { op: '!=', rest: v.slice(2) }
  if (v.startsWith('>')) return { op: '>', rest: v.slice(1) }
  if (v.startsWith('<')) return { op: '<', rest: v.slice(1) }
  if (v.startsWith('=')) return { op: '=', rest: v.slice(1) }
  return { op: '=', rest: v }
}

function compareN(a: number, op: NumOp, b: number): boolean {
  switch (op) {
    case '=': return a === b
    case '!=': return a !== b
    case '>': return a > b
    case '<': return a < b
    case '>=': return a >= b
    case '<=': return a <= b
  }
}

function statusPredicate(value: string): Predicate | null {
  // Accept a comma-list: status:404,500
  const parts = value.split(',').map(s => s.trim()).filter(Boolean)
  if (parts.length === 0) return null
  const checks: Array<(s: number) => boolean> = []
  for (const part of parts) {
    if (/^[1-5]xx$/i.test(part)) {
      const lead = parseInt(part[0], 10)
      checks.push((s) => s >= lead * 100 && s < (lead + 1) * 100)
      continue
    }
    const { op, rest } = parseNumOp(part)
    const n = parseInt(rest, 10)
    if (Number.isNaN(n)) return null
    checks.push((s) => compareN(s, op, n))
  }
  return (r) => checks.some(c => c(r.statusCode))
}

function durationPredicate(value: string): Predicate | null {
  const { op, rest } = parseNumOp(value)
  const m = /^(\d+(?:\.\d+)?)(ms|s)?$/.exec(rest)
  if (!m) return null
  let n = parseFloat(m[1])
  if (m[2] === 's') n *= 1000
  if (!Number.isFinite(n)) return null
  return (r) => compareN(r.durationMs, op, n)
}

function methodPredicate(value: string): Predicate {
  const set = new Set(value.split(',').map(s => s.trim().toUpperCase()).filter(Boolean))
  return (r) => set.has(r.method.toUpperCase())
}

function headerPredicate(value: string): Predicate | null {
  if (!value) return null
  const eq = value.indexOf('=')
  const name = (eq < 0 ? value : value.slice(0, eq)).toLowerCase().trim()
  const expected = eq < 0 ? null : value.slice(eq + 1).toLowerCase()
  if (!name) return null
  return (r) => {
    const all = { ...r.requestHeaders, ...r.responseHeaders }
    for (const [k, v] of Object.entries(all)) {
      if (k.toLowerCase() === name) {
        if (expected === null) return true
        return v.toLowerCase().includes(expected)
      }
    }
    return false
  }
}

function isPredicate(value: string): Predicate | null {
  switch (value.toLowerCase()) {
    case 'ws':
    case 'websocket':
      return (r) => !!r.isWebSocket
    case 'sse':
      return (r) => !!r.isStream && !r.isWebSocket
    case 'stream':
      return (r) => !!r.isStream || !!r.isWebSocket
    case 'live':
      return (r) => (!!r.isStream || !!r.isWebSocket) && !r.streamCompleted
    case 'error':
      return (r) => !!r.error || r.statusCode === 0 || r.statusCode >= 400
    case 'ok':
      return (r) => r.statusCode >= 200 && r.statusCode < 300
    case 'redirect':
      return (r) => r.statusCode >= 300 && r.statusCode < 400
    case 'client-error':
    case '4xx':
      return (r) => r.statusCode >= 400 && r.statusCode < 500
    case 'server-error':
    case '5xx':
      return (r) => r.statusCode >= 500 && r.statusCode < 600
    default:
      return null
  }
}

function hasPredicate(value: string): Predicate | null {
  switch (value.toLowerCase()) {
    case 'body':
      return (r) => !!(r.requestBody && r.requestBody.length > 0)
    case 'resp-body':
    case 'response-body':
      return (r) => !!(r.responseBody && r.responseBody.length > 0)
    case 'error':
      return (r) => !!r.error
    case 'sse':
      return (r) => (r.sseEvents?.length ?? 0) > 0
    case 'ws':
    case 'websocket':
      return (r) => (r.webSocketMessages?.length ?? 0) > 0
    default:
      return null
  }
}

function bodyPredicate(value: string): Predicate {
  const v = value.toLowerCase()
  return (r) => {
    if (r.requestBody && r.requestBody.toLowerCase().includes(v)) return true
    if (r.responseBody && r.responseBody.toLowerCase().includes(v)) return true
    return false
  }
}

function hostPredicate(value: string): Predicate {
  const v = value.toLowerCase()
  return (r) => r.host.toLowerCase().includes(v)
}

function pathPredicate(value: string): Predicate {
  const v = value.toLowerCase()
  return (r) => r.path.toLowerCase().includes(v)
}

function clausePredicate(token: string): Predicate {
  // negation
  let negated = false
  let body = token
  while (body.startsWith('-') && body.length > 1) {
    negated = !negated
    body = body.slice(1)
  }

  const colon = body.indexOf(':')
  let pred: Predicate | null = null
  if (colon > 0) {
    const key = body.slice(0, colon).toLowerCase()
    const value = body.slice(colon + 1)
    if (KEYS.has(key) && value.length > 0) {
      switch (key) {
        case 'method': pred = methodPredicate(value); break
        case 'status': pred = statusPredicate(value); break
        case 'host': pred = hostPredicate(value); break
        case 'path': pred = pathPredicate(value); break
        case 'body': pred = bodyPredicate(value); break
        case 'header': pred = headerPredicate(value); break
        case 'dur': pred = durationPredicate(value); break
        case 'is': pred = isPredicate(value); break
        case 'has': pred = hasPredicate(value); break
      }
    }
  }
  if (!pred) {
    // Fall back to substring match across the whole (un-negated) token.
    pred = (r) => freeMatch(r, body)
  }
  return negated ? (r) => !pred!(r) : pred
}

export function parseFilter(input: string): ParsedFilter {
  const raw = input.trim()
  if (!raw) {
    return { raw: '', predicate: () => true, structured: false }
  }
  const tokens = tokenize(raw)
  let structured = false
  const preds: Predicate[] = []
  for (const t of tokens) {
    const stripped = t.replace(/^-+/, '')
    const colon = stripped.indexOf(':')
    if (colon > 0 && KEYS.has(stripped.slice(0, colon).toLowerCase())) {
      structured = true
    }
    preds.push(clausePredicate(t))
  }
  const predicate: Predicate = (r) => preds.every((p) => p(r))
  return { raw, predicate, structured }
}

/** Cheat-sheet rows used by the help popover. */
export const FILTER_HELP: Array<{ syntax: string; meaning: string }> = [
  { syntax: 'method:POST', meaning: 'comma-list OK: method:GET,POST' },
  { syntax: 'status:>=400', meaning: 'or status:404, status:4xx, status:!=204' },
  { syntax: 'host:api.foo.com', meaning: 'substring, case-insensitive' },
  { syntax: 'path:/users', meaning: 'substring on path + query' },
  { syntax: 'body:user_id', meaning: 'substring on request OR response body' },
  { syntax: 'header:authorization', meaning: 'header exists; or header:name=value' },
  { syntax: 'dur:>500', meaning: 'in ms, or dur:<=2s' },
  { syntax: 'is:ws | sse | stream | live', meaning: 'type / liveness predicates' },
  { syntax: 'is:error | ok | redirect', meaning: 'status-class shortcuts' },
  { syntax: 'has:body | error | sse | ws', meaning: 'presence predicates' },
  { syntax: '-method:GET', meaning: 'leading - negates any clause' },
  { syntax: '"two words"', meaning: 'quote tokens with spaces' },
  { syntax: 'plain text', meaning: 'falls back to substring across method/host/path/status' },
]
