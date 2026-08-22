import { useCallback, useSyncExternalStore } from 'react'
import { api } from '../api/client'
import { useTapStore } from '../store'
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

/** Everything the response panel renders for one tab. */
export interface ExecutionState {
  rendered: RenderedRequest | null
  execution: ExecutionResult | null
  error: string | null
  sending: boolean
  /** Set when the user aborts an in-flight Send — keeps the partial result on screen but
   *  tags it so the panel shows a "cancelled" marker instead of pretending it finished. */
  stopped: boolean
  /** Which request produced what is on screen. A `.http` file holds several, so the panel
   *  has to name the one it is showing. */
  sentPath: string | null
}

const EMPTY: ExecutionState = {
  rendered: null, execution: null, error: null, sending: false, stopped: false, sentPath: null,
}

interface Entry {
  state: ExecutionState
  /** In-flight stream controller, so a re-Send / close / tab close can abort it. */
  ctrl: AbortController | null
  /** Wall-clock start of the current Send — lets stop() fill in an approximate duration,
   *  since the server's `done` event (which carries the real timing) never arrives on abort. */
  startedAt: number
}

/**
 * Responses live here, keyed by tab path, rather than in the editor's `useState`.
 *
 * Only the active tab's editor is mounted, so component state would take the response down
 * with it on every tab switch — and a long SSE or WebSocket stream would be cut off mid-flight
 * just because you glanced at another request. Keeping the state (and the AbortController)
 * outside React means a stream keeps filling its panel while you are elsewhere, and the panel
 * is exactly where you left it when you come back.
 *
 * Listeners are keyed separately from entries so {@link moveExecution} can carry a response to
 * a renamed tab without stranding the subscribers already bound to the new path.
 */
const entries = new Map<string, Entry>()
const listeners = new Map<string, Set<() => void>>()

function notify(key: string) {
  const subs = listeners.get(key)
  if (subs) for (const l of subs) l()
}

function entryFor(key: string): Entry {
  let entry = entries.get(key)
  if (!entry) {
    entry = { state: EMPTY, ctrl: null, startedAt: 0 }
    entries.set(key, entry)
  }
  return entry
}

function patch(key: string, next: Partial<ExecutionState>) {
  const entry = entryFor(key)
  entry.state = { ...entry.state, ...next }
  notify(key)
}

function subscribe(key: string, listener: () => void): () => void {
  let subs = listeners.get(key)
  if (!subs) { subs = new Set(); listeners.set(key, subs) }
  subs.add(listener)
  return () => {
    subs.delete(listener)
    if (subs.size === 0) listeners.delete(key)
  }
}

function abortFor(key: string) {
  const entry = entries.get(key)
  if (!entry) return
  entry.ctrl?.abort()
  entry.ctrl = null
}

/** Drop the response entirely — panel closed by the user. */
function clearFor(key: string) {
  abortFor(key)
  const entry = entries.get(key)
  if (!entry) return
  entry.state = EMPTY
  entry.startedAt = 0
  notify(key)
}

/** User-initiated cancel. Unlike {@link clearFor} this keeps the partial response visible. */
function stopFor(key: string) {
  const entry = entries.get(key)
  if (!entry?.ctrl) return
  abortFor(key)
  const elapsed = entry.startedAt ? Math.max(0, Date.now() - entry.startedAt) : 0
  const current = entry.state.execution
  patch(key, {
    execution: current ? { ...current, durationMs: current.durationMs || elapsed } : current,
    stopped: true,
    sending: false,
  })
}

/**
 * Carry a response to a tab that changed path. Saving a renamed request moves its tab, which
 * remounts the editor under the new path — without this the response it is showing would be
 * pruned along with the old key. Call it *before* `renameTab`, which is what triggers the prune.
 */
export function moveExecution(from: string, to: string) {
  if (from === to) return
  const entry = entries.get(from)
  if (!entry) return
  entries.get(to)?.ctrl?.abort()
  entries.delete(from)
  entries.set(to, entry)
  notify(from)
  notify(to)
}

// Entries outlive their editor by design, so something has to reclaim them: a tab that is
// gone can never show its response again, and a stream still running for it is pure waste.
// The tab list is the one signal that covers every close route — the X, middle-click, close
// others / all, and switching workspace.
useTapStore.subscribe((state, prev) => {
  if (state.tabs === prev.tabs) return
  const open = new Set(state.tabs.map((t) => t.path))
  for (const [key, entry] of entries) {
    if (open.has(key)) continue
    entry.ctrl?.abort()
    entries.delete(key)
  }
})

function sendFor(key: string, target: SendTarget) {
  abortFor(key)
  const entry = entryFor(key)
  entry.startedAt = Date.now()
  patch(key, { sending: true, error: null, execution: null, stopped: false, sentPath: target.path })

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

  entry.ctrl = api.executeStream(target.path, target.env, target.stage, (ev) => {
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
        patch(key, {
          rendered: {
            method: ev.payload.method,
            url: ev.payload.url,
            headers: ev.payload.requestHeaders,
            body: ev.payload.requestBody,
            variablesUsed: [],
            stage: null,
            protocol: ev.payload.protocol,
          },
          execution: { ...snapshot },
        })
        break
      case 'body':
        snapshot.responseBody = ev.payload.responseBody
        snapshot.responseBodyBytes = ev.payload.responseBodyBytes
        snapshot.responseBodyInlineBytes = ev.payload.responseBodyInlineBytes
        // Absent means the whole body rode inline — there is nothing to fetch later.
        snapshot.bodyId = ev.payload.bodyId ?? undefined
        snapshot.retainedBytes = ev.payload.retainedBytes
        patch(key, { execution: { ...snapshot } })
        break
      case 'sse':
        sseAccum.push(ev.payload)
        // New array reference so React notices the change.
        patch(key, { execution: { ...snapshot, sseEvents: [...sseAccum] } })
        break
      case 'ws':
        wsAccum.push(ev.payload)
        patch(key, { execution: { ...snapshot, wsFrames: [...wsAccum] } })
        break
      case 'done':
        snapshot.durationMs = ev.payload.durationMs
        snapshot.responseBodyBytes = ev.payload.responseBodyBytes || snapshot.responseBodyBytes
        snapshot.variablesUsed = ev.payload.variablesUsed
        snapshot.stage = ev.payload.stage
        snapshot.assertions = ev.payload.assertions
        snapshot.assertSummary = ev.payload.assertSummary
        if (ev.payload.error) snapshot.error = ev.payload.error
        entryFor(key).ctrl = null
        patch(key, {
          execution: {
            ...snapshot,
            sseEvents: sseAccum.length > 0 ? [...sseAccum] : undefined,
            wsFrames: wsAccum.length > 0 ? [...wsAccum] : undefined,
          },
          sending: false,
        })
        break
      case 'error':
        snapshot.error = ev.payload.message
        entryFor(key).ctrl = null
        patch(key, { error: ev.payload.message, execution: { ...snapshot }, sending: false })
        break
    }
  }, undefined, target.spec, target.source)
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
 *
 * `key` is the tab the response belongs to — see the registry above for why it doesn't live in
 * component state. Two tabs can stream at once and neither disturbs the other.
 */
export function useExecution(key: string) {
  const state = useSyncExternalStore(
    useCallback((listener: () => void) => subscribe(key, listener), [key]),
    useCallback(() => entries.get(key)?.state ?? EMPTY, [key]),
  )

  const send = useCallback((target: SendTarget) => sendFor(key, target), [key])
  const stop = useCallback(() => stopFor(key), [key])
  const clear = useCallback(() => clearFor(key), [key])
  const abort = useCallback(() => abortFor(key), [key])
  const setError = useCallback((message: string | null) => patch(key, { error: message }), [key])

  return { ...state, send, stop, clear, abort, setError }
}
