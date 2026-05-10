import { useEffect, useMemo, useState } from 'react'
import type { EndpointDescriptor, TapDescriptor } from './types'
import { EndpointCard } from './EndpointCard'
import { signHs256 } from './jwt'

const jwtConfig = {
  secret: import.meta.env.VITE_JWT_SECRET ?? '',
  issuer: import.meta.env.VITE_JWT_ISSUER ?? 'tap-sample-client',
  audience: import.meta.env.VITE_JWT_AUDIENCE ?? 'tap-sample-api',
}

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

    const fetchCatalog = async () => {
      const headers: Record<string, string> = {}
      if (selected.requiresJwt && jwtConfig.secret) {
        const token = await signHs256({
          secret: jwtConfig.secret,
          issuer: jwtConfig.issuer,
          audience: jwtConfig.audience,
          extraClaims: { role: 'tap-demo' },
        })
        headers.Authorization = `Bearer ${token}`
      }
      const r = await fetch(`${selected.url.replace(/\/$/, '')}/endpoints`, { headers })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      return (await r.json()) as EndpointDescriptor[]
    }

    fetchCatalog()
      .then((d) => { if (!cancelled) setEndpoints(d) })
      .catch((e) => { if (!cancelled) setError(String(e)) })
      .finally(() => { if (!cancelled) setLoading(false) })

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
          request flow in. The <code>/sse</code> endpoint streams Server-Sent Events and the <code>/ws</code>
          endpoint opens a WebSocket; both appear live in the inspector's <strong>SSE</strong> /
          <strong>WS</strong> tabs.
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
            tap={selected}
            jwtConfig={jwtConfig}
          />
        ))}
      </div>
    </div>
  )
}
