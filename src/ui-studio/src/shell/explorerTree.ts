import type { TreeNode } from '../api/types'

/** Logical node displayed by the explorer.
 *
 *  Shape per `kind`:
 *  - `collection` — top-level directory under `.tap/collections/`. Carries optional
 *                   metadata via `_collection.md` (baseUrl, stages, defaults, vars).
 *  - `folder`     — pure grouping directory inside a collection. No metadata.
 *  - `request`    — leaf request file. Inherits everything from its containing collection.
 *  - `auth`       — leaf auth profile owned by the collection it sits in. Resolves its
 *                   `{{var}}` refs against that collection's variables and stages.
 *                   Workspace-scoped profiles live under `auth/` and don't appear here.
 */
export type ExplorerKind = 'collection' | 'folder' | 'request' | 'auth'

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

/** Build the Requests view: one row per `collections/<slug>/` directory, with nested
 *  folders, auth profiles, and requests below. The metadata file `_collection.md` is
 *  hidden — its display name surfaces on the collection node. */
export function buildRequestsView(tree: TreeNode[]): ExplorerNode[] {
  const collectionsRoot = findCollectionsRoot(tree)
  if (!collectionsRoot) return []

  const collections: ExplorerNode[] = []
  for (const child of collectionsRoot.children) {
    if (child.kind !== 'directory') continue
    const node = collectionNode(child)
    for (const grandchild of child.children) {
      const built = visitCollectionChild(grandchild)
      if (built) node.children.push(built)
    }
    node.children.sort(byKindThenName)
    collections.push(node)
  }
  collections.sort(byName)
  return collections
}

/** Build the Auth view: every auth profile in the workspace. Workspace-scoped ones keep
 *  their `auth/` folder structure; collection-scoped ones are grouped under their
 *  collection's display name rather than the raw `collections/<slug>` nesting. Collections
 *  without any auth profile are omitted entirely. */
export function buildAuthView(tree: TreeNode[]): ExplorerNode[] {
  const collectionsRoot = findCollectionsRoot(tree)
  const workspaceScoped: ExplorerNode[] = []
  const collections: ExplorerNode[] = []

  for (const node of tree) {
    if (node === collectionsRoot) {
      for (const child of node.children) {
        if (child.kind !== 'directory') continue
        const pruned = pruneToAuth(child)
        if (!pruned) continue
        const group = collectionNode(child)
        group.children = pruned.children
        collections.push(group)
      }
      continue
    }
    const pruned = pruneToAuth(node)
    if (pruned) workspaceScoped.push(pruned)
  }

  workspaceScoped.sort(byKindThenName)
  collections.sort(byName)
  return [...workspaceScoped, ...collections]
}

const COLLECTIONS_ROOT = 'collections'

/** Basename of a collection's metadata file. A collection is addressed by its directory
 *  (`collections/<slug>`) everywhere in the UI — tabs, the editor, the API slug — while
 *  on disk the metadata lives in this file inside it. */
export const COLLECTION_FILE = '_collection.md'

/** `collections/<slug>/_collection.md` → `collections/<slug>`. */
export function collectionDirOf(filePath: string): string {
  const cut = filePath.lastIndexOf('/')
  return cut < 0 ? filePath : filePath.slice(0, cut)
}

/** `collections/<slug>` → `collections/<slug>/_collection.md`. */
export function collectionFileOf(dirPath: string): string {
  return `${dirPath}/${COLLECTION_FILE}`
}

function findCollectionsRoot(tree: TreeNode[]): TreeNode | undefined {
  return tree.find((n) => n.kind === 'directory' && n.path.toLowerCase() === COLLECTIONS_ROOT)
}

/** Turn a `collections/<slug>/` directory node into a collection node, taking its display
 *  name off the `_collection.md` child when there is one. */
function collectionNode(dir: TreeNode): ExplorerNode {
  const slug = dir.path.split('/').pop() ?? dir.path
  const meta = dir.children.find((c) => c.kind === 'collection')
  return {
    kind: 'collection',
    path: dir.path,
    slug,
    name: meta?.name || slug,
    source: dir,
    children: [],
  }
}

/** Keep only auth leaves and the directories that lead to one. Returns null for a subtree
 *  with no auth profile in it. */
function pruneToAuth(node: TreeNode): ExplorerNode | null {
  if (node.kind === 'auth') {
    return { kind: 'auth', path: node.path, name: node.name, source: node, children: [] }
  }
  if (node.kind !== 'directory') return null
  const kept: ExplorerNode[] = []
  for (const c of node.children) {
    const built = pruneToAuth(c)
    if (built) kept.push(built)
  }
  if (kept.length === 0) return null
  kept.sort(byKindThenName)
  return { kind: 'folder', path: node.path, name: node.name, source: node, children: kept }
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
  if (node.kind === 'request' || node.kind === 'auth') {
    return { kind: node.kind, path: node.path, name: node.name, source: node, children: [] }
  }
  return null
}

/** Sub-folders first, then the collection's own auth profiles, then requests — config
 *  before payload. Ties broken by display name. */
const KIND_ORDER: Record<ExplorerKind, number> = { collection: 0, folder: 0, auth: 1, request: 2 }

function byKindThenName(a: ExplorerNode, b: ExplorerNode): number {
  if (KIND_ORDER[a.kind] !== KIND_ORDER[b.kind]) return KIND_ORDER[a.kind] - KIND_ORDER[b.kind]
  return byName(a, b)
}

function byName(a: ExplorerNode, b: ExplorerNode): number {
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
