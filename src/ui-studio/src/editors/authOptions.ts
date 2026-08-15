import type { ComboboxItem, ComboboxItemGroup } from '@mantine/core'
import type { AuthSummary, CollectionSummary } from '../api/types'

/** Turn an absolute workspace path into one relative to the file that declares the ref.
 *  `auth:` / `defaultAuth:` refs are resolved relative to their own file, so a request at
 *  `collections/demo/x.req.md` pointing at `auth/y.auth.md` writes `../../auth/y.auth.md`,
 *  and one pointing at a sibling profile in its own collection writes just `y.auth.md`. */
export function relativizeFrom(from: string, to: string): string {
  const fromParts = from.split('/').slice(0, -1)
  const toParts = to.split('/')
  let i = 0
  while (i < fromParts.length && i < toParts.length - 1 && fromParts[i] === toParts[i]) i++
  return '../'.repeat(fromParts.length - i) + toParts.slice(i).join('/')
}

/** Slug of the collection owning a workspace path, or null when it isn't under a collection. */
export function collectionSlugOf(path: string): string | null {
  const parts = path.split('/')
  if (parts.length < 3 || parts[0].toLowerCase() !== 'collections') return null
  return parts[1]
}

/**
 * Group the workspace's auth profiles for a `<Select>`, so it's obvious which ones are
 * owned by the collection at hand (and can therefore use its variables and stages), which
 * are shared workspace-wide, and which belong to some *other* collection — legal to
 * reference, but they resolve against that collection's variables, not this one's.
 *
 * Values are refs relative to `fromPath` (the request or `_collection.md` doing the
 * referencing), matching what the emitter writes to disk.
 */
export function authSelectGroups(opts: {
  auths: AuthSummary[]
  collections: CollectionSummary[]
  fromPath: string
}): ComboboxItemGroup<ComboboxItem>[] {
  const { auths, collections, fromPath } = opts
  const ownSlug = collectionSlugOf(fromPath)
  const nameOf = (slug: string) => collections.find((c) => c.slug === slug)?.name ?? slug

  const own: AuthSummary[] = []
  const workspace: AuthSummary[] = []
  const others = new Map<string, AuthSummary[]>()
  for (const a of auths) {
    if (a.collection === null) workspace.push(a)
    else if (a.collection === ownSlug) own.push(a)
    else {
      const bucket = others.get(a.collection)
      if (bucket) bucket.push(a)
      else others.set(a.collection, [a])
    }
  }

  const toItems = (list: AuthSummary[]) => list
    .map((a) => ({ value: relativizeFrom(fromPath, a.path), label: a.name }))
    .sort((x, y) => x.label.localeCompare(y.label, undefined, { sensitivity: 'base' }))

  const groups: ComboboxItemGroup<ComboboxItem>[] = []
  if (own.length > 0) groups.push({ group: 'This collection', items: toItems(own) })
  if (workspace.length > 0) groups.push({ group: 'Workspace', items: toItems(workspace) })
  for (const slug of [...others.keys()].sort((a, b) => nameOf(a).localeCompare(nameOf(b)))) {
    groups.push({ group: nameOf(slug), items: toItems(others.get(slug)!) })
  }
  return groups
}
