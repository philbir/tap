import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { useTapStore } from '../store'

/** Subscribes to the workspace tag dictionary (`/api/tags/dictionary`) — the union of
 *  curated tags declared in `tap.md` and tags currently in use on any entity. Refetches
 *  on every workspace `generation` bump so newly-added tags surface immediately in
 *  every editor's autocomplete. */
export function useTagDictionary(): string[] {
  const generation = useTapStore((s) => s.generation)
  const [tags, setTags] = useState<string[]>([])

  useEffect(() => {
    let cancelled = false
    api.tagDictionary()
      .then((rows) => { if (!cancelled) setTags(rows) })
      .catch(() => !cancelled && setTags([]))
    return () => { cancelled = true }
  }, [generation])

  return tags
}
