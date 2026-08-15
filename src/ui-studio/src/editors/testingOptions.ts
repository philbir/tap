import type { ComboboxItem, ComboboxItemGroup } from '@mantine/core'
import type { CollectionSummary, FlowSummary, RequestSummary } from '../api/types'
import { relativizeFrom } from './authOptions'

export { relativizeFrom }

/**
 * Group every request in the workspace by collection for a `<Select>`. A flow spans
 * collections by design, so unlike the auth picker there is no "this one first" ordering to
 * apply — the grouping is purely so a workspace with two hundred requests is navigable.
 *
 * Values are refs relative to `fromPath` (the flow or test-set file doing the referencing),
 * matching what the emitter writes to disk.
 */
export function requestSelectGroups(opts: {
  requests: RequestSummary[]
  collections: CollectionSummary[]
  fromPath: string
}): ComboboxItemGroup<ComboboxItem>[] {
  const { requests, collections, fromPath } = opts
  const nameOf = (slug: string) => collections.find((c) => c.slug === slug)?.name ?? slug

  const buckets = new Map<string, RequestSummary[]>()
  for (const r of requests) {
    const slug = collectionSlugOf(r.path) ?? ''
    const bucket = buckets.get(slug)
    if (bucket) bucket.push(r)
    else buckets.set(slug, [r])
  }

  const groups: ComboboxItemGroup<ComboboxItem>[] = []
  for (const slug of [...buckets.keys()].sort((a, b) => label(a).localeCompare(label(b)))) {
    groups.push({
      group: label(slug),
      items: buckets.get(slug)!
        .map((r) => ({ value: relativizeFrom(fromPath, r.path), label: displayName(r) }))
        .sort((x, y) => x.label.localeCompare(y.label, undefined, { sensitivity: 'base' })),
    })
  }
  return groups

  function label(slug: string) { return slug === '' ? 'Other' : nameOf(slug) }
}

/** Flow refs for a test set's flow picker, relative to the set file. */
export function flowSelectItems(flows: FlowSummary[], fromPath: string): ComboboxItem[] {
  return flows
    .map((f) => ({ value: relativizeFrom(fromPath, f.path), label: f.name }))
    .sort((x, y) => x.label.localeCompare(y.label, undefined, { sensitivity: 'base' }))
}

/**
 * The option value a picker should show for `ref`.
 *
 * A ref is matched by where it *points*, not by how it was spelled: `./checkout.flow.md` and
 * `checkout.flow.md` are the same file, and a picker that compares strings shows an empty
 * select over a perfectly good file — then silently rewrites the ref the moment the user
 * touches it. Falls back to the raw ref so a dangling one stays visible rather than vanishing.
 */
export function matchRefOption(
  fromPath: string,
  ref: string | null | undefined,
  options: readonly { value: string }[],
): string | null {
  if (!ref) return null
  const target = resolveRef(fromPath, ref)
  const hit = options.find((o) => resolveRef(fromPath, o.value) === target)
  return hit?.value ?? ref
}

/** Same, over grouped options. */
export function matchRefOptionGrouped(
  fromPath: string,
  ref: string | null | undefined,
  groups: readonly { items: readonly (ComboboxItem | string)[] }[],
): string | null {
  const flat = groups.flatMap((g) => g.items.map((i) => (typeof i === 'string' ? { value: i } : i)))
  return matchRefOption(fromPath, ref, flat)
}

/** Resolve a ref written relative to `fromPath` back to a workspace path, so a picker can
 *  match the option the file already names. Mirrors `LoadedWorkspace.Resolve`'s path half. */
export function resolveRef(fromPath: string, ref: string): string {
  if (ref.startsWith('id:')) return ref
  const base = fromPath.split('/').slice(0, -1)
  for (const part of ref.split('/')) {
    if (part === '..') base.pop()
    else if (part !== '.' && part !== '') base.push(part)
  }
  return base.join('/')
}

function collectionSlugOf(path: string): string | null {
  const parts = path.split('/')
  if (parts.length < 3 || parts[0].toLowerCase() !== 'collections') return null
  return parts[1]
}

function displayName(request: RequestSummary): string {
  return request.name || request.path.split('/').pop()?.replace(/\.req\.md$/i, '') || request.path
}
