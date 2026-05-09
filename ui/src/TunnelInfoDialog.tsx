import { useEffect, useState } from 'react'
import { TunnelFlowDiagram } from './TunnelFlowDiagram'

interface TunnelDetails {
  mode: string
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

interface TailscaleSnapshot {
  available: boolean
  version: string | null
  backendState: string | null
  online: boolean
  hostName: string | null
  dnsName: string | null
  tailscaleIps: string[]
  tailnetName: string | null
  funnelRules: { hostPort: string; path: string; upstream: string; isFunnel: boolean }[]
  error: string | null
}

interface Props {
  open: boolean
  upstream: string
  proxyPort: number
  provider: 'cloudflare' | 'tailscale' | null
  publicExpose: boolean
  onClose: () => void
}

export function TunnelInfoDialog({ open, upstream, proxyPort, provider, publicExpose, onClose }: Props) {
  const [details, setDetails] = useState<TunnelDetails | null>(null)
  const [ts, setTs] = useState<TailscaleSnapshot | null>(null)
  const [loading, setLoading] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setLoading(true); setErr(null); setDetails(null); setTs(null)
    const ctl = new AbortController()
    const tasks: Promise<unknown>[] = [
      fetch('/api/tunnel/details', { signal: ctl.signal })
        .then(async (r) => {
          const j = await r.json()
          if (!r.ok) throw new Error(j?.error ?? `HTTP ${r.status}`)
          setDetails(j as TunnelDetails)
        }),
    ]
    if (provider === 'tailscale') {
      tasks.push(fetchTailscale(ctl.signal).then(setTs).catch(() => {}))
    }
    Promise.all(tasks)
      .catch((e) => { if (e.name !== 'AbortError') setErr(String(e?.message ?? e)) })
      .finally(() => setLoading(false))
    return () => ctl.abort()
  }, [open, provider])

  if (!open) return null

  const isTs = provider === 'tailscale'
  const accent = isTs ? '#5e64f4' : '#f6821f'
  const icon = isTs ? '/tailscale-mark.png' : '/cloudflare.svg'

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
          width: 'min(94vw, 860px)',
          maxHeight: '90vh',
          overflowY: 'auto',
          boxShadow: '0 16px 40px rgba(0,0,0,0.4)',
        }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '14px' }}>
          <img src={icon} alt="" height={18} />
          <h3 style={{ margin: 0, fontSize: '15px', flex: 1 }}>
            {isTs ? (publicExpose ? 'Tailscale Funnel' : 'Tailscale Serve') : 'Tunnel'}
            {' · '}<span style={{ color: accent }}>{details?.name ?? '…'}</span>
            {details?.mode && (
              <span style={{ marginLeft: '10px', fontSize: '10px', textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-muted)' }}>
                {details.mode}
              </span>
            )}
            <span
              style={{
                marginLeft: '10px',
                fontSize: '10px',
                padding: '1px 6px',
                borderRadius: '3px',
                textTransform: 'uppercase',
                letterSpacing: '0.05em',
                color: publicExpose ? '#b45309' : 'var(--ok)',
                border: publicExpose
                  ? '1px solid color-mix(in srgb, #d97706 40%, transparent)'
                  : '1px solid color-mix(in srgb, var(--ok) 40%, transparent)',
                background: publicExpose
                  ? 'color-mix(in srgb, #d97706 12%, transparent)'
                  : 'color-mix(in srgb, var(--ok) 10%, transparent)',
              }}
              title={publicExpose
                ? 'Reachable from the public internet — pair with auth.'
                : 'Reachable only from your tailnet (private).'}
            >
              {publicExpose ? 'public' : 'tailnet-only'}
            </span>
          </h3>
          <button onClick={onClose}>Close</button>
        </div>

        {publicExpose && (
          <div style={{
            padding: '10px 12px',
            marginBottom: 14,
            border: '1px solid color-mix(in srgb, #d97706 40%, transparent)',
            background: 'color-mix(in srgb, #d97706 10%, transparent)',
            borderRadius: 6,
            fontSize: 12,
            lineHeight: 1.5,
            color: 'var(--text)',
          }}>
            <b style={{ color: '#b45309' }}>⚠ Public exposure.</b> Opportunistic scanners typically
            find new public tunnel hostnames within minutes of the cert appearing in CT logs.
            <br />Pair this tap with auth: <code>WithHeaderAuth(...)</code>, <code>WithIpAllowList(...)</code>,
            <code>WithCountryAllowList(...)</code>, or <code>WithOidcAuth(...)</code>{isTs && (<>; or switch to <code>WithTailscaleServe(...)</code> for tailnet-only access</>)}.
          </div>
        )}

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
          {!isTs && <Field label="Tunnel ID" value={details?.tunnelId} mono />}
          {!isTs && <Field label="Account ID" value={details?.accountId} mono />}
          {!isTs && details?.apiResolved && (
            <>
              <Field label="Status" value={details.status} valueColor={details.status === 'healthy' ? 'var(--ok)' : 'var(--err)'} />
              <Field label="Active connections" value={details.connections?.toString()} />
              <Field label="Created" value={details.createdAt ? new Date(details.createdAt).toLocaleString() : null} />
            </>
          )}
        </div>

        {isTs && <TailscaleSection snap={ts} accent={accent} />}

        {!isTs && details?.dashboardUrl && (
          <div style={{ marginTop: '16px' }}>
            <a href={details.dashboardUrl} target="_blank" rel="noreferrer"
              style={{
                display: 'inline-flex', alignItems: 'center', gap: '8px',
                padding: '7px 12px', borderRadius: '6px',
                background: accent, color: '#fff',
                textDecoration: 'none', fontSize: '12px', fontWeight: 500,
              }}>
              <img src={icon} alt="" height={12} style={{ filter: 'brightness(0) invert(1)' }} />
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
        {!isTs && details?.error && (
          <div style={{ marginTop: '12px', fontSize: '11px', color: 'var(--text-muted)' }}>
            Cloudflare API error: {details.error}
          </div>
        )}
      </div>
    </div>
  )
}

async function fetchTailscale(signal: AbortSignal): Promise<TailscaleSnapshot> {
  const r = await fetch('/api/tunnel/tailscale/status', { signal })
  if (!r.ok) {
    const j = await r.json().catch(() => ({}))
    throw new Error((j as { error?: string }).error ?? `HTTP ${r.status}`)
  }
  return (await r.json()) as TailscaleSnapshot
}

function TailscaleSection({ snap, accent }: { snap: TailscaleSnapshot | null; accent: string }) {
  if (!snap) {
    return (
      <div style={{ marginTop: 18, fontSize: 12, color: 'var(--text-muted)' }}>
        Loading Tailscale daemon status…
      </div>
    )
  }
  if (!snap.available) {
    return (
      <div style={{ marginTop: 18, fontSize: 12, color: 'var(--err)' }}>
        Tailscale CLI unavailable on the inspector host{snap.error ? ` — ${snap.error}` : '.'}
      </div>
    )
  }

  return (
    <div style={{ marginTop: 22 }}>
      <h4 style={{ margin: '0 0 10px', fontSize: 13, color: 'var(--text)' }}>Daemon</h4>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 18px', fontSize: 12 }}>
        <Field label="Backend state" value={snap.backendState}
          valueColor={snap.backendState === 'Running' ? 'var(--ok)' : 'var(--err)'} />
        <Field label="Online"
          value={snap.online ? 'yes' : 'no'}
          valueColor={snap.online ? 'var(--ok)' : 'var(--err)'} />
        <Field label="Host" value={snap.hostName} mono />
        <Field label="MagicDNS" value={snap.dnsName} mono />
        <Field label="Tailnet" value={snap.tailnetName} />
        <Field label="Version" value={snap.version} mono />
        <Field label="Tailscale IPs" value={snap.tailscaleIps?.length ? snap.tailscaleIps.join(', ') : null} mono />
      </div>

      <h4 style={{ margin: '18px 0 8px', fontSize: 13, color: 'var(--text)' }}>
        Active rules ({snap.funnelRules.length})
      </h4>
      {snap.funnelRules.length === 0 ? (
        <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
          No <code>tailscale serve</code> or <code>funnel</code> rules configured on this node.
        </div>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
          <thead>
            <tr style={{ textAlign: 'left', color: 'var(--text-muted)', fontSize: 10, textTransform: 'uppercase' }}>
              <th style={{ padding: '6px 8px' }}>Public</th>
              <th style={{ padding: '6px 8px' }}>Host:Port</th>
              <th style={{ padding: '6px 8px' }}>Path</th>
              <th style={{ padding: '6px 8px' }}>→ Upstream</th>
            </tr>
          </thead>
          <tbody>
            {snap.funnelRules.map((r, i) => (
              <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                <td style={{ padding: '6px 8px' }}>
                  <span style={{
                    fontSize: 9.5, padding: '1px 6px', borderRadius: 3,
                    color: r.isFunnel ? '#fff' : 'var(--text-muted)',
                    background: r.isFunnel ? accent : 'var(--bg-input)',
                    border: `1px solid ${r.isFunnel ? accent : 'var(--border)'}`,
                    textTransform: 'uppercase', letterSpacing: '0.05em',
                  }}>
                    {r.isFunnel ? 'funnel' : 'serve'}
                  </span>
                </td>
                <td style={{ padding: '6px 8px', fontFamily: 'SF Mono, Menlo, monospace' }}>{r.hostPort}</td>
                <td style={{ padding: '6px 8px', fontFamily: 'SF Mono, Menlo, monospace' }}>{r.path}</td>
                <td style={{ padding: '6px 8px', fontFamily: 'SF Mono, Menlo, monospace', color: 'var(--text-muted)' }}>{r.upstream}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {snap.error && (
        <div style={{ marginTop: 12, fontSize: 11, color: 'var(--err)' }}>
          {snap.error}
        </div>
      )}
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
