import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { TestStepResult } from '../api/types'
import { emptyRunState, type RunState } from './TestRunPanel'

/**
 * Owns a test-set / flow run: the SSE subscription, the accumulating result state, and the
 * abort handle. Shared by both editors — the run mechanics are identical, only what sits
 * above them differs.
 *
 * The stream is aborted on unmount and on every new run, so switching tabs mid-run doesn't
 * leave a request fanning out against someone's API with nothing listening.
 */
export function useTestRun(path: string) {
  const [state, setState] = useState<RunState>(emptyRunState)
  const ctrl = useRef<AbortController | null>(null)

  const stop = useCallback(() => {
    ctrl.current?.abort()
    ctrl.current = null
    setState((s) => (s.running ? { ...s, running: false } : s))
  }, [])

  useEffect(() => () => { ctrl.current?.abort() }, [])

  const run = useCallback((env: string | null, stage: string | null, only?: number | null) => {
    ctrl.current?.abort()
    setState({ ...emptyRunState(), running: true })

    ctrl.current = api.runTests(path, env, stage, (event) => {
      setState((s) => {
        switch (event.kind) {
          case 'start':
            return { ...s, plan: event.payload.entries }

          case 'step': {
            const { entryIndex, step } = event.payload
            const steps = new Map(s.steps)
            steps.set(entryIndex, upsertStep(steps.get(entryIndex) ?? [], step))
            return { ...s, steps }
          }

          case 'entry': {
            const entries = new Map(s.entries)
            entries.set(event.payload.index, event.payload)
            return { ...s, entries }
          }

          case 'done':
            return { ...s, result: event.payload, running: false }

          case 'error':
            return { ...s, error: event.payload.message, running: false }
        }
      })
    }, { only: only ?? null })
  }, [path])

  const clear = useCallback(() => {
    ctrl.current?.abort()
    ctrl.current = null
    setState(emptyRunState())
  }, [])

  /** True once a run has started — the editor mounts the results pane on this. */
  const active = state.running || state.plan.length > 0 || state.error !== null

  return { state, run, stop, clear, active }
}

/** Replace a step in place if it's already there, otherwise append. The server re-sends a
 *  step only when re-running one entry, but "append blindly" would duplicate rows if it
 *  ever did otherwise. */
function upsertStep(existing: TestStepResult[], step: TestStepResult): TestStepResult[] {
  const at = existing.findIndex((s) => s.index === step.index)
  if (at < 0) return [...existing, step]
  const next = existing.slice()
  next[at] = step
  return next
}
