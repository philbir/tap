import { useEffect, useState } from 'react'
import type { RequestRecord, SseEventEnvelope, WebSocketMessageEnvelope } from './types'

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
      if (es.readyState === EventSource.CLOSED) setConnected(false)
    }
    es.onmessage = (ev) => {
      setConnected(true)
      try {
        const record = JSON.parse(ev.data) as RequestRecord
        setRecords((prev) => {
          const existing = prev.find((r) => r.id === record.id)
          const incomingSseLen = record.sseEvents?.length ?? 0
          const existingSseLen = existing?.sseEvents?.length ?? 0
          const incomingWsLen = record.webSocketMessages?.length ?? 0
          const existingWsLen = existing?.webSocketMessages?.length ?? 0
          // Snapshots may arrive out-of-order with named `sse`/`ws` events. Keep whichever
          // copy of each list is longer.
          const merged: RequestRecord = { ...record }
          if (existing && existingSseLen > incomingSseLen) merged.sseEvents = existing.sseEvents
          if (existing && existingWsLen > incomingWsLen) merged.webSocketMessages = existing.webSocketMessages
          return [merged, ...prev.filter((r) => r.id !== record.id)].slice(0, 500)
        })
      } catch {
        /* ignore malformed */
      }
    }

    es.addEventListener('sse', (ev) => {
      try {
        const env = JSON.parse((ev as MessageEvent).data) as SseEventEnvelope
        setRecords((prev) =>
          prev.map((r) => {
            if (r.id !== env.recordId) return r
            const events = r.sseEvents ?? []
            // Server-assigned sequence == final position; skip if already captured
            // via an out-of-order record snapshot.
            if (env.sequence < events.length) return r
            return { ...r, sseEvents: [...events, env.event] }
          }),
        )
      } catch {
        /* ignore */
      }
    })

    es.addEventListener('ws', (ev) => {
      try {
        const env = JSON.parse((ev as MessageEvent).data) as WebSocketMessageEnvelope
        setRecords((prev) =>
          prev.map((r) => {
            if (r.id !== env.recordId) return r
            const msgs = r.webSocketMessages ?? []
            if (env.sequence < msgs.length) return r
            return { ...r, webSocketMessages: [...msgs, env.message] }
          }),
        )
      } catch {
        /* ignore */
      }
    })

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
