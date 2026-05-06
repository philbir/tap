import type { SseTick } from './types'

interface SseHandlers {
  onOpen: () => void
  onEvent: (ev: SseTick) => void
  onError: (msg: string) => void
  onClose: () => void
}

/**
 * Fetch-based SSE consumer. Unlike `EventSource`, this works with relative paths
 * and lets us pass `signal` for cancellation. The parser is a minimal subset of
 * the WHATWG SSE spec — enough for tick / done events plus comments.
 */
export async function runSse(url: string, signal: AbortSignal, h: SseHandlers): Promise<void> {
  try {
    const res = await fetch(url, { signal, headers: { Accept: 'text/event-stream' } })
    if (!res.ok || !res.body) {
      h.onError(`HTTP ${res.status}`)
      h.onClose()
      return
    }
    h.onOpen()

    const reader = res.body.getReader()
    const decoder = new TextDecoder('utf-8')
    let buffer = ''
    let dataLines: string[] = []
    let eventName = 'message'
    let id: string | null = null

    const dispatch = () => {
      if (dataLines.length === 0 && id === null && eventName === 'message') return
      const data = dataLines.join('\n')
      h.onEvent({ event: eventName, data, id, receivedAt: new Date().toISOString() })
      dataLines = []
      eventName = 'message'
    }

    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })

      let nlIdx: number
      while ((nlIdx = buffer.indexOf('\n')) >= 0) {
        let line = buffer.slice(0, nlIdx)
        if (line.endsWith('\r')) line = line.slice(0, -1)
        buffer = buffer.slice(nlIdx + 1)

        if (line === '') {
          dispatch()
          continue
        }
        if (line.startsWith(':')) continue
        const colon = line.indexOf(':')
        const field = colon < 0 ? line : line.slice(0, colon)
        let value = colon < 0 ? '' : line.slice(colon + 1)
        if (value.startsWith(' ')) value = value.slice(1)

        switch (field) {
          case 'event': eventName = value; break
          case 'data': dataLines.push(value); break
          case 'id': id = value; break
          case 'retry': /* ignored */ break
        }
      }
    }
    dispatch()
    h.onClose()
  } catch (ex) {
    if ((ex as DOMException)?.name === 'AbortError') {
      h.onClose()
      return
    }
    h.onError(String(ex))
    h.onClose()
  }
}
