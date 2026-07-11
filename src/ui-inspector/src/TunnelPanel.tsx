import { useEffect, useState } from 'react'
import type { HostnameResult } from './types'

interface Props {
  proxyPort: number
  open: boolean
  onClose: () => void
}

export function TunnelPanel({ proxyPort, open, onClose }: Props) {
  const [rules, setRules] = useState<HostnameResult[]>([])
  const [hostname, setHostname] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    let cancelled = false
    fetch('/api/tunnel/ingress')
      .then(async (r) => {
        const json = await r.json()
        if (!cancelled) {
          if (r.ok) setRules(json as HostnameResult[])
          else setError((json as { error?: string }).error ?? `HTTP ${r.status}`)
        }
      })
      .catch((e) => !cancelled && setError(String(e)))
    return () => { cancelled = true }
  }, [open])

  const submit = async () => {
    if (!hostname.trim()) return
    setBusy(true)
    setError(null)
    try {
      const r = await fetch('/api/tunnel/ingress', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ hostname: hostname.trim() }),
      })
      const json = await r.json()
      if (!r.ok) {
        setError((json as { error?: string }).error ?? `HTTP ${r.status}`)
      } else {
        setRules(json as HostnameResult[])
        setHostname('')
      }
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  if (!open) return null

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.4)',
        display: 'flex',
        alignItems: 'flex-start',
        justifyContent: 'center',
        paddingTop: '80px',
        zIndex: 100,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--bg-raised)',
          border: '1px solid var(--border)',
          borderRadius: '8px',
          padding: '20px',
          width: '520px',
          maxWidth: '90vw',
          boxShadow: '0 10px 30px rgba(0, 0, 0, 0.3)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', marginBottom: '12px' }}>
          <h3 style={{ margin: 0, fontSize: '14px', flex: 1 }}>Tunnel Public Hostnames</h3>
          <button onClick={onClose}>Close</button>
        </div>
        <p style={{ margin: '0 0 12px', fontSize: '12px', color: 'var(--text-muted)' }}>
          Update your Cloudflare tunnel's ingress rules. New or updated hostnames will route through the inspector
          at <code>http://localhost:{proxyPort}</code>.
        </p>

        <div style={{ marginBottom: '16px' }}>
          <div style={{ fontSize: '11px', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: '6px' }}>
            Current rules
          </div>
          {rules.length === 0 ? (
            <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>(none)</div>
          ) : (
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '12px', fontFamily: 'SF Mono, Menlo, monospace' }}>
              <tbody>
                {rules.map((r) => (
                  <tr key={r.hostname} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '4px 8px 4px 0', color: 'var(--accent)' }}>{r.hostname}</td>
                    <td style={{ padding: '4px 0', color: 'var(--text-muted)' }}>→ {r.service}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
          <input
            type="text"
            placeholder="new-hostname.example.com"
            value={hostname}
            onChange={(e) => setHostname(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') submit() }}
            disabled={busy}
            style={{ flex: 1 }}
          />
          <button onClick={submit} disabled={busy || !hostname.trim()}>
            {busy ? 'Saving…' : 'Point at inspector'}
          </button>
        </div>
        <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginTop: '6px' }}>
          Creates or replaces the ingress rule. DNS records must already exist in the Cloudflare dashboard.
        </div>

        {error && (
          <div style={{ marginTop: '12px', padding: '8px 10px', background: 'var(--bg-input)', border: '1px solid var(--err)', borderRadius: '4px', color: 'var(--err)', fontSize: '12px' }}>
            {error}
          </div>
        )}
      </div>
    </div>
  )
}
