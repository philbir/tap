// Multi-format request export.
//
// All three formats accept the same `RequestRecord`; they differ only in
// shape and metadata richness:
//
//   .http  — VS Code REST Client / IntelliJ HTTP Client (request only)
//   curl   — single-shot terminal command (request only)
//   HAR    — HTTP Archive 1.2 entry (request + response, machine-readable)
//
// Each generator returns a string suitable for both clipboard copy and file
// download. `downloadExport` wires up the right filename + MIME type.

import type { RequestRecord } from './types'
import { parseRequestCookies, parseResponseCookies } from './cookies'
import { generateHttpFile } from './httpFile'

export type ExportFormat = 'http' | 'curl' | 'har'

export interface ExportFormatMeta {
  id: ExportFormat
  label: string
  detail: string
  /** Filename extension without the dot. */
  ext: string
  mime: string
  /** Best-effort syntax language for preview highlighting; currently unused (preview is plain pre). */
  language: 'http' | 'bash' | 'json'
}

export const EXPORT_FORMATS: ExportFormatMeta[] = [
  { id: 'http', label: '.http', detail: 'VS Code REST Client / IntelliJ HTTP Client', ext: 'http', mime: 'application/x-http;charset=utf-8', language: 'http' },
  { id: 'curl', label: 'cURL', detail: 'shell command — paste into a terminal', ext: 'sh', mime: 'application/x-sh;charset=utf-8', language: 'bash' },
  { id: 'har', label: 'HAR', detail: 'HTTP Archive 1.2 — Chrome DevTools, Fiddler, Charles', ext: 'har', mime: 'application/json;charset=utf-8', language: 'json' },
]

// --- cURL --------------------------------------------------------------------

/** Quote a single argument for POSIX `sh`/`bash` using single-quotes (safest — no shell expansion inside). */
function shellQuote(value: string): string {
  if (value === '') return "''"
  // The only character that can't appear inside single quotes is the single
  // quote itself; the standard escape is `'\''` (close, escape, reopen).
  return `'${value.replace(/'/g, "'\\''")}'`
}

/** Headers that cURL manages itself and which would conflict if echoed back. */
const CURL_SKIP_HEADERS = new Set([
  'content-length',
  'host',
])

export function generateCurl(record: RequestRecord): string {
  const url = `${record.scheme}://${record.host}${record.path}`
  const lines: string[] = []
  lines.push(`curl -X ${record.method.toUpperCase()} ${shellQuote(url)}`)
  for (const [k, v] of Object.entries(record.requestHeaders)) {
    if (CURL_SKIP_HEADERS.has(k.toLowerCase())) continue
    lines.push(`  -H ${shellQuote(`${k}: ${v}`)}`)
  }
  if (record.requestBody) {
    // --data-raw avoids cURL's `@filename` quirk (treating @-prefixed bodies as
    // file references) and preserves the body verbatim — exactly what we want
    // when replaying a captured request.
    lines.push(`  --data-raw ${shellQuote(record.requestBody)}`)
  }
  // Join with ` \\\n` so the command is one logical line in the shell but
  // wraps readably in the dialog preview.
  return lines.join(' \\\n')
}

// --- HAR ---------------------------------------------------------------------

interface HarNameValue { name: string; value: string }
interface HarCookie {
  name: string
  value: string
  path?: string
  domain?: string
  expires?: string
  httpOnly?: boolean
  secure?: boolean
}

interface HarEntry {
  startedDateTime: string
  time: number
  request: {
    method: string
    url: string
    httpVersion: string
    cookies: HarCookie[]
    headers: HarNameValue[]
    queryString: HarNameValue[]
    postData?: { mimeType: string; text: string }
    headersSize: number
    bodySize: number
  }
  response: {
    status: number
    statusText: string
    httpVersion: string
    cookies: HarCookie[]
    headers: HarNameValue[]
    content: { size: number; mimeType: string; text?: string; encoding?: string }
    redirectURL: string
    headersSize: number
    bodySize: number
  }
  cache: Record<string, never>
  timings: { send: number; wait: number; receive: number }
  serverIPAddress?: string
  _truncated?: { request?: boolean; response?: boolean }
}

interface HarDocument {
  log: {
    version: string
    creator: { name: string; version: string }
    entries: HarEntry[]
  }
}

function toHarHeaders(headers: Record<string, string>): HarNameValue[] {
  return Object.entries(headers).map(([name, value]) => ({ name, value }))
}

function toHarQueryString(path: string): HarNameValue[] {
  const q = path.indexOf('?')
  if (q < 0) return []
  const out: HarNameValue[] = []
  for (const part of path.slice(q + 1).split('&')) {
    if (!part) continue
    const eq = part.indexOf('=')
    const name = eq < 0 ? part : part.slice(0, eq)
    const value = eq < 0 ? '' : part.slice(eq + 1)
    let decodedName = name
    let decodedValue = value
    try { decodedName = decodeURIComponent(name.replace(/\+/g, ' ')) } catch { /* keep raw */ }
    try { decodedValue = decodeURIComponent(value.replace(/\+/g, ' ')) } catch { /* keep raw */ }
    out.push({ name: decodedName, value: decodedValue })
  }
  return out
}

function statusText(code: number): string {
  // Minimal table — the common cases. Unknown codes return empty (HAR allows it).
  const M: Record<number, string> = {
    200: 'OK', 201: 'Created', 202: 'Accepted', 204: 'No Content',
    301: 'Moved Permanently', 302: 'Found', 304: 'Not Modified', 307: 'Temporary Redirect', 308: 'Permanent Redirect',
    400: 'Bad Request', 401: 'Unauthorized', 403: 'Forbidden', 404: 'Not Found',
    405: 'Method Not Allowed', 409: 'Conflict', 410: 'Gone', 422: 'Unprocessable Entity',
    429: 'Too Many Requests',
    500: 'Internal Server Error', 502: 'Bad Gateway', 503: 'Service Unavailable', 504: 'Gateway Timeout',
  }
  return M[code] ?? ''
}

function requestHarCookies(headers: Record<string, string>): HarCookie[] {
  for (const [k, v] of Object.entries(headers)) {
    if (k.toLowerCase() === 'cookie') {
      return parseRequestCookies(v).map((c) => ({ name: c.name, value: c.value }))
    }
  }
  return []
}

function responseHarCookies(headers: Record<string, string>): HarCookie[] {
  for (const [k, v] of Object.entries(headers)) {
    if (k.toLowerCase() === 'set-cookie') {
      return parseResponseCookies(v).map((c) => {
        const out: HarCookie = { name: c.name, value: c.value }
        for (const a of c.attrs) {
          if (a.key === 'path' && a.value) out.path = a.value
          else if (a.key === 'domain' && a.value) out.domain = a.value
          else if (a.key === 'expires' && a.value) out.expires = a.value
          else if (a.key === 'httponly') out.httpOnly = true
          else if (a.key === 'secure') out.secure = true
        }
        return out
      })
    }
  }
  return []
}

export function generateHar(record: RequestRecord): string {
  const url = `${record.scheme}://${record.host}${record.path}`
  const requestBody = record.requestBody ?? ''
  const responseBody = record.responseBody ?? ''
  const responseBase64 = record.responseBodyBase64 ?? null

  const entry: HarEntry = {
    startedDateTime: new Date(record.timestamp).toISOString(),
    time: record.durationMs,
    request: {
      method: record.method.toUpperCase(),
      url,
      httpVersion: 'HTTP/1.1',
      cookies: requestHarCookies(record.requestHeaders),
      headers: toHarHeaders(record.requestHeaders),
      queryString: toHarQueryString(record.path),
      headersSize: -1,
      bodySize: record.requestBodyOriginalSize > 0 ? record.requestBodyOriginalSize : requestBody.length,
      ...(requestBody
        ? { postData: { mimeType: record.requestContentType ?? 'application/octet-stream', text: requestBody } }
        : {}),
    },
    response: {
      status: record.statusCode,
      statusText: statusText(record.statusCode),
      httpVersion: 'HTTP/1.1',
      cookies: responseHarCookies(record.responseHeaders),
      headers: toHarHeaders(record.responseHeaders),
      content: {
        size: record.responseBodyOriginalSize > 0 ? record.responseBodyOriginalSize : (responseBody.length || (responseBase64?.length ?? 0)),
        mimeType: record.responseContentType ?? 'application/octet-stream',
        ...(responseBase64
          ? { text: responseBase64, encoding: 'base64' }
          : responseBody ? { text: responseBody } : {}),
      },
      redirectURL: '',
      headersSize: -1,
      bodySize: record.responseBodyOriginalSize > 0 ? record.responseBodyOriginalSize : (responseBody.length || 0),
    },
    cache: {},
    // We don't capture connect/send/receive separately — wait holds the whole
    // server-side duration; send/receive are 0 by convention when unknown.
    timings: { send: 0, wait: record.durationMs, receive: 0 },
    ...(record.remoteIp ? { serverIPAddress: record.remoteIp } : {}),
    ...(record.requestBodyTruncated || record.responseBodyTruncated
      ? { _truncated: {
          ...(record.requestBodyTruncated ? { request: true } : {}),
          ...(record.responseBodyTruncated ? { response: true } : {}),
        } }
      : {}),
  }

  const har: HarDocument = {
    log: {
      version: '1.2',
      creator: { name: 'Tap Inspector', version: '0.1' },
      entries: [entry],
    },
  }
  return JSON.stringify(har, null, 2)
}

// --- Shared ------------------------------------------------------------------

export function generateExport(record: RequestRecord, format: ExportFormat): string {
  switch (format) {
    case 'http': return generateHttpFile(record)
    case 'curl': return generateCurl(record)
    case 'har': return generateHar(record)
  }
}

function exportSlug(record: RequestRecord): string {
  return (record.path.replace(/[^a-z0-9]+/gi, '_').replace(/^_|_$/g, '') || 'request').slice(0, 40)
}

export function downloadExport(record: RequestRecord, format: ExportFormat): void {
  const meta = EXPORT_FORMATS.find((f) => f.id === format)!
  const content = generateExport(record, format)
  const slug = exportSlug(record)
  const name = `${record.method.toLowerCase()}-${slug}.${meta.ext}`

  const blob = new Blob([content], { type: meta.mime })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = name
  document.body.appendChild(anchor)
  anchor.click()
  document.body.removeChild(anchor)
  URL.revokeObjectURL(url)
}
