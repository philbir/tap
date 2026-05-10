import { Fragment, useMemo, useRef, useState } from 'react'
import type { EndpointDescriptor, SseTick, TapDescriptor, WsFrame } from './types'
import { runSse } from './sseClient'
import { signHs256 } from './jwt'

interface CallState {
  status: 'idle' | 'pending' | 'ok' | 'err' | 'streaming' | 'closed'
  message?: string
  body?: string
  contentType?: string
  events?: SseTick[]
  frames?: WsFrame[]
  durationMs?: number
}

interface JwtConfig {
  secret: string
  issuer: string
  audience: string
}

interface Props {
  endpoint: EndpointDescriptor
  baseUrl: string
  tap?: TapDescriptor
  jwtConfig: JwtConfig
}

export function EndpointCard({ endpoint, baseUrl, tap, jwtConfig }: Props) {
  const needsJwt = (tap?.requiresJwt ?? false) || (endpoint.requiresAuth ?? false)

  async function buildAuthHeader(): Promise<Record<string, string>> {
    if (!needsJwt) return {}
    if (!jwtConfig.secret) {
      throw new Error('VITE_JWT_SECRET not provided to client')
    }
    const token = await signHs256({
      secret: jwtConfig.secret,
      issuer: jwtConfig.issuer,
      audience: jwtConfig.audience,
      extraClaims: { role: 'tap-demo' },
    })
    return { Authorization: `Bearer ${token}` }
  }
  const [params, setParams] = useState<Record<string, string>>(() => {
    const o: Record<string, string> = {}
    for (const p of endpoint.parameters ?? []) o[p.name] = p.default
    return o
  })
  const [body, setBody] = useState(endpoint.sampleBody ?? '')
  const [state, setState] = useState<CallState>({ status: 'idle' })
  const [wsInput, setWsInput] = useState('hello, tap!')
  const abortRef = useRef<AbortController | null>(null)
  const wsRef = useRef<WebSocket | null>(null)

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
    if (wsRef.current && wsRef.current.readyState <= WebSocket.OPEN) {
      try { wsRef.current.close(1000, 'client closed') } catch { /* ignore */ }
    }
    wsRef.current = null
  }

  const callStandard = async () => {
    stop()
    setState({ status: 'pending' })
    const started = performance.now()
    try {
      const authHeaders = await buildAuthHeader()
      const init: RequestInit = { method: endpoint.method }
      const headers: Record<string, string> = { ...authHeaders }
      if (endpoint.method !== 'GET' && body.trim().length > 0) {
        headers['Content-Type'] = 'application/json'
        init.body = body
      }
      if (Object.keys(headers).length > 0) init.headers = headers
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

  const callSse = async () => {
    stop()
    const controller = new AbortController()
    abortRef.current = controller
    setState({ status: 'streaming', events: [], message: 'connecting…' })

    let authHeaders: Record<string, string> = {}
    try {
      authHeaders = await buildAuthHeader()
    } catch (ex) {
      setState({ status: 'err', message: String(ex) })
      return
    }

    runSse(
      fullUrl,
      controller.signal,
      {
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
      },
      authHeaders,
    )
  }

  const callWebSocket = () => {
    stop()
    const wsUrl = fullUrl.replace(/^http(s?):/i, (_m, s) => `ws${s}:`)
    setState({ status: 'streaming', frames: [], message: 'connecting…' })
    let socket: WebSocket
    try {
      socket = new WebSocket(wsUrl)
    } catch (ex) {
      setState({ status: 'err', message: String(ex) })
      return
    }
    wsRef.current = socket

    socket.onopen = () => {
      setState((s) => ({
        ...s,
        status: 'streaming',
        message: 'open',
        frames: [...(s.frames ?? []), { direction: 'received', type: 'open', data: '(socket open)', receivedAt: new Date().toISOString() }],
      }))
    }
    socket.onmessage = (ev) => {
      const data = typeof ev.data === 'string' ? ev.data : '(binary frame)'
      setState((s) => ({
        ...s,
        frames: [...(s.frames ?? []), {
          direction: 'received',
          type: typeof ev.data === 'string' ? 'text' : 'binary',
          data,
          receivedAt: new Date().toISOString(),
        }],
      }))
    }
    socket.onerror = () => {
      setState((s) => ({
        ...s,
        status: 'err',
        message: 'socket error',
        frames: [...(s.frames ?? []), { direction: 'received', type: 'error', data: 'WebSocket error', receivedAt: new Date().toISOString() }],
      }))
    }
    socket.onclose = (ev) => {
      setState((s) => ({
        ...s,
        status: s.status === 'err' ? 'err' : 'closed',
        message: s.status === 'err' ? s.message : `closed (${ev.code})`,
        frames: [...(s.frames ?? []), {
          direction: 'received',
          type: 'close',
          data: `code ${ev.code}${ev.reason ? ` · ${ev.reason}` : ''}`,
          receivedAt: new Date().toISOString(),
        }],
      }))
      if (wsRef.current === socket) wsRef.current = null
    }
  }

  const sendWsFrame = () => {
    const socket = wsRef.current
    if (!socket || socket.readyState !== WebSocket.OPEN) return
    socket.send(wsInput)
    const sent = wsInput
    setState((s) => ({
      ...s,
      frames: [...(s.frames ?? []), { direction: 'sent', type: 'text', data: sent, receivedAt: new Date().toISOString() }],
    }))
  }

  const onCall = () => {
    if (endpoint.isWebSocket) callWebSocket()
    else if (endpoint.isStream) callSse()
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
        {endpoint.isWebSocket && <span className="tag">WS</span>}
        {needsJwt && <span className="tag" title="Authorization: Bearer <HS256 JWT>">JWT</span>}
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

      {endpoint.method !== 'GET' && !endpoint.isWebSocket && (
        <div className="body-row">
          <label>JSON body</label>
          <textarea value={body} onChange={(e) => setBody(e.target.value)} spellCheck={false} />
        </div>
      )}

      <div className="actions">
        <button className="primary" onClick={onCall} disabled={state.status === 'pending'}>
          {endpoint.isWebSocket
            ? (isStreaming ? 'Reconnect' : 'Connect')
            : endpoint.isStream
              ? (isStreaming ? 'Restart' : 'Open stream')
              : 'Send'}
        </button>
        {isStreaming && <button onClick={stop}>{endpoint.isWebSocket ? 'Disconnect' : 'Stop'}</button>}
        {state.message && (
          <span className={`status ${state.status === 'ok' || state.status === 'closed' ? 'ok' : state.status === 'err' ? 'err' : ''}`}>
            {state.message}
            {state.durationMs !== undefined && ` · ${state.durationMs}ms`}
          </span>
        )}
      </div>
      <div className="full-url" title={fullUrl}>{fullUrl}</div>

      {endpoint.isWebSocket && state.status === 'streaming' && (
        <div className="body-row">
          <label>send text frame</label>
          <div style={{ display: 'flex', gap: 6 }}>
            <input
              value={wsInput}
              onChange={(e) => setWsInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') sendWsFrame() }}
              style={{ flex: 1 }}
            />
            <button onClick={sendWsFrame} disabled={!wsInput}>Send</button>
          </div>
        </div>
      )}

      {endpoint.isWebSocket && state.frames && state.frames.length > 0 && (
        <div className="result">
          <div className="result-head">frames ({state.frames.length})</div>
          <div className="event-list">
            {state.frames.map((f, i) => (
              <div className="sse-event" key={i}>
                <div className="meta">
                  {new Date(f.receivedAt).toLocaleTimeString()} · {f.direction === 'sent' ? '→ sent' : '← received'} · {f.type}
                </div>
                <div className="data">{f.data}</div>
              </div>
            ))}
          </div>
        </div>
      )}

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

      {!endpoint.isStream && !endpoint.isWebSocket && state.body !== undefined && (
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
