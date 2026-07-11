// Best-effort JWT decoder. Used by the AuthEditor's Execute panel to surface a decoded
// payload + expiry in the UI. JWTs are three base64url-encoded segments separated by '.';
// we only care about the first two (header, payload). The signature is opaque to us.

export interface DecodedJwt {
  header: Record<string, unknown>
  payload: Record<string, unknown>
  /** `exp` claim as a Date, or null if absent / unparseable. */
  expiresAt: Date | null
  /** `iss` claim if present. */
  issuer: string | null
  /** `sub` claim if present. */
  subject: string | null
}

export function decodeJwt(token: string): DecodedJwt | null {
  const parts = token.split('.')
  if (parts.length < 2) return null
  try {
    const header = JSON.parse(base64UrlDecode(parts[0]))
    const payload = JSON.parse(base64UrlDecode(parts[1]))
    const exp = typeof payload['exp'] === 'number' ? new Date(payload['exp'] * 1000) : null
    return {
      header: header as Record<string, unknown>,
      payload: payload as Record<string, unknown>,
      expiresAt: exp,
      issuer: typeof payload['iss'] === 'string' ? (payload['iss'] as string) : null,
      subject: typeof payload['sub'] === 'string' ? (payload['sub'] as string) : null,
    }
  } catch {
    return null
  }
}

function base64UrlDecode(input: string): string {
  // Base64url → base64, then atob, then UTF-8 decode (for multi-byte chars like umlauts).
  const padded = input.replace(/-/g, '+').replace(/_/g, '/').padEnd(input.length + ((4 - (input.length % 4)) % 4), '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return new TextDecoder('utf-8').decode(bytes)
}
