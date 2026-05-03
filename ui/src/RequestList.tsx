import { useEffect, useState } from 'react'
import { formatDistanceToNowStrict } from 'date-fns'
import type { RequestRecord } from './types'

interface Props {
  records: RequestRecord[]
  selectedId: string | null
  filter: string
  onSelect: (id: string) => void
  onFilterChange: (value: string) => void
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

export function RequestList({ records, selectedId, filter, onSelect, onFilterChange }: Props) {
  const filtered = filter
    ? records.filter(r =>
        r.path.toLowerCase().includes(filter.toLowerCase()) ||
        r.method.toLowerCase().includes(filter.toLowerCase()) ||
        r.host.toLowerCase().includes(filter.toLowerCase()) ||
        String(r.statusCode).includes(filter),
      )
    : records

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', borderRight: '1px solid var(--border)' }}>
      <div style={{ padding: '8px', borderBottom: '1px solid var(--border)' }}>
        <input
          type="text"
          placeholder="Filter by path, method, host, status…"
          value={filter}
          onChange={(e) => onFilterChange(e.target.value)}
          style={{ width: '100%' }}
        />
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
