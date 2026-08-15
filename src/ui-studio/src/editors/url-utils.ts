/**
 * Lightweight URL helpers that operate on partially-interpolated URLs. They tolerate
 * `{{var}}` tokens inside path and query (URLSearchParams chokes on raw `{}`) by encoding
 * them to placeholders pre-split and decoding back after.
 */

const VAR_RE = /(\{\{[^}]+\}\}|\$\{\{[^}]+\}\})/g

interface SplitUrl {
  path: string
  query: Array<{ key: string; value: string }>
  hash: string
}

export function splitUrl(url: string): SplitUrl {
  const hashIdx = url.indexOf('#')
  const hash = hashIdx >= 0 ? url.slice(hashIdx + 1) : ''
  const beforeHash = hashIdx >= 0 ? url.slice(0, hashIdx) : url

  const qIdx = beforeHash.indexOf('?')
  const path = qIdx >= 0 ? beforeHash.slice(0, qIdx) : beforeHash
  const qs = qIdx >= 0 ? beforeHash.slice(qIdx + 1) : ''

  const query = parseQuery(qs)
  return { path, query, hash }
}

export function joinUrl(parts: SplitUrl): string {
  let out = parts.path
  if (parts.query.length > 0) {
    const enc = parts.query
      .filter((p) => p.key)
      .map((p) => `${encodeKey(p.key)}=${encodeValue(p.value)}`)
      .join('&')
    if (enc) out += '?' + enc
  }
  if (parts.hash) out += '#' + parts.hash
  return out
}

function parseQuery(qs: string): Array<{ key: string; value: string }> {
  if (!qs) return []
  return qs.split('&').filter(Boolean).map((pair) => {
    const eq = pair.indexOf('=')
    if (eq < 0) return { key: decodeKey(pair), value: '' }
    return { key: decodeKey(pair.slice(0, eq)), value: decodeValue(pair.slice(eq + 1)) }
  })
}

// We avoid URL-encoding inside `{{var}}` so the template stays readable.
function encodeKey(k: string): string { return encodeKeepVars(k) }
function encodeValue(v: string): string { return encodeKeepVars(v) }
function decodeKey(k: string): string { return decodeKeepVars(k) }
function decodeValue(v: string): string { return decodeKeepVars(v) }

function encodeKeepVars(s: string): string {
  return s.replace(/\{\{[^}]+\}\}|\$\{\{[^}]+\}\}|[^]/g, (chunk) => {
    if (VAR_RE.test(chunk)) { VAR_RE.lastIndex = 0; return chunk }
    return encodeURIComponent(chunk)
  })
}

function decodeKeepVars(s: string): string {
  try { return decodeURIComponent(s) } catch { return s }
}
