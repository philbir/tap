// Signing-key generation for the JWT auth profile. Everything happens in the browser via
// WebCrypto — no key material is ever sent to the Studio backend, and the private half only
// reaches disk if the user saves the profile.
//
// HS*  → a random shared secret (base64 text; the minter hashes its UTF-8 bytes).
// RS*/PS*/ES* → a fresh keypair; the PKCS#8 private key goes into the profile, the SPKI
// public key is handed back so the user can give it to whoever verifies the token.

export interface GeneratedSigningKey {
  /** Value to put in the profile's `key` field. */
  privateKey: string
  /** PEM public key for asymmetric algorithms; null for HMAC (the secret is symmetric). */
  publicKey: string | null
  /** Human label for the generated material, e.g. "HS256 secret" / "ES256 keypair". */
  label: string
}

export async function generateSigningKey(algorithm: string): Promise<GeneratedSigningKey> {
  const alg = algorithm.toUpperCase()

  if (alg.startsWith('HS')) {
    // Secret length matches the HMAC output size — the recommended minimum for each variant.
    const bytes = new Uint8Array(alg === 'HS512' ? 64 : alg === 'HS384' ? 48 : 32)
    crypto.getRandomValues(bytes)
    return { privateKey: base64(bytes), publicKey: null, label: `${alg} secret` }
  }

  const subtle = crypto.subtle
  if (!subtle) {
    // crypto.subtle is only exposed in secure contexts (https / localhost / the Tauri shell).
    throw new Error('Key generation needs a secure context (https or localhost).')
  }

  const pair = await subtle.generateKey(keyGenParams(alg), true, ['sign', 'verify']) as CryptoKeyPair
  const [pkcs8, spki] = await Promise.all([
    subtle.exportKey('pkcs8', pair.privateKey),
    subtle.exportKey('spki', pair.publicKey),
  ])
  return {
    privateKey: toPem('PRIVATE KEY', new Uint8Array(pkcs8)),
    publicKey: toPem('PUBLIC KEY', new Uint8Array(spki)),
    label: `${alg} keypair`,
  }
}

function keyGenParams(alg: string): RsaHashedKeyGenParams | EcKeyGenParams {
  const hash = `SHA-${alg.slice(2)}` // ES512 is the odd one out — overridden below.
  switch (alg) {
    case 'RS256': case 'RS384': case 'RS512':
      return { name: 'RSASSA-PKCS1-v1_5', modulusLength: 2048, publicExponent: RSA_F4, hash }
    case 'PS256': case 'PS384': case 'PS512':
      return { name: 'RSA-PSS', modulusLength: 2048, publicExponent: RSA_F4, hash }
    // ES512 signs with SHA-512 over P-521 — the curve name doesn't match the digest.
    case 'ES256': return { name: 'ECDSA', namedCurve: 'P-256' }
    case 'ES384': return { name: 'ECDSA', namedCurve: 'P-384' }
    case 'ES512': return { name: 'ECDSA', namedCurve: 'P-521' }
    default:
      throw new Error(`Can't generate a key for algorithm '${alg}'.`)
  }
}

const RSA_F4 = new Uint8Array([0x01, 0x00, 0x01])

function base64(bytes: Uint8Array): string {
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary)
}

/** DER bytes → PEM, 64 base64 chars per line (what .NET's ImportFromPem expects). */
function toPem(label: string, der: Uint8Array): string {
  const body = base64(der).replace(/(.{64})/g, '$1\n').trimEnd()
  return `-----BEGIN ${label}-----\n${body}\n-----END ${label}-----`
}
