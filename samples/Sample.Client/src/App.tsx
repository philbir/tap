import { useEffect, useMemo, useState } from 'react'
import type { EndpointDescriptor, TapDescriptor } from './types'
import { EndpointCard } from './EndpointCard'

function parseTaps(): TapDescriptor[] {
  const raw = import.meta.env.VITE_TAPS
  if (!raw) {
    return [
      // Fallback for `yarn dev` outside the AppHost.
      { name: 'tap-standalone', mode: 'standalone', url: 'http://localhost:5299' },
    ]
  }
  try {
    return JSON.parse(raw) as TapDescriptor[]
  } catch (e) {
    console.warn('VITE_TAPS could not be parsed', e)
    return []
  }
}

export function App() {
  const taps = useMemo(parseTaps, [])
  const [selectedName, setSelectedName] = useState(() => taps[0]?.name ?? '')
  const selected = useMemo(() => taps.find((t) => t.name === selectedName), [taps, selectedName])
  const [endpoints, setEndpoints] = useState<EndpointDescriptor[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!selected) {
      setEndpoints([])
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    fetch(`${selected.url.replace(/\/$/, '')}/endpoints`)
      .then((r) => (r.ok ? r.json() : Promise.reject(`HTTP ${r.status}`)))
      .then((d: EndpointDescriptor[]) => {
        if (!cancelled) setEndpoints(d)
      })
      .catch((e) => {
        if (!cancelled) setError(String(e))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [selected])

  return (
    <div className="app">
      <header className="header">
        <div className="brand">
          <h1>Sample.Client</h1>
          <span className="tag">tap</span>
        </div>
        <p className="lede">
          Pick a tap proxy. Endpoint cards call through it — open the matching inspector UI to watch each
          request flow in. The <code>/sse</code> endpoint streams Server-Sent Events; tick events appear
          inline and in the inspector's <strong>SSE</strong> tab.
        </p>
      </header>

      <section className="taps">
        <div className="taps-label">Proxy</div>
        <div className="tap-pills" role="tablist">
          {taps.map((t) => {
            const active = t.name === selectedName
            return (
              <button
                key={t.name}
                role="tab"
                aria-selected={active}
                onClick={() => setSelectedName(t.name)}
                className={`tap-pill ${active ? 'active' : ''}`}
                title={t.url}
              >
                <span className="tap-name">{t.name}</span>
                <span className="tap-mode">{t.mode}</span>
              </button>
            )
          })}
          {taps.length === 0 && (
            <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>No taps configured.</span>
          )}
        </div>
        {selected && (
          <div className="tap-meta">
            <code>{selected.url}</code>
          </div>
        )}
      </section>

      {error && (
        <div className="card error">
          Failed to load endpoints from {selected?.url}: {error}
        </div>
      )}

      {loading && !error && (
        <div className="muted">Loading endpoints from {selected?.url}…</div>
      )}

      <div className="grid">
        {endpoints.map((ep) => (
          <EndpointCard
            key={`${ep.method} ${ep.path}`}
            endpoint={ep}
            baseUrl={selected?.url ?? ''}
          />
        ))}
      </div>
    </div>
  )
}
