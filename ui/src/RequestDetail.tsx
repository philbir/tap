import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import type { RequestRecord, SseEvent, WebSocketMessage } from './types'
import { useCodeView } from './CodeViewer'
import { TokenInspector } from './TokenInspector'
import { decodeJwt, findAuthHeader } from './jwt'
import { ExportDialog } from './ExportDialog'
import { EditReplayDialog } from './EditReplayDialog'
import { parseRequestCookies, parseResponseCookies, type ResponseCookieAttr } from './cookies'

interface Props {
  record: RequestRecord | null
  theme: 'light' | 'dark'
}

type Tab = 'request' | 'response' | 'sse' | 'ws'

function SectionLabel({ text }: { text: string }) {
  return (
    <div style={{ fontSize: '10px', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 600 }}>
      {text}
    </div>
  )
}

function splitPath(path: string): { pathname: string; query: string; params: Array<[string, string]> } {
  const qIdx = path.indexOf('?')
  if (qIdx < 0) return { pathname: path, query: '', params: [] }
  const pathname = path.slice(0, qIdx)
  const query = path.slice(qIdx + 1)
  const params: Array<[string, string]> = []
  for (const part of query.split('&')) {
    if (!part) continue
    const eq = part.indexOf('=')
    const rawK = eq < 0 ? part : part.slice(0, eq)
    const rawV = eq < 0 ? '' : part.slice(eq + 1)
    let k = rawK
    let v = rawV
    try { k = decodeURIComponent(rawK.replace(/\+/g, ' ')) } catch { /* keep raw */ }
    try { v = decodeURIComponent(rawV.replace(/\+/g, ' ')) } catch { /* keep raw */ }
    params.push([k, v])
  }
  return { pathname, query, params }
}

function looksLikeJwt(value: string): boolean {
  // Three base64url segments separated by dots, each non-empty. Cheaper guard
  // than calling decodeJwt() on every param.
  if (!value || value.length < 16) return false
  const parts = value.split('.')
  if (parts.length !== 3) return false
  return parts.every((p) => p.length > 0 && /^[A-Za-z0-9_-]+$/.test(p))
}

function QueryParamsPanel({ params, theme, defaultExpanded = true }: { params: Array<[string, string]>; theme: 'light' | 'dark'; defaultExpanded?: boolean }) {
  const [expanded, setExpanded] = useState(defaultExpanded)
  const [search, setSearch] = useState('')
  const [openTokens, setOpenTokens] = useState<Set<number>>(new Set())

  const filtered = useMemo(() => {
    if (!search) return params.map((p, i) => [p, i] as const)
    const q = search.toLowerCase()
    return params
      .map((p, i) => [p, i] as const)
      .filter(([[k, v]]) => k.toLowerCase().includes(q) || v.toLowerCase().includes(q))
  }, [params, search])

  const toggleToken = (i: number) => {
    setOpenTokens((prev) => {
      const next = new Set(prev)
      if (next.has(i)) next.delete(i)
      else next.add(i)
      return next
    })
  }

  return (
    <div style={{ flexShrink: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: expanded ? '6px' : 0 }}>
        <button
          onClick={() => setExpanded((v) => !v)}
          style={{
            background: 'transparent',
            border: 'none',
            padding: '2px 4px',
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            color: 'var(--text-muted)',
            fontSize: '10px',
            fontWeight: 600,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
          }}
        >
          <span style={{ display: 'inline-block', width: '10px', transform: expanded ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>▸</span>
          Query ({params.length})
        </button>
        {expanded && params.length > 0 && (
          <input
            type="text"
            placeholder="Search params…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            style={{ flex: 1, maxWidth: '260px', fontSize: '11px', padding: '2px 6px' }}
          />
        )}
      </div>
      {expanded && (
        <div style={{ maxHeight: '320px', overflowY: 'auto', border: '1px solid var(--border)', borderRadius: '4px', background: 'var(--bg-input)' }}>
          {filtered.length === 0 ? (
            <div style={{ padding: '8px 10px', color: 'var(--text-muted)', fontSize: '12px' }}>
              {params.length === 0 ? '(none)' : 'No matches.'}
            </div>
          ) : (
            <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: 'SF Mono, Menlo, monospace', fontSize: '12px' }}>
              <tbody>
                {filtered.map(([[k, v], idx]) => {
                  const jwt = looksLikeJwt(v) && decodeJwt(v) !== null
                  const isOpen = openTokens.has(idx)
                  return (
                    <Fragment key={`${k}-${idx}`}>
                      <tr
                        onClick={jwt ? () => toggleToken(idx) : undefined}
                        style={jwt ? { cursor: 'pointer' } : undefined}
                        title={jwt ? (isOpen ? 'Hide decoded token' : 'Click to decode JWT') : undefined}
                      >
                        <td style={{ padding: '3px 12px 3px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                          {jwt && (
                            <span style={{ display: 'inline-block', width: '10px', color: 'var(--accent)', transform: isOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>▸</span>
                          )}
                          {k}
                        </td>
                        <td style={{ padding: '3px 10px 3px 0', wordBreak: 'break-all', color: jwt ? 'var(--accent)' : undefined }}>
                          {v || <span style={{ color: 'var(--text-muted)', fontStyle: 'italic' }}>(empty)</span>}
                          {jwt && (
                            <span style={{ marginLeft: 8, fontSize: 9.5, color: 'var(--text-muted)', fontWeight: 600, letterSpacing: '0.06em' }}>
                              JWT
                            </span>
                          )}
                        </td>
                      </tr>
                      {jwt && isOpen && (
                        <tr>
                          <td colSpan={2} style={{ padding: '0 10px 10px' }}>
                            <TokenInspector authHeader={v} theme={theme} />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  )
                })}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}

function CookieAttrChip({ attr }: { attr: ResponseCookieAttr }) {
  const label = attr.value === null
    ? attr.key
    : `${attr.key}=${attr.value}`
  // Highlight security-relevant attributes.
  const isSec = attr.key === 'secure' || attr.key === 'httponly' || attr.key === 'samesite' || attr.key === 'partitioned'
  return (
    <span
      title={label}
      style={{
        display: 'inline-block',
        fontSize: 10,
        padding: '1px 6px',
        borderRadius: 3,
        border: '1px solid var(--border)',
        background: 'var(--bg-raised)',
        color: isSec ? 'var(--accent)' : 'var(--text-muted)',
        marginRight: 4,
        marginTop: 2,
        whiteSpace: 'nowrap',
      }}
    >
      {label}
    </span>
  )
}

// Browser per-cookie limit is 4096 bytes for the `name=value` pair (RFC 6265 §6.1
// recommends ≥4096; every major browser caps at exactly that). Total per-domain
// budget is typically ~80 cookies. We flag individual cookies near/over 4 KB.
const COOKIE_BYTE_WARN = 4096
const TEXT_ENCODER = typeof TextEncoder !== 'undefined' ? new TextEncoder() : null

function byteLen(s: string): number {
  if (!s) return 0
  if (TEXT_ENCODER) return TEXT_ENCODER.encode(s).length
  // Fallback for environments without TextEncoder.
  let n = 0
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i)
    if (c < 0x80) n += 1
    else if (c < 0x800) n += 2
    else if (c >= 0xd800 && c <= 0xdbff) { n += 4; i++ }
    else n += 3
  }
  return n
}

function cookiePairBytes(name: string, value: string): number {
  // Bytes counted toward the browser's per-cookie storage limit: name + "=" + value.
  return byteLen(name) + 1 + byteLen(value)
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  return `${(n / 1024).toFixed(n < 10 * 1024 ? 2 : 1)} KB`
}

function SizeBadge({ bytes, warn }: { bytes: number; warn?: boolean }) {
  return (
    <span
      title={warn ? `${bytes} bytes — exceeds typical browser per-cookie limit (4096 B)` : `${bytes} bytes`}
      style={{
        display: 'inline-block',
        fontSize: 10,
        padding: '0 6px',
        borderRadius: 3,
        marginLeft: 6,
        color: warn ? 'var(--warn)' : 'var(--text-muted)',
        border: `1px solid ${warn ? 'var(--warn)' : 'var(--border)'}`,
        background: 'transparent',
        whiteSpace: 'nowrap',
        fontWeight: warn ? 600 : 400,
      }}
    >
      {formatBytes(bytes)}
    </span>
  )
}

function CookiePanel({ raw, mode }: { raw: string; mode: 'request' | 'response' }) {
  const reqCookies = useMemo(() => mode === 'request' ? parseRequestCookies(raw) : [], [raw, mode])
  const respCookies = useMemo(() => mode === 'response' ? parseResponseCookies(raw) : [], [raw, mode])
  const total = mode === 'request' ? reqCookies.length : respCookies.length

  const sizes = useMemo(() => {
    const list = mode === 'request'
      ? reqCookies.map((c) => cookiePairBytes(c.name, c.value))
      : respCookies.map((c) => cookiePairBytes(c.name, c.value))
    return { list, total: list.reduce((a, b) => a + b, 0) }
  }, [mode, reqCookies, respCookies])

  if (total === 0) return null

  return (
    <div style={{ marginTop: 6, border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-raised)', overflow: 'hidden' }}>
      <div
        style={{
          padding: '4px 10px',
          borderBottom: '1px solid var(--border)',
          fontSize: 10,
          color: 'var(--text-muted)',
          fontWeight: 600,
          textTransform: 'uppercase',
          letterSpacing: '0.06em',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
        }}
      >
        <span>{total} cookie{total === 1 ? '' : 's'}</span>
        <span style={{ opacity: 0.5 }}>·</span>
        <span>total {formatBytes(sizes.total)}</span>
      </div>
      <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}>
        <tbody>
          {mode === 'request' && reqCookies.map((c, i) => (
            <tr key={`${c.name}-${i}`} style={{ borderTop: i === 0 ? undefined : '1px solid var(--border)' }}>
              <td style={{ padding: '4px 12px 4px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                {c.name}
                <SizeBadge bytes={sizes.list[i]} warn={sizes.list[i] > COOKIE_BYTE_WARN} />
              </td>
              <td style={{ padding: '4px 10px 4px 0', wordBreak: 'break-all', color: 'var(--text)' }}>
                {c.value || <span style={{ color: 'var(--text-muted)', fontStyle: 'italic' }}>(empty)</span>}
              </td>
            </tr>
          ))}
          {mode === 'response' && respCookies.map((c, i) => (
            <tr key={`${c.name}-${i}`} style={{ borderTop: i === 0 ? undefined : '1px solid var(--border)' }}>
              <td style={{ padding: '4px 12px 4px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                {c.name}
                <SizeBadge bytes={sizes.list[i]} warn={sizes.list[i] > COOKIE_BYTE_WARN} />
              </td>
              <td style={{ padding: '4px 10px 4px 0', wordBreak: 'break-all' }}>
                <span style={{ color: 'var(--text)' }}>
                  {c.value || <span style={{ color: 'var(--text-muted)', fontStyle: 'italic' }}>(empty)</span>}
                </span>
                {c.attrs.length > 0 && (
                  <div style={{ marginTop: 4 }}>
                    {c.attrs.map((a, ai) => <CookieAttrChip key={`${a.key}-${ai}`} attr={a} />)}
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function HeadersPanel({ headers, theme, defaultExpanded = true }: { headers: Record<string, string>; theme: 'light' | 'dark'; defaultExpanded?: boolean }) {
  const authHeader = findAuthHeader(headers)
  const hasJwt = useMemo(() => (authHeader ? decodeJwt(authHeader) !== null : false), [authHeader])
  const [expanded, setExpanded] = useState(defaultExpanded)
  const [search, setSearch] = useState('')
  const [openRows, setOpenRows] = useState<Set<string>>(new Set())

  const toggleRow = (key: string) => {
    setOpenRows((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const entries = Object.entries(headers)
  const filtered = useMemo(() => {
    if (!search) return entries
    const q = search.toLowerCase()
    return entries.filter(([k, v]) => k.toLowerCase().includes(q) || v.toLowerCase().includes(q))
  }, [entries, search])

  return (
    <div style={{ flexShrink: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: expanded ? '6px' : 0 }}>
        <button
          onClick={() => setExpanded((v) => !v)}
          style={{
            background: 'transparent',
            border: 'none',
            padding: '2px 4px',
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            color: 'var(--text-muted)',
            fontSize: '10px',
            fontWeight: 600,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
          }}
        >
          <span style={{ display: 'inline-block', width: '10px', transform: expanded ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>▸</span>
          Headers ({entries.length})
        </button>
        {expanded && (
          <input
            type="text"
            placeholder="Search headers…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            style={{ flex: 1, maxWidth: '260px', fontSize: '11px', padding: '2px 6px' }}
          />
        )}
      </div>
      {expanded && (
        <div style={{ maxHeight: '320px', overflowY: 'auto', border: '1px solid var(--border)', borderRadius: '4px', background: 'var(--bg-input)' }}>
          {filtered.length === 0 ? (
            <div style={{ padding: '8px 10px', color: 'var(--text-muted)', fontSize: '12px' }}>
              {entries.length === 0 ? '(none)' : 'No matches.'}
            </div>
          ) : (
            <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: 'SF Mono, Menlo, monospace', fontSize: '12px' }}>
              <tbody>
                {filtered.map(([k, v]) => {
                  const lk = k.toLowerCase()
                  const isAuth = lk === 'authorization' && hasJwt
                  const isCookieReq = lk === 'cookie'
                  const isCookieResp = lk === 'set-cookie'
                  const reqCookies = isCookieReq ? parseRequestCookies(v) : null
                  const respCookies = isCookieResp ? parseResponseCookies(v) : null
                  const cookieCount = reqCookies?.length ?? respCookies?.length ?? 0
                  const isCookie = (isCookieReq || isCookieResp) && cookieCount > 0
                  const clickable = isAuth || isCookie
                  const isOpen = openRows.has(k)
                  const cookieList = reqCookies ?? respCookies ?? []
                  const totalCookieBytes = cookieList.reduce((sum, c) => sum + cookiePairBytes(c.name, c.value), 0)
                  const summary = isCookie
                    ? `${cookieCount} cookie${cookieCount === 1 ? '' : 's'} · ${formatBytes(totalCookieBytes)} · ${cookieList.map((c) => c.name).join(', ')}`
                    : v
                  const title = isAuth
                    ? (isOpen ? 'Hide decoded token' : 'Click to decode JWT')
                    : isCookie
                    ? (isOpen ? 'Hide cookie list' : 'Click to expand cookies')
                    : undefined
                  return (
                    <Fragment key={k}>
                      <tr
                        onClick={clickable ? () => toggleRow(k) : undefined}
                        style={clickable ? { cursor: 'pointer' } : undefined}
                        title={title}
                      >
                        <td style={{ padding: '3px 12px 3px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                          {clickable && (
                            <span style={{ display: 'inline-block', width: '10px', color: 'var(--accent)', transform: isOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>▸</span>
                          )}
                          {k}
                        </td>
                        <td
                          style={{
                            padding: '3px 10px 3px 0',
                            wordBreak: 'break-all',
                            color: clickable ? 'var(--accent)' : undefined,
                            // Avoid blowing up the table when a cookie blob is huge — show the summary collapsed.
                            ...(isCookie && !isOpen
                              ? { whiteSpace: 'nowrap' as const, overflow: 'hidden' as const, textOverflow: 'ellipsis' as const, maxWidth: 0 }
                              : {}),
                          }}
                        >
                          {isCookie && !isOpen ? summary : v}
                        </td>
                      </tr>
                      {isAuth && isOpen && authHeader && (
                        <tr>
                          <td colSpan={2} style={{ padding: '0 10px 10px' }}>
                            <TokenInspector authHeader={authHeader} theme={theme} />
                          </td>
                        </tr>
                      )}
                      {isCookie && isOpen && (
                        <tr>
                          <td colSpan={2} style={{ padding: '0 10px 10px' }}>
                            <CookiePanel raw={v} mode={isCookieReq ? 'request' : 'response'} />
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  )
                })}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}

function SsePanel({ events, isLive }: { events: SseEvent[]; isLive: boolean }) {
  const scrollRef = useRef<HTMLDivElement | null>(null)
  const [autoScroll, setAutoScroll] = useState(true)

  useEffect(() => {
    if (!autoScroll) return
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [events.length, autoScroll])

  if (events.length === 0) {
    return (
      <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
        {isLive ? 'Waiting for events…' : '(no SSE events captured)'}
      </div>
    )
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, gap: 6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: 11, color: 'var(--text-muted)' }}>
        <span>{events.length} event{events.length === 1 ? '' : 's'}</span>
        {isLive && (
          <span style={{ color: 'var(--ok)' }}>
            <span style={{ display: 'inline-block', width: 6, height: 6, borderRadius: '50%', background: 'var(--ok)', marginRight: 5 }} />
            live
          </span>
        )}
        <label style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: 5, cursor: 'pointer' }}>
          <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} />
          auto-scroll
        </label>
      </div>
      <div ref={scrollRef} style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 6 }}>
        {events.map((ev, i) => {
          const isComment = ev.event === 'comment'
          const formatted = (() => {
            if (!ev.data) return ''
            const trimmed = ev.data.trim()
            if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
              try { return JSON.stringify(JSON.parse(trimmed), null, 2) } catch { /* fall through */ }
            }
            return ev.data
          })()
          return (
            <div
              key={i}
              style={{
                border: '1px solid var(--border)',
                borderLeft: `3px solid ${isComment ? 'var(--text-muted)' : 'var(--accent)'}`,
                borderRadius: 4,
                padding: '6px 10px',
                background: 'var(--bg-input)',
                fontFamily: 'SF Mono, Menlo, Consolas, monospace',
                fontSize: 12,
              }}
            >
              <div style={{ display: 'flex', gap: 10, fontSize: 10.5, color: 'var(--text-muted)', marginBottom: 3 }}>
                <span>#{i + 1}</span>
                <span>{new Date(ev.timestamp).toLocaleTimeString()}</span>
                <span>event: <strong style={{ color: 'var(--text)' }}>{ev.event}</strong></span>
                {ev.id && <span>id: {ev.id}</span>}
                {ev.retry !== null && <span>retry: {ev.retry}ms</span>}
              </div>
              <div style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{formatted}</div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function WsPanel({ messages, isLive }: { messages: WebSocketMessage[]; isLive: boolean }) {
  const scrollRef = useRef<HTMLDivElement | null>(null)
  const [autoScroll, setAutoScroll] = useState(true)
  const [filter, setFilter] = useState<'all' | 'client' | 'server'>('all')

  useEffect(() => {
    if (!autoScroll) return
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages.length, autoScroll])

  const filtered = useMemo(
    () => filter === 'all' ? messages : messages.filter((m) => m.direction === filter),
    [messages, filter],
  )

  if (messages.length === 0) {
    return (
      <div style={{ color: 'var(--text-muted)', fontSize: 12 }}>
        {isLive ? 'Waiting for frames…' : '(no WebSocket frames captured)'}
      </div>
    )
  }

  const counts = { client: 0, server: 0 }
  for (const m of messages) counts[m.direction] += 1

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, gap: 6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: 11, color: 'var(--text-muted)' }}>
        <span>{messages.length} frame{messages.length === 1 ? '' : 's'}</span>
        <span title="Browser → upstream">↑ {counts.client}</span>
        <span title="Upstream → browser">↓ {counts.server}</span>
        <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: 6 }}>
          {(['all', 'client', 'server'] as const).map((d) => (
            <button
              key={d}
              onClick={() => setFilter(d)}
              style={{
                fontSize: 10.5,
                padding: '1px 8px',
                borderRadius: 3,
                background: filter === d ? 'var(--accent)' : 'transparent',
                color: filter === d ? '#fff' : 'var(--text-muted)',
                border: `1px solid ${filter === d ? 'var(--accent)' : 'var(--border)'}`,
                textTransform: 'capitalize',
              }}
            >
              {d === 'all' ? 'all' : d === 'client' ? '↑ client' : '↓ server'}
            </button>
          ))}
        </span>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 5, cursor: 'pointer' }}>
          <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} />
          auto-scroll
        </label>
      </div>
      <div ref={scrollRef} style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 6 }}>
        {filtered.map((m, i) => {
          const isClient = m.direction === 'client'
          const isClose = m.type === 'close'
          const accent = isClose ? 'var(--text-muted)' : isClient ? 'var(--method-post)' : 'var(--accent)'
          const formatted = (() => {
            if (m.type === 'close') {
              return `code ${m.closeStatus ?? '—'}${m.closeDescription ? ` · ${m.closeDescription}` : ''}`
            }
            if (m.text != null) {
              const trimmed = m.text.trim()
              if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
                try { return JSON.stringify(JSON.parse(trimmed), null, 2) } catch { /* fall through */ }
              }
              return m.text
            }
            if (m.base64 != null) return `(binary · ${m.size} bytes · base64) ${m.base64.slice(0, 96)}${m.base64.length > 96 ? '…' : ''}`
            return ''
          })()
          return (
            <div
              key={i}
              style={{
                border: '1px solid var(--border)',
                borderLeft: `3px solid ${accent}`,
                borderRadius: 4,
                padding: '6px 10px',
                background: 'var(--bg-input)',
                fontFamily: 'SF Mono, Menlo, Consolas, monospace',
                fontSize: 12,
              }}
            >
              <div style={{ display: 'flex', gap: 10, fontSize: 10.5, color: 'var(--text-muted)', marginBottom: 3, alignItems: 'center' }}>
                <span>#{i + 1}</span>
                <span style={{ color: accent, fontWeight: 600 }}>
                  {isClient ? '↑ client' : '↓ server'}
                </span>
                <span>{m.type}</span>
                <span>{new Date(m.timestamp).toLocaleTimeString()}</span>
                <span>{m.size}B</span>
                {m.truncated && <span style={{ color: 'var(--warn)' }}>truncated</span>}
              </div>
              <div style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{formatted}</div>
            </div>
          )
        })}
      </div>
    </div>
  )
}

export function RequestDetail({ record, theme }: Props) {
  const showSse = !!(record?.isStream && !record?.isWebSocket) || (!!record?.sseEvents && record.sseEvents.length > 0)
  const showWs = !!record?.isWebSocket || (!!record?.webSocketMessages && record.webSocketMessages.length > 0)
  const [tab, setTab] = useState<Tab>('request')
  const [replayState, setReplayState] = useState<{ status: 'idle' | 'pending' | 'ok' | 'err'; message?: string }>({ status: 'idle' })
  const [exportDialogOpen, setExportDialogOpen] = useState(false)
  const [editReplayOpen, setEditReplayOpen] = useState(false)
  const [showRawUrl, setShowRawUrl] = useState(false)
  const split = useMemo(() => (record ? splitPath(record.path) : { pathname: '', query: '', params: [] }), [record])

  // If selection moves away from a streamed/ws record, drop the matching tab.
  useEffect(() => {
    if (!showSse && tab === 'sse') setTab('response')
    if (!showWs && tab === 'ws') setTab('response')
  }, [showSse, showWs, tab])

  // For WebSocket records the request/response tabs don't carry payload, so
  // jump straight to the frame timeline whenever the user selects one.
  const recordId = record?.id
  const isWsRecord = !!record?.isWebSocket
  useEffect(() => {
    if (recordId && isWsRecord) setTab('ws')
  }, [recordId, isWsRecord])

  const isRequest = tab === 'request'
  const isSse = tab === 'sse'
  const isWs = tab === 'ws'
  const isBodyTab = !isSse && !isWs
  const bodyView = useCodeView(
    record && isBodyTab
      ? isRequest
        ? {
            body: record.requestBody,
            contentType: record.requestContentType,
            truncated: record.requestBodyTruncated,
            originalSize: record.requestBodyOriginalSize,
            theme,
          }
        : {
            body: record.responseBody,
            base64: record.responseBodyBase64,
            contentType: record.responseContentType,
            truncated: record.responseBodyTruncated,
            originalSize: record.responseBodyOriginalSize,
            theme,
          }
      : { body: null, contentType: null, truncated: false, originalSize: 0, theme },
  )

  if (!record) {
    return (
      <div className="empty-detail">
        <img
          className="empty-detail__hero"
          src={theme === 'dark' ? '/tap-hero-dark.png' : '/tap-hero.png'}
          alt="Tap tunnel carrying traffic from cloud to a developer laptop"
        />
        <div className="empty-detail__copy">
          <h2>Ready to tap traffic</h2>
          <p>Select a request to inspect headers, bodies, tokens, and replay details.</p>
        </div>
      </div>
    )
  }

  const replay = async () => {
    setReplayState({ status: 'pending' })
    try {
      const r = await fetch(`/api/requests/${record.id}/replay`, { method: 'POST' })
      const body = await r.json() as { replayed: boolean; status?: number; error?: string }
      if (r.ok && body.replayed) {
        setReplayState({ status: 'ok', message: `Replayed → ${body.status}` })
      } else {
        setReplayState({ status: 'err', message: body.error ?? `HTTP ${r.status}` })
      }
      setTimeout(() => setReplayState({ status: 'idle' }), 2500)
    } catch (ex) {
      setReplayState({ status: 'err', message: String(ex) })
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Meta row */}
      <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', flexShrink: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '6px' }}>
          <div style={{ fontFamily: 'SF Mono, Menlo, monospace', fontSize: '13px', flex: 1, overflow: 'hidden', display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>
              <span style={{ fontWeight: 600 }}>{record.method}</span>{' '}
              <span style={{ color: 'var(--text-muted)' }}>{record.scheme}://</span>
              {record.host}
              {showRawUrl ? record.path : split.pathname}
              {!showRawUrl && split.params.length > 0 && (
                <span style={{ color: 'var(--text-muted)' }}> ?{split.params.length}</span>
              )}
            </span>
            {split.query && (
              <button
                onClick={() => setShowRawUrl((v) => !v)}
                title={showRawUrl ? 'Show path only' : 'Show raw URL with query string'}
                style={{
                  background: 'transparent',
                  border: '1px solid var(--border)',
                  color: 'var(--text-muted)',
                  fontSize: 10,
                  padding: '1px 6px',
                  borderRadius: 3,
                  textTransform: 'uppercase',
                  letterSpacing: '0.06em',
                  flexShrink: 0,
                }}
              >
                {showRawUrl ? 'path' : 'raw'}
              </button>
            )}
          </div>
          <button onClick={() => setExportDialogOpen(true)} title="Preview & export — .http, cURL, or HAR">
            Export…
          </button>
          <button
            onClick={() => setEditReplayOpen(true)}
            title="Open editor to tweak method, URL, headers, body before replaying"
          >
            Edit & replay…
          </button>
          <button onClick={replay} disabled={replayState.status === 'pending'} title="Replay this request as-is through the proxy">
            {replayState.status === 'pending' ? 'Replaying…' : 'Replay'}
          </button>
        </div>
        <div style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
          {new Date(record.timestamp).toLocaleString()} · {record.durationMs}ms · status {record.statusCode || '—'}
          {record.upstream && ` · → ${record.upstream}`}
          {record.remoteIp && ` · from ${record.remoteIp}`}
        </div>
        {record.error && (
          <div style={{ marginTop: '6px', color: 'var(--err)', fontSize: '11px' }}>
            Error: {record.error}
          </div>
        )}
        {replayState.message && (
          <div style={{ marginTop: '6px', color: replayState.status === 'err' ? 'var(--err)' : 'var(--ok)', fontSize: '11px' }}>
            {replayState.message}
          </div>
        )}
      </div>

      {/* Request / Response / SSE / WS tabs */}
      <div style={{ display: 'flex', borderBottom: '1px solid var(--border)', flexShrink: 0 }}>
        {([
          'request',
          'response',
          ...(showSse ? (['sse'] as const) : []),
          ...(showWs ? (['ws'] as const) : []),
        ] as const).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            style={{
              background: 'transparent',
              border: 'none',
              borderRadius: 0,
              padding: '10px 16px',
              color: tab === t ? 'var(--accent)' : 'var(--text-muted)',
              borderBottom: tab === t ? '2px solid var(--accent)' : '2px solid transparent',
              fontWeight: tab === t ? 600 : 400,
              textTransform: t === 'sse' || t === 'ws' ? 'uppercase' : 'capitalize',
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
            }}
          >
            {t}
            {t === 'sse' && (
              <span
                style={{
                  fontSize: 10,
                  color: record.streamCompleted ? 'var(--text-muted)' : 'var(--ok)',
                  fontWeight: 500,
                  textTransform: 'none',
                  letterSpacing: 0,
                }}
              >
                {record.sseEvents?.length ?? 0}
                {!record.streamCompleted && record.isStream && ' · live'}
              </span>
            )}
            {t === 'ws' && (
              <span
                style={{
                  fontSize: 10,
                  color: record.streamCompleted ? 'var(--text-muted)' : 'var(--ok)',
                  fontWeight: 500,
                  textTransform: 'none',
                  letterSpacing: 0,
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 5,
                }}
              >
                {record.webSocketMessages?.length ?? 0}
                {!record.streamCompleted && record.isWebSocket && (
                  <>
                    <span style={{ opacity: 0.5 }}>·</span>
                    <span className="tap-ws-dot live" style={{ marginRight: 0 }} />
                    <span className="tap-ws-live-text" style={{ letterSpacing: '0.04em', fontWeight: 600, textTransform: 'uppercase' }}>live</span>
                  </>
                )}
              </span>
            )}
          </button>
        ))}
      </div>

      {/* Content region: headers (shrink) + body (flex:1 with its own scroll) */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', padding: '12px 16px', gap: '12px', overflow: 'hidden', minHeight: 0 }}>
        {isBodyTab && isRequest && split.params.length > 0 && (
          <QueryParamsPanel params={split.params} theme={theme} />
        )}
        {isBodyTab && <HeadersPanel headers={isRequest ? record.requestHeaders : record.responseHeaders} theme={theme} defaultExpanded={!(isRequest && split.params.length > 0)} />}
        {isSse && <HeadersPanel headers={record.responseHeaders} theme={theme} defaultExpanded={false} />}
        {isWs && <HeadersPanel headers={record.requestHeaders} theme={theme} defaultExpanded={false} />}

        {isSse ? (
          <SsePanel events={record.sseEvents ?? []} isLive={!!record.isStream && !record.streamCompleted} />
        ) : isWs ? (
          <WsPanel messages={record.webSocketMessages ?? []} isLive={!!record.isWebSocket && !record.streamCompleted} />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, gap: '4px' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '12px', minHeight: '22px' }}>
              <SectionLabel text="Body" />
              {bodyView.toolbar}
            </div>
            <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
              {bodyView.content}
            </div>
          </div>
        )}
      </div>

      <ExportDialog record={record} open={exportDialogOpen} onClose={() => setExportDialogOpen(false)} />
      <EditReplayDialog record={record} open={editReplayOpen} onClose={() => setEditReplayOpen(false)} />
    </div>
  )
}
