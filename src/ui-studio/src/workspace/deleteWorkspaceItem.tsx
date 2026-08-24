import { Text } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { api } from '../api/client'
import { useTapStore } from '../store'

/**
 * Deleting a workspace item, from wherever the user is looking at it — the sidebar trees,
 * the Testing list, an editor's Source tab. One module so the confirm wording, the API call,
 * and the tab clean-up can't drift between entry points.
 */

/** Every kind that can carry a Delete action. */
export type WorkspaceItemKind =
  | 'collection' | 'folder' | 'httpfile' | 'request' | 'auth' | 'env' | 'test' | 'flow'

/** How a kind reads in a menu item and in the confirm dialog that follows, so the raw slugs
 *  ('env', 'test') never reach the user. */
export const KIND_LABELS: Record<WorkspaceItemKind, string> = {
  collection: 'collection',
  folder: 'folder',
  // A .http row groups requests but is a single file — "Delete file", not "Delete httpfile".
  httpfile: 'file',
  request: 'request',
  auth: 'auth profile',
  env: 'environment',
  test: 'test set',
  flow: 'flow',
}

export interface DeleteTarget {
  kind: WorkspaceItemKind
  /** Workspace-relative path of the file or directory. */
  path: string
  /** Display name, shown in the confirm dialog. */
  name: string
  /** Collection slug — only for `kind: 'collection'`, which deletes through its own endpoint. */
  slug?: string
}

/** A collection and a grouping folder are directories; everything else is a single file —
 *  including `httpfile`, which shows up as an expandable row but IS one file. Routing on
 *  the kind here is what keeps every caller from having to know that. */
const isDirectory = (kind: WorkspaceItemKind) => kind === 'collection' || kind === 'folder'

/** Delete whatever the target names — the entry point every caller should use. */
export function confirmDelete(target: DeleteTarget) {
  return isDirectory(target.kind) ? confirmDeleteDirectory(target) : confirmDeleteFile(target)
}

/** Delete a single file: request / auth / env / test set / flow / `.http` file. */
function confirmDeleteFile({ kind, path, name }: DeleteTarget) {
  modals.openConfirmModal({
    title: <Text fw={600}>Delete {KIND_LABELS[kind]}</Text>,
    children: (
      <Text size="sm">
        Delete <Text component="span" fw={600}>{name}</Text>? This can't be undone.
      </Text>
    ),
    labels: { confirm: 'Delete', cancel: 'Cancel' },
    confirmProps: { color: 'red' },
    onConfirm: () => run(() => api.deleteFile(path), (t) => isSameFile(t.path, path)),
  })
}

/** Delete a collection or a grouping folder, and everything inside it. */
function confirmDeleteDirectory({ kind, path, name, slug }: DeleteTarget) {
  modals.openConfirmModal({
    title: <Text fw={600}>Delete {KIND_LABELS[kind]}</Text>,
    children: (
      <Text size="sm">
        Delete <Text component="span" fw={600}>{name}</Text> and everything inside it?
        This can't be undone.
      </Text>
    ),
    labels: { confirm: 'Delete', cancel: 'Cancel' },
    confirmProps: { color: 'red' },
    onConfirm: () => {
      const prefix = path.endsWith('/') ? path : path + '/'
      return run(
        () => kind === 'collection' && slug ? api.deleteCollection(slug) : api.deleteFolder(path),
        (t) => t.path === path || t.path.startsWith(prefix),
      )
    },
  })
}

/** A tab points at the deleted file when it names it — or, for a `.http` file, when it names
 *  one request inside it (`orders.http#get-order`). Both have to go. */
function isSameFile(tabPath: string, deleted: string): boolean {
  return tabPath === deleted || tabPath.startsWith(deleted + '#')
}

/** Delete, close the tabs the removed files were open in, then reload the workspace. */
async function run(remove: () => Promise<unknown>, orphaned: (tab: { path: string }) => boolean) {
  const store = useTapStore.getState()
  try {
    await remove()
    // Close first: an editor still mounted when the reload bumps `generation` refetches the
    // file that just went away, and shows the failure rather than quietly going with it.
    for (const t of store.tabs) {
      if (orphaned(t)) store.closeTab(t.path)
    }
    await store.reload()
  } catch (e) {
    notifications.show({
      color: 'red',
      title: 'Delete failed',
      message: e instanceof Error ? e.message : String(e),
    })
  }
}
