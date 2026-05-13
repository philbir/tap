// Cookie / Set-Cookie parsers for the inspector UI.
//
// Request side: `Cookie: a=1; b=2; c=3` — semicolon-separated, no attributes.
//
// Response side: ASP.NET joins multiple Set-Cookie headers with `, `. Splitting
// is ambiguous because `Expires=Wed, 09 Jun 2024 10:18:14 GMT` also contains
// commas. The safe heuristic: only split on a comma that is followed by a
// cookie-name token + `=`. Cookie names use the RFC 7230 `token` charset, which
// excludes spaces — so `Expires=Wed, 09 …` won't false-positive (the part after
// the comma starts with a digit and a space, no `=`).

export interface RequestCookie {
  name: string
  value: string
}

export interface ResponseCookieAttr {
  /** Lower-case attribute name (path, domain, expires, max-age, samesite, secure, httponly, partitioned, priority). */
  key: string
  /** Original-cased value, or null for boolean attributes (Secure, HttpOnly, Partitioned). */
  value: string | null
}

export interface ResponseCookie {
  name: string
  value: string
  attrs: ResponseCookieAttr[]
}

function decode(s: string): string {
  try { return decodeURIComponent(s) } catch { return s }
}

export function parseRequestCookies(header: string): RequestCookie[] {
  if (!header) return []
  const out: RequestCookie[] = []
  for (const raw of header.split(';')) {
    const part = raw.trim()
    if (!part) continue
    const eq = part.indexOf('=')
    if (eq < 0) {
      out.push({ name: part, value: '' })
    } else {
      const name = part.slice(0, eq).trim()
      const value = part.slice(eq + 1).trim()
      // Strip surrounding quotes if present (RFC 6265 §5.2).
      const unquoted = value.length >= 2 && value.startsWith('"') && value.endsWith('"')
        ? value.slice(1, -1)
        : value
      out.push({ name, value: decode(unquoted) })
    }
  }
  return out
}

const BOOLEAN_ATTRS = new Set(['secure', 'httponly', 'partitioned'])

function splitSetCookieHeader(header: string): string[] {
  // Split on `,(?=\s*token=)` where token is the cookie-name charset.
  // We scan manually to keep this dependency-free and fast.
  const out: string[] = []
  let start = 0
  for (let i = 0; i < header.length; i++) {
    if (header[i] !== ',') continue
    // Look ahead past whitespace.
    let j = i + 1
    while (j < header.length && (header[j] === ' ' || header[j] === '\t')) j++
    // Try to consume a cookie-name token.
    let k = j
    while (k < header.length && isTokenChar(header[k])) k++
    if (k > j && header[k] === '=') {
      // Looks like the start of a new cookie.
      out.push(header.slice(start, i).trim())
      start = j
    }
  }
  const tail = header.slice(start).trim()
  if (tail) out.push(tail)
  return out
}

function isTokenChar(c: string): boolean {
  // RFC 7230 token = 1*tchar; cookie-name uses the same set.
  // tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "." /
  //         "^" / "_" / "`" / "|" / "~" / DIGIT / ALPHA
  return /[A-Za-z0-9!#$%&'*+\-.^_`|~]/.test(c)
}

export function parseResponseCookies(header: string): ResponseCookie[] {
  if (!header) return []
  const out: ResponseCookie[] = []
  for (const piece of splitSetCookieHeader(header)) {
    const segs = piece.split(';')
    if (segs.length === 0) continue
    const first = segs[0].trim()
    const eq = first.indexOf('=')
    if (eq < 0) continue
    const name = first.slice(0, eq).trim()
    if (!name) continue
    const rawValue = first.slice(eq + 1).trim()
    const unquoted = rawValue.length >= 2 && rawValue.startsWith('"') && rawValue.endsWith('"')
      ? rawValue.slice(1, -1)
      : rawValue
    const attrs: ResponseCookieAttr[] = []
    for (let i = 1; i < segs.length; i++) {
      const a = segs[i].trim()
      if (!a) continue
      const aEq = a.indexOf('=')
      if (aEq < 0) {
        const key = a.toLowerCase()
        attrs.push({ key, value: BOOLEAN_ATTRS.has(key) ? null : '' })
      } else {
        attrs.push({ key: a.slice(0, aEq).trim().toLowerCase(), value: a.slice(aEq + 1).trim() })
      }
    }
    out.push({ name, value: decode(unquoted), attrs })
  }
  return out
}
