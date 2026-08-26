/**
 * Turning a transport failure back into something a person can act on.
 *
 * When a send never reaches the server the panel has one string to work with — the exception
 * chain the engine flattened (`HttpTransport.DescribeException`). On its own that string is a
 * dead end: "The SSL connection could not be established, see inner exception." tells you
 * nothing about which knob fixes it. This reads the shape of the message and names the fault,
 * so the panel can offer the two things that actually help — a diagnosis, and the setting.
 *
 * Message matching is deliberately loose. The wording comes from the platform (and differs
 * across macOS / Linux / Windows), so every rule matches on the stable fragment only and the
 * fallthrough is always the raw message, never a wrong guess.
 */

export type RequestErrorKind = 'tls' | 'dns' | 'connection' | 'timeout' | 'protocol' | 'unknown'

export interface RequestErrorInfo {
  kind: RequestErrorKind
  /** Headline for the error card. */
  title: string
  /** One sentence saying what went wrong in the reader's terms. Null when the raw message
   *  is already the clearest thing we have to say. */
  explanation: string | null
  /** What to try next. Null when there is no better advice than "look at the message". */
  hint: string | null
  /** The certificate-chain flag the message named, when it named one. */
  tlsReason: string | null
}

/** The chain flags .NET spells out in `AuthenticationException`, in the reader's words. */
const TLS_REASONS: Record<string, { title: string; explanation: string; hint: string }> = {
  NotTimeValid: {
    title: 'Certificate expired',
    explanation: "The server's certificate is outside its validity period — it has expired, or it is not valid yet.",
    hint: 'Renew the certificate on the server. If this is a test endpoint you trust, you can accept the error for this request.',
  },
  RemoteCertificateNameMismatch: {
    title: 'Certificate is for a different host',
    explanation: 'The certificate the server presented does not cover the hostname you sent the request to.',
    hint: 'Check the URL for a typo, or send to the hostname the certificate was issued for.',
  },
  UntrustedRoot: {
    title: 'Certificate is not trusted',
    explanation: "The certificate chain ends at a root this machine doesn't trust — the usual sign of a self-signed or corporate-CA certificate.",
    hint: 'Install the issuing CA in the system trust store, or accept the error for this request.',
  },
  PartialChain: {
    title: 'Incomplete certificate chain',
    explanation: "The server didn't send the intermediate certificates needed to reach a trusted root.",
    hint: 'Fix the server to serve the full chain, or accept the error for this request.',
  },
  Revoked: {
    title: 'Certificate revoked',
    explanation: 'The issuer has revoked this certificate.',
    hint: 'Do not bypass this one — the certificate has been withdrawn for a reason. Replace it on the server.',
  },
  RevocationStatusUnknown: {
    title: 'Revocation status unknown',
    explanation: "This machine couldn't reach the issuer to check whether the certificate is still valid.",
    hint: 'Usually a blocked outbound connection to the CA. Check network/proxy access to the issuer.',
  },
  NotSignatureValid: {
    title: 'Invalid certificate signature',
    explanation: "A certificate in the chain isn't correctly signed by the one above it.",
    hint: 'The chain the server sends is broken or tampered with — inspect it before trusting anything from this host.',
  },
}

const NAME_MISMATCH_PHRASES = [
  'according to the validation procedure',
  'remotecertificatenamemismatch',
  'hostname mismatch',
]

/** Classify a failed send. `message` is the flattened exception chain from the engine. */
export function describeRequestError(message: string): RequestErrorInfo {
  const text = message.toLowerCase()

  // Ahead of the certificate check on purpose: a version/cipher mismatch still arrives wrapped
  // in "The SSL connection could not be established", and calling it a certificate problem sends
  // the reader to a dialog that has nothing to show — the handshake died before any cert.
  if (text.includes('bad protocol version') || text.includes('protocol version') || text.includes('no common cipher')) {
    return {
      kind: 'protocol',
      title: 'TLS negotiation failed',
      explanation: 'The connection was refused before any certificate was exchanged — the two ends share no protocol version or cipher.',
      hint: 'Verify this URL and port actually serve TLS (try http:// for a plaintext endpoint), or update the server if it only offers obsolete TLS versions.',
      tlsReason: null,
    }
  }

  if (isTlsError(text)) {
    const reason = tlsReason(message, text)
    const known = reason ? TLS_REASONS[reason] : undefined
    return {
      kind: 'tls',
      title: known?.title ?? 'TLS certificate validation failed',
      explanation: known?.explanation
        ?? "The certificate the server presented didn't pass validation, so the connection was refused before the request was sent.",
      hint: known?.hint ?? 'Run a diagnosis to see the chain the server actually sent.',
      tlsReason: reason,
    }
  }

  if (text.includes('no such host') || text.includes('name or service not known')
    || text.includes('nodename nor servname') || text.includes('name resolution')) {
    return {
      kind: 'dns',
      title: 'Host not found',
      explanation: "The hostname in the URL doesn't resolve.",
      hint: 'Check the spelling, the environment you have selected, and whether the name needs a VPN or private DNS to resolve.',
      tlsReason: null,
    }
  }

  if (text.includes('connection refused') || text.includes('actively refused')
    || text.includes('no connection could be made')) {
    return {
      kind: 'connection',
      title: 'Connection refused',
      explanation: 'The host answered, but nothing is listening on that port.',
      hint: 'Check the port, and that the service is running.',
      tlsReason: null,
    }
  }

  if (text.includes('connection reset') || text.includes('unreachable') || text.includes('broken pipe')) {
    return {
      kind: 'connection',
      title: 'Connection failed',
      explanation: 'The connection was dropped before a response arrived.',
      hint: null,
      tlsReason: null,
    }
  }

  if (text.includes('timed out') || text.includes('timeout') || text.includes('operation was canceled')) {
    return {
      kind: 'timeout',
      title: 'Request timed out',
      explanation: 'No response arrived within the time allowed for this request.',
      hint: 'Raise the timeout under Transport, or check whether the upstream is simply slow.',
      tlsReason: null,
    }
  }

  return { kind: 'unknown', title: 'Request failed', explanation: null, hint: null, tlsReason: null }
}

function isTlsError(text: string): boolean {
  return text.includes('ssl connection could not be established')
    || text.includes('remote certificate is invalid')
    || text.includes('certificate chain')
    || text.includes('remotecertificate')
    || NAME_MISMATCH_PHRASES.some((p) => text.includes(p))
}

/** Pull the chain flag out of ".. errors in the certificate chain: NotTimeValid, PartialChain".
 *  Takes whichever flag appears earliest in the message — the platform lists them in the order
 *  it found them, and leading with the first keeps the headline matching the message below it. */
function tlsReason(message: string, text: string): string | null {
  let best: { key: string; at: number } | null = null
  for (const key of Object.keys(TLS_REASONS)) {
    const at = message.indexOf(key)
    if (at >= 0 && (best === null || at < best.at)) best = { key, at }
  }
  if (best) return best.key
  // Older / localized phrasings say it in prose instead of naming the flag.
  if (NAME_MISMATCH_PHRASES.some((p) => text.includes(p))) return 'RemoteCertificateNameMismatch'
  if (text.includes('expired')) return 'NotTimeValid'
  if (text.includes('untrusted') || text.includes('self signed') || text.includes('self-signed')) return 'UntrustedRoot'
  return null
}
