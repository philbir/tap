// Browser-side HS256 JWT signer. Sample.Client uses this to mint tokens against a
// shared secret passed in via VITE_JWT_SECRET. The secret lives in the AppHost; in
// production a real client would fetch tokens from an issuer, never sign locally.

const enc = new TextEncoder()

function base64UrlEncode(bytes: Uint8Array): string {
  let s = ''
  for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i])
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function base64UrlEncodeString(value: string): string {
  return base64UrlEncode(enc.encode(value))
}

export interface JwtClaims {
  iss?: string
  aud?: string
  sub?: string
  exp?: number
  iat?: number
  [claim: string]: unknown
}

export interface JwtConfig {
  secret: string
  issuer?: string
  audience?: string
  ttlSeconds?: number
  extraClaims?: Record<string, unknown>
}

export async function signHs256(config: JwtConfig): Promise<string> {
  const now = Math.floor(Date.now() / 1000)
  const ttl = config.ttlSeconds ?? 300

  const claims: JwtClaims = {
    iss: config.issuer,
    aud: config.audience,
    sub: 'sample-client',
    iat: now,
    exp: now + ttl,
    ...config.extraClaims,
  }
  for (const k of Object.keys(claims)) if (claims[k] === undefined) delete claims[k]

  const header = { alg: 'HS256', typ: 'JWT' }
  const headerB64 = base64UrlEncodeString(JSON.stringify(header))
  const payloadB64 = base64UrlEncodeString(JSON.stringify(claims))
  const signingInput = `${headerB64}.${payloadB64}`

  const key = await crypto.subtle.importKey(
    'raw',
    enc.encode(config.secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  )
  const sig = await crypto.subtle.sign('HMAC', key, enc.encode(signingInput))
  const sigB64 = base64UrlEncode(new Uint8Array(sig))
  return `${signingInput}.${sigB64}`
}
