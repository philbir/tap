import {
  ActionIcon, Alert, Box, Center, Group, MultiSelect, Paper, ScrollArea, SegmentedControl, Stack,
  Text, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import {
  IconAlertCircle, IconBrandGit, IconCopy, IconEdit, IconFolderPlus, IconFolderShare, IconLayoutDashboard, IconLock,
  IconPlus, IconSearch, IconSend, IconTag, IconTrash, type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { TaggedItem, TreeNode, WorkspaceErrorDto } from '../api/types'
import { useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { DuplicateRequestDialog } from './DuplicateRequestDialog'
import { buildRequestsView, filterExplorerTree, type ExplorerNode } from './explorerTree'
import { FilesystemView } from './FilesystemView'
import { GitView } from './GitView'
import { NewFolderDialog } from './NewFolderDialog'
import { PierreFileTree } from './PierreFileTree'
import { TagsView } from './TagsView'
import { makeTapTreeIcons, TAP_ICONS, TAP_TREE_UNSAFE_CSS } from './treeIcons'

type Filter = 'requests' | 'auth' | 'tags' | 'fs' | 'git'

const BASE_FILTER_TABS: { value: Filter; label: string; icon: TablerIcon }[] = [
  { value: 'requests', label: 'Requests', icon: IconSend },
  { value: 'auth', label: 'Auth', icon: IconLock },
  { value: 'tags', label: 'Tags', icon: IconTag },
  { value: 'fs', label: 'Filesystem', icon: IconFolderShare },
]
const GIT_TAB = { value: 'git' as const, label: 'Git', icon: IconBrandGit }

interface Props {
  hasActiveWorkspace: boolean
  onOpenFile: (node: TreeNode) => void
  onCreateNew: () => void
}

const EMPTY_ERRORS: WorkspaceErrorDto[] = []

export function Sidebar({ hasActiveWorkspace, onOpenFile, onCreateNew }: Props) {
  const tree = useTapStore((s) => s.tree)
  const errors = useTapStore((s) => s.info?.errors ?? EMPTY_ERRORS)
  const activePath = useTapStore((s) => s.activeTab)
  const reload = useTapStore((s) => s.reload)
  const closeTab = useTapStore((s) => s.closeTab)
  const openTab = useTapStore((s) => s.openTab)
  const renameTab = useTapStore((s) => s.renameTab)
  const hasGit = useTapStore((s) =>
    s.knownWorkspaces.some((w) => w.isActive && w.git !== null))
  const filterTabs = useMemo(
    () => (hasGit ? [...BASE_FILTER_TABS, GIT_TAB] : BASE_FILTER_TABS),
    [hasGit],
  )

  const [filter, setFilter] = useState<Filter>('requests')
  // Snap back to a visible tab if the workspace stops having a git repo while git was selected.
  useEffect(() => {
    if (filter === 'git' && !hasGit) setFilter('requests')
  }, [filter, hasGit])
  const [search, setSearch] = useState('')
  const [selectedTags, setSelectedTags] = useState<string[]>([])
  const [tagItems, setTagItems] = useState<TaggedItem[]>([])
  const generation = useTapStore((s) => s.generation)
  const dictionary = useTagDictionary()

  useEffect(() => {
    let cancelled = false
    api.tags()
      .then((rows) => { if (!cancelled) setTagItems(rows) })
      .catch(() => !cancelled && setTagItems([]))
    return () => { cancelled = true }
  }, [generation])

  const filterOptions = useMemo(() => {
    const counts = new Map<string, number>()
    for (const r of tagItems) counts.set(r.tag, (counts.get(r.tag) ?? 0) + 1)
    return dictionary.map((tag) => {
      const c = counts.get(tag) ?? 0
      return { value: tag, label: c > 0 ? `${tag} (${c})` : `${tag} (0)` }
    })
  }, [dictionary, tagItems])

  const requestsView = useMemo(
    () => filterExplorerTree(buildRequestsView(tree), search),
    [tree, search],
  )
  // Build the display-only path list for the pierre tree (no `collections/` root, no
  // `.req.md` suffix) plus a map from display path → callback that opens the real
  // workspace file. Mirrored for the auth tab.
  const requestsLookup = useMemo(() => collectRequestRows(requestsView, onOpenFile), [requestsView, onOpenFile])
  const requestsPaths = requestsLookup.paths
  const requestsActivePath = useMemo(
    () => activePath ? requestsLookup.realToDisplay.get(activePath) ?? null : null,
    [activePath, requestsLookup.realToDisplay],
  )
  const requestsActivate = useMemo(
    () => (path: string) => requestsLookup.activators.get(path)?.(),
    [requestsLookup.activators],
  )
  const requestsIcons = useMemo(() => makeTapTreeIcons(requestsLookup.iconByBasename), [requestsLookup.iconByBasename])

  const authNodes = useMemo(() => filterByKind(tree, 'auth'), [tree])
  const filteredAuth = useMemo(
    () => (search ? filterTreeByQuery(authNodes, search.toLowerCase()) : authNodes),
    [authNodes, search],
  )
  const authLookup = useMemo(() => collectAuthRows(filteredAuth, onOpenFile), [filteredAuth, onOpenFile])
  const authPaths = authLookup.paths
  const authActivePath = useMemo(
    () => activePath ? authLookup.realToDisplay.get(activePath) ?? null : null,
    [activePath, authLookup.realToDisplay],
  )
  const authActivate = useMemo(
    () => (path: string) => authLookup.activators.get(path)?.(),
    [authLookup.activators],
  )
  const authIcons = useMemo(() => makeTapTreeIcons(authLookup.iconByBasename), [authLookup.iconByBasename])

  // New-folder dialog state. The parent directory is set by the row the user
  // right-clicked; the dialog only asks for the leaf name.
  const [newFolderState, setNewFolderState] = useState<{ open: boolean; parentDir: string }>({
    open: false, parentDir: '',
  })

  // Duplicate-request dialog state. The source row is captured when the user clicks
  // "Duplicate" in the context menu; the dialog only asks for the new name.
  const [duplicateState, setDuplicateState] = useState<{ open: boolean; source: { realPath: string; name: string } | null }>({
    open: false, source: null,
  })

  // Hand-rolled cursor-anchored context menu state. Pierre's own renderContextMenu
  // surface doesn't deliver clicks back to React reliably, so we suppress pierre's
  // menu and render our own from this state, positioned at the cursor.
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; row: RowContext } | null>(null)

  const confirmDeleteDir = useCallback((dir: DirContext) => {
    modals.openConfirmModal({
      title: <Text fw={600}>Delete {dir.kind}</Text>,
      children: (
        <Text size="sm">
          Delete <Text component="span" fw={600}>{dir.name}</Text> and everything inside it?
          This can't be undone.
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: async () => {
        try {
          if (dir.kind === 'collection' && dir.slug) await api.deleteCollection(dir.slug)
          else await api.deleteFolder(dir.realPath)
          await reload()
          // Close any tabs that referenced files under the removed directory.
          const prefix = dir.realPath.endsWith('/') ? dir.realPath : dir.realPath + '/'
          for (const t of useTapStore.getState().tabs) {
            if (t.path === dir.realPath || t.path.startsWith(prefix)) closeTab(t.path)
          }
        } catch (e) {
          notifications.show({
            color: 'red',
            title: 'Delete failed',
            message: e instanceof Error ? e.message : String(e),
          })
        }
      },
    })
  }, [reload, closeTab])

  const confirmDeleteFile = useCallback((file: FileContext) => {
    modals.openConfirmModal({
      title: <Text fw={600}>Delete {file.kind}</Text>,
      children: (
        <Text size="sm">
          Delete <Text component="span" fw={600}>{file.name}</Text>? This can't be undone.
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: async () => {
        try {
          await api.deleteFile(file.realPath)
          await reload()
          for (const t of useTapStore.getState().tabs) {
            if (t.path === file.realPath) closeTab(t.path)
          }
        } catch (e) {
          notifications.show({
            color: 'red',
            title: 'Delete failed',
            message: e instanceof Error ? e.message : String(e),
          })
        }
      },
    })
  }, [reload, closeTab])

  /** Build an `onContextMenuRequest` handler for one tree. Resolves the row's
   *  display path back to either a directory or file context and opens our own
   *  cursor-anchored menu. */
  const makeContextMenuHandler = useCallback((lookup: TreeLookup) => {
    return (path: string, kind: 'directory' | 'file', event: { clientX: number; clientY: number }) => {
      if (kind === 'directory') {
        const dir = lookup.dirsByDisplayPath.get(path)
        if (!dir) return
        setCtxMenu({ x: event.clientX, y: event.clientY, row: { row: 'dir', ...dir } })
      } else {
        const file = lookup.filesByDisplayPath.get(path)
        if (!file) return
        setCtxMenu({ x: event.clientX, y: event.clientY, row: { row: 'file', ...file } })
      }
    }
  }, [])

  const requestsContextMenu = useMemo(() => makeContextMenuHandler(requestsLookup), [makeContextMenuHandler, requestsLookup])
  const authContextMenu = useMemo(() => makeContextMenuHandler(authLookup), [makeContextMenuHandler, authLookup])
  const editCollection = useCallback((dir: DirContext) => {
    if (dir.kind !== 'collection') return
    openTab({ path: dir.realPath, kind: 'collection', label: dir.name })
  }, [openTab])

  // Drag-and-drop for the requests tree: the pierre tree has already moved the rows
  // visually by the time onDrop fires, so this handler just persists each move and
  // reloads — a reload also reverts the optimistic state if the server rejects it.
  const moveRequests = useCallback(async (draggedPaths: readonly string[], targetDir: string | null) => {
    const dir = targetDir ? requestsLookup.dirsByDisplayPath.get(targetDir) : undefined
    try {
      if (!dir) return
      for (const dropped of draggedPaths) {
        const file = requestsLookup.filesByDisplayPath.get(dropped)
        if (!file) continue
        const toReal = `${dir.realPath}/${file.realPath.split('/').pop()!}`
        if (toReal === file.realPath) continue
        await api.moveItem(file.realPath, toReal)
        // Keep an open tab pointing at the moved file alive under its new path.
        renameTab(file.realPath, toReal, file.name)
      }
    } catch (e) {
      notifications.show({
        color: 'red',
        title: 'Move failed',
        message: e instanceof Error ? e.message : String(e),
      })
    } finally {
      await reload()
    }
  }, [requestsLookup, reload, renameTab])

  const requestsDragAndDrop = useMemo(() => ({
    // Only request leaves are draggable — collections/folders stay put for now.
    canDrag: (paths: readonly string[]) => paths.every((p) => requestsLookup.filesByDisplayPath.has(p)),
    // Requests must land inside a collection or folder, never at the tree root
    // (that would put the file directly under `collections/`).
    canDrop: (_paths: readonly string[], targetDir: string | null) =>
      targetDir !== null && requestsLookup.dirsByDisplayPath.has(targetDir),
    onDrop: moveRequests,
    onError: (message: string) => notifications.show({ color: 'red', title: 'Move failed', message }),
  }), [requestsLookup, moveRequests])

  if (!hasActiveWorkspace) {
    return (
      <Center h="100%" px="md">
        <Stack align="center" gap="xs" maw={220} ta="center">
          <IconLayoutDashboard size={28} stroke={1.5} color="var(--mantine-color-dimmed)" />
          <Text fw={600} size="sm">No workspace selected</Text>
          <Text size="xs" c="dimmed">
            Pick or add one from the switcher in the top-left to start creating collections and auth profiles.
          </Text>
        </Stack>
      </Center>
    )
  }


  return (
    <Stack gap={0} h="100%">
      <Box p="sm" pb={0}>
        <SegmentedControl
          fullWidth
          size="xs"
          value={filter}
          onChange={(v) => setFilter(v as Filter)}
          data={filterTabs.map((t) => {
            const TabIcon = t.icon
            return {
              value: t.value,
              label: (
                <Tooltip label={t.label} withArrow openDelay={300}>
                  <Box style={{ display: 'flex', justifyContent: 'center', padding: '2px 0' }} aria-label={t.label}>
                    <TabIcon size={15} />
                  </Box>
                </Tooltip>
              ),
            }
          })}
        />
      </Box>

      {filter === 'git' ? (
        <Box style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          <GitView />
        </Box>
      ) : (
      <>
      <Group gap="xs" p="sm" wrap="nowrap">
        {filter === 'tags' ? (
          <MultiSelect
            flex={1}
            data={filterOptions}
            value={selectedTags}
            onChange={setSelectedTags}
            placeholder={selectedTags.length === 0 ? 'Filter by tag…' : ''}
            leftSection={<IconTag size={14} />}
            clearable
            searchable
            nothingFoundMessage="No tags"
            comboboxProps={{ withinPortal: true }}
          />
        ) : (
          <TextInput
            flex={1}
            placeholder="Search files…"
            value={search}
            onChange={(e) => setSearch(e.currentTarget.value)}
            leftSection={<IconSearch size={14} />}
          />
        )}
        <ActionIcon size="lg" variant="filled" onClick={onCreateNew} aria-label="Create new" title="Create new">
          <IconPlus size={16} />
        </ActionIcon>
      </Group>

      {errors.length > 0 && (
        <Box px="sm" pb="xs">
          <Alert variant="light" color="red" icon={<IconAlertCircle size={14} />} p="xs">
            <Stack gap={2}>
              {errors.slice(0, 5).map((e, i) => (
                <Text key={i} size="xs" ff="var(--mono)" title={e.message} truncate>
                  <Text component="span" fw={600}>{e.code}</Text> · {e.path ?? '(no path)'}
                </Text>
              ))}
              {errors.length > 5 && <Text size="xs" c="dimmed">+{errors.length - 5} more</Text>}
            </Stack>
          </Alert>
        </Box>
      )}

      {filter === 'requests' && (
        requestsPaths.length === 0
          ? <EmptyState kind="requests" />
          : <PierreFileTree
              paths={requestsPaths}
              selectedPath={requestsActivePath}
              initialExpansion="open"
              icons={requestsIcons}
              unsafeCSS={REQUEST_TREE_UNSAFE_CSS}
              fillParent
              onActivateFile={requestsActivate}
              onContextMenuRequest={requestsContextMenu}
              dragAndDrop={requestsDragAndDrop}
            />
      )}

      {filter === 'auth' && (
        authPaths.length === 0
          ? <EmptyState kind="auth" />
          : <PierreFileTree
              paths={authPaths}
              selectedPath={authActivePath}
              initialExpansion="open"
              icons={authIcons}
              unsafeCSS={TAP_TREE_UNSAFE_CSS}
              fillParent
              onActivateFile={authActivate}
              onContextMenuRequest={authContextMenu}
            />
      )}

      {filter === 'fs' && (
        <FilesystemView tree={tree} search={search} activePath={activePath} onOpenFile={onOpenFile} />
      )}

      {filter === 'tags' && (
        <ScrollArea flex={1} type="hover" scrollbarSize={8}>
          <Box pb="md">
            <TagsView
              items={tagItems}
              selectedTags={selectedTags}
              activePath={activePath}
              onOpenFile={onOpenFile}
            />
          </Box>
        </ScrollArea>
      )}
      </>
      )}

      <NewFolderDialog
        open={newFolderState.open}
        onOpenChange={(open) => setNewFolderState((s) => ({ ...s, open }))}
        parent={newFolderState.parentDir}
      />

      <FloatingCtxMenu
        ctx={ctxMenu}
        onClose={() => setCtxMenu(null)}
        onNewFolder={(dir) => { setCtxMenu(null); setNewFolderState({ open: true, parentDir: dir.realPath }) }}
        onDeleteDir={(dir) => { setCtxMenu(null); confirmDeleteDir(dir) }}
        onDeleteFile={(file) => { setCtxMenu(null); confirmDeleteFile(file) }}
        onDuplicateFile={(file) => { setCtxMenu(null); setDuplicateState({ open: true, source: { realPath: file.realPath, name: file.name } }) }}
        onEditCollection={(dir) => { setCtxMenu(null); editCollection(dir) }}
      />

      <DuplicateRequestDialog
        open={duplicateState.open}
        onOpenChange={(open) => setDuplicateState((s) => ({ ...s, open }))}
        source={duplicateState.source}
        onCreated={(path) => openTab({ path, kind: 'request', label: path.split('/').pop()?.replace(/\.req\.md$/, '') ?? path })}
      />
    </Stack>
  )
}

/** Cursor-anchored floating context menu — handles its own outside-click dismissal
 *  and ESC. Rendered in a portal-style `position: fixed` so it overlays the sidebar.
 *  Item set depends on whether the row is a directory or a file. */
function FloatingCtxMenu({
  ctx, onClose, onNewFolder, onDeleteDir, onDeleteFile, onDuplicateFile, onEditCollection,
}: {
  ctx: { x: number; y: number; row: RowContext } | null
  onClose: () => void
  onNewFolder: (dir: DirContext) => void
  onDeleteDir: (dir: DirContext) => void
  onDeleteFile: (file: FileContext) => void
  onDuplicateFile: (file: FileContext) => void
  onEditCollection: (dir: DirContext) => void
}) {
  useEffect(() => {
    if (!ctx) return
    const onDown = (e: MouseEvent) => {
      if (e.target instanceof Element && e.target.closest('[data-tap-ctx-menu]')) return
      onClose()
    }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [ctx, onClose])

  if (!ctx) return null
  const isDir = ctx.row.row === 'dir'
  return (
    <Paper
      data-tap-ctx-menu
      shadow="md"
      radius="sm"
      p={4}
      miw={180}
      style={{
        position: 'fixed',
        left: ctx.x,
        top: ctx.y,
        zIndex: 9999,
        background: 'var(--mantine-color-body)',
        border: '1px solid var(--mantine-color-default-border)',
      }}
    >
      <Stack gap={2}>
        {isDir && (
          (ctx.row as DirContext).kind === 'collection' && (
            <UnstyledButton
              px={10} py={6}
              className="tap-tree-row"
              style={{ fontSize: 13, borderRadius: 4 }}
              onClick={() => onEditCollection(ctx.row as DirContext)}
            >
              <Group gap={8} wrap="nowrap"><IconEdit size={14} />Edit collection…</Group>
            </UnstyledButton>
          )
        )}
        {isDir && (
          <UnstyledButton
            px={10} py={6}
            className="tap-tree-row"
            style={{ fontSize: 13, borderRadius: 4 }}
            onClick={() => onNewFolder(ctx.row as DirContext)}
          >
            <Group gap={8} wrap="nowrap"><IconFolderPlus size={14} />New folder…</Group>
          </UnstyledButton>
        )}
        {!isDir && (ctx.row as FileContext).kind === 'request' && (
          <UnstyledButton
            px={10} py={6}
            className="tap-tree-row"
            style={{ fontSize: 13, borderRadius: 4 }}
            onClick={() => onDuplicateFile(ctx.row as FileContext)}
          >
            <Group gap={8} wrap="nowrap"><IconCopy size={14} />Duplicate…</Group>
          </UnstyledButton>
        )}
        <UnstyledButton
          px={10} py={6}
          className="tap-tree-row"
          style={{ fontSize: 13, borderRadius: 4, color: 'var(--mantine-color-red-6)' }}
          onClick={() => {
            if (ctx.row.row === 'dir') onDeleteDir(ctx.row)
            else onDeleteFile(ctx.row)
          }}
        >
          <Group gap={8} wrap="nowrap"><IconTrash size={14} />Delete {ctx.row.kind}</Group>
        </UnstyledButton>
      </Stack>
    </Paper>
  )
}


type RowKind = 'collection' | 'folder' | 'request' | 'auth' | 'env'

interface DirContext {
  kind: 'collection' | 'folder'
  /** Real workspace path of the directory (for `api.createFolder` / `api.deleteFolder`). */
  realPath: string
  /** Display name shown in confirm dialogs. */
  name: string
  /** Collection slug (only set when `kind === 'collection'`). */
  slug?: string
}

interface FileContext {
  kind: 'request' | 'auth' | 'env'
  /** Real workspace path of the file (used for `api.deleteFile`). */
  realPath: string
  /** Display name (basename without the kind suffix). */
  name: string
}

type RowContext = ({ row: 'dir' } & DirContext) | ({ row: 'file' } & FileContext)

interface TreeLookup {
  /** Display paths fed to the pierre tree (the segments the user sees). */
  paths: string[]
  /** displayPath → real workspace path. Used to mirror the active tab's selection. */
  realToDisplay: Map<string, string>
  /** displayPath → callback that opens the underlying workspace file. */
  activators: Map<string, () => void>
  /** Per-basename icon overrides. We strip the kind suffix off the display path, so
   *  pierre's extension-based icon mapping can't tell req from auth from env any more.
   *  Instead we hand it an explicit `byFileName` map at config build time. */
  iconByBasename: Record<string, typeof TAP_ICONS[keyof typeof TAP_ICONS]>
  /** displayPath → kind for context-menu dispatch. Files use the underlying TreeNode
   *  kind ('request' / 'auth'); directories surface as 'collection' or 'folder' so the
   *  menu can route deletes to the right API. */
  kindByDisplayPath: Map<string, RowKind>
  /** displayPath → directory metadata for collections + folders (real path, name,
   *  optional slug). Used to wire the menu actions to the right API call. */
  dirsByDisplayPath: Map<string, DirContext>
  /** displayPath → file metadata for request / auth / env rows. */
  filesByDisplayPath: Map<string, FileContext>
}

/** Walk the requests explorer view into the display-path list the pierre tree wants,
 *  plus activate callbacks keyed on display path. The pierre tree derives each row's
 *  label from its display-path basename, so we build the display path hierarchically
 *  from each node's *spec name* (collection / request name) rather than its filename —
 *  rows render as "Budgets - List" instead of "bugets-lists". Pure grouping folders
 *  (no spec) fall back to their directory name. Sibling display-name collisions are
 *  disambiguated with a numeric suffix so every display path stays unique.
 *
 *  Collection metadata rows aren't emitted as separate leaves — the collection IS its
 *  enclosing folder. */
function collectRequestRows(nodes: ExplorerNode[], onOpenFile: (n: TreeNode) => void): TreeLookup {
  const lookup = newTreeLookup()
  const used = new Set<string>()

  const visit = (node: ExplorerNode, parentDisplay: string) => {
    const fallback = leafBasename(node.path, REQUEST_TREE_PREFIX)
    const display = uniqueDisplayPath(parentDisplay, displaySegment(node.name, fallback), used)
    if (node.kind === 'collection') {
      lookup.paths.push(dirPath(display))
      lookup.realToDisplay.set(node.path, dirPath(display))
      lookup.kindByDisplayPath.set(display, 'collection')
      lookup.dirsByDisplayPath.set(display, {
        kind: 'collection', realPath: node.path, name: node.name, slug: node.slug,
      })
    } else if (node.kind === 'folder') {
      lookup.paths.push(dirPath(display))
      lookup.realToDisplay.set(node.path, dirPath(display))
      lookup.kindByDisplayPath.set(display, 'folder')
      lookup.dirsByDisplayPath.set(display, { kind: 'folder', realPath: node.path, name: node.name })
    } else if (node.kind === 'request' && node.source) {
      lookup.paths.push(display)
      lookup.realToDisplay.set(node.path, display)
      lookup.kindByDisplayPath.set(display, 'request')
      const src = node.source
      lookup.activators.set(display, () => onOpenFile(src))
      lookup.filesByDisplayPath.set(display, { kind: 'request', realPath: node.path, name: node.name })
      lookup.iconByBasename[display.split('/').pop()!] = TAP_ICONS.send
      return // requests are leaves
    }
    for (const c of node.children) visit(c, display)
  }

  for (const root of nodes) visit(root, '')
  return lookup
}

/** Same shape as collectRequestRows but for auth profiles — these don't sit under a
 *  collections/ root. Auth rows render under their spec name; grouping folders keep
 *  their directory name. */
function collectAuthRows(nodes: TreeNode[], onOpenFile: (n: TreeNode) => void): TreeLookup {
  const lookup = newTreeLookup()
  const used = new Set<string>()

  const visit = (n: TreeNode, parentDisplay: string) => {
    if (n.kind === 'directory') {
      const display = uniqueDisplayPath(parentDisplay, displaySegment(n.name, leafBasename(n.path, null)), used)
      lookup.kindByDisplayPath.set(display, 'folder')
      lookup.dirsByDisplayPath.set(display, { kind: 'folder', realPath: n.path, name: n.name })
      for (const c of n.children) visit(c, display)
      return
    }
    if (n.kind !== 'auth') return
    const display = uniqueDisplayPath(parentDisplay, displaySegment(n.name, leafBasename(n.path, null)), used)
    lookup.paths.push(display)
    lookup.realToDisplay.set(n.path, display)
    lookup.kindByDisplayPath.set(display, 'auth')
    lookup.activators.set(display, () => onOpenFile(n))
    lookup.filesByDisplayPath.set(display, { kind: 'auth', realPath: n.path, name: n.name })
    lookup.iconByBasename[display.split('/').pop()!] = TAP_ICONS.lock
  }

  for (const root of nodes) visit(root, '')
  return lookup
}

function newTreeLookup(): TreeLookup {
  return {
    paths: [],
    realToDisplay: new Map<string, string>(),
    activators: new Map<string, () => void>(),
    iconByBasename: {},
    kindByDisplayPath: new Map<string, RowKind>(),
    dirsByDisplayPath: new Map<string, DirContext>(),
    filesByDisplayPath: new Map<string, FileContext>(),
  }
}

/** A node's spec name as a single path segment: '/' is illegal inside a segment so we
 *  fold it to '-', and we fall back to the on-disk basename when the spec name is blank. */
function displaySegment(name: string, fallback: string): string {
  const seg = (name ?? '').replace(/[/\\]+/g, '-').trim()
  return seg.length > 0 ? seg : fallback
}

/** Join `segment` under `parentDisplay`, suffixing " (n)" until the full path is unique
 *  (case-insensitively) so the pierre tree never collapses two rows into one. */
function uniqueDisplayPath(parentDisplay: string, segment: string, used: Set<string>): string {
  const base = parentDisplay ? `${parentDisplay}/${segment}` : segment
  let candidate = base
  let i = 2
  while (used.has(candidate.toLowerCase())) candidate = `${base} (${i++})`
  used.add(candidate.toLowerCase())
  return candidate
}

const REQUEST_TREE_PREFIX = '.tap/collections/'
const REQUEST_TREE_UNSAFE_CSS = `${TAP_TREE_UNSAFE_CSS}
  [data-item-type="folder"][aria-level="1"] [data-item-section="icon"]::after,
  [data-item-type="folder"][data-item-path]:not([data-item-path*="/"]) [data-item-section="icon"]::after {
    background-color: var(--mantine-color-grape-6);
  }
`
/** The on-disk basename for a node, used as the fallback display segment when a node has
 *  no spec name. Strips the leading prefix (if present) and the trailing Tap kind
 *  extension so the row reads as a plain filename. */
function leafBasename(realPath: string, stripPrefix: string | null): string {
  let p = realPath
  if (stripPrefix && p.startsWith(stripPrefix)) p = p.slice(stripPrefix.length)
  // Strip `.req.md` / `.auth.md` / `.env.md` / a bare `.md` — keep other extensions intact
  // so non-tap files (e.g. uploads under `.files/`) still read naturally.
  return p.replace(/\.(req|auth|env)\.md$/i, '').split('/').pop() ?? p
}

function dirPath(path: string): string {
  return path.endsWith('/') ? path : `${path}/`
}

function EmptyState({ kind }: { kind: 'requests' | 'auth' }) {
  const message = kind === 'requests'
    ? 'No collections yet — create one with +.'
    : 'No auth profiles yet — create one with +.'
  return (
    <Text size="xs" c="dimmed" ta="center" py="xl" px="md">{message}</Text>
  )
}

function filterByKind(nodes: TreeNode[], kind: TreeNode['kind']): TreeNode[] {
  const recurse = (n: TreeNode): TreeNode | null => {
    if (n.kind === kind) return n
    if (n.kind !== 'directory') return null
    const kids = n.children.map(recurse).filter((x): x is TreeNode => x !== null)
    return kids.length > 0 ? { ...n, children: kids } : null
  }
  return nodes.map(recurse).filter((x): x is TreeNode => x !== null)
}

function filterTreeByQuery(nodes: TreeNode[], q: string): TreeNode[] {
  const recurse = (n: TreeNode): TreeNode | null => {
    if (n.kind !== 'directory') {
      return n.name.toLowerCase().includes(q) || n.path.toLowerCase().includes(q) ? n : null
    }
    const kids = n.children.map(recurse).filter((x): x is TreeNode => x !== null)
    return kids.length > 0 ? { ...n, children: kids } : null
  }
  return nodes.map(recurse).filter((x): x is TreeNode => x !== null)
}
