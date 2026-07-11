import { css } from '@codemirror/lang-css'
import { html } from '@codemirror/lang-html'
import { javascript } from '@codemirror/lang-javascript'
import { json } from '@codemirror/lang-json'
import { markdown } from '@codemirror/lang-markdown'
import { xml } from '@codemirror/lang-xml'
import { yaml } from '@codemirror/lang-yaml'
import { StreamLanguage, type StreamParser, type StringStream } from '@codemirror/language'
import { vscodeLight } from '@uiw/codemirror-theme-vscode'
import CodeMirror, { type Extension } from '@uiw/react-codemirror'
import { useMemo } from 'react'

/**
 * Read-only / editable code viewer powered by CodeMirror 6. The language pack is picked
 * from the content-type or an explicit `language` prop. We use the VSCode *light* theme
 * unconditionally so the response viewer stays readable regardless of the app's
 * dark/light Mantine scheme — matches the dreamr behavior.
 *
 * Supported languages: json, xml, html, yaml, javascript, css, markdown, plain text.
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
}

export function CodeBlock({
  value, language, contentType, readOnly = true, onChange, height, maxHeight,
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

  // For JSON, try to pretty-print the value so the user doesn't have to. We leave the
  // original text alone if it's not valid JSON.
  const display = useMemo(() => {
    if (picked !== 'json') return value
    return tryPrettyJson(value)
  }, [value, picked])

  return (
    <CodeMirror
      value={display}
      onChange={onChange}
      readOnly={readOnly}
      editable={!readOnly}
      theme={vscodeLight}
      extensions={lang}
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

function tryPrettyJson(s: string): string {
  if (!s.trim()) return s
  try {
    return JSON.stringify(JSON.parse(s), null, 2)
  } catch {
    return s
  }
}
