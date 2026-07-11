import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { VariableContext, VariableView } from '../api/types'
import { useTapStore } from '../store'

/**
 * Fetches the layered + merged variable view for an editor context. Re-runs whenever the
 * context changes OR the workspace generation ticks (FS save propagates). Components use
 * this to power autocomplete in `VariableInput` and to render the cascade in `VariablesPanel`.
 *
 * Pass `null` to suspend the fetch — useful when the editor doesn't have a context yet.
 */
export function useVariableView(context: VariableContext | null): VariableView | null {
  const generation = useTapStore((s) => s.generation)
  const [view, setView] = useState<VariableView | null>(null)

  // Serialize the context so we don't refetch on every reference-changed object.
  const key = context ? JSON.stringify(context) : null

  useEffect(() => {
    if (!key) { setView(null); return }
    let cancelled = false
    api.variablesView(JSON.parse(key) as VariableContext)
      .then((v) => { if (!cancelled) setView(v) })
      .catch(() => { if (!cancelled) setView(null) })
    return () => { cancelled = true }
  }, [key, generation])

  // Lookup-friendly map: name → Variable. Memoized so consumers don't rebuild it.
  return useMemo(() => view, [view])
}

/** Build a lookup map by name from a view's merged `result`. */
export function variableMap(view: VariableView | null): Map<string, VariableView['result'][number]> {
  const out = new Map<string, VariableView['result'][number]>()
  if (!view) return out
  for (const v of view.result) out.set(v.name, v)
  return out
}
