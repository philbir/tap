import { useMemo, useState } from 'react'
import { RequestList } from './RequestList'
import { RequestDetail } from './RequestDetail'
import { TunnelPanel } from './TunnelPanel'
import { useRequestStream } from './useRequestStream'
import { useInspectorConfig } from './useIngress'
import { useTheme } from './useTheme'

export function App() {
  const { records, connected, clear } = useRequestStream()
  const config = useInspectorConfig()
  const { theme, toggle: toggleTheme } = useTheme()
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [tunnelOpen, setTunnelOpen] = useState(false)
  const [selectedHostname, setSelectedHostname] = useState<string | null>(null)

  const selected = useMemo(
    () => records.find((r) => r.id === selectedId) ?? null,
    [records, selectedId],
  )

  const proxyPort = config?.proxyPort
  const ingress = config?.ingress ?? []
  const apiMode = config?.apiMode ?? 'token'
  const mode = config?.mode ?? 'standalone'

  const hostnameCounts = useMemo(() => {
    const counts: Record<string, number> = {}
    for (const r of records) {
      counts[r.host] = (counts[r.host] ?? 0) + 1
    }
    return counts
  }, [records])

  const visibleRecords = useMemo(
    () => (selectedHostname ? records.filter((r) => r.host === selectedHostname) : records),
    [records, selectedHostname],
  )

  const showSelector = ingress.length >= 2

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          padding: '8px 16px',
          borderBottom: '1px solid var(--border)',
          background: 'var(--bg-raised)',
          gap: '16px',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '8px', whiteSpace: 'nowrap' }}>
          <img src="/icon.svg" width="22" height="22" alt="" />
          <span style={{ fontWeight: 600, fontSize: '14px', letterSpacing: '-0.01em' }}>
            <span style={{ color: 'var(--accent)' }}>tap</span>
          </span>
        </div>

        <div style={{ fontSize: '11px', color: 'var(--text-muted)', display: 'flex', gap: '6px', alignItems: 'center' }}>
          <span style={{ color: connected ? 'var(--ok)' : 'var(--err)' }}>●</span>
          {connected ? 'live' : 'disconnected'}
        </div>

        {ingress.length > 0 && proxyPort !== undefined && !showSelector && (
          <div
            style={{
              fontSize: '11px',
              fontFamily: 'SF Mono, Menlo, monospace',
              color: 'var(--text-muted)',
              display: 'flex',
              flexWrap: 'wrap',
              gap: '10px',
              flex: 1,
              overflow: 'hidden',
            }}
            title={
              apiMode === 'cloudflare-api'
                ? 'Cloudflare API mode: use the Tunnel button to add or update public hostnames directly.'
                : `In the Cloudflare Zero Trust dashboard, set every public hostname's target URL to http://localhost:${proxyPort} so traffic flows through the inspector.`
            }
          >
            {ingress.map((i) => (
              <span key={i.hostname || i.upstream} style={{ whiteSpace: 'nowrap' }}>
                <span style={{ color: 'var(--accent)' }}>{i.hostname || 'any'}</span>
                <span style={{ margin: '0 6px' }}>→</span>
                <span>{i.upstream}</span>
              </span>
            ))}
            {mode === 'tunnel' && (
              <span style={{ whiteSpace: 'nowrap', color: 'var(--text-muted)', opacity: 0.8 }}>
                (Cloudflare target: <code style={{ fontSize: '10px' }}>http://localhost:{proxyPort}</code>)
              </span>
            )}
            {mode === 'standalone' && (
              <span style={{ whiteSpace: 'nowrap', color: 'var(--text-muted)', opacity: 0.8 }}>
                (proxy: <code style={{ fontSize: '10px' }}>http://localhost:{proxyPort}</code>)
              </span>
            )}
          </div>
        )}

        {showSelector && <div style={{ flex: 1 }} />}

        {apiMode === 'cloudflare-api' && proxyPort !== undefined && (
          <button onClick={() => setTunnelOpen(true)} title="Manage tunnel hostnames via Cloudflare API">
            Tunnel…
          </button>
        )}
        <button onClick={toggleTheme} title="Toggle theme" style={apiMode === 'cloudflare-api' ? undefined : { marginLeft: 'auto' }}>
          {theme === 'dark' ? '☀' : '☾'}
        </button>
        <button onClick={clear}>Clear</button>
      </header>

      {showSelector && (
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            padding: '6px 14px',
            borderBottom: '1px solid var(--border)',
            background: 'var(--bg-raised)',
            overflowX: 'auto',
          }}
        >
          <SelectorPill
            label="All"
            count={records.length}
            active={selectedHostname === null}
            onClick={() => { setSelectedHostname(null); setSelectedId(null) }}
          />
          {ingress.map((entry) => (
            <SelectorPill
              key={entry.hostname}
              label={entry.hostname}
              count={hostnameCounts[entry.hostname] ?? 0}
              active={selectedHostname === entry.hostname}
              onClick={() => { setSelectedHostname(entry.hostname); setSelectedId(null) }}
            />
          ))}
        </div>
      )}

      <div
        style={{
          flex: 1,
          display: 'grid',
          gridTemplateColumns: '380px 1fr',
          gridTemplateRows: 'minmax(0, 1fr)',
          overflow: 'hidden',
          minHeight: 0,
        }}
      >
        <RequestList
          records={visibleRecords}
          selectedId={selectedId}
          filter={filter}
          onSelect={setSelectedId}
          onFilterChange={setFilter}
        />
        <RequestDetail record={selected} theme={theme} />
      </div>

      {proxyPort !== undefined && (
        <TunnelPanel proxyPort={proxyPort} open={tunnelOpen} onClose={() => setTunnelOpen(false)} />
      )}
    </div>
  )
}

interface PillProps {
  label: string
  count: number
  active: boolean
  onClick: () => void
}

function SelectorPill({ label, count, active, onClick }: PillProps) {
  return (
    <button
      onClick={onClick}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: '6px',
        padding: '3px 10px',
        borderRadius: '999px',
        border: active ? '1px solid var(--accent)' : '1px solid var(--border)',
        background: active ? 'color-mix(in srgb, var(--accent) 12%, transparent)' : 'transparent',
        color: active ? 'var(--accent)' : 'var(--text-muted)',
        fontSize: '11.5px',
        fontFamily: 'SF Mono, Menlo, monospace',
        cursor: 'pointer',
        whiteSpace: 'nowrap',
        transition: 'border-color 0.12s, background 0.12s, color 0.12s',
      }}
    >
      {label}
      <span
        style={{
          background: active ? 'var(--accent-solid)' : 'var(--bg-input)',
          color: active ? '#fff' : 'var(--text-muted)',
          borderRadius: '999px',
          padding: '0 5px',
          fontSize: '10px',
          fontFamily: 'inherit',
          minWidth: '18px',
          textAlign: 'center',
          lineHeight: '16px',
          display: 'inline-block',
        }}
      >
        {count}
      </span>
    </button>
  )
}
