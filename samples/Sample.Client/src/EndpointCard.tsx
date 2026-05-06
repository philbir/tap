import { Fragment, useMemo, useRef, useState } from 'react'
import type { EndpointDescriptor, SseTick } from './types'
import { runSse } from './sseClient'

interface CallState {
  status: 'idle' | 'pending' | 'ok' | 'err' | 'streaming' | 'closed'
  message?: string
  body?: string
  contentType?: string
  events?: SseTick[]
  durationMs?: number
}

interface Props {
  endpoint: EndpointDescriptor
  baseUrl: string
}

export function EndpointCard({ endpoint, baseUrl }: Props) {
  const [params, setParams] = useState<Record<string, string>>(() => {
    const o: Record<string, string> = {}
    for (const p of endpoint.parameters ?? []) o[p.name] = p.default
    return o
  })
  const [body, setBody] = useState(endpoint.sampleBody ?? '')
  const [state, setState] = useState<CallState>({ status: 'idle' })
  const abortRef = useRef<AbortController | null>(null)

  const expandedPath = useMemo(() => {
    let path = endpoint.path
    const queryParams: string[] = []
    for (const p of endpoint.parameters ?? []) {
      const value = params[p.name] ?? ''
      const placeholder = `{${p.name}}`
      if (path.includes(placeholder)) {
        path = path.replace(placeholder, encodeURIComponent(value))
      } else if (value !== '') {
        queryParams.push(`${encodeURIComponent(p.name)}=${encodeURIComponent(value)}`)
      }
    }
    return queryParams.length ? `${path}?${queryParams.join('&')}` : path
  }, [endpoint.path, endpoint.parameters, params])

  const fullUrl = baseUrl ? `${baseUrl.replace(/\/$/, '')}${expandedPath}` : expandedPath

  const stop = () => {
    abortRef.current?.abort()
    abortRef.current = null
  }

  const callStandard = async () => {
    stop()
    setState({ status: 'pending' })
    const started = performance.now()
    try {
      const init: RequestInit = { method: endpoint.method }
      if (endpoint.method !== 'GET' && body.trim().length > 0) {
        init.headers = { 'Content-Type': 'application/json' }
        init.body = body
      }
      const r = await fetch(fullUrl, init)
      const text = await r.text()
      const durationMs = Math.round(performance.now() - started)
      setState({
        status: r.ok ? 'ok' : 'err',
        message: `HTTP ${r.status}`,
        body: text,
        contentType: r.headers.get('content-type') ?? undefined,
        durationMs,
      })
    } catch (ex) {
      setState({ status: 'err', message: String(ex) })
    }
  }

  const callSse = () => {
    stop()
    const controller = new AbortController()
    abortRef.current = controller
    setState({ status: 'streaming', events: [], message: 'connecting…' })

    runSse(fullUrl, controller.signal, {
      onOpen: () => setState((s) => ({ ...s, status: 'streaming', message: 'streaming' })),
      onEvent: (ev) =>
        setState((s) => ({
          ...s,
          status: 'streaming',
          events: [...(s.events ?? []), ev],
        })),
      onError: (msg) => setState((s) => ({ ...s, status: 'err', message: msg })),
      onClose: () =>
        setState((s) => ({
          ...s,
          status: s.status === 'err' ? 'err' : 'closed',
          message: s.status === 'err' ? s.message : 'stream closed',
        })),
    })
  }

  const onCall = () => {
    if (endpoint.isStream) callSse()
    else callStandard()
  }

  const isStreaming = state.status === 'streaming'
  const formattedBody = useMemo(() => {
    if (!state.body) return ''
    if (state.contentType?.includes('json')) {
      try {
        return JSON.stringify(JSON.parse(state.body), null, 2)
      } catch {
        return state.body
      }
    }
    return state.body
  }, [state.body, state.contentType])

  return (
    <div className="card">
      <div className="card-head">
        <span className={`method ${endpoint.method}`}>{endpoint.method}</span>
        <span className="path">{endpoint.path}</span>
        {endpoint.isStream && <span className="tag">SSE</span>}
      </div>
      <div className="descr">{endpoint.description}</div>

      {(endpoint.parameters?.length ?? 0) > 0 && (
        <div className="params">
          {endpoint.parameters!.map((p) => (
            <Fragment key={p.name}>
              <label htmlFor={`p-${endpoint.path}-${p.name}`}>
                {p.name}
                {p.description && <span className="hint">({p.description})</span>}
              </label>
              <input
                id={`p-${endpoint.path}-${p.name}`}
                value={params[p.name] ?? ''}
                onChange={(e) => setParams((s) => ({ ...s, [p.name]: e.target.value }))}
              />
            </Fragment>
          ))}
        </div>
      )}

      {endpoint.method !== 'GET' && (
        <div className="body-row">
          <label>JSON body</label>
          <textarea value={body} onChange={(e) => setBody(e.target.value)} spellCheck={false} />
        </div>
      )}

      <div className="actions">
        <button className="primary" onClick={onCall} disabled={state.status === 'pending'}>
          {endpoint.isStream ? (isStreaming ? 'Restart' : 'Open stream') : 'Send'}
        </button>
        {isStreaming && <button onClick={stop}>Stop</button>}
        {state.message && (
          <span className={`status ${state.status === 'ok' || state.status === 'closed' ? 'ok' : state.status === 'err' ? 'err' : ''}`}>
            {state.message}
            {state.durationMs !== undefined && ` · ${state.durationMs}ms`}
          </span>
        )}
      </div>
      <div className="full-url" title={fullUrl}>{fullUrl}</div>

      {endpoint.isStream && state.events && state.events.length > 0 && (
        <div className="result">
          <div className="result-head">events ({state.events.length})</div>
          <div className="event-list">
            {state.events.map((ev, i) => (
              <div className="sse-event" key={i}>
                <div className="meta">
                  {new Date(ev.receivedAt).toLocaleTimeString()} · event: {ev.event}
                  {ev.id && ` · id: ${ev.id}`}
                </div>
                <div className="data">{ev.data}</div>
              </div>
            ))}
          </div>
        </div>
      )}

      {!endpoint.isStream && state.body !== undefined && (
        <div className="result">
          <div className="result-head">
            response{state.contentType && ` · ${state.contentType}`}
          </div>
          <pre>{formattedBody || '(empty)'}</pre>
        </div>
      )}
    </div>
  )
}
