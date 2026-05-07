import { useMemo, useState } from 'react'
import { RequestList } from './RequestList'
import { RequestDetail } from './RequestDetail'
import { TunnelPanel } from './TunnelPanel'
import { TunnelInfoDialog } from './TunnelInfoDialog'
import { CloudflarePage } from './CloudflarePage'
import { useRequestStream } from './useRequestStream'
import { useInspectorConfig } from './useIngress'
import { useTheme } from './useTheme'
import type { IngressEntry } from './types'

type View = 'inspector' | 'cloudflare'

function initialViewFromLocation(): View {
  if (typeof window === 'undefined') return 'inspector'
  const hash = window.location.hash.replace(/^#\/?/, '').toLowerCase()
  return hash === 'cloudflare' ? 'cloudflare' : 'inspector'
}

function TunnelBadge({ entry, onClick }: { entry: IngressEntry; onClick: () => void }) {
  const mode = entry.tunnelMode ?? ''
  const label = mode === 'token' ? 'token'
    : mode === 'api-managed' ? 'api-managed'
    : mode === 'dynamic' ? 'dynamic'
    : mode === 'quick' ? 'quick'
    : mode
  const tip = [
    `Cloudflare tunnel: ${label}`,
    entry.tunnelName ? `name: ${entry.tunnelName}` : null,
    entry.publicUrl ? `public: ${entry.publicUrl}` : null,
  ].filter(Boolean).join(' · ')

  return (
    <button
      onClick={onClick}
      title={`${tip} · click for details`}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '5px',
        fontSize: '9.5px',
        fontFamily: 'SF Mono, Menlo, monospace',
        textTransform: 'uppercase',
        letterSpacing: '0.04em',
        padding: '1px 6px 1px 5px',
        borderRadius: '3px',
        background: '#fff',
        color: '#f6821f',
        border: '1px solid color-mix(in srgb, #f6821f 35%, transparent)',
        cursor: 'pointer',
      }}
    >
      <img src="/cloudflare.svg" alt="Cloudflare" height={11} style={{ display: 'block' }} />
      <span style={{ borderLeft: '1px solid color-mix(in srgb, #f6821f 35%, transparent)', paddingLeft: '5px' }}>
        {label}
      </span>
    </button>
  )
}

export function App() {
  const { records, connected, clear } = useRequestStream()
  const config = useInspectorConfig()
  const { theme, toggle: toggleTheme } = useTheme()
  const [view, setView] = useState<View>(initialViewFromLocation)
  const switchView = (next: View) => {
    setView(next)
    if (typeof window !== 'undefined') {
      const hash = next === 'cloudflare' ? '#cloudflare' : ''
      if (window.location.hash !== hash) {
        history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`)
      }
    }
  }
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [tunnelOpen, setTunnelOpen] = useState(false)
  const [tunnelInfoOpen, setTunnelInfoOpen] = useState(false)
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

  const showSelector = view === 'inspector' && ingress.length >= 2

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
        <div className="app-brand" aria-label="Tap">
          <img src="/tap-mark.svg" width="28" height="28" alt="" />
          <span>Tap</span>
        </div>

        <nav style={{ display: 'flex', gap: 4 }}>
          <NavTab label="Inspector" active={view === 'inspector'} onClick={() => switchView('inspector')} />
          <NavTab label="Cloudflare" active={view === 'cloudflare'} onClick={() => switchView('cloudflare')} />
        </nav>

        {view === 'inspector' && (
          <div style={{ fontSize: '11px', color: 'var(--text-muted)', display: 'flex', gap: '6px', alignItems: 'center' }}>
            <span style={{ color: connected ? 'var(--ok)' : 'var(--err)' }}>●</span>
            {connected ? 'live' : 'disconnected'}
          </div>
        )}

        {view === 'inspector' && ingress.length > 0 && proxyPort !== undefined && !showSelector && (
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
              <span key={i.hostname || i.upstream} style={{ display: 'inline-flex', alignItems: 'center', gap: '6px', whiteSpace: 'nowrap' }}>
                {i.publicUrl ? (
                  <a href={i.publicUrl} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)', textDecoration: 'none' }}>
                    {i.hostname || 'any'}
                  </a>
                ) : (
                  <span style={{ color: 'var(--accent)' }}>{i.hostname || 'any'}</span>
                )}
                <span>→</span>
                <span>{i.upstream}</span>
                {i.tunnelMode && i.tunnelMode !== 'local' && <TunnelBadge entry={i} onClick={() => setTunnelInfoOpen(true)} />}
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

        {(showSelector || view === 'cloudflare') && <div style={{ flex: 1 }} />}

        {view === 'inspector' && apiMode === 'cloudflare-api' && proxyPort !== undefined && (
          <button onClick={() => setTunnelOpen(true)} title="Manage tunnel hostnames via Cloudflare API">
            Tunnel…
          </button>
        )}
        <button
          onClick={toggleTheme}
          title="Toggle theme"
          style={view === 'inspector' && apiMode === 'cloudflare-api' ? undefined : { marginLeft: 'auto' }}
        >
          {theme === 'dark' ? '☀' : '☾'}
        </button>
        {view === 'inspector' && <button onClick={clear}>Clear</button>}
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
              entry={entry}
              onBadgeClick={() => setTunnelInfoOpen(true)}
            />
          ))}
          {proxyPort !== undefined && (
            <span
              style={{
                marginLeft: 'auto',
                fontSize: '11px',
                fontFamily: 'SF Mono, Menlo, monospace',
                color: 'var(--text-muted)',
                opacity: 0.8,
                whiteSpace: 'nowrap',
              }}
            >
              {mode === 'tunnel' ? 'Cloudflare target: ' : 'proxy: '}
              <code style={{ fontSize: '10px' }}>http://localhost:{proxyPort}</code>
            </span>
          )}
        </div>
      )}

      {view === 'inspector' ? (
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
      ) : (
        <CloudflarePage />
      )}

      {proxyPort !== undefined && (
        <TunnelPanel proxyPort={proxyPort} open={tunnelOpen} onClose={() => setTunnelOpen(false)} />
      )}

      {proxyPort !== undefined && ingress.length > 0 && (
        <TunnelInfoDialog
          open={tunnelInfoOpen}
          proxyPort={proxyPort}
          upstream={ingress[0].upstream}
          onClose={() => setTunnelInfoOpen(false)}
        />
      )}
    </div>
  )
}

function NavTab({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      style={{
        background: active ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
        color: active ? 'var(--accent)' : 'var(--text-muted)',
        border: '1px solid ' + (active ? 'color-mix(in srgb, var(--accent) 35%, transparent)' : 'transparent'),
        borderRadius: 6,
        padding: '4px 10px',
        fontSize: 12,
        fontWeight: 600,
      }}
    >
      {label}
    </button>
  )
}

interface PillProps {
  label: string
  count: number
  active: boolean
  onClick: () => void
  entry?: IngressEntry
  onBadgeClick?: () => void
}

function SelectorPill({ label, count, active, onClick, entry, onBadgeClick }: PillProps) {
  const showBadge = !!(entry?.tunnelMode && entry.tunnelMode !== 'local' && onBadgeClick)
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
      {showBadge && entry && (
        <span
          role="button"
          tabIndex={0}
          onClick={(e) => { e.stopPropagation(); onBadgeClick!() }}
          onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.stopPropagation(); e.preventDefault(); onBadgeClick!() } }}
          title={`Cloudflare tunnel: ${entry.tunnelMode}${entry.tunnelName ? ' · ' + entry.tunnelName : ''} · click for details`}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            fontSize: '9px',
            textTransform: 'uppercase',
            letterSpacing: '0.04em',
            padding: '1px 5px 1px 4px',
            borderRadius: '3px',
            background: '#fff',
            color: '#f6821f',
            border: '1px solid color-mix(in srgb, #f6821f 35%, transparent)',
            cursor: 'pointer',
          }}
        >
          <img src="/cloudflare.svg" alt="" height={9} style={{ display: 'block' }} />
          <span style={{ borderLeft: '1px solid color-mix(in srgb, #f6821f 35%, transparent)', paddingLeft: '4px' }}>
            {entry.tunnelMode}
          </span>
        </span>
      )}
    </button>
  )
}
