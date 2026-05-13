// Token-aware autocompletion for the filter input.
//
// Given the current input + caret position, returns the token span under the
// caret and a ranked list of suggestions. Suggestions know how to replace the
// token: the caller swaps `input.slice(token.start, token.end)` with the
// suggestion's `insert` text.

import type { RequestRecord } from './types'

export type SuggestionKind = 'key' | 'value' | 'observed' | 'operator'

export interface Suggestion {
  /** Text that replaces the current token (already includes the leading negation, key prefix, etc.). */
  insert: string
  /** Label shown in the dropdown (right-aligned key prefix is stripped for readability). */
  label: string
  /** Optional grey secondary line. */
  detail?: string
  kind: SuggestionKind
  /** True when the suggestion is a complete clause (vs. a key prefix that still needs a value). */
  terminal: boolean
}

export interface TokenSpan {
  start: number
  end: number
  text: string
}

export interface SuggestResult {
  token: TokenSpan
  suggestions: Suggestion[]
}

const KEY_INFO: Array<{ key: string; detail: string }> = [
  { key: 'method', detail: 'HTTP method — comma-list OK (method:GET,POST)' },
  { key: 'status', detail: 'status code · supports 4xx, 5xx, >=400, !=204' },
  { key: 'host', detail: 'hostname substring' },
  { key: 'path', detail: 'path substring' },
  { key: 'body', detail: 'substring in request OR response body' },
  { key: 'header', detail: 'header exists · or header:name=value' },
  { key: 'dur', detail: 'duration ms — dur:>500, dur:<=2s' },
  { key: 'is', detail: 'ws, sse, stream, live, error, ok, redirect' },
  { key: 'has', detail: 'body, error, sse, ws' },
]

const KEY_NAMES = new Set(KEY_INFO.map((k) => k.key))

const METHOD_VALUES = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']
const IS_VALUES: Array<{ v: string; detail: string }> = [
  { v: 'ws', detail: 'WebSocket records' },
  { v: 'sse', detail: 'Server-Sent Events records' },
  { v: 'stream', detail: 'either SSE or WS' },
  { v: 'live', detail: 'open stream (not yet closed)' },
  { v: 'error', detail: 'status ≥ 400 or transport error' },
  { v: 'ok', detail: 'status 2xx' },
  { v: 'redirect', detail: 'status 3xx' },
  { v: '4xx', detail: 'client-error class' },
  { v: '5xx', detail: 'server-error class' },
]
const HAS_VALUES: Array<{ v: string; detail: string }> = [
  { v: 'body', detail: 'request has a body' },
  { v: 'resp-body', detail: 'response has a body' },
  { v: 'error', detail: 'transport error captured' },
  { v: 'sse', detail: '≥ 1 SSE event captured' },
  { v: 'ws', detail: '≥ 1 WS frame captured' },
]
const STATUS_HINTS: Array<{ v: string; detail: string }> = [
  { v: '200', detail: 'OK' },
  { v: '201', detail: 'Created' },
  { v: '204', detail: 'No Content' },
  { v: '301', detail: 'Moved Permanently' },
  { v: '302', detail: 'Found' },
  { v: '304', detail: 'Not Modified' },
  { v: '400', detail: 'Bad Request' },
  { v: '401', detail: 'Unauthorized' },
  { v: '403', detail: 'Forbidden' },
  { v: '404', detail: 'Not Found' },
  { v: '409', detail: 'Conflict' },
  { v: '422', detail: 'Unprocessable' },
  { v: '429', detail: 'Too Many Requests' },
  { v: '500', detail: 'Internal Server Error' },
  { v: '502', detail: 'Bad Gateway' },
  { v: '503', detail: 'Service Unavailable' },
  { v: '4xx', detail: 'any client error' },
  { v: '5xx', detail: 'any server error' },
  { v: '>=400', detail: 'errors only' },
  { v: '!=204', detail: 'exclude empty responses' },
]
const DUR_HINTS: Array<{ v: string; detail: string }> = [
  { v: '>500', detail: 'slower than 500 ms' },
  { v: '>1000', detail: 'slower than 1 s' },
  { v: '<100', detail: 'faster than 100 ms' },
  { v: '<=2s', detail: 's-suffix supported' },
]

export function tokenAtCaret(input: string, caret: number): TokenSpan {
  // We treat tokens as whitespace-separated. Quoted spans (typed with " or ')
  // are rare during typing — we don't bother extending across them; the user
  // can dismiss the popover with Esc if they want.
  let start = caret
  while (start > 0 && !/\s/.test(input[start - 1])) start--
  let end = caret
  while (end < input.length && !/\s/.test(input[end])) end++
  return { start, end, text: input.slice(start, end) }
}

function fuzzyRank(query: string, label: string): number {
  // 0 = no match · 1 = contained · 2 = starts with · 3 = exact (case-insensitive)
  if (!query) return 1
  const q = query.toLowerCase()
  const l = label.toLowerCase()
  if (l === q) return 3
  if (l.startsWith(q)) return 2
  if (l.includes(q)) return 1
  return 0
}

function uniqueObserved<T>(iter: Iterable<T>, key: (v: T) => string | null | undefined, limit = 24): string[] {
  const seen = new Set<string>()
  for (const item of iter) {
    const k = key(item)
    if (k) seen.add(k)
    if (seen.size >= limit * 4) break
  }
  return Array.from(seen).sort().slice(0, limit)
}

interface ValueCandidate {
  value: string
  detail?: string
  kind: SuggestionKind
}

function suggestForKey(key: string, partial: string, records: RequestRecord[]): ValueCandidate[] {
  const out: ValueCandidate[] = []
  const pushAll = (arr: Array<{ v: string; detail?: string }>, kind: SuggestionKind) => {
    for (const { v, detail } of arr) out.push({ value: v, detail, kind })
  }
  switch (key) {
    case 'method': {
      pushAll(METHOD_VALUES.map((v) => ({ v })), 'value')
      for (const m of uniqueObserved(records, (r) => r.method?.toUpperCase())) {
        if (!METHOD_VALUES.includes(m)) out.push({ value: m, kind: 'observed' })
      }
      break
    }
    case 'status': {
      pushAll(STATUS_HINTS, 'value')
      for (const s of uniqueObserved(records, (r) => (r.statusCode > 0 ? String(r.statusCode) : null), 16)) {
        if (!STATUS_HINTS.some((h) => h.v === s)) out.push({ value: s, detail: 'observed', kind: 'observed' })
      }
      break
    }
    case 'host': {
      for (const h of uniqueObserved(records, (r) => r.host, 30)) {
        out.push({ value: h, kind: 'observed' })
      }
      break
    }
    case 'path': {
      const seen = new Map<string, number>()
      for (const r of records) {
        const q = r.path.indexOf('?')
        const p = q < 0 ? r.path : r.path.slice(0, q)
        if (!p) continue
        seen.set(p, (seen.get(p) ?? 0) + 1)
      }
      const ranked = Array.from(seen.entries()).sort((a, b) => b[1] - a[1]).slice(0, 30)
      for (const [p, n] of ranked) out.push({ value: p, detail: `${n}×`, kind: 'observed' })
      break
    }
    case 'header': {
      const names = new Set<string>()
      for (const r of records) {
        for (const k of Object.keys(r.requestHeaders ?? {})) names.add(k.toLowerCase())
        for (const k of Object.keys(r.responseHeaders ?? {})) names.add(k.toLowerCase())
      }
      for (const h of Array.from(names).sort().slice(0, 30)) {
        out.push({ value: h, kind: 'observed' })
      }
      // After "header:name=" we can't suggest values practically — bail to no values.
      if (partial.includes('=')) return []
      break
    }
    case 'dur': {
      pushAll(DUR_HINTS, 'operator')
      break
    }
    case 'is': {
      pushAll(IS_VALUES.map((x) => ({ v: x.v, detail: x.detail })), 'value')
      break
    }
    case 'has': {
      pushAll(HAS_VALUES.map((x) => ({ v: x.v, detail: x.detail })), 'value')
      break
    }
  }
  return out
}

export function suggest(input: string, caret: number, records: RequestRecord[]): SuggestResult {
  const token = tokenAtCaret(input, caret)
  // Suggest based on what's been typed up to the caret (not the full token —
  // the user may have positioned the caret in the middle of `header:auth|`).
  const typedPrefix = input.slice(token.start, caret)

  // Peel off any leading `-` negations so we can re-add them on insert.
  let negation = ''
  let body = typedPrefix
  while (body.startsWith('-')) { negation += '-'; body = body.slice(1) }

  const colon = body.indexOf(':')
  const suggestions: Suggestion[] = []

  if (colon < 0) {
    // Suggest keys + a handful of stand-alone shortcuts.
    for (const k of KEY_INFO) {
      const rank = fuzzyRank(body, k.key)
      if (rank === 0) continue
      suggestions.push({
        insert: `${negation}${k.key}:`,
        label: `${k.key}:`,
        detail: k.detail,
        kind: 'key',
        terminal: false,
      })
    }
    // Shortcuts: typing "err" / "ok" / "redirect" / "live" / "ws" / "sse" maps
    // directly to is:<that> — handy without going through the colon.
    for (const v of IS_VALUES) {
      if (fuzzyRank(body, v.v) === 0) continue
      suggestions.push({
        insert: `${negation}is:${v.v}`,
        label: `is:${v.v}`,
        detail: v.detail,
        kind: 'value',
        terminal: true,
      })
    }
  } else {
    const key = body.slice(0, colon).toLowerCase()
    if (!KEY_NAMES.has(key)) {
      // Unknown key — fall back to free text; no completions.
      return { token, suggestions: [] }
    }
    const valPart = body.slice(colon + 1)
    // For comma-lists (method:GET,) only complete the final segment.
    const lastComma = valPart.lastIndexOf(',')
    const prefixBeforeFinal = lastComma >= 0 ? valPart.slice(0, lastComma + 1) : ''
    const finalSeg = lastComma >= 0 ? valPart.slice(lastComma + 1) : valPart
    // header:name=value — once `=` is typed, no useful completion.
    const candidates = suggestForKey(key, valPart, records)
    for (const c of candidates) {
      const rank = fuzzyRank(finalSeg, c.value)
      if (rank === 0) continue
      suggestions.push({
        insert: `${negation}${key}:${prefixBeforeFinal}${c.value}`,
        label: c.value,
        detail: c.detail,
        kind: c.kind,
        terminal: true,
      })
    }
  }

  // Rank: prioritise exact / starts-with matches, then by kind weight, then
  // alphabetical. We re-rank in a second pass so that observed values appear
  // before built-in lists when the user has actual data to pick from.
  const kindWeight: Record<SuggestionKind, number> = { observed: 0, value: 1, operator: 2, key: 3 }
  const rankQuery = colon < 0 ? body : (body.split(':')[1].split(',').pop() ?? '')
  suggestions.sort((a, b) => {
    const ra = fuzzyRank(rankQuery, a.label)
    const rb = fuzzyRank(rankQuery, b.label)
    if (rb !== ra) return rb - ra
    if (kindWeight[a.kind] !== kindWeight[b.kind]) return kindWeight[a.kind] - kindWeight[b.kind]
    return a.label.localeCompare(b.label)
  })

  return { token, suggestions: suggestions.slice(0, 10) }
}

export function applySuggestion(input: string, token: TokenSpan, suggestion: Suggestion): { value: string; caret: number } {
  // For non-terminal suggestions (a bare key like `method:`) keep the caret
  // immediately after — the user is about to type the value. For terminal
  // ones append a space so the user can keep chaining clauses.
  const tail = suggestion.terminal ? ' ' : ''
  const value = input.slice(0, token.start) + suggestion.insert + tail + input.slice(token.end)
  const caret = token.start + suggestion.insert.length + tail.length
  return { value, caret }
}
