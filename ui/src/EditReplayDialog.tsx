import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import type { RequestRecord } from './types'

interface Props {
  record: RequestRecord | null
  open: boolean
  onClose: () => void
}

interface HeaderRow {
  /** Stable id; React keys + the user can have duplicate names temporarily. */
  id: number
  name: string
  value: string
  enabled: boolean
}

type Mode = 'proxy' | 'absolute'

interface ReplayResult {
  status: 'idle' | 'pending' | 'ok' | 'err'
  message?: string
}

const SKIPPED_HEADERS = new Set([
  // The server strips these anyway; Content-Type has its own editable field.
  'content-length',
  'content-type',
])

let nextRowId = 1

function buildInitialHeaders(record: RequestRecord): HeaderRow[] {
  return Object.entries(record.requestHeaders)
    .filter(([k]) => !SKIPPED_HEADERS.has(k.toLowerCase()))
    .map(([name, value]) => ({ id: nextRowId++, name, value, enabled: true }))
}

function initialHeaderEntries(record: RequestRecord): Array<[string, string]> {
  return Object.entries(record.requestHeaders)
    .filter(([k]) => !SKIPPED_HEADERS.has(k.toLowerCase()))
}

function headersMatchRecord(headers: HeaderRow[], record: RequestRecord): boolean {
  const original = initialHeaderEntries(record)
  if (headers.length !== original.length) return false
  for (let i = 0; i < headers.length; i++) {
    if (headers[i].name !== original[i][0]) return false
    if (headers[i].value !== original[i][1]) return false
    if (!headers[i].enabled) return false
  }
  return true
}

export function EditReplayDialog({ record, open, onClose }: Props) {
  const [method, setMethod] = useState('GET')
  const [mode, setMode] = useState<Mode>('proxy')
  const [host, setHost] = useState('')
  const [path, setPath] = useState('')
  const [absoluteUrl, setAbsoluteUrl] = useState('')
  const [body, setBody] = useState('')
  const [contentType, setContentType] = useState<string>('')
  const [headers, setHeaders] = useState<HeaderRow[]>([])
  const [result, setResult] = useState<ReplayResult>({ status: 'idle' })
  const firstFieldRef = useRef<HTMLInputElement | null>(null)

  // Re-seed the form whenever a new record is opened — but only on the
  // open-transition, so the user's edits persist while the dialog stays open.
  useEffect(() => {
    if (!open || !record) return
    setMethod(record.method)
    setMode('proxy')
    setHost(record.host)
    setPath(record.path)
    setAbsoluteUrl(`${record.scheme}://${record.host}${record.path}`)
    setBody(record.requestBody ?? '')
    setContentType(record.requestContentType ?? '')
    setHeaders(buildInitialHeaders(record))
    setResult({ status: 'idle' })
  }, [open, record?.id])

  // Focus first field on open for keyboard-driven flow.
  useEffect(() => {
    if (open) firstFieldRef.current?.focus()
  }, [open])

  // Esc closes; Cmd/Ctrl+Enter sends.
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); onClose() }
      if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); send() }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // send() intentionally omitted — captured via closure each render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, method, mode, host, path, absoluteUrl, body, contentType, headers])

  const dirty = useMemo(() => {
    if (!record) return false
    if (method !== record.method) return true
    if (mode === 'proxy') {
      if (host !== record.host) return true
      if (path !== record.path) return true
    } else {
      if (absoluteUrl !== `${record.scheme}://${record.host}${record.path}`) return true
    }
    if (body !== (record.requestBody ?? '')) return true
    if (contentType !== (record.requestContentType ?? '')) return true
    return !headersMatchRecord(headers, record)
  }, [record, method, mode, host, path, absoluteUrl, body, contentType, headers])

  const updateHeader = (id: number, patch: Partial<HeaderRow>) => {
    setHeaders((prev) => prev.map((h) => (h.id === id ? { ...h, ...patch } : h)))
  }
  const removeHeader = (id: number) => setHeaders((prev) => prev.filter((h) => h.id !== id))
  const addHeader = () => setHeaders((prev) => [...prev, { id: nextRowId++, name: '', value: '', enabled: true }])

  const send = async () => {
    if (result.status === 'pending') return
    setResult({ status: 'pending' })
    try {
      const headerObj: Record<string, string> = {}
      for (const h of headers) {
        if (!h.enabled) continue
        const name = h.name.trim()
        if (!name) continue
        headerObj[name] = h.value
      }
      const payload = mode === 'absolute'
        ? { method, url: absoluteUrl, headers: headerObj, body: body || null, contentType: contentType || null }
        : { method, path, host, headers: headerObj, body: body || null, contentType: contentType || null }
      const resp = await fetch('/api/replay', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(payload),
      })
      const json = await resp.json() as { replayed: boolean; status?: number; error?: string }
      if (resp.ok && json.replayed) {
        setResult({ status: 'ok', message: `Replayed → ${json.status}` })
      } else {
        setResult({ status: 'err', message: json.error ?? `HTTP ${resp.status}` })
      }
    } catch (ex) {
      setResult({ status: 'err', message: String(ex) })
    }
  }

  if (!open || !record) return null

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.4)',
        display: 'flex',
        alignItems: 'flex-start',
        justifyContent: 'center',
        paddingTop: 50,
        zIndex: 100,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--bg-raised)',
          border: '1px solid var(--border)',
          borderRadius: 8,
          padding: 16,
          width: 800,
          maxWidth: '92vw',
          maxHeight: '88vh',
          display: 'flex',
          flexDirection: 'column',
          boxShadow: '0 10px 30px rgba(0, 0, 0, 0.3)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, marginBottom: 12 }}>
          <h3 style={{ margin: 0, fontSize: 14 }}>Edit & replay</h3>
          <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
            tweak any field — fire it through the proxy or directly to a host
          </span>
          {dirty && (
            <span style={{ fontSize: 10, color: 'var(--accent)', fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase' }}>
              edited
            </span>
          )}
          <button
            onClick={onClose}
            aria-label="Close"
            style={{ marginLeft: 'auto', background: 'transparent', border: 'none', fontSize: 18, color: 'var(--text-muted)', cursor: 'pointer' }}
          >
            ×
          </button>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 10, overflowY: 'auto', minHeight: 0 }}>
          {/* Mode selector */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 11 }}>
            <span style={{ color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 600 }}>
              Send via
            </span>
            <div style={{ display: 'inline-flex', border: '1px solid var(--border)', borderRadius: 4, overflow: 'hidden' }}>
              {(['proxy', 'absolute'] as const).map((m) => (
                <button
                  key={m}
                  onClick={() => setMode(m)}
                  title={m === 'proxy'
                    ? 'Send to localhost proxy with the configured Host header — captured by the inspector'
                    : 'Send directly to an absolute URL — bypasses the proxy, not captured'}
                  style={{
                    background: mode === m ? 'color-mix(in srgb, var(--accent) 18%, transparent)' : 'transparent',
                    color: mode === m ? 'var(--accent)' : 'var(--text-muted)',
                    border: 'none',
                    borderRadius: 0,
                    padding: '4px 10px',
                    fontSize: 11,
                    fontWeight: 600,
                  }}
                >
                  {m === 'proxy' ? 'proxy (captured)' : 'absolute URL'}
                </button>
              ))}
            </div>
          </div>

          {/* Method + URL/path */}
          <div style={{ display: 'flex', gap: 8, alignItems: 'stretch' }}>
            <select
              value={method}
              onChange={(e) => setMethod(e.target.value)}
              style={{ padding: '6px 8px', fontSize: 12, fontFamily: 'SF Mono, Menlo, monospace', fontWeight: 600 }}
            >
              {['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].map((m) => (
                <option key={m} value={m}>{m}</option>
              ))}
            </select>
            {mode === 'proxy' ? (
              <>
                <input
                  ref={firstFieldRef}
                  type="text"
                  value={host}
                  onChange={(e) => setHost(e.target.value)}
                  placeholder="Host header (e.g. api.foo.com)"
                  style={{ flex: '0 0 220px', fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}
                />
                <input
                  type="text"
                  value={path}
                  onChange={(e) => setPath(e.target.value)}
                  placeholder="/path?query"
                  style={{ flex: 1, fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}
                />
              </>
            ) : (
              <input
                ref={firstFieldRef}
                type="text"
                value={absoluteUrl}
                onChange={(e) => setAbsoluteUrl(e.target.value)}
                placeholder="https://staging.example.com/path"
                style={{ flex: 1, fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}
              />
            )}
          </div>

          {/* Headers */}
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
              <span style={{ fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 600 }}>
                Headers ({headers.filter((h) => h.enabled && h.name.trim()).length})
              </span>
              <button onClick={addHeader} style={{ fontSize: 11, padding: '1px 8px' }}>+ add</button>
            </div>
            <div style={{ border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-input)', maxHeight: 220, overflowY: 'auto' }}>
              {headers.length === 0 && (
                <div style={{ padding: '8px 10px', color: 'var(--text-muted)', fontSize: 11 }}>
                  No headers — click "+ add" to insert one.
                </div>
              )}
              {headers.map((h) => (
                <div
                  key={h.id}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '24px minmax(120px, 1fr) minmax(160px, 2fr) 24px',
                    gap: 6,
                    alignItems: 'center',
                    padding: '3px 8px',
                    borderBottom: '1px solid var(--border)',
                    opacity: h.enabled ? 1 : 0.45,
                  }}
                >
                  <input
                    type="checkbox"
                    checked={h.enabled}
                    onChange={(e) => updateHeader(h.id, { enabled: e.target.checked })}
                    title={h.enabled ? 'Disable this header' : 'Enable this header'}
                  />
                  <input
                    type="text"
                    value={h.name}
                    onChange={(e) => updateHeader(h.id, { name: e.target.value })}
                    placeholder="header-name"
                    style={{ fontFamily: 'SF Mono, Menlo, monospace', fontSize: 11.5, padding: '2px 6px' }}
                  />
                  <input
                    type="text"
                    value={h.value}
                    onChange={(e) => updateHeader(h.id, { value: e.target.value })}
                    placeholder="value"
                    style={{ fontFamily: 'SF Mono, Menlo, monospace', fontSize: 11.5, padding: '2px 6px' }}
                  />
                  <button
                    onClick={() => removeHeader(h.id)}
                    aria-label="Remove header"
                    title="Remove header"
                    style={{ background: 'transparent', border: 'none', color: 'var(--text-muted)', fontSize: 14, cursor: 'pointer', padding: 0 }}
                  >
                    ×
                  </button>
                </div>
              ))}
            </div>
          </div>

          {/* Body */}
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
              <span style={{ fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 600 }}>
                Body
              </span>
              <input
                type="text"
                value={contentType}
                onChange={(e) => setContentType(e.target.value)}
                placeholder="content-type (e.g. application/json)"
                style={{ flex: '0 0 280px', fontSize: 11, padding: '2px 6px', fontFamily: 'SF Mono, Menlo, monospace' }}
              />
              {body && (() => {
                try { JSON.parse(body); return <span style={{ fontSize: 10, color: 'var(--ok)' }}>valid JSON</span> }
                catch { return contentType.toLowerCase().includes('json')
                  ? <span style={{ fontSize: 10, color: 'var(--warn)' }}>invalid JSON</span>
                  : null
                }
              })()}
              <span style={{ marginLeft: 'auto', fontSize: 10, color: 'var(--text-muted)' }}>
                {body.length} ch
              </span>
            </div>
            <textarea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              placeholder="(empty)"
              spellCheck={false}
              rows={8}
              style={{
                width: '100%',
                fontFamily: 'SF Mono, Menlo, monospace',
                fontSize: 12,
                padding: 8,
                resize: 'vertical',
                background: 'var(--bg-input)',
                color: 'var(--text)',
                border: '1px solid var(--border)',
                borderRadius: 4,
              }}
            />
          </div>

          {result.message && (
            <div
              style={{
                fontSize: 11,
                padding: '6px 10px',
                borderRadius: 4,
                background: result.status === 'err'
                  ? 'color-mix(in srgb, var(--err) 12%, transparent)'
                  : 'color-mix(in srgb, var(--ok) 12%, transparent)',
                color: result.status === 'err' ? 'var(--err)' : 'var(--ok)',
                border: `1px solid ${result.status === 'err' ? 'var(--err)' : 'var(--ok)'}`,
              }}
            >
              {result.message}
            </div>
          )}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, paddingTop: 12, borderTop: '1px solid var(--border)' }}>
          <span style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>
            <kbd style={kbdStyle}>⌘/Ctrl</kbd>+<kbd style={kbdStyle}>Enter</kbd> to send · <kbd style={kbdStyle}>Esc</kbd> to close
          </span>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
            <button onClick={onClose} style={{ background: 'transparent' }}>Close</button>
            <button
              onClick={send}
              disabled={result.status === 'pending'}
              style={{ fontWeight: 600 }}
            >
              {result.status === 'pending' ? 'Sending…' : 'Send'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

const kbdStyle: CSSProperties = {
  fontFamily: 'SF Mono, Menlo, monospace',
  fontSize: 10,
  padding: '0 4px',
  border: '1px solid var(--border)',
  borderRadius: 3,
  background: 'var(--bg-input)',
}
