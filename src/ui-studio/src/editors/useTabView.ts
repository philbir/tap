import { useCallback } from 'react'
import { useTapStore } from '../store'

/**
 * `useState` for view state that belongs to the tab rather than to the editor's mount —
 * which sub-tab is open, a stage override, and anything else in that class.
 *
 * Only the active tab's editor is mounted, so plain `useState` resets every one of these on
 * a tab switch: you come back to the request you were reading and it has snapped from Body
 * back to Params, and from the stage you picked back to the collection default. Parking the
 * value in the store under the tab path fixes that, and closing the tab reclaims it along
 * with the tab's draft.
 *
 * `slot` names the value within the tab and must be stable — it is the key. `initial` is the
 * fallback until something is stored, so it may be computed per render.
 *
 * Like drafts, this is in-memory only: a stage override stays "for this session", it just no
 * longer treats a tab switch as the end of one.
 */
export function useTabView<T>(tabPath: string, slot: string, initial: T): [T, (next: T) => void] {
  const stored = useTapStore((s) => s.tabState[tabPath]?.view?.[slot])
  const setTabView = useTapStore((s) => s.setTabView)
  const set = useCallback((next: T) => setTabView(tabPath, slot, next), [tabPath, slot, setTabView])
  return [stored === undefined ? initial : (stored as T), set]
}
