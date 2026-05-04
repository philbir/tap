import { useEffect, useState } from 'react'
import { TunnelFlowDiagram } from './TunnelFlowDiagram'

interface TunnelDetails {
  mode: 'standalone' | 'token' | 'api-managed' | 'dynamic' | 'quick' | 'local'
  name: string
  resourceName: string
  publicUrl: string | null
  accountId: string | null
  tunnelId: string | null
  dashboardUrl: string | null
  apiResolved: boolean
  status: string | null
  createdAt: string | null
  connections: number | null
  error: string | null
}

interface Props {
  open: boolean
  upstream: string
  proxyPort: number
  onClose: () => void
}

export function TunnelInfoDialog({ open, upstream, proxyPort, onClose }: Props) {
  const [details, setDetails] = useState<TunnelDetails | null>(null)
  const [loading, setLoading] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setLoading(true)
    setErr(null)
    setDetails(null)
    const ctl = new AbortController()
    fetch('/api/tunnel/details', { signal: ctl.signal })
      .then(async (r) => {
        const j = await r.json()
        if (!r.ok) throw new Error(j?.error ?? `HTTP ${r.status}`)
        setDetails(j as TunnelDetails)
      })
      .catch((e) => { if (e.name !== 'AbortError') setErr(String(e?.message ?? e)) })
      .finally(() => setLoading(false))
    return () => ctl.abort()
  }, [open])

  if (!open) return null

  return (
    <div onClick={onClose}
      style={{
        position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)',
        display: 'flex', alignItems: 'flex-start', justifyContent: 'center',
        paddingTop: '40px', zIndex: 100,
      }}>
      <div onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--bg-raised)',
          border: '1px solid var(--border)',
          borderRadius: '10px',
          padding: '20px 22px',
          // Sized to fit the 5-box flow exactly: 5×140 + 4×22 (gaps) + 2×22 (panel padding) = 832px.
          width: 'min(94vw, 860px)',
          boxShadow: '0 16px 40px rgba(0,0,0,0.4)',
        }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '14px' }}>
          <img src="/cloudflare.svg" alt="" height={18} />
          <h3 style={{ margin: 0, fontSize: '15px', flex: 1 }}>
            Tunnel · <span style={{ color: 'var(--accent)' }}>{details?.name ?? '…'}</span>
            {details?.mode && (
              <span style={{ marginLeft: '10px', fontSize: '10px', textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-muted)' }}>
                {details.mode}
              </span>
            )}
          </h3>
          <button onClick={onClose}>Close</button>
        </div>

        <TunnelFlowDiagram
          mode={details?.mode ?? 'standalone'}
          publicUrl={details?.publicUrl ?? null}
          tunnelName={details?.name ?? null}
          proxyPort={proxyPort}
          upstream={upstream}
        />

        <div style={{ marginTop: '18px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 18px', fontSize: '12px' }}>
          <Field label="Public URL" value={details?.publicUrl} link />
          <Field label="Mode" value={details?.mode} />
          <Field label="Tunnel name" value={details?.name} />
          <Field label="Resource" value={details?.resourceName} />
          <Field label="Tunnel ID" value={details?.tunnelId} mono />
          <Field label="Account ID" value={details?.accountId} mono />
          {details?.apiResolved && (
            <>
              <Field label="Status" value={details.status} valueColor={details.status === 'healthy' ? 'var(--ok)' : 'var(--err)'} />
              <Field label="Active connections" value={details.connections?.toString()} />
              <Field label="Created" value={details.createdAt ? new Date(details.createdAt).toLocaleString() : null} />
            </>
          )}
        </div>

        {details?.dashboardUrl && (
          <div style={{ marginTop: '16px' }}>
            <a href={details.dashboardUrl} target="_blank" rel="noreferrer"
              style={{
                display: 'inline-flex', alignItems: 'center', gap: '8px',
                padding: '7px 12px', borderRadius: '6px',
                background: '#f6821f', color: '#fff',
                textDecoration: 'none', fontSize: '12px', fontWeight: 500,
              }}>
              <img src="/cloudflare.svg" alt="" height={12} style={{ filter: 'brightness(0) invert(1)' }} />
              Open in Cloudflare dashboard ↗
            </a>
            {!details.apiResolved && (
              <span style={{ marginLeft: '10px', fontSize: '11px', color: 'var(--text-muted)' }}>
                Set <code>Cloudflare:ApiToken</code> on the AppHost to resolve live tunnel status.
              </span>
            )}
          </div>
        )}

        {loading && <div style={{ marginTop: '12px', fontSize: '11px', color: 'var(--text-muted)' }}>Resolving…</div>}
        {err && <div style={{ marginTop: '12px', fontSize: '12px', color: 'var(--err)' }}>{err}</div>}
        {details?.error && (
          <div style={{ marginTop: '12px', fontSize: '11px', color: 'var(--text-muted)' }}>
            Cloudflare API error: {details.error}
          </div>
        )}
      </div>
    </div>
  )
}

interface FieldProps {
  label: string
  value: string | null | undefined
  mono?: boolean
  link?: boolean
  valueColor?: string
}

function Field({ label, value, mono, link, valueColor }: FieldProps) {
  return (
    <div>
      <div style={{ fontSize: '10px', textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-muted)', marginBottom: '2px' }}>
        {label}
      </div>
      <div style={{
        fontFamily: mono ? 'SF Mono, Menlo, monospace' : undefined,
        fontSize: mono ? '11px' : '12px',
        color: valueColor ?? (value ? 'var(--text)' : 'var(--text-muted)'),
        wordBreak: 'break-all',
      }}>
        {value
          ? (link
              ? <a href={value} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)' }}>{value}</a>
              : value)
          : '—'}
      </div>
    </div>
  )
}
