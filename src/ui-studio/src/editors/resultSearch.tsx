import { Mark } from '@mantine/core'
import { Fragment, type ReactNode } from 'react'

/**
 * Find-in-result: the one search model every tab of the response panel shares.
 *
 * A response is the thing you actually came to read, and until now the only way through a
 * 400 KB body was the scrollbar. The panel is not one view though — a body is text you
 * *navigate* (jump match to match), while events, frames and headers are lists you *filter*
 * (show me only the rows that mention this). Both meanings come off the same compiled query,
 * which is why the query lives here rather than in either view.
 *
 * Plain queries are escaped and matched literally; `regex` hands the string to `RegExp`
 * as-is. `m` rides along with regex mode so `^` / `$` anchor to lines rather than to the whole
 * body — that is what people mean when they anchor a search inside a document.
 */
export interface ResultSearchState {
  open: boolean
  query: string
  regex: boolean
  caseSensitive: boolean
}

export const EMPTY_RESULT_SEARCH: ResultSearchState = {
  open: false,
  query: '',
  regex: false,
  caseSensitive: false,
}

export interface MatchRange { from: number; to: number }

/**
 * A compiled query. `source` + `flags` are exposed because the CodeMirror side re-compiles
 * against its own document (which may be pretty-printed JSON, so offsets computed here would
 * not line up).
 */
export interface ResultMatcher {
  source: string
  flags: string
  test(text: string): boolean
  ranges(text: string): MatchRange[]
}

/** Guard against a query like `.` on a megabyte body: past this we stop collecting. */
export const MAX_MATCHES = 5000

export function compileSearch(state: ResultSearchState): { matcher: ResultMatcher | null; error: string | null } {
  const query = state.query
  if (!query) return { matcher: null, error: null }

  const source = state.regex ? query : escapeRegExp(query)
  const flags = `g${state.caseSensitive ? '' : 'i'}${state.regex ? 'm' : ''}`

  let re: RegExp
  try {
    re = new RegExp(source, flags)
  } catch (e) {
    return { matcher: null, error: describeRegexError(e) }
  }

  return {
    matcher: {
      source,
      flags,
      test(text: string) {
        re.lastIndex = 0
        return re.test(text)
      },
      ranges(text: string) {
        return findRanges(text, re)
      },
    },
    error: null,
  }
}

/**
 * All matches of `re` in `text`, in document order. `re` must carry `g`; its `lastIndex` is
 * reset on entry, so one compiled instance can be reused across many strings.
 *
 * Zero-width matches (`a*`, `^`, a lookahead) would otherwise spin forever — they are stepped
 * over and dropped, since there is nothing to highlight or scroll to.
 */
export function findRanges(text: string, re: RegExp): MatchRange[] {
  const out: MatchRange[] = []
  re.lastIndex = 0
  let m: RegExpExecArray | null
  while ((m = re.exec(text)) !== null) {
    if (m.index === re.lastIndex) { re.lastIndex++; continue }
    out.push({ from: m.index, to: m.index + m[0].length })
    if (out.length >= MAX_MATCHES) break
  }
  return out
}

/**
 * `RegExp` prefixes its complaint with the pattern it was handed —
 * `Invalid regular expression: /[abc/gim: Unterminated character class`. The user is looking
 * at that pattern in the input directly above, so only the reason is worth the width.
 */
function describeRegexError(e: unknown): string {
  const msg = e instanceof Error ? e.message : String(e)
  return msg.replace(/^Invalid regular expression: \/[\s\S]*\/[a-z]*: /, '')
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/**
 * `text` with every match wrapped in a `<Mark>`. Used by the list tabs, where a row survives
 * the filter because *something* in it matched and the reader still has to find out what.
 */
export function Highlighted({ text, matcher }: { text: string; matcher: ResultMatcher | null }): ReactNode {
  if (!matcher || !text) return text
  const ranges = matcher.ranges(text)
  if (ranges.length === 0) return text

  const parts: ReactNode[] = []
  let at = 0
  ranges.forEach((r, i) => {
    if (r.from > at) parts.push(<Fragment key={`t${i}`}>{text.slice(at, r.from)}</Fragment>)
    parts.push(<Mark key={`m${i}`} color="yellow">{text.slice(r.from, r.to)}</Mark>)
    at = r.to
  })
  if (at < text.length) parts.push(<Fragment key="tail">{text.slice(at)}</Fragment>)
  return <>{parts}</>
}

/** True when any of the supplied strings matches — the row-level predicate for list tabs. */
export function matchesAny(matcher: ResultMatcher, ...fields: (string | null | undefined)[]): boolean {
  for (const f of fields) {
    if (f && matcher.test(f)) return true
  }
  return false
}
