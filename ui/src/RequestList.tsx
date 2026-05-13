import { useEffect, useMemo, useRef, useState } from 'react'
import { formatDistanceToNowStrict } from 'date-fns'
import type { RequestRecord } from './types'
import { FILTER_HELP, parseFilter } from './filter'
import { applySuggestion, suggest, type Suggestion } from './filterSuggest'

interface Props {
  records: RequestRecord[]
  selectedId: string | null
  filter: string
  onSelect: (id: string) => void
  onFilterChange: (value: string) => void
  /** True when armed: the next inbound record will auto-select. */
  captureArmed?: boolean
  /** Briefly true after the next record fired but was hidden by the host filter. */
  captureMissed?: boolean
  onToggleCapture?: () => void
}

interface SavedFilter {
  name: string
  query: string
}

const SAVED_FILTERS_KEY = 'tap.savedFilters'

function loadSavedFilters(): SavedFilter[] {
  try {
    const raw = localStorage.getItem(SAVED_FILTERS_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((x): x is SavedFilter =>
      x && typeof x.name === 'string' && typeof x.query === 'string',
    )
  } catch {
    return []
  }
}

function saveSavedFilters(items: SavedFilter[]) {
  try { localStorage.setItem(SAVED_FILTERS_KEY, JSON.stringify(items)) } catch { /* ignore */ }
}

function statusColor(status: number): string {
  if (status === 0) return 'var(--err)'
  if (status >= 500) return 'var(--err)'
  if (status >= 400) return 'var(--warn)'
  if (status >= 300) return 'var(--method-get)'
  return 'var(--ok)'
}

function methodColor(method: string): string {
  switch (method.toUpperCase()) {
    case 'GET': return 'var(--method-get)'
    case 'POST': return 'var(--method-post)'
    case 'PUT': return 'var(--method-put)'
    case 'DELETE': return 'var(--method-delete)'
    case 'PATCH': return 'var(--method-patch)'
    default: return 'var(--text-muted)'
  }
}

function RelativeTime({ iso }: { iso: string }) {
  const [, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick((t) => t + 1), 10_000)
    return () => clearInterval(id)
  }, [])
  return <>{formatDistanceToNowStrict(new Date(iso), { addSuffix: true })}</>
}

export function RequestList({ records, selectedId, filter, onSelect, onFilterChange, captureArmed = false, captureMissed = false, onToggleCapture }: Props) {
  const [saved, setSaved] = useState<SavedFilter[]>(loadSavedFilters)
  const [helpOpen, setHelpOpen] = useState(false)
  const [savePromptOpen, setSavePromptOpen] = useState(false)
  const [pendingName, setPendingName] = useState('')
  const saveInputRef = useRef<HTMLInputElement | null>(null)
  const filterInputRef = useRef<HTMLInputElement | null>(null)
  const [caret, setCaret] = useState(0)
  const [suggestOpen, setSuggestOpen] = useState(false)
  const [suggestIndex, setSuggestIndex] = useState(0)
  // Dismissed-until-next-change: pressing Esc closes the popover but typing
  // re-opens it. Tracks the filter snapshot at dismiss time.
  const [dismissedAt, setDismissedAt] = useState<string | null>(null)

  const suggestion = useMemo(() => {
    if (document.activeElement !== filterInputRef.current) return null
    return suggest(filter, caret, records)
  }, [filter, caret, records])

  const showSuggestions = suggestOpen
    && suggestion !== null
    && suggestion.suggestions.length > 0
    && filter !== dismissedAt

  useEffect(() => {
    if (!showSuggestions) return
    setSuggestIndex((i) => Math.min(i, suggestion!.suggestions.length - 1))
  }, [showSuggestions, suggestion])

  const parsed = useMemo(() => parseFilter(filter), [filter])
  const filtered = useMemo(
    () => filter ? records.filter(parsed.predicate) : records,
    [records, parsed, filter],
  )

  useEffect(() => {
    if (savePromptOpen) saveInputRef.current?.focus()
  }, [savePromptOpen])

  const persistSaved = (next: SavedFilter[]) => {
    setSaved(next)
    saveSavedFilters(next)
  }

  const matchedSavedName = useMemo(() => {
    const trimmed = filter.trim()
    if (!trimmed) return null
    return saved.find((s) => s.query === trimmed)?.name ?? null
  }, [saved, filter])

  const refreshCaret = () => {
    const el = filterInputRef.current
    if (el) setCaret(el.selectionStart ?? el.value.length)
  }

  const acceptSuggestion = (s: Suggestion) => {
    if (!suggestion) return
    const { value, caret: nextCaret } = applySuggestion(filter, suggestion.token, s)
    onFilterChange(value)
    setDismissedAt(null)
    // Defer caret placement until the controlled input has re-rendered.
    requestAnimationFrame(() => {
      const el = filterInputRef.current
      if (!el) return
      el.focus()
      el.setSelectionRange(nextCaret, nextCaret)
      setCaret(nextCaret)
      setSuggestOpen(true)
    })
  }

  const commitSave = () => {
    const name = pendingName.trim()
    const query = filter.trim()
    if (!name || !query) { setSavePromptOpen(false); setPendingName(''); return }
    const existingIdx = saved.findIndex((s) => s.name === name)
    const next = existingIdx >= 0
      ? saved.map((s, i) => i === existingIdx ? { name, query } : s)
      : [...saved, { name, query }]
    persistSaved(next)
    setSavePromptOpen(false)
    setPendingName('')
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', borderRight: '1px solid var(--border)' }}>
      {saved.length > 0 && (
        <div
          style={{
            display: 'flex',
            gap: 4,
            padding: '6px 8px 0',
            overflowX: 'auto',
            flexWrap: 'nowrap',
          }}
        >
          {saved.map((s) => {
            const active = filter.trim() === s.query
            return (
              <span
                key={s.name}
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 4,
                  fontSize: 11,
                  padding: '2px 4px 2px 8px',
                  borderRadius: 999,
                  border: active ? '1px solid var(--accent)' : '1px solid var(--border)',
                  background: active ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'var(--bg-raised)',
                  color: active ? 'var(--accent)' : 'var(--text-muted)',
                  whiteSpace: 'nowrap',
                  flexShrink: 0,
                }}
              >
                <button
                  onClick={() => onFilterChange(s.query)}
                  title={s.query}
                  style={{
                    background: 'transparent',
                    border: 'none',
                    color: 'inherit',
                    padding: 0,
                    fontSize: 11,
                    cursor: 'pointer',
                  }}
                >
                  {s.name}
                </button>
                <button
                  onClick={() => persistSaved(saved.filter((x) => x.name !== s.name))}
                  title={`Delete saved filter "${s.name}"`}
                  aria-label={`Delete saved filter ${s.name}`}
                  style={{
                    background: 'transparent',
                    border: 'none',
                    color: 'inherit',
                    padding: '0 2px',
                    fontSize: 12,
                    lineHeight: 1,
                    opacity: 0.55,
                    cursor: 'pointer',
                  }}
                >
                  ×
                </button>
              </span>
            )
          })}
        </div>
      )}
      <div style={{ padding: '8px', borderBottom: '1px solid var(--border)', display: 'flex', flexDirection: 'column', gap: 6 }}>
        {onToggleCapture && (
          <button
            type="button"
            onClick={onToggleCapture}
            title={
              captureArmed
                ? 'Armed — next inbound request will auto-select. Click to disarm.'
                : 'Auto-select the next inbound request (handy when chasing one webhook through noisy traffic)'
            }
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              fontSize: 11,
              fontWeight: captureArmed ? 600 : 500,
              padding: '4px 10px',
              borderRadius: 4,
              border: `1px solid ${captureArmed ? 'var(--accent)' : 'var(--border)'}`,
              background: captureArmed ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'var(--bg-raised)',
              color: captureArmed ? 'var(--accent)' : 'var(--text-muted)',
              cursor: 'pointer',
              textAlign: 'left',
            }}
          >
            <span
              style={{
                display: 'inline-block',
                width: 8,
                height: 8,
                borderRadius: '50%',
                background: captureArmed ? 'var(--accent)' : 'var(--text-muted)',
                opacity: captureArmed ? 1 : 0.4,
                animation: captureArmed ? 'tap-capture-pulse 1.2s ease-in-out infinite' : undefined,
                flexShrink: 0,
              }}
            />
            {captureArmed ? 'Capture next — armed (click to cancel)' : 'Capture next request'}
            {captureMissed && !captureArmed && (
              <span style={{ marginLeft: 'auto', fontSize: 10, color: 'var(--warn)' }}>
                fired — hidden by host filter
              </span>
            )}
          </button>
        )}
        <div style={{ display: 'flex', gap: 6, alignItems: 'stretch' }}>
          <div style={{ flex: 1, position: 'relative' }}>
            <input
              ref={filterInputRef}
              type="text"
              placeholder="Filter — e.g. method:POST status:>=400 path:/api"
              value={filter}
              onChange={(e) => {
                onFilterChange(e.target.value)
                setCaret(e.currentTarget.selectionStart ?? e.currentTarget.value.length)
                setDismissedAt(null)
                setSuggestOpen(true)
              }}
              onFocus={() => { refreshCaret(); setSuggestOpen(true) }}
              onClick={refreshCaret}
              onKeyUp={(e) => {
                // Arrow keys move the caret — refresh, but don't reopen if
                // the user just dismissed via Esc.
                if (e.key.startsWith('Arrow') || e.key === 'Home' || e.key === 'End') refreshCaret()
              }}
              onBlur={() => {
                // Delay so a mousedown on a suggestion can register before
                // the popover unmounts.
                setTimeout(() => setSuggestOpen(false), 120)
              }}
              onKeyDown={(e) => {
                if (!showSuggestions) {
                  if (e.key === 'ArrowDown' && suggestion && suggestion.suggestions.length > 0) {
                    e.preventDefault()
                    setDismissedAt(null)
                    setSuggestOpen(true)
                    return
                  }
                  return
                }
                const items = suggestion!.suggestions
                if (e.key === 'ArrowDown') { e.preventDefault(); setSuggestIndex((i) => (i + 1) % items.length); return }
                if (e.key === 'ArrowUp') { e.preventDefault(); setSuggestIndex((i) => (i - 1 + items.length) % items.length); return }
                if (e.key === 'Enter' || e.key === 'Tab') {
                  e.preventDefault()
                  acceptSuggestion(items[suggestIndex])
                  return
                }
                if (e.key === 'Escape') { e.preventDefault(); setDismissedAt(filter); setSuggestOpen(false); return }
              }}
              autoComplete="off"
              spellCheck={false}
              style={{ width: '100%' }}
            />
            {showSuggestions && (
              <div
                role="listbox"
                style={{
                  position: 'absolute',
                  top: 'calc(100% + 2px)',
                  left: 0,
                  right: 0,
                  zIndex: 30,
                  background: 'var(--bg-raised)',
                  border: '1px solid var(--border)',
                  borderRadius: 4,
                  boxShadow: '0 6px 20px rgba(0, 0, 0, 0.18)',
                  maxHeight: 280,
                  overflowY: 'auto',
                  fontSize: 12,
                }}
              >
                {suggestion!.suggestions.map((s, i) => {
                  const active = i === suggestIndex
                  return (
                    <div
                      key={`${s.kind}:${s.label}:${i}`}
                      role="option"
                      aria-selected={active}
                      onMouseDown={(e) => { e.preventDefault(); acceptSuggestion(s) }}
                      onMouseEnter={() => setSuggestIndex(i)}
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 10,
                        padding: '4px 10px',
                        cursor: 'pointer',
                        background: active ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
                        borderLeft: active ? '2px solid var(--accent)' : '2px solid transparent',
                      }}
                    >
                      <span
                        style={{
                          fontFamily: 'SF Mono, Menlo, monospace',
                          color: active ? 'var(--accent)' : 'var(--text)',
                          fontWeight: s.kind === 'key' ? 600 : 500,
                        }}
                      >
                        {s.label}
                      </span>
                      {s.detail && (
                        <span style={{ color: 'var(--text-muted)', fontSize: 11, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {s.detail}
                        </span>
                      )}
                      <span
                        style={{
                          marginLeft: 'auto',
                          fontSize: 9.5,
                          textTransform: 'uppercase',
                          letterSpacing: '0.06em',
                          color: 'var(--text-muted)',
                          opacity: 0.7,
                          fontWeight: 600,
                        }}
                      >
                        {s.kind === 'observed' ? 'seen' : s.kind}
                      </span>
                    </div>
                  )
                })}
                <div
                  style={{
                    padding: '3px 10px',
                    borderTop: '1px solid var(--border)',
                    fontSize: 10,
                    color: 'var(--text-muted)',
                    background: 'var(--bg-input)',
                  }}
                >
                  ↑↓ navigate · Enter/Tab accept · Esc dismiss
                </div>
              </div>
            )}
          </div>
          <button
            type="button"
            onClick={() => {
              if (!filter.trim()) return
              setPendingName(matchedSavedName ?? '')
              setSavePromptOpen((v) => !v)
            }}
            disabled={!filter.trim()}
            title={
              matchedSavedName
                ? `Already saved as "${matchedSavedName}" — rename or update`
                : 'Save current filter as a chip'
            }
            style={{
              padding: '0 8px',
              fontSize: 12,
              background: matchedSavedName ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : undefined,
              color: matchedSavedName ? 'var(--accent)' : undefined,
              borderColor: matchedSavedName ? 'color-mix(in srgb, var(--accent) 35%, var(--border))' : undefined,
            }}
          >
            {matchedSavedName ? '★' : '+'}
          </button>
          <button
            type="button"
            onClick={() => setHelpOpen((v) => !v)}
            title="Filter syntax help"
            aria-expanded={helpOpen}
            style={{
              padding: '0 8px',
              fontSize: 12,
              background: helpOpen ? 'var(--bg-input)' : undefined,
            }}
          >
            ?
          </button>
        </div>
        {savePromptOpen && (
          <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
            <input
              ref={saveInputRef}
              type="text"
              placeholder="Name for this filter"
              value={pendingName}
              onChange={(e) => setPendingName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') { e.preventDefault(); commitSave() }
                if (e.key === 'Escape') { e.preventDefault(); setSavePromptOpen(false); setPendingName('') }
              }}
              style={{ flex: 1, fontSize: 12 }}
            />
            <button type="button" onClick={commitSave} disabled={!pendingName.trim()} style={{ fontSize: 12 }}>
              Save
            </button>
            <button
              type="button"
              onClick={() => { setSavePromptOpen(false); setPendingName('') }}
              style={{ fontSize: 12, background: 'transparent' }}
            >
              Cancel
            </button>
          </div>
        )}
        {helpOpen && (
          <div
            style={{
              border: '1px solid var(--border)',
              borderRadius: 4,
              background: 'var(--bg-input)',
              padding: '6px 8px',
              fontSize: 11,
              lineHeight: 1.5,
              maxHeight: 220,
              overflowY: 'auto',
            }}
          >
            <div style={{ fontWeight: 600, color: 'var(--text-muted)', marginBottom: 4 }}>
              Filter syntax — tokens are ANDed; prefix with <code>-</code> to negate
            </div>
            <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: 'SF Mono, Menlo, monospace' }}>
              <tbody>
                {FILTER_HELP.map((row) => (
                  <tr key={row.syntax}>
                    <td style={{ padding: '1px 8px 1px 0', whiteSpace: 'nowrap', color: 'var(--accent)', verticalAlign: 'top' }}>
                      {row.syntax}
                    </td>
                    <td style={{ padding: '1px 0', color: 'var(--text-muted)' }}>{row.meaning}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {filter && parsed.structured && (
          <div style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>
            {filtered.length} of {records.length} match
          </div>
        )}
      </div>
      <div style={{ flex: 1, overflowY: 'auto' }}>
        {filtered.length === 0 && (
          <div style={{ padding: '20px', textAlign: 'center', color: 'var(--text-muted)' }}>
            {records.length === 0 ? 'Waiting for requests…' : 'No matches.'}
          </div>
        )}
        {filtered.map((r) => (
          <button
            key={r.id}
            onClick={() => onSelect(r.id)}
            style={{
              display: 'block',
              width: '100%',
              textAlign: 'left',
              padding: '8px 10px',
              border: 'none',
              borderRadius: 0,
              borderBottom: '1px solid var(--border)',
              background: selectedId === r.id ? 'var(--bg-input)' : 'var(--bg-raised)',
              borderLeft: selectedId === r.id ? '3px solid var(--accent)' : '3px solid transparent',
              cursor: 'pointer',
            }}
          >
            <div style={{ display: 'flex', gap: '8px', alignItems: 'center', marginBottom: '2px' }}>
              <span style={{ color: methodColor(r.method), fontWeight: 600, minWidth: '48px' }}>{r.method}</span>
              <span style={{ color: statusColor(r.statusCode), fontWeight: 600, minWidth: '32px' }}>
                {r.statusCode || '—'}
              </span>
              {r.isStream && !r.isWebSocket && (
                <span
                  title={r.streamCompleted ? 'Stream finished' : 'Live stream'}
                  style={{
                    fontSize: '9.5px',
                    fontWeight: 600,
                    letterSpacing: '0.04em',
                    padding: '1px 5px',
                    borderRadius: 3,
                    color: r.streamCompleted ? 'var(--text-muted)' : 'var(--ok)',
                    border: `1px solid ${r.streamCompleted ? 'var(--border)' : 'var(--ok)'}`,
                  }}
                >
                  SSE{(r.sseEvents?.length ?? 0) > 0 && ` · ${r.sseEvents!.length}`}
                </span>
              )}
              {r.isWebSocket && (
                <span
                  className={r.streamCompleted ? undefined : 'tap-ws-live'}
                  title={r.streamCompleted ? 'WebSocket closed' : 'WebSocket open'}
                  style={{
                    fontSize: '9.5px',
                    fontWeight: 600,
                    letterSpacing: '0.04em',
                    padding: '1px 5px',
                    borderRadius: 3,
                    display: 'inline-flex',
                    alignItems: 'center',
                    color: r.streamCompleted ? 'var(--text-muted)' : 'var(--ok)',
                    border: `1px solid ${r.streamCompleted ? 'var(--border)' : 'var(--ok)'}`,
                  }}
                >
                  {!r.streamCompleted && <span className="tap-ws-dot live" />}
                  WS{(r.webSocketMessages?.length ?? 0) > 0 && ` · ${r.webSocketMessages!.length}`}
                </span>
              )}
              <span style={{ color: 'var(--text-muted)', marginLeft: 'auto', fontSize: '11px' }}>
                {r.durationMs}ms
              </span>
            </div>
            <div style={{ fontFamily: 'SF Mono, Menlo, monospace', fontSize: '11.5px', color: 'var(--text)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {r.path}
            </div>
            <div style={{ fontSize: '10.5px', color: 'var(--text-muted)', marginTop: '2px' }}>
              {r.host} · <RelativeTime iso={r.timestamp} />
            </div>
          </button>
        ))}
      </div>
    </div>
  )
}
