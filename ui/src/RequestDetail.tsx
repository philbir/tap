import { Fragment, useMemo, useState } from 'react'
import type { RequestRecord } from './types'
import { useCodeView } from './CodeViewer'
import { TokenInspector } from './TokenInspector'
import { decodeJwt, findAuthHeader } from './jwt'
import { HttpFileDialog } from './HttpFileDialog'

interface Props {
  record: RequestRecord | null
  theme: 'light' | 'dark'
}

type Tab = 'request' | 'response'

function SectionLabel({ text }: { text: string }) {
  return (
    <div style={{ fontSize: '10px', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 600 }}>
      {text}
    </div>
  )
}

function HeadersPanel({ headers, theme }: { headers: Record<string, string>; theme: 'light' | 'dark' }) {
  const authHeader = findAuthHeader(headers)
  const hasJwt = useMemo(() => (authHeader ? decodeJwt(authHeader) !== null : false), [authHeader])
  const [expanded, setExpanded] = useState(true)
  const [search, setSearch] = useState('')
  const [tokenOpen, setTokenOpen] = useState(false)

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
                  const isAuth = k.toLowerCase() === 'authorization' && hasJwt
                  const clickable = isAuth
                  return (
                    <Fragment key={k}>
                      <tr
                        onClick={clickable ? () => setTokenOpen((o) => !o) : undefined}
                        style={clickable ? { cursor: 'pointer' } : undefined}
                        title={clickable ? (tokenOpen ? 'Hide decoded token' : 'Click to decode JWT') : undefined}
                      >
                        <td style={{ padding: '3px 12px 3px 10px', color: 'var(--text-muted)', verticalAlign: 'top', whiteSpace: 'nowrap', width: '1%' }}>
                          {clickable && (
                            <span style={{ display: 'inline-block', width: '10px', color: 'var(--accent)', transform: tokenOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>▸</span>
                          )}
                          {k}
                        </td>
                        <td style={{ padding: '3px 10px 3px 0', wordBreak: 'break-all', color: clickable ? 'var(--accent)' : undefined }}>
                          {v}
                        </td>
                      </tr>
                      {clickable && tokenOpen && authHeader && (
                        <tr>
                          <td colSpan={2} style={{ padding: '0 10px 10px' }}>
                            <TokenInspector authHeader={authHeader} theme={theme} />
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

export function RequestDetail({ record, theme }: Props) {
  const [tab, setTab] = useState<Tab>('request')
  const [replayState, setReplayState] = useState<{ status: 'idle' | 'pending' | 'ok' | 'err'; message?: string }>({ status: 'idle' })
  const [httpDialogOpen, setHttpDialogOpen] = useState(false)

  const isRequest = tab === 'request'
  const bodyView = useCodeView(
    record
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
      <div style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
        Select a request to inspect.
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
          <div style={{ fontFamily: 'SF Mono, Menlo, monospace', fontSize: '13px', flex: 1, overflow: 'hidden', textOverflow: 'ellipsis' }}>
            <span style={{ fontWeight: 600 }}>{record.method}</span>{' '}
            <span style={{ color: 'var(--text-muted)' }}>{record.scheme}://</span>
            {record.host}
            {record.path}
          </div>
          <button onClick={() => setHttpDialogOpen(true)} title="Preview & export as .http file">
            Export .http
          </button>
          <button onClick={replay} disabled={replayState.status === 'pending'} title="Replay this request through the proxy">
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

      {/* Request / Response tabs */}
      <div style={{ display: 'flex', borderBottom: '1px solid var(--border)', flexShrink: 0 }}>
        {(['request', 'response'] as const).map((t) => (
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
              textTransform: 'capitalize',
            }}
          >
            {t}
          </button>
        ))}
      </div>

      {/* Content region: headers (shrink) + body (flex:1 with its own scroll) */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', padding: '12px 16px', gap: '12px', overflow: 'hidden', minHeight: 0 }}>
        <HeadersPanel headers={isRequest ? record.requestHeaders : record.responseHeaders} theme={theme} />

        <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, gap: '4px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '12px', minHeight: '22px' }}>
            <SectionLabel text="Body" />
            {bodyView.toolbar}
          </div>
          <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
            {bodyView.content}
          </div>
        </div>
      </div>

      <HttpFileDialog record={record} open={httpDialogOpen} onClose={() => setHttpDialogOpen(false)} />
    </div>
  )
}
