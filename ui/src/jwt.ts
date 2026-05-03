export interface DecodedJwt {
  header: Record<string, unknown>
  payload: Record<string, unknown>
  signaturePreview: string
}

function base64UrlDecode(input: string): string {
  const padded = input.replace(/-/g, '+').replace(/_/g, '/')
    .padEnd(input.length + ((4 - (input.length % 4)) % 4), '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return new TextDecoder('utf-8').decode(bytes)
}

export function decodeJwt(authHeader: string | null | undefined): DecodedJwt | null {
  if (!authHeader) return null
  const stripped = authHeader.replace(/^Bearer\s+/i, '').trim()
  const parts = stripped.split('.')
  if (parts.length !== 3) return null
  try {
    const header = JSON.parse(base64UrlDecode(parts[0])) as Record<string, unknown>
    const payload = JSON.parse(base64UrlDecode(parts[1])) as Record<string, unknown>
    return {
      header,
      payload,
      signaturePreview: parts[2].slice(0, 12) + (parts[2].length > 12 ? '…' : ''),
    }
  } catch {
    return null
  }
}

export function findAuthHeader(headers: Record<string, string>): string | null {
  for (const [k, v] of Object.entries(headers)) {
    if (k.toLowerCase() === 'authorization') return v
  }
  return null
}
