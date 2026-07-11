import { useEffect, useMemo, useState } from 'react'
import { ApiError } from '../api/client'
import { useTapStore } from '../store'

/**
 * Shared spec-editor lifecycle. Every Mantine editor (`RequestEditor`, `AuthEditor`,
 * `EnvEditor`, `CollectionEditor`, `WorkspaceEditor`) repeats the same pattern: fetch
 * detail on `generation`, derive a `spec`, track a `savedSpec`, compute a dirty flag with
 * `JSON.stringify`, then expose `save` / `discard` / `error` / `saving`. This hook owns
 * that loop so editor code can focus on kind-specific fields and layout.
 *
 * Keying the effect on `key + generation` mirrors the previous behavior: the workspace
 * file-system watcher bumps `generation`, which forces a re-fetch.
 *
 * The hook does NOT compare deeply — `JSON.stringify` is the same equality check the editors
 * already used, and it's good enough for the typed spec shapes we hand in. Callers that need
 * a stable shape can normalize fields inside `specFromDetail`.
 */
export interface SpecEditor<TDetail, TSpec> {
  detail: TDetail | null
  spec: TSpec | null
  setSpec: React.Dispatch<React.SetStateAction<TSpec | null>>
  /** Patch a single field. Equivalent to `setSpec(cur => ({ ...cur, [key]: value }))`. */
  update: <K extends keyof TSpec>(key: K, value: TSpec[K]) => void
  dirty: boolean
  saving: boolean
  errorMessage: string | null
  save: () => Promise<void>
  discard: () => void
  /** Manually set the saved baseline — used by editors that handle a save outside the hook
   *  (for example, the create-on-save flow in `CollectionEditor`). */
  setSavedSpec: React.Dispatch<React.SetStateAction<TSpec | null>>
  /** Manually clear the error state (e.g. after the user types past it). */
  clearError: () => void
}

export interface UseSpecEditorArgs<TDetail, TSpec> {
  /** Stable identity for the resource being edited. The detail fetch and save both key
   *  on this — passing `null` keeps the editor in its loading state. */
  key: string | null
  fetchDetail: (key: string) => Promise<TDetail>
  specFromDetail: (detail: TDetail) => TSpec
  saveSpec: (spec: TSpec) => Promise<void>
}

export function useSpecEditor<TDetail, TSpec>({
  key,
  fetchDetail,
  specFromDetail,
  saveSpec,
}: UseSpecEditorArgs<TDetail, TSpec>): SpecEditor<TDetail, TSpec> {
  const generation = useTapStore((s) => s.generation)
  const [detail, setDetail] = useState<TDetail | null>(null)
  const [spec, setSpec] = useState<TSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<TSpec | null>(null)
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)

  useEffect(() => {
    if (key === null) {
      setDetail(null)
      setSpec(null)
      setSavedSpec(null)
      return
    }
    let cancelled = false
    setError(null)
    fetchDetail(key).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d)
      setSpec(initial)
      setSavedSpec(initial)
    }).catch((e: Error) => { if (!cancelled) setError(e.message) })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- specFromDetail/fetchDetail
    // are stable-by-convention; depending on them would refetch on every render. The
    // `generation` dep is the cross-cutting signal the file watcher uses.
  }, [key, generation])

  const dirty = useMemo(
    () => JSON.stringify(spec) !== JSON.stringify(savedSpec),
    [spec, savedSpec],
  )

  function update<K extends keyof TSpec>(k: K, value: TSpec[K]) {
    setSpec((cur) => (cur ? { ...cur, [k]: value } : cur))
  }

  async function save() {
    if (!spec) return
    setSaving(true); setError(null)
    try {
      await saveSpec(spec)
      setSavedSpec(spec)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  function discard() {
    setSpec(savedSpec)
    setError(null)
  }

  return {
    detail, spec, setSpec, update, dirty, saving, errorMessage,
    save, discard, setSavedSpec, clearError: () => setError(null),
  }
}
