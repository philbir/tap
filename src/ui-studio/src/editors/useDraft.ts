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
 * The pending draft for `tabPath`, or `fresh` when there is none — with any variables
 * declared into this file from outside its editor folded on top. Reads the store without
 * subscribing: this is called from inside a fetch callback, not during render.
 *
 * <p>The fold is what keeps a convert-to-variable write from being undone. That write lands on
 * disk while this editor may be holding a draft that predates it; the draft is the user's
 * unsaved work and must win over `fresh`, but it knows nothing of the new `vars:` entry, so
 * saving it would drop the entry. Applying the declaration on every seed settles it without
 * either side having to know about the other's timing.</p>
 */
export function restoreDraft<T>(tabPath: string, fresh: T): T {
  const state = useTapStore.getState()
  const draft = state.tabState[tabPath]?.draft
  const base = draft === undefined ? fresh : (draft as T)

  const declared = state.declaredVars[tabPath]
  if (!declared || base === null || typeof base !== 'object') return base

  // The flat `(vars, secrets)` pair every spec DTO carries — see `varRows.ts`.
  const spec = base as { vars?: Record<string, string>; secrets?: string[] }
  const vars = { ...spec.vars }
  const secrets = new Set(spec.secrets ?? [])
  for (const [name, entry] of Object.entries(declared)) {
    vars[name] = entry.value
    if (entry.secret) secrets.add(name)
    else secrets.delete(name)
  }
  return {
    ...spec,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.size > 0 ? [...secrets] : undefined,
  } as T
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
  const clearDeclaredVars = useTapStore((s) => s.clearDeclaredVars)
  useEffect(() => {
    if (!tabPath || !loaded) return
    if (dirty) { setDraft(tabPath, value); return }
    // Not dirty means saved or discarded, and either way the declaration is now in what the
    // editor holds — in the file it just wrote, or in the baseline it just reverted to. Left
    // behind, it would keep re-applying itself to a file the user may since have edited.
    clearDraft(tabPath)
    clearDeclaredVars(tabPath)
  }, [tabPath, value, dirty, loaded, setDraft, clearDraft, clearDeclaredVars])
}
