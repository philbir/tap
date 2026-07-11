import { useMemo, useState } from 'react'
import { formatDistanceToNowStrict } from 'date-fns'
import { decodeJwt } from './jwt'
import { PrismLight as SyntaxHighlighter } from 'react-syntax-highlighter'
import { oneDark, oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism'

interface Props {
  authHeader: string
  theme: 'light' | 'dark'
}

const TIME_CLAIMS = new Set(['exp', 'iat', 'nbf', 'auth_time'])

function formatTimestamp(value: unknown): string | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  const date = new Date(value * 1000)
  if (isNaN(date.getTime())) return null
  const rel = formatDistanceToNowStrict(date, { addSuffix: true })
  return `${date.toISOString().replace('.000Z', 'Z')} (${rel})`
}

function summaryRows(payload: Record<string, unknown>): Array<{ key: string; value: string; isTime?: boolean; expired?: boolean }> {
  const rows: Array<{ key: string; value: string; isTime?: boolean; expired?: boolean }> = []
  const now = Math.floor(Date.now() / 1000)
  const interesting = ['sub', 'iss', 'aud', 'azp', 'scp', 'scope', 'roles', 'email', 'name', 'preferred_username']

  for (const k of interesting) {
    if (k in payload && payload[k] !== undefined && payload[k] !== null) {
      const v = payload[k]
      rows.push({ key: k, value: Array.isArray(v) ? v.join(', ') : String(v) })
    }
  }

  for (const k of ['iat', 'nbf', 'exp']) {
    const v = payload[k]
    const formatted = formatTimestamp(v)
    if (formatted) {
      rows.push({
        key: k,
        value: formatted,
        isTime: true,
        expired: k === 'exp' && typeof v === 'number' && v < now,
      })
    }
  }

  return rows
}

export function TokenInspector({ authHeader, theme }: Props) {
  const decoded = useMemo(() => decodeJwt(authHeader), [authHeader])
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)

  if (!decoded) return null

  const rawToken = authHeader.replace(/^Bearer\s+/i, '').trim()

  const copyToken = async () => {
    try {
      await navigator.clipboard.writeText(rawToken)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* ignore */
    }
  }

  const rows = summaryRows(decoded.payload)
  const alg = String(decoded.header['alg'] ?? '?')
  const typ = String(decoded.header['typ'] ?? '?')

  return (
    <div style={{ marginTop: '8px', border: '1px solid var(--border)', borderRadius: '4px', background: 'var(--bg-input)' }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '12px',
          padding: '6px 10px',
          borderBottom: '1px solid var(--border)',
          fontSize: '10px',
          color: 'var(--text-muted)',
          textTransform: 'uppercase',
          letterSpacing: '0.08em',
          fontWeight: 600,
        }}
      >
        <span>JWT · {alg} · {typ}</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: '4px' }}>
          <button
            onClick={copyToken}
            title="Copy raw token to clipboard"
            style={{ padding: '1px 8px', fontSize: '10px', background: 'transparent', borderColor: 'var(--border)' }}
          >
            {copied ? 'Copied ✓' : 'Copy token'}
          </button>
          <button
            onClick={() => setExpanded((v) => !v)}
            style={{ padding: '1px 8px', fontSize: '10px', background: 'transparent', borderColor: 'var(--border)' }}
          >
            {expanded ? 'Hide raw' : 'Show raw'}
          </button>
        </div>
      </div>

      <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: 'SF Mono, Menlo, monospace', fontSize: '12px' }}>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key}>
              <td style={{ padding: '3px 12px 3px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                {r.key}
              </td>
              <td style={{ padding: '3px 10px 3px 0', wordBreak: 'break-all', color: r.expired ? 'var(--err)' : undefined }}>
                {r.value}
                {r.expired && <span style={{ marginLeft: '8px', fontSize: '10px', fontWeight: 600 }}>EXPIRED</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {expanded && (
        <div style={{ padding: '6px 10px 10px', display: 'flex', flexDirection: 'column', gap: '6px' }}>
          <div style={{ fontSize: '10px', color: 'var(--text-muted)', fontWeight: 600 }}>Header</div>
          <SyntaxHighlighter
            language="json"
            style={theme === 'dark' ? oneDark : oneLight}
            customStyle={{ margin: 0, borderRadius: '4px', padding: '8px', fontSize: '11.5px' }}
            codeTagProps={{ style: { fontFamily: 'SF Mono, Menlo, Consolas, monospace' } }}
          >
            {JSON.stringify(decoded.header, null, 2)}
          </SyntaxHighlighter>
          <div style={{ fontSize: '10px', color: 'var(--text-muted)', fontWeight: 600 }}>Payload</div>
          <SyntaxHighlighter
            language="json"
            style={theme === 'dark' ? oneDark : oneLight}
            customStyle={{ margin: 0, borderRadius: '4px', padding: '8px', fontSize: '11.5px' }}
            codeTagProps={{ style: { fontFamily: 'SF Mono, Menlo, Consolas, monospace' } }}
          >
            {stringifyPayload(decoded.payload)}
          </SyntaxHighlighter>
        </div>
      )}
    </div>
  )
}

function stringifyPayload(payload: Record<string, unknown>): string {
  const enhanced: Record<string, unknown> = { ...payload }
  for (const k of Object.keys(enhanced)) {
    if (TIME_CLAIMS.has(k)) {
      const v = enhanced[k]
      if (typeof v === 'number') {
        enhanced[k] = `${v} (${new Date(v * 1000).toISOString()})`
      }
    }
  }
  return JSON.stringify(enhanced, null, 2)
}
