import type { TreeNode } from '../api/types'

/** Logical node displayed by the explorer.
 *
 *  Shape per `kind`:
 *  - `collection` — top-level directory under `.tap/collections/`. Carries optional
 *                   metadata via `_collection.md` (baseUrl, stages, defaults, vars).
 *  - `folder`     — pure grouping directory inside a collection. No metadata.
 *  - `request`    — leaf request file. Inherits everything from its containing collection.
 */
export type ExplorerKind = 'collection' | 'folder' | 'request'

export interface ExplorerNode {
  kind: ExplorerKind
  /** Workspace-relative path. For `collection`, the `collections/<slug>` directory; for
   *  `folder`/`request`, the file/dir path. */
  path: string
  name: string
  source?: TreeNode
  children: ExplorerNode[]
  /** For `collection` nodes: the collection slug (last segment of `collections/<slug>`). */
  slug?: string
}

/** Locate the `collections` directory in the tree. The workspace loader roots the
 *  tree at the workspace dir, so `collections` sits under a `.tap` wrapper node
 *  (path `.tap/collections`); older trees exposed it at the top level. Handle both. */
function findCollectionsDir(nodes: TreeNode[]): TreeNode | undefined {
  for (const n of nodes) {
    if (n.kind !== 'directory') continue
    const p = n.path.toLowerCase()
    if (p === 'collections' || p === '.tap/collections') return n
    if (p === '.tap') {
      const inner = findCollectionsDir(n.children)
      if (inner) return inner
    }
  }
  return undefined
}

/** Build the Requests view: one row per `collections/<slug>/` directory, with nested
 *  folders and requests below. The metadata file `_collection.md` is hidden — its
 *  display name surfaces on the collection node. */
export function buildRequestsView(tree: TreeNode[]): ExplorerNode[] {
  const collectionsRoot = findCollectionsDir(tree)
  if (!collectionsRoot) return []

  const collections: ExplorerNode[] = []
  for (const child of collectionsRoot.children) {
    if (child.kind !== 'directory') continue
    const slug = child.path.split('/').pop() ?? child.path
    let displayName = slug
    for (const f of child.children) {
      if (f.kind === 'collection') {
        displayName = f.name || slug
        break
      }
    }
    const node: ExplorerNode = {
      kind: 'collection',
      path: child.path,
      slug,
      name: displayName,
      source: child,
      children: [],
    }
    for (const grandchild of child.children) {
      const built = visitCollectionChild(grandchild)
      if (built) node.children.push(built)
    }
    node.children.sort(byKindThenName)
    collections.push(node)
  }
  collections.sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
  return collections
}

function visitCollectionChild(node: TreeNode): ExplorerNode | null {
  if (node.kind === 'directory') {
    const folder: ExplorerNode = {
      kind: 'folder', path: node.path, name: node.name, source: node, children: [],
    }
    for (const c of node.children) {
      const built = visitCollectionChild(c)
      if (built) folder.children.push(built)
    }
    folder.children.sort(byKindThenName)
    return folder
  }
  if (node.kind === 'request') {
    return { kind: 'request', path: node.path, name: node.name, source: node, children: [] }
  }
  return null
}

function byKindThenName(a: ExplorerNode, b: ExplorerNode): number {
  if (a.kind !== b.kind) return a.kind === 'folder' ? -1 : 1
  return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
}

/** Case-insensitive name filter that preserves ancestors of every match. */
export function filterExplorerTree(nodes: ExplorerNode[], query: string): ExplorerNode[] {
  const q = query.trim().toLowerCase()
  if (!q) return nodes
  const recurse = (n: ExplorerNode): ExplorerNode | null => {
    const selfHit = n.name.toLowerCase().includes(q) || n.path.toLowerCase().includes(q)
    const kept = n.children.map(recurse).filter((c): c is ExplorerNode => c !== null)
    if (!selfHit && kept.length === 0) return null
    return { ...n, children: kept }
  }
  return nodes.map(recurse).filter((n): n is ExplorerNode => n !== null)
}
