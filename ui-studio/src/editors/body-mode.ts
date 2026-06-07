/**
 * Body modes for the request editor. The on-disk format always stores the body as raw text
 * inside the fenced `http` block, plus a Content-Type header. Mode is a UI affordance: it
 * picks the right editor surface (raw textarea, form table, multipart table, binary
 * dropzone) and, when changing modes, proposes a Content-Type header to match.
 */
export type BodyMode = 'none' | 'form-urlencoded' | 'multipart' | 'raw' | 'binary' | 'graphql'

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
}

export const BODY_MODE_HINT: Record<BodyMode, string> = {
  'none': 'No body sent.',
  'form-urlencoded': 'application/x-www-form-urlencoded',
  'multipart': 'multipart/form-data — fields and files.',
  'raw': 'Raw body. Pick JSON / Text / XML for the right Content-Type and editor.',
  'binary': 'Send a file as the request body. Pick a content type for the upload.',
  'graphql': 'GraphQL query/mutation. Sent as JSON.',
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
  if (ct.includes('xml')) return 'raw'
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

function looksLikeGraphql(body: string): boolean {
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

/** Pretty-print JSON if valid, else return as-is. */
export function tryPrettyJson(body: string): string {
  try { return JSON.stringify(JSON.parse(body), null, 2) } catch { return body }
}

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
