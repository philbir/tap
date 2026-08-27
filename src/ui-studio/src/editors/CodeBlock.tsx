import { css } from '@codemirror/lang-css'
import { html } from '@codemirror/lang-html'
import { javascript } from '@codemirror/lang-javascript'
import { json } from '@codemirror/lang-json'
import { markdown } from '@codemirror/lang-markdown'
import { xml } from '@codemirror/lang-xml'
import { yaml } from '@codemirror/lang-yaml'
import { StreamLanguage, type StreamParser, type StringStream } from '@codemirror/language'
import type { EditorView } from '@codemirror/view'
import { vscodeLight } from '@uiw/codemirror-theme-vscode'
import CodeMirror, { type Extension } from '@uiw/react-codemirror'
import { useEffect, useMemo, useRef, useState } from 'react'
import { codeSearch, codeSearchMatches, scrollToCodeMatch, setCodeSearch, type CodeSearchSpec } from './codeSearch'
import { tryPrettyJson, tryPrettyXml } from './prettyPrint'

/**
 * Read-only / editable code viewer powered by CodeMirror 6. The language pack is picked
 * from the content-type or an explicit `language` prop. We use the VSCode *light* theme
 * unconditionally so the response viewer stays readable regardless of the app's
 * dark/light Mantine scheme — matches the dreamr behavior.
 *
 * Supported languages: json, xml, html, yaml, javascript, css, markdown, plain text.
 * JSON and XML are pretty-printed for display unless `format={false}`.
 *
 * Used in:
 *   - ResponsePanel (JSON / XML / HTML / YAML / JS / CSS / Markdown body previews)
 *   - Source tab of each editor (read-only YAML view of the on-disk file)
 *   - RenderPanel (preview of the resolved request body)
 */
export type CodeBlockLanguage =
  | 'json'
  | 'xml'
  | 'html'
  | 'yaml'
  | 'javascript'
  | 'css'
  | 'markdown'
  | 'http'
  | 'text'

export interface CodeBlockProps {
  value: string
  language?: CodeBlockLanguage
  /** Pass an HTTP `Content-Type` value; we auto-pick the language. */
  contentType?: string | null
  readOnly?: boolean
  onChange?: (value: string) => void
  /** Approximate height. Defaults to autosize for read-only content. */
  height?: number | string
  /** Maximum height before scrolling — only applies in autosize mode (no explicit height). */
  maxHeight?: number | string
  /** Highlight every match of this query, emphasise `active`, and scroll it into view.
   *  `null` clears the highlighting. See `./codeSearch`. */
  search?: CodeSearchSpec | null
  /** How many matches the query found in the *rendered* document — which is not the same
   *  number as in `value` once JSON has been pretty-printed. Fires on every query and
   *  document change. */
  onSearchCount?: (count: number) => void
  /** Re-indent structured content (JSON, XML) before display. On by default: a minified
   *  body is unreadable and every caller showing one wants it wrapped. Pass `false` to show
   *  the bytes exactly as they arrived — what the response panel's Raw toggle does. */
  format?: boolean
}

export function CodeBlock({
  value, language, contentType, readOnly = true, onChange, height, maxHeight, search, onSearchCount, format = true,
}: CodeBlockProps) {
  const picked = language ?? detectLanguage(contentType)

  const lang = useMemo<Extension[]>(() => {
    switch (picked) {
      case 'json': return [json()]
      case 'xml': return [xml()]
      case 'html': return [html({ matchClosingTags: true, autoCloseTags: false })]
      case 'yaml': return [yaml()]
      case 'javascript': return [javascript()]
      case 'css': return [css()]
      case 'markdown': return [markdown()]
      case 'http': return [StreamLanguage.define(httpMode)]
      default: return []
    }
  }, [picked])

  // Identity has to stay stable: `@uiw/react-codemirror` reconfigures the whole editor
  // whenever the `extensions` array changes, and the search query changes on every keystroke.
  // The query travels as a state effect instead (below), so this array never has to move.
  const extensions = useMemo<Extension[]>(() => [...lang, codeSearch], [lang])

  // Re-indent JSON and XML so the user doesn't have to. Both formatters are total — text
  // that doesn't parse comes back untouched — so a truncated or mislabelled body still shows.
  const display = useMemo(() => {
    if (!format) return value
    if (picked === 'json') return tryPrettyJson(value)
    if (picked === 'xml') return tryPrettyXml(value)
    return value
  }, [value, picked, format])

  const viewRef = useRef<EditorView | null>(null)
  const [ready, setReady] = useState(false)
  // Kept in a ref so a caller passing an inline arrow doesn't re-run the search effect.
  const countRef = useRef(onSearchCount)
  countRef.current = onSearchCount

  // `display` is a dependency because the document has to be current before offsets mean
  // anything — and the child's value-sync effect runs before this parent one, so by the time
  // we get here the editor already holds the new text.
  useEffect(() => {
    const view = viewRef.current
    if (!view) return
    view.dispatch({ effects: setCodeSearch.of(search?.source ? search : null) })
    countRef.current?.(codeSearchMatches(view).length)
    if (search && search.active >= 0) scrollToCodeMatch(view, search.active)
  }, [search?.source, search?.flags, search?.active, display, ready])

  return (
    <CodeMirror
      value={display}
      onChange={onChange}
      readOnly={readOnly}
      editable={!readOnly}
      theme={vscodeLight}
      extensions={extensions}
      onCreateEditor={(view) => { viewRef.current = view; setReady(true) }}
      height={typeof height === 'number' ? `${height}px` : height}
      maxHeight={typeof maxHeight === 'number' ? `${maxHeight}px` : maxHeight}
      basicSetup={{
        // Read-only viewers don't need a gutter cluttered with line numbers below ~12 lines,
        // but for response bodies (which can be long) keep them on — they help when sharing
        // line references with a teammate.
        lineNumbers: true,
        foldGutter: true,
        highlightActiveLine: !readOnly,
        highlightActiveLineGutter: !readOnly,
        bracketMatching: true,
        autocompletion: !readOnly,
        searchKeymap: true,
      }}
      style={{ fontSize: 12 }}
    />
  )
}

/**
 * Map an HTTP `Content-Type` value to one of our CodeMirror languages. Falls back to
 * `text` for anything we don't recognize (the body still renders, just without syntax
 * highlighting).
 *
 * Aliases handled:
 *   - application/json, application/ld+json, application/vnd.api+json, application/hal+json, application/problem+json → json
 *   - application/xml, text/xml, application/atom+xml, application/rss+xml, image/svg+xml → xml
 *   - text/html, application/xhtml+xml → html
 *   - application/yaml, text/yaml, application/x-yaml → yaml
 *   - application/javascript, text/javascript, application/x-javascript → javascript
 *   - text/css → css
 *   - text/markdown, text/x-markdown → markdown
 *   - text/plain, text/csv, text/tab-separated-values → text
 */
export function detectLanguage(contentType?: string | null): CodeBlockLanguage {
  if (!contentType) return 'text'
  const ct = contentType.split(';')[0].trim().toLowerCase()

  // JSON family (incl. JSON:API, Problem Details, HAL, JSON-LD)
  if (ct === 'application/json' || ct.endsWith('+json') || ct.includes('/json')) return 'json'

  // XML family (incl. SVG, Atom, RSS)
  if (ct === 'application/xml' || ct === 'text/xml' || ct.endsWith('+xml') || ct.includes('/xml')) return 'xml'

  // HTML
  if (ct === 'text/html' || ct === 'application/xhtml+xml') return 'html'

  // YAML
  if (ct === 'application/yaml' || ct === 'application/x-yaml' || ct === 'text/yaml' || ct.endsWith('+yaml')) return 'yaml'

  // JavaScript
  if (ct === 'application/javascript' || ct === 'application/x-javascript' || ct === 'text/javascript') return 'javascript'

  // CSS
  if (ct === 'text/css') return 'css'

  // Markdown
  if (ct === 'text/markdown' || ct === 'text/x-markdown') return 'markdown'

  return 'text'
}

/**
 * Minimal CodeMirror StreamLanguage for the `.http` / REST Client format used by the
 * Request view in `ResponsePanel`. The format is:
 *
 *   METHOD url [HTTP/1.1]
 *   Header-Name: value
 *   Header-Name: value
 *
 *   body...
 *
 * We highlight:
 *   - METHOD (GET/POST/...) as a keyword
 *   - URL as a string
 *   - Header name as a property, `:` as an operator, header value as a string
 *   - Comments (lines starting with `#` or `//`) for forward compatibility with .http files
 *
 * The body is left as plain text — content-type-aware highlighting would mean nesting
 * another language, which adds complexity we don't need for a read-only preview.
 */
const HTTP_METHODS = new Set([
  'GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT',
])
interface HttpState { atLineStart: boolean; inHeaders: boolean; sawBlankLine: boolean }
const httpMode: StreamParser<HttpState> = {
  name: 'http',
  startState: () => ({ atLineStart: true, inHeaders: true, sawBlankLine: false }),
  token(stream: StringStream, state: HttpState) {
    if (stream.sol()) {
      state.atLineStart = true
      // A blank line ends the header block — anything after is body.
      if (stream.match(/^\s*$/, false) && state.inHeaders && !state.sawBlankLine) {
        // The first time we see a blank line after headers, flip into body mode.
        // (We only do this if we've already parsed the request line + at least one header.)
        state.sawBlankLine = true
      }
      if (state.sawBlankLine) state.inHeaders = false
    }
    // Comments — `.http` files use `#` or `//` for line comments
    if (state.atLineStart && (stream.match(/^\s*#/, true) || stream.match(/^\s*\/\//, true))) {
      stream.skipToEnd()
      return 'comment'
    }
    if (stream.eatSpace()) return null
    if (state.atLineStart && state.inHeaders) {
      state.atLineStart = false
      // Request line: METHOD url [HTTP/x.y]
      const m = stream.match(/^([A-Z]+)\b/, true)
      if (Array.isArray(m) && HTTP_METHODS.has(m[1])) {
        return 'keyword'
      }
      // Header name: token chars up to `:`
      const h = stream.match(/^([!#$%&'*+\-.^_`|~0-9A-Za-z]+)\s*(?=:)/, true)
      if (h) return 'propertyName'
      // Fall through to default token handling
    }
    if (state.inHeaders) {
      if (stream.match(/^:/, true)) return 'operator'
      // URL on request line (after METHOD) — match the rest of the line as a string-ish token
      if (stream.match(/^https?:\/\/\S+/, true)) return 'string'
      if (stream.match(/^\/\S*/, true)) return 'string'
      // Header value — eat to end of line as a string
      if (stream.match(/^[^\r\n]+/, true)) return 'string'
    }
    // Body: just eat the rest of the line as plain text
    stream.skipToEnd()
    return null
  },
  blankLine(state: HttpState) {
    state.sawBlankLine = true
    state.inHeaders = false
  },
  copyState(state: HttpState) {
    return { ...state }
  },
  languageData: { commentTokens: { line: '#' } },
}
