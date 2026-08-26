// Well-known HTTP header names + suggested values, surfaced as autocomplete options
// wherever the user types a header (request, API defaults, custom auth).
// Names are case-insensitive on the wire but we normalize to the conventional canonical
// casing here so the autocomplete output matches what most servers log.

export const COMMON_HEADER_NAMES: readonly string[] = [
  // Content negotiation
  'Accept',
  'Accept-Charset',
  'Accept-Encoding',
  'Accept-Language',
  'Content-Type',
  'Content-Encoding',
  'Content-Language',
  'Content-Length',
  'Content-Disposition',
  // Auth
  'Authorization',
  'Proxy-Authorization',
  'WWW-Authenticate',
  'Cookie',
  'Set-Cookie',
  // Caching / conditional
  'Cache-Control',
  'Pragma',
  'Expires',
  'ETag',
  'If-Match',
  'If-None-Match',
  'If-Modified-Since',
  'If-Unmodified-Since',
  'Last-Modified',
  'Vary',
  'Age',
  // Connection / transport
  'Connection',
  'Keep-Alive',
  'Upgrade',
  'Transfer-Encoding',
  'TE',
  'Trailer',
  // Routing / addressing
  'Host',
  'Origin',
  'Referer',
  'Referrer-Policy',
  'User-Agent',
  'Forwarded',
  'X-Forwarded-For',
  'X-Forwarded-Host',
  'X-Forwarded-Proto',
  'X-Real-IP',
  // Range
  'Range',
  'Accept-Ranges',
  'Content-Range',
  // CORS
  'Access-Control-Allow-Origin',
  'Access-Control-Allow-Methods',
  'Access-Control-Allow-Headers',
  'Access-Control-Allow-Credentials',
  'Access-Control-Expose-Headers',
  'Access-Control-Max-Age',
  'Access-Control-Request-Method',
  'Access-Control-Request-Headers',
  // Security
  'Strict-Transport-Security',
  'Content-Security-Policy',
  'X-Content-Type-Options',
  'X-Frame-Options',
  'X-XSS-Protection',
  // Custom / common app
  'X-API-Key',
  'X-Auth-Token',
  'X-CSRF-Token',
  'X-Requested-With',
  'X-Request-ID',
  'X-Correlation-ID',
  'X-Trace-ID',
  'X-HTTP-Method-Override',
  // GraphQL / app conventions
  'GraphQL-Operation-Name',
  // SOAP
  'SOAPAction',
  // Websocket
  'Sec-WebSocket-Protocol',
  'Sec-WebSocket-Version',
  'Sec-WebSocket-Extensions',
]

// Case-insensitive map from lowercase header name → suggested values. Values are full
// strings ready to drop into the field; the user can still edit afterwards.
const HEADER_VALUE_MAP: Record<string, string[]> = {
  'accept': [
    'application/json',
    'application/xml',
    'application/x-www-form-urlencoded',
    'text/html',
    'text/plain',
    'text/event-stream',
    '*/*',
  ],
  'content-type': [
    'application/json',
    'application/xml',
    'application/x-www-form-urlencoded',
    'multipart/form-data',
    'text/plain',
    'text/html',
    'text/xml',
    'application/octet-stream',
    'application/graphql',
    'application/json; charset=utf-8',
    'text/xml; charset=utf-8',
    'application/soap+xml; charset=utf-8',
  ],
  'accept-encoding': [
    'gzip',
    'gzip, deflate',
    'gzip, deflate, br',
    'br',
    'identity',
  ],
  'accept-language': [
    'en-US,en;q=0.9',
    'en',
    '*',
  ],
  'accept-charset': [
    'utf-8',
    'utf-8, iso-8859-1;q=0.5',
  ],
  'cache-control': [
    'no-cache',
    'no-store',
    'no-store, no-cache, must-revalidate',
    'max-age=0',
    'max-age=3600',
    'private',
    'public',
    'public, max-age=31536000, immutable',
  ],
  'pragma': ['no-cache'],
  'authorization': [
    'Bearer {{token}}',
    'Bearer ',
    'Basic ',
  ],
  'connection': ['keep-alive', 'close', 'upgrade'],
  'upgrade': ['websocket'],
  'transfer-encoding': ['chunked', 'identity'],
  'content-encoding': ['gzip', 'deflate', 'br', 'identity'],
  'x-requested-with': ['XMLHttpRequest'],
  'x-http-method-override': ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'],
  'origin': ['https://example.com', 'http://localhost:3000'],
  'referer': ['https://example.com'],
  'referrer-policy': [
    'no-referrer',
    'no-referrer-when-downgrade',
    'origin',
    'origin-when-cross-origin',
    'same-origin',
    'strict-origin',
    'strict-origin-when-cross-origin',
    'unsafe-url',
  ],
  'access-control-allow-origin': ['*', 'null', 'https://example.com'],
  'access-control-allow-methods': [
    'GET, POST, PUT, DELETE, OPTIONS',
    'GET, POST',
    '*',
  ],
  'access-control-allow-headers': [
    'Content-Type, Authorization',
    '*',
  ],
  'access-control-allow-credentials': ['true'],
  'access-control-max-age': ['86400', '3600'],
  'access-control-request-method': ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'],
  'strict-transport-security': ['max-age=31536000; includeSubDomains'],
  'x-content-type-options': ['nosniff'],
  'x-frame-options': ['DENY', 'SAMEORIGIN'],
  'x-xss-protection': ['0', '1; mode=block'],
  'content-security-policy': [
    "default-src 'self'",
  ],
  'sec-websocket-version': ['13'],
  'expect': ['100-continue'],
  'te': ['trailers', 'gzip'],
}

/**
 * Suggested values for a header name. Returns an empty array when no curated list
 * exists. Case-insensitive lookup.
 */
export function valuesForHeader(name: string): string[] {
  if (!name) return []
  const k = name.trim().toLowerCase()
  return HEADER_VALUE_MAP[k] ?? []
}
