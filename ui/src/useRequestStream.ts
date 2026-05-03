import { useEffect, useState } from 'react'
import type { RequestRecord } from './types'

export function useRequestStream() {
  const [records, setRecords] = useState<RequestRecord[]>([])
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    let cancelled = false
    const controller = new AbortController()

    fetch('/api/requests', { signal: controller.signal })
      .then((r) => (r.ok ? (r.json() as Promise<RequestRecord[]>) : Promise.reject(r.statusText)))
      .then((initial) => {
        if (!cancelled) setRecords(initial)
      })
      .catch((err) => {
        if (!cancelled && !(err instanceof DOMException && err.name === 'AbortError')) {
          console.warn('[inspector] initial fetch failed:', err)
        }
      })

    const es = new EventSource('/api/stream')
    es.onopen = () => setConnected(true)
    es.onerror = () => {
      // EventSource auto-reconnects on transient errors. Only mark disconnected
      // when the connection is fully CLOSED (readyState 2).
      if (es.readyState === EventSource.CLOSED) setConnected(false)
    }
    es.onmessage = (ev) => {
      setConnected(true)
      try {
        const record = JSON.parse(ev.data) as RequestRecord
        setRecords((prev) => [record, ...prev.filter((r) => r.id !== record.id)].slice(0, 500))
      } catch {
        /* ignore malformed */
      }
    }

    return () => {
      cancelled = true
      controller.abort()
      es.close()
    }
  }, [])

  const clear = async () => {
    await fetch('/api/requests', { method: 'DELETE' })
    setRecords([])
  }

  return { records, connected, clear }
}
