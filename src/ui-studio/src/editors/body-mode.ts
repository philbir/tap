/**
 * Body modes for the request editor. The on-disk format always stores the body as raw text
 * inside the fenced `http` block, plus a Content-Type header. Mode is a UI affordance: it
 * picks the right editor surface (raw textarea, form table, multipart table, binary
 * dropzone) and, when changing modes, proposes a Content-Type header to match.
 */
export type BodyMode = 'none' | 'form-urlencoded' | 'multipart' | 'raw' | 'binary' | 'graphql' | 'soap'

/** Sub-type inside the {@link BodyMode} `raw` family. Drives both Content-Type and the
 *  editor language hint (JSON pretty-print / XML highlighting / plain text). */
export type RawSubType = 'json' | 'text' | 'xml'

export const BODY_MODE_LABELS: Record<BodyMode, string> = {
  'none': 'None',
  'form-urlencoded': 'Form',
  'multipart': 'Multipart',
  'raw': 'Raw',
  'binary': 'Binary',
  'graphql': 'GraphQL',
  'soap': 'SOAP',
}

export const BODY_MODE_HINT: Record<BodyMode, string> = {
  'none': 'No body sent.',
  'form-urlencoded': 'application/x-www-form-urlencoded',
  'multipart': 'multipart/form-data — fields and files.',
  'raw': 'Raw body. Pick JSON / Text / XML for the right Content-Type and editor.',
  'binary': 'Send a file as the request body. Pick a content type for the upload.',
  'graphql': 'GraphQL query/mutation. Sent as JSON.',
  'soap': 'SOAP 1.1 envelope built from an operation name and its XML payload.',
}

/** Stable boundary string used by the multipart serializer. Fixed so saving + reloading
 *  produces byte-identical YAML and diffs stay quiet. */
export const MULTIPART_BOUNDARY = 'tap-multipart-boundary'

export const RAW_SUB_LABELS: Record<RawSubType, string> = {
  json: 'JSON',
  text: 'Text',
  xml: 'XML',
}

export function contentTypeForBodyMode(mode: BodyMode, raw: RawSubType = 'json'): string | null {
  switch (mode) {
    case 'none': return null
    case 'form-urlencoded': return 'application/x-www-form-urlencoded'
    case 'multipart': return `multipart/form-data; boundary=${MULTIPART_BOUNDARY}`
    case 'raw':
      return raw === 'json' ? 'application/json'
        : raw === 'xml' ? 'application/xml'
        : 'text/plain'
    case 'binary': return 'application/octet-stream'
    case 'graphql': return 'application/json'
    case 'soap': return 'text/xml; charset=utf-8'
  }
}

/** Detect the highest-level body mode from the current Content-Type + body content.
 *  Content-Type is the primary signal — when present, it wins even with an empty body
 *  (e.g., the user just picked a mode and hasn't typed yet). Only when there's no
 *  Content-Type at all do we fall back to body-presence to distinguish None from Raw. */
export function detectBodyMode(contentType: string | null | undefined, body: string): BodyMode {
  const ct = (contentType ?? '').toLowerCase()
  if (ct.includes('multipart/form-data')) return 'multipart'
  if (ct.includes('x-www-form-urlencoded')) return 'form-urlencoded'
  if (ct.includes('json')) return looksLikeGraphql(body) ? 'graphql' : 'raw'
  if (ct.includes('xml')) return looksLikeSoap(body) ? 'soap' : 'raw'
  if (ct.startsWith('text/')) return 'raw'
  // Anything else with a media type (image/*, audio/*, video/*, application/pdf,
  // application/octet-stream, application/zip, …) is a binary upload. The Raw editor
  // can't surface a sensible sub-type for these, so route them to Binary.
  if (ct && ct !== '') return 'binary'
  return body ? 'raw' : 'none'
}

/** Within {@link detectBodyMode} == 'raw', which sub-type does the Content-Type imply? */
export function detectRawSubType(contentType: string | null | undefined): RawSubType {
  const ct = (contentType ?? '').toLowerCase()
  if (ct.includes('json')) return 'json'
  if (ct.includes('xml')) return 'xml'
  return 'text'
}

export function looksLikeGraphql(body: string): boolean {
  try {
    const obj = JSON.parse(body)
    return typeof obj === 'object' && obj !== null && typeof (obj as { query?: unknown }).query === 'string'
  } catch { return false }
}

/** Parse a urlencoded body into key/value rows. Tolerates unencoded `{{var}}` tokens. */
export function parseFormBody(body: string): Array<{ key: string; value: string }> {
  if (!body) return []
  return body.split('&').filter(Boolean).map((pair) => {
    const eq = pair.indexOf('=')
    if (eq < 0) return { key: safeDecode(pair), value: '' }
    return { key: safeDecode(pair.slice(0, eq)), value: safeDecode(pair.slice(eq + 1)) }
  })
}

export function serializeFormBody(rows: Array<{ key: string; value: string }>): string {
  return rows
    .filter((r) => r.key)
    .map((r) => `${safeEncode(r.key)}=${safeEncode(r.value)}`)
    .join('&')
}

function safeDecode(s: string): string { try { return decodeURIComponent(s.replace(/\+/g, ' ')) } catch { return s } }
function safeEncode(s: string): string {
  return s.replace(/\{\{[^}]+\}\}|\$\{\{[^}]+\}\}|[^]/g, (c) =>
    /^(\$?\{\{[^}]+\}\})$/.test(c) ? c : encodeURIComponent(c))
}

/** Pretty-print JSON if valid, else return as-is. One implementation, shared with the
 *  response viewer's Formatted/Raw toggle — see `./prettyPrint`. */
export { tryPrettyJson } from './prettyPrint'

// -------------------------------- Multipart ------------------------------------------

/** One part inside a multipart/form-data body. Text parts have `kind: 'text'` and store
 *  their text in `value`. File parts have `kind: 'file'`, an optional `filename`, an
 *  explicit `contentType`, and store the bytes (text-decoded) in `value`. */
export interface MultipartPart {
  /** Field name from `Content-Disposition: form-data; name="…"`. */
  name: string
  /** When 'file', the row renders a file picker + filename. Otherwise a value input. */
  kind: 'text' | 'file'
  value: string
  /** Filename token in Content-Disposition. Only meaningful for file parts. */
  filename?: string
  /** Explicit Content-Type for the part. Auto-filled from the file's MIME on upload;
   *  blank means the wire body omits the part's Content-Type header. */
  contentType?: string
}

/** Parse a multipart body back into a list of parts. The boundary is sniffed from the
 *  body's first non-empty line so we tolerate hand-edited specs whose boundary doesn't
 *  match {@link MULTIPART_BOUNDARY}. */
export function parseMultipartBody(body: string): MultipartPart[] {
  if (!body) return []
  const lines = body.split(/\r?\n/)
  const first = lines.find((l) => l.startsWith('--'))
  if (!first) return []
  const boundary = first.slice(2).replace(/--$/, '')
  const delim = `--${boundary}`
  const close = `--${boundary}--`

  const parts: MultipartPart[] = []
  let i = 0
  while (i < lines.length) {
    if (lines[i] !== delim) { i++; continue }
    i++ // past delimiter
    const headers: Record<string, string> = {}
    while (i < lines.length && lines[i] !== '') {
      const m = /^([^:]+):\s*(.*)$/.exec(lines[i])
      if (m) headers[m[1].toLowerCase()] = m[2]
      i++
    }
    i++ // past blank line
    const valueLines: string[] = []
    while (i < lines.length && lines[i] !== delim && lines[i] !== close) {
      valueLines.push(lines[i])
      i++
    }
    while (valueLines.length > 0 && valueLines[valueLines.length - 1] === '') valueLines.pop()

    const disposition = headers['content-disposition'] ?? ''
    const name = /name="([^"]*)"/.exec(disposition)?.[1] ?? ''
    const filename = /filename="([^"]*)"/.exec(disposition)?.[1]
    const contentType = headers['content-type']
    const kind: MultipartPart['kind'] = filename !== undefined ? 'file' : 'text'
    parts.push({ name, kind, value: valueLines.join('\n'), filename, contentType })
  }
  return parts
}

/** Serialize parts back to a multipart body using the canonical {@link MULTIPART_BOUNDARY}. */
export function serializeMultipartBody(parts: MultipartPart[]): string {
  const kept = parts.filter((p) => p.name)
  if (kept.length === 0) return ''
  const delim = `--${MULTIPART_BOUNDARY}`
  const sb: string[] = []
  for (const p of kept) {
    sb.push(delim)
    let disp = `Content-Disposition: form-data; name="${p.name}"`
    if (p.kind === 'file') disp += `; filename="${p.filename ?? ''}"`
    sb.push(disp)
    const ct = p.contentType?.trim()
    if (ct) sb.push(`Content-Type: ${ct}`)
    sb.push('')
    sb.push(p.value)
  }
  sb.push(`--${MULTIPART_BOUNDARY}--`)
  return sb.join('\n')
}

// -------------------------------- GraphQL --------------------------------------------

/** Parsed GraphQL request body. Variables are always a JSON-formatted string so the user
 *  can type free-form (commas, comments) — we only validate at send time. */
export interface GraphQLBody {
  query: string
  /** Pretty-printed JSON object source. Empty string means "no variables". */
  variables: string
  operationName?: string
}

/** Parse the request body string into its GraphQL parts. Tolerant: anything that doesn't
 *  look like a `{ query }` JSON envelope is treated as the query itself with no variables. */
export function parseGraphQLBody(body: string): GraphQLBody {
  if (!body) return { query: '', variables: '' }
  try {
    const obj = JSON.parse(body)
    if (obj && typeof obj === 'object' && typeof obj.query === 'string') {
      const vars = obj.variables
      let variables = ''
      if (vars !== undefined && vars !== null) {
        variables = typeof vars === 'string' ? vars : JSON.stringify(vars, null, 2)
      }
      return {
        query: obj.query,
        variables,
        operationName: typeof obj.operationName === 'string' ? obj.operationName : undefined,
      }
    }
  } catch { /* fall through */ }
  return { query: body, variables: '' }
}

/** Serialize parts back to the wire envelope. Empty/invalid variables collapse to omitted
 *  rather than `"variables": null` so the body stays minimal. */
export function serializeGraphQLBody(parts: GraphQLBody): string {
  const out: Record<string, unknown> = { query: parts.query }
  const v = parts.variables.trim()
  if (v.length > 0) {
    try { out.variables = JSON.parse(v) }
    catch { out.variables = parts.variables }
  }
  if (parts.operationName) out.operationName = parts.operationName
  return JSON.stringify(out, null, 2)
}

// -------------------------------- SOAP ------------------------------------------------

/** Envelope namespace for SOAP 1.1 — the version Tap authors. */
export const SOAP_11_NS = 'http://schemas.xmlsoap.org/soap/envelope/'
/** Envelope namespace for SOAP 1.2. Tap never writes this itself, but it recognises and
 *  preserves it so pasting a 1.2 envelope doesn't silently rewrite it as 1.1. */
export const SOAP_12_NS = 'http://www.w3.org/2003/05/soap-envelope'

/**
 * A SOAP request split into the two parts the Body tab edits — the operation element
 * inside `<soap:Body>` and its inner XML — plus the envelope scaffolding around them.
 * The scaffolding fields are captured verbatim rather than regenerated so a hand-written
 * or pasted envelope survives a round-trip through the editor unchanged.
 */
export interface SoapBody {
  /** Name of the operation element inside `<soap:Body>`, e.g. `GetWeather`. Carries its
   *  prefix when the envelope uses one (`m:GetWeather`). */
  operation: string
  /** Default namespace on the operation element (`xmlns="…"`) — the WSDL's target
   *  namespace, which nearly every service requires. Empty when absent. */
  namespace: string
  /** Inner XML of the operation element: the operation's arguments, dedented. */
  payload: string
  /** Attributes on the operation element other than the default `xmlns`, kept verbatim. */
  attributes: string
  /** The whole `<soap:Header>…</soap:Header>` block, verbatim; empty when there is none.
   *  Tap never authors one, but a WS-Security header must not vanish on save. */
  header: string
  /** Envelope namespace found in the body — {@link SOAP_11_NS} unless it says otherwise. */
  envelopeNs: string
  /** Prefix bound to the envelope namespace (`soap`, `soapenv`, `s`). Empty means the
   *  envelope declares SOAP as its default namespace instead of using a prefix. */
  prefix: string
}

export function emptySoapBody(): SoapBody {
  return { operation: '', namespace: '', payload: '', attributes: '', header: '', envelopeNs: SOAP_11_NS, prefix: 'soap' }
}

const SOAP_ENVELOPE_OPEN = /<(?:([\w.-]+):)?Envelope\b([^>]*)>/i
const SOAP_HEADER_BLOCK = /<(?:[\w.-]+:)?Header\b[^>]*\/>|<(?:[\w.-]+:)?Header\b[^>]*>[\s\S]*?<\/(?:[\w.-]+:)?Header\s*>/i
// Greedy on purpose: the envelope has exactly one Body, so the *last* closing tag is the
// right one even when the payload nests an element that happens to be called `Body`.
const SOAP_BODY_BLOCK = /<(?:[\w.-]+:)?Body\b[^>]*>([\s\S]*)<\/(?:[\w.-]+:)?Body\s*>/i
const SOAP_OP_SELF_CLOSING = /^<([\w.:-]+)((?:\s[^>]*?)?)\/>$/
const SOAP_OP_PAIRED = /^<([\w.:-]+)((?:\s[^>]*?)?)>([\s\S]*)<\/\1\s*>$/
const XMLNS_DEFAULT = /(^|\s)xmlns\s*=\s*"([^"]*)"/i

/** True when the body is a SOAP envelope. Both an `Envelope` element *and* one of the two
 *  SOAP namespaces are required — plain XML with an `<Envelope>` root is not SOAP. */
export function looksLikeSoap(body: string): boolean {
  if (!body) return false
  if (!SOAP_ENVELOPE_OPEN.test(body)) return false
  return body.includes(SOAP_11_NS) || body.includes(SOAP_12_NS)
}

/** Parse a SOAP envelope into its editable parts. Deliberately regex-based rather than
 *  `DOMParser`: bodies here carry unexpanded `{{var}}` tokens and are often mid-edit, and
 *  a strict parse would throw away the user's text on the first unbalanced tag. Anything
 *  that isn't an envelope becomes the payload, so switching in from Raw/XML keeps the XML. */
export function parseSoapBody(body: string): SoapBody {
  const out = emptySoapBody()
  if (!body.trim()) return out

  const envelope = SOAP_ENVELOPE_OPEN.exec(body)
  if (!envelope) return { ...out, payload: dedentXml(body) }

  out.prefix = envelope[1] ?? ''
  const envelopeAttrs = envelope[2] ?? ''
  const nsPattern = out.prefix
    ? new RegExp(`xmlns:${escapeRegExp(out.prefix)}\\s*=\\s*"([^"]*)"`, 'i')
    : /(?:^|\s)xmlns\s*=\s*"([^"]*)"/i
  out.envelopeNs = nsPattern.exec(envelopeAttrs)?.[1] ?? SOAP_11_NS
  out.header = dedentTailXml(SOAP_HEADER_BLOCK.exec(body)?.[0] ?? '')

  // Keep the raw slice around: `dedentXml` needs the first line's original indentation to
  // work out the common depth, and trimming would have thrown it away.
  const innerRaw = SOAP_BODY_BLOCK.exec(body)?.[1] ?? ''
  const inner = innerRaw.trim()
  if (!inner) return out

  const selfClosing = SOAP_OP_SELF_CLOSING.exec(inner)
  const paired = selfClosing ? null : SOAP_OP_PAIRED.exec(inner)
  const operation = selfClosing ?? paired
  // No single wrapping element (empty Body, or several top-level elements) — there is no
  // operation to name, so the whole thing stays in the payload editor.
  if (!operation) return { ...out, payload: dedentXml(innerRaw) }

  out.operation = operation[1]
  const attrs = operation[2] ?? ''
  const defaultNs = XMLNS_DEFAULT.exec(attrs)
  out.namespace = defaultNs?.[2] ?? ''
  out.attributes = (defaultNs ? attrs.replace(defaultNs[0], ' ') : attrs).trim()
  out.payload = paired ? dedentXml(paired[3]) : ''
  return out
}

/** Render the parts back into a full envelope. Always emits an envelope — even an empty
 *  one — so the Body tab's mode selector stays on SOAP after a round-trip through
 *  {@link detectBodyMode}. */
export function serializeSoapBody(parts: SoapBody): string {
  const { prefix } = parts
  const qualify = (name: string) => (prefix ? `${prefix}:${name}` : name)
  const ns = parts.envelopeNs || SOAP_11_NS
  const xmlnsAttr = prefix ? `xmlns:${prefix}="${ns}"` : `xmlns="${ns}"`

  const lines = [`<${qualify('Envelope')} ${xmlnsAttr}>`]
  if (parts.header.trim()) lines.push(indentXml(parts.header.trim(), 2))
  lines.push(`  <${qualify('Body')}>`)

  const operation = parts.operation.trim()
  const payload = parts.payload.trim()
  if (operation) {
    const attrs = [parts.namespace.trim() ? `xmlns="${parts.namespace.trim()}"` : '', parts.attributes.trim()]
      .filter(Boolean)
      .join(' ')
    const openTag = attrs ? `<${operation} ${attrs}` : `<${operation}`
    if (payload) {
      lines.push(indentXml(`${openTag}>`, 4), indentXml(payload, 6), indentXml(`</${operation}>`, 4))
    } else {
      lines.push(indentXml(`${openTag} />`, 4))
    }
  } else if (payload) {
    lines.push(indentXml(payload, 4))
  }

  lines.push(`  </${qualify('Body')}>`, `</${qualify('Envelope')}>`)
  return lines.join('\n')
}

/** Strip the common leading indentation (and surrounding blank lines) off an XML fragment
 *  so it reads flush-left in its own editor, whatever depth it sat at in the envelope. */
function dedentXml(xml: string): string {
  const lines = xml.replace(/\s+$/, '').split(/\r?\n/)
  while (lines.length > 0 && lines[0].trim() === '') lines.shift()
  const widths = lines.filter((l) => l.trim() !== '').map((l) => (/^[ \t]*/.exec(l)?.[0] ?? '').length)
  const common = widths.length > 0 ? Math.min(...widths) : 0
  return lines.map((l) => l.slice(common)).join('\n')
}

/** Dedent a block whose first line already sits at column 0 because the match that
 *  produced it started at the `<`. The remaining lines are still at their envelope depth,
 *  so they are shifted by *their* common indentation instead. */
function dedentTailXml(xml: string): string {
  const lines = xml.split(/\r?\n/)
  if (lines.length < 2) return xml
  const widths = lines.slice(1).filter((l) => l.trim() !== '').map((l) => (/^[ \t]*/.exec(l)?.[0] ?? '').length)
  const common = widths.length > 0 ? Math.min(...widths) : 0
  return [lines[0], ...lines.slice(1).map((l) => l.slice(common))].join('\n')
}

function indentXml(xml: string, spaces: number): string {
  const pad = ' '.repeat(spaces)
  return xml.split('\n').map((l) => (l.trim() === '' ? '' : pad + l)).join('\n')
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

// -------------------------------- Content-Type origin --------------------------------

/**
 * Where the request's `Content-Type` header came from.
 *
 * `auto` — the Body tab produced it: it is exactly what {@link contentTypeForBodyMode}
 * emits for the current mode, so switching modes will rewrite it and the Headers tab
 * marks it as managed rather than hand-written.
 *
 * `override` — a Content-Type is present but differs from the mode default, i.e. the user
 * typed it (`application/vnd.api+json`, a charset parameter, a vendor media type). Tap
 * leaves it alone until the body mode is switched.
 */
export type ContentTypeOrigin = 'auto' | 'override'

/** Modes whose Content-Type is fully determined by the mode (and, for `raw`, its sub-type).
 *  `binary` is deliberately absent: its content type is the uploaded file's MIME, edited in
 *  the Body tab's own field, so *any* value there is still "what the body says". */
const FIXED_CONTENT_TYPE_MODES: ReadonlySet<BodyMode> = new Set<BodyMode>([
  'form-urlencoded', 'multipart', 'raw', 'graphql', 'soap',
])

/** Case- and whitespace-insensitive comparison key for a media type. `application/json;
 *  charset=utf-8` and `application/json;charset=UTF-8` are the same header. */
function contentTypeKey(contentType: string): string {
  return contentType
    .split(';')
    .map((part) => part.trim().toLowerCase())
    .filter((part) => part.length > 0)
    .join(';')
}

/** True when two Content-Type strings mean the same thing on the wire. */
export function sameContentType(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return !a && !b
  return contentTypeKey(a) === contentTypeKey(b)
}

/**
 * Classify the request's current Content-Type against what {@link contentTypeForBodyMode}
 * would set for `mode`/`raw`. Modes outside {@link FIXED_CONTENT_TYPE_MODES} never report
 * `override` — nothing is being overridden when the mode has no opinion of its own.
 */
export function contentTypeOrigin(
  contentType: string | null | undefined,
  mode: BodyMode,
  raw: RawSubType,
): ContentTypeOrigin {
  if (!FIXED_CONTENT_TYPE_MODES.has(mode)) return 'auto'
  return sameContentType(contentType, contentTypeForBodyMode(mode, raw)) ? 'auto' : 'override'
}
