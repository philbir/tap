import { useEffect } from 'react'
import { useTapStore } from '../store'

/**
 * Unsaved-edit survival, in two halves.
 *
 * Only the active tab's editor is mounted, so switching tabs unmounted the form and took
 * every in-progress edit with it — and a `generation` bump (the file watcher, or saving any
 * *other* file) re-seeded the same form straight from disk. Editors now hand their draft to
 * the store keyed by tab path, and pick it back up on the way in:
 *
 *   - `restoreDraft(tabPath, fresh)` where the editor seeds state from a fetch;
 *   - `usePublishDraft(tabPath, value, dirty, loaded)` to keep the stored draft in step.
 *
 * The store drops the draft when the tab closes, and `dirty` going false (a save, or
 * Discard) clears it — so a stored draft means exactly "this tab has unsaved changes",
 * which is what the tab strip's marker reads.
 */

/**
 * The pending draft for `tabPath`, or `fresh` when there is none. Reads the store without
 * subscribing: this is called from inside a fetch callback, not during render.
 */
export function restoreDraft<T>(tabPath: string, fresh: T): T {
  const draft = useTapStore.getState().tabState[tabPath]?.draft
  return draft === undefined ? fresh : (draft as T)
}

/**
 * Mirrors the editor's unsaved value into the store.
 *
 * `loaded` gates the very first write: while the initial fetch is in flight the editor has
 * nothing to say, and publishing its empty, not-dirty state would clear the draft it is
 * about to restore. It defaults to "the value has arrived", which is the shape every
 * spec editor already has; editors whose value isn't nullable pass their own flag.
 */
export function usePublishDraft<T>(
  tabPath: string,
  value: T,
  dirty: boolean,
  loaded: boolean = value != null,
): void {
  const setDraft = useTapStore((s) => s.setDraft)
  const clearDraft = useTapStore((s) => s.clearDraft)
  useEffect(() => {
    if (!tabPath || !loaded) return
    if (dirty) setDraft(tabPath, value)
    else clearDraft(tabPath)
  }, [tabPath, value, dirty, loaded, setDraft, clearDraft])
}
