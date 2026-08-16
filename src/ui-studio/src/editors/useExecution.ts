import { useCallback, useRef, useState } from 'react'
import { api } from '../api/client'
import type { ExecutionResult, RenderedRequest, RequestProtocol, RequestSpec, SseEvent, WsFrame } from '../api/types'

/** What to send. `spec` and `source` are the two flavours of "run the unsaved draft":
 *  a structured kind emits a spec, a `.http` file hands over its raw text. */
export interface SendTarget {
  /** Workspace-relative path — for a `.http` request, its `file.http#fragment` form. */
  path: string
  env: string | null
  stage: string | null
  /** Unsaved draft of a structured request. Omit to run the file on disk. */
  spec?: RequestSpec
  /** Unsaved raw text of the `.http` file `path` points into. Omit to run the file on disk. */
  source?: string
  /** Protocol to assume until the server's `meta` event says otherwise, so the response
   *  panel opens on the right tab instead of flashing the HTTP one first. */
  protocol?: RequestProtocol
}

/**
 * Drives one request through `/api/execute/stream` and assembles the progressive events into
 * a single {@link ExecutionResult} the {@link ResponsePanel} can render.
 *
 * Shared because there are now two places to send from — the RequestEditor and the `.http`
 * editor's per-request Send — and the assembly is not the trivial part it looks like: `meta`,
 * `body`, `sse`, `ws` and `done` each contribute a different slice of the result, frames have
 * to land as new array references to repaint while streaming, and an aborted send has to keep
 * whatever already arrived while stamping a duration the server will never send. Two copies of
 * that would drift on the first bug fix.
 */
export function useExecution() {
  const [rendered, setRendered] = useState<RenderedRequest | null>(null)
  const [execution, setExecution] = useState<ExecutionResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)
  // Set when the user aborts an in-flight Send — keeps the partial result on screen but tags
  // it so the panel shows a "cancelled" marker instead of pretending the response finished.
  const [stopped, setStopped] = useState(false)

  // Holds the in-flight stream controller so we can abort on Send-again / close / unmount.
  const ctrlRef = useRef<AbortController | null>(null)
  // Wall-clock start of the current Send — lets stop() fill in an approximate duration, since
  // the server's `done` event (which carries the real timing) never arrives on abort.
  const startedAtRef = useRef(0)

  const abort = useCallback(() => {
    ctrlRef.current?.abort()
    ctrlRef.current = null
  }, [])

  /** Drop the response entirely — panel closed, or the editor switched files. */
  const clear = useCallback(() => {
    abort()
    setRendered(null); setExecution(null); setError(null); setSending(false); setStopped(false)
  }, [abort])

  /** User-initiated cancel. Unlike {@link clear} this keeps the partial response visible. */
  const stop = useCallback(() => {
    if (!ctrlRef.current) return
    abort()
    const elapsed = startedAtRef.current ? Math.max(0, Date.now() - startedAtRef.current) : 0
    setExecution((cur) => (cur ? { ...cur, durationMs: cur.durationMs || elapsed } : cur))
    setStopped(true)
    setSending(false)
  }, [abort])

  const send = useCallback((target: SendTarget) => {
    abort()
    startedAtRef.current = Date.now()
    setSending(true); setError(null); setExecution(null); setStopped(false)

    // Built up as events flow in. The UI gets the latest snapshot on every event, so SSE/WS
    // frames appear in the panel as the upstream emits them rather than at the end.
    const snapshot: ExecutionResult = {
      status: 0, statusText: null, url: '', method: '',
      requestHeaders: {}, requestBody: null,
      responseHeaders: {}, responseBody: null,
      contentType: null, responseBodyBytes: 0, durationMs: 0,
      variablesUsed: [], stage: null, error: null,
      protocol: target.protocol ?? 'http',
    }
    const sseAccum: SseEvent[] = []
    const wsAccum: WsFrame[] = []

    ctrlRef.current = api.executeStream(target.path, target.env, target.stage, (ev) => {
      switch (ev.kind) {
        case 'meta':
          Object.assign(snapshot, {
            method: ev.payload.method,
            url: ev.payload.url,
            status: ev.payload.status,
            statusText: ev.payload.statusText,
            requestHeaders: ev.payload.requestHeaders,
            requestBody: ev.payload.requestBody,
            responseHeaders: ev.payload.responseHeaders,
            contentType: ev.payload.contentType,
            protocol: ev.payload.protocol,
            authStatus: ev.payload.authStatus,
          })
          setRendered({
            method: ev.payload.method,
            url: ev.payload.url,
            headers: ev.payload.requestHeaders,
            body: ev.payload.requestBody,
            variablesUsed: [],
            stage: null,
            protocol: ev.payload.protocol,
          })
          setExecution({ ...snapshot })
          break
        case 'body':
          snapshot.responseBody = ev.payload.responseBody
          snapshot.responseBodyBytes = ev.payload.responseBodyBytes
          setExecution({ ...snapshot })
          break
        case 'sse':
          sseAccum.push(ev.payload)
          // New array reference so React notices the change.
          setExecution({ ...snapshot, sseEvents: [...sseAccum] })
          break
        case 'ws':
          wsAccum.push(ev.payload)
          setExecution({ ...snapshot, wsFrames: [...wsAccum] })
          break
        case 'done':
          snapshot.durationMs = ev.payload.durationMs
          snapshot.responseBodyBytes = ev.payload.responseBodyBytes || snapshot.responseBodyBytes
          snapshot.variablesUsed = ev.payload.variablesUsed
          snapshot.stage = ev.payload.stage
          snapshot.assertions = ev.payload.assertions
          snapshot.assertSummary = ev.payload.assertSummary
          if (ev.payload.error) snapshot.error = ev.payload.error
          setExecution({
            ...snapshot,
            sseEvents: sseAccum.length > 0 ? [...sseAccum] : undefined,
            wsFrames: wsAccum.length > 0 ? [...wsAccum] : undefined,
          })
          setSending(false)
          ctrlRef.current = null
          break
        case 'error':
          snapshot.error = ev.payload.message
          setError(ev.payload.message)
          setExecution({ ...snapshot })
          setSending(false)
          ctrlRef.current = null
          break
      }
    }, undefined, target.spec, target.source)
  }, [abort])

  return { rendered, execution, error, sending, stopped, send, stop, clear, abort, setError }
}
