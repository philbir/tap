import {
  ActionIcon, Alert, Box, Center, Group, Menu, MultiSelect, ScrollArea, SegmentedControl, Stack,
  Text, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import {
  IconAlertCircle, IconChevronRight, IconDots, IconFolder, IconFolderOpen, IconFolderPlus,
  IconFolders, IconFolderShare, IconLayoutDashboard, IconLock, IconPlus, IconSearch, IconSend,
  IconTag, IconTrash, IconWorld, type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { TaggedItem, TreeNode, WorkspaceErrorDto, WorkspaceFileKind } from '../api/types'
import { useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { buildRequestsView, filterExplorerTree, type ExplorerNode } from './explorerTree'
import { FilesystemView } from './FilesystemView'
import { NewFolderDialog } from './NewFolderDialog'
import { TagsView } from './TagsView'

type Filter = 'requests' | 'auth' | 'tags' | 'fs'

const FILTER_TABS: { value: Filter; label: string; icon: TablerIcon }[] = [
  { value: 'requests', label: 'Requests', icon: IconSend },
  { value: 'auth', label: 'Auth', icon: IconLock },
  { value: 'tags', label: 'Tags', icon: IconTag },
  { value: 'fs', label: 'Filesystem', icon: IconFolderShare },
]

interface Props {
  hasActiveWorkspace: boolean
  onOpenFile: (node: TreeNode) => void
  onCreateNew: () => void
}

const EMPTY_ERRORS: WorkspaceErrorDto[] = []

const KIND_ICON: Record<WorkspaceFileKind, TablerIcon> = {
  workspace: IconLayoutDashboard,
  request: IconSend,
  auth: IconLock,
  env: IconWorld,
  collection: IconFolders,
  folder: IconFolder,
  settings: IconLayoutDashboard,
}

const KIND_COLOR: Partial<Record<WorkspaceFileKind, string>> = {
  request: 'tap.6',
  auth: 'orange.6',
  env: 'grape.6',
}

export function Sidebar({ hasActiveWorkspace, onOpenFile, onCreateNew }: Props) {
  const tree = useTapStore((s) => s.tree)
  const errors = useTapStore((s) => s.info?.errors ?? EMPTY_ERRORS)
  const activePath = useTapStore((s) => s.activeTab)
  const reload = useTapStore((s) => s.reload)
  const closeTab = useTapStore((s) => s.closeTab)

  const [filter, setFilter] = useState<Filter>('requests')
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

  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set())

  const [newFolderState, setNewFolderState] = useState<{ open: boolean; parentDir: string }>({
    open: false, parentDir: '',
  })

  const requestsView = useMemo(
    () => filterExplorerTree(buildRequestsView(tree), search),
    [tree, search],
  )

  const authNodes = useMemo(() => filterByKind(tree, 'auth'), [tree])
  const filteredAuth = useMemo(
    () => (search ? filterTreeByQuery(authNodes, search.toLowerCase()) : authNodes),
    [authNodes, search],
  )

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

  const toggle = (path: string) => setCollapsed((cur) => {
    const next = new Set(cur)
    if (next.has(path)) next.delete(path); else next.add(path)
    return next
  })
  const isOpen = (path: string) => !collapsed.has(path)

  async function performMove(from: string, to: string) {
    try {
      await api.moveItem(from, to)
      await reload()
      closeTab(from)
    } catch (e) {
      notifications.show({
        color: 'red',
        title: 'Move failed',
        message: e instanceof ApiError ? e.message : String(e),
      })
    }
  }

  function confirmDeleteFolder(path: string, name: string, kind: 'folder' | 'collection') {
    modals.openConfirmModal({
      title: <Text fw={600}>Delete {kind}</Text>,
      children: (
        <Text size="sm">
          Delete <Text component="span" fw={600}>{name}</Text> and everything inside it? This
          can't be undone.
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: async () => {
        try {
          if (kind === 'collection') {
            const slug = path.split('/').pop() ?? path
            await api.deleteCollection(slug)
          } else {
            await api.deleteFolder(path)
          }
          await reload()
          const prefix = path.endsWith('/') ? path : path + '/'
          for (const t of useTapStore.getState().tabs) {
            if (t.path === path || t.path.startsWith(prefix)) closeTab(t.path)
          }
        } catch (e) {
          notifications.show({
            color: 'red',
            title: 'Delete failed',
            message: e instanceof ApiError ? e.message : String(e),
          })
        }
      },
    })
  }

  return (
    <Stack gap={0} h="100%">
      <Box p="sm" pb={0}>
        <SegmentedControl
          fullWidth
          size="xs"
          value={filter}
          onChange={(v) => setFilter(v as Filter)}
          data={FILTER_TABS.map((t) => {
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

      <ScrollArea flex={1} type="hover" scrollbarSize={8}>
        <Box pb="md">
          {filter === 'requests' && (
            requestsView.length === 0
              ? <EmptyState kind="requests" />
              : requestsView.map((n) => (
                  <ExplorerRow
                    key={n.path}
                    node={n}
                    depth={0}
                    activePath={activePath}
                    isOpen={isOpen}
                    onToggle={toggle}
                    onOpenFile={onOpenFile}
                    onMove={performMove}
                    onNewFolderHere={(parentDir) => setNewFolderState({ open: true, parentDir })}
                    onDeleteFolder={confirmDeleteFolder}
                  />
                ))
          )}

          {filter === 'auth' && (
            filteredAuth.length === 0
              ? <EmptyState kind="auth" />
              : filteredAuth.map((n) => (
                  <AuthRow key={n.path} node={n} depth={0} activePath={activePath} onOpenFile={onOpenFile} />
                ))
          )}

          {filter === 'tags' && (
            <TagsView
              items={tagItems}
              selectedTags={selectedTags}
              activePath={activePath}
              onOpenFile={onOpenFile}
            />
          )}

          {filter === 'fs' && (
            <FilesystemView tree={tree} search={search} activePath={activePath} onOpenFile={onOpenFile} />
          )}
        </Box>
      </ScrollArea>

      <NewFolderDialog
        open={newFolderState.open}
        onOpenChange={(open) => setNewFolderState((s) => ({ ...s, open }))}
        parent={newFolderState.parentDir}
      />
    </Stack>
  )
}

interface ExplorerRowProps {
  node: ExplorerNode
  depth: number
  activePath: string | null
  isOpen: (path: string) => boolean
  onToggle: (path: string) => void
  onOpenFile: (n: TreeNode) => void
  onMove: (from: string, to: string) => void
  onNewFolderHere: (parentDir: string) => void
  onDeleteFolder: (path: string, name: string, kind: 'folder' | 'collection') => void
}

function ExplorerRow(props: ExplorerRowProps) {
  const { node, depth, activePath, isOpen, onToggle, onOpenFile, onMove, onNewFolderHere, onDeleteFolder } = props
  const open = isOpen(node.path)
  const isCollectionRoot = node.kind === 'collection'
  const isFolder = node.kind === 'folder'
  const isRequest = node.kind === 'request'
  const expandable = isCollectionRoot || isFolder
  const active = activePath === node.path
  const indent = 8 + depth * 14

  const [dropActive, setDropActive] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)

  const Icon = isCollectionRoot ? IconFolders
    : isFolder ? (open ? IconFolderOpen : IconFolder)
    : IconSend
  const iconColor = isCollectionRoot ? 'var(--mantine-color-blue-6)'
    : isFolder ? 'var(--mantine-color-dimmed)'
    : 'var(--mantine-color-tap-6)'

  // Actions menu on collections and folders only. Both offer "New folder…"; both can be deleted.
  const hasMenu = isFolder || isCollectionRoot
  const canDelete = isFolder || isCollectionRoot

  const draggable = isFolder || isRequest
  const isDropTarget = isFolder || isCollectionRoot

  const onDragStart = (e: React.DragEvent) => {
    if (!draggable) return
    e.dataTransfer.setData('application/x-tap-path', node.path)
    e.dataTransfer.effectAllowed = 'move'
  }
  const onDragOver = (e: React.DragEvent) => {
    if (!isDropTarget) return
    if (!e.dataTransfer.types.includes('application/x-tap-path')) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (!dropActive) setDropActive(true)
  }
  const onDragLeave = () => setDropActive(false)
  const onDrop = (e: React.DragEvent) => {
    if (!isDropTarget) return
    e.preventDefault()
    setDropActive(false)
    const from = e.dataTransfer.getData('application/x-tap-path')
    if (!from || from === node.path) return
    if (node.path.startsWith(from + '/')) return
    const filename = from.split('/').pop()!
    const to = `${node.path}/${filename}`
    if (from === to) return
    onMove(from, to)
  }

  const onClick = () => {
    if (expandable) onToggle(node.path)
    if (isCollectionRoot && node.slug) {
      onOpenFile({ kind: 'collection', path: node.path, name: node.name, id: null, children: [] })
      return
    }
    if (isRequest && node.source) onOpenFile(node.source)
  }

  const onContextMenu = (e: React.MouseEvent) => {
    if (!hasMenu) return
    e.preventDefault()
    setMenuOpen(true)
  }

  const contextMenu = hasMenu ? (
    <Menu
      position="right-start"
      withArrow
      shadow="md"
      withinPortal
      opened={menuOpen}
      onChange={setMenuOpen}
    >
      <Menu.Target>
        <ActionIcon
          variant="subtle" color="gray" size="sm"
          className="tap-tree-row__actions"
          onClick={(e) => { e.stopPropagation(); setMenuOpen((o) => !o) }}
          aria-label={`Actions for ${node.name}`}
          style={{ position: 'absolute', right: 4, top: '50%', transform: 'translateY(-50%)' }}
        >
          <IconDots size={14} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item
          leftSection={<IconFolderPlus size={14} />}
          onClick={() => onNewFolderHere(node.path)}
        >
          New folder…
        </Menu.Item>
        {canDelete && (
          <Menu.Item
            color="red"
            leftSection={<IconTrash size={14} />}
            onClick={() => onDeleteFolder(node.path, node.name, isCollectionRoot ? 'collection' : 'folder')}
          >
            {isCollectionRoot ? 'Delete collection' : 'Delete folder'}
          </Menu.Item>
        )}
      </Menu.Dropdown>
    </Menu>
  ) : null

  return (
    <>
      <Box
        className="tap-tree-row"
        draggable={draggable}
        onDragStart={onDragStart}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
        onContextMenu={onContextMenu}
        style={{
          position: 'relative',
          background: active
            ? 'var(--mantine-color-default-hover)'
            : dropActive
              ? 'var(--mantine-color-tap-light)'
              : undefined,
          outline: dropActive ? '2px solid var(--mantine-color-tap-filled)' : 'none',
          outlineOffset: -2,
          borderLeft: `3px solid ${active ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
        }}
      >
        <UnstyledButton
          onClick={onClick}
          w="100%"
          px={indent}
          py={isCollectionRoot ? 7 : 5}
          style={{
            color: active ? 'var(--mantine-color-tap-light-color)' : undefined,
            fontSize: 13,
            fontWeight: isCollectionRoot ? 600 : 400,
          }}
        >
          <Group gap={6} wrap="nowrap" pr={28}>
            {expandable
              ? <IconChevronRight
                  size={11}
                  style={{
                    transform: open ? 'rotate(90deg)' : 'none',
                    transition: 'transform 0.12s',
                    color: 'var(--mantine-color-dimmed)',
                  }}
                />
              : <Box w={11} />}
            <Box style={{ display: 'inline-flex', color: iconColor }}>
              <Icon size={isCollectionRoot ? 16 : 14} />
            </Box>
            <Text size="sm" truncate flex={1}>{node.name}</Text>
          </Group>
        </UnstyledButton>
        {contextMenu}
      </Box>
      {open && node.children.map((c) => (
        <ExplorerRow
          key={c.path}
          node={c}
          depth={depth + 1}
          activePath={activePath}
          isOpen={isOpen}
          onToggle={onToggle}
          onOpenFile={onOpenFile}
          onMove={onMove}
          onNewFolderHere={onNewFolderHere}
          onDeleteFolder={onDeleteFolder}
        />
      ))}
    </>
  )
}

interface AuthRowProps {
  node: TreeNode
  depth: number
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

function AuthRow({ node, depth, activePath, onOpenFile }: AuthRowProps) {
  const isDir = node.kind === 'directory'
  const [open, setOpen] = useState(true)
  const active = !isDir && activePath === node.path
  const indent = 8 + depth * 14
  const Icon = isDir ? (open ? IconFolderOpen : IconFolder) : (KIND_ICON[node.kind as WorkspaceFileKind] ?? IconLayoutDashboard)
  const color = isDir ? 'dimmed' : (KIND_COLOR[node.kind as WorkspaceFileKind] ?? 'dimmed')
  return (
    <>
      <UnstyledButton
        onClick={() => (isDir ? setOpen(!open) : onOpenFile(node))}
        w="100%"
        px={indent}
        py={5}
        bg={active ? 'var(--mantine-color-default-hover)' : undefined}
        style={{
          borderLeft: `3px solid ${active ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
          color: active ? 'var(--mantine-color-tap-light-color)' : undefined,
          fontSize: 13,
        }}
        className="tap-tree-row"
      >
        <Group gap={6} wrap="nowrap">
          {isDir
            ? <IconChevronRight
                size={11}
                style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s', color: 'var(--mantine-color-dimmed)' }}
              />
            : <Box w={11} />}
          <Box style={{ color: `var(--mantine-color-${color.split('.')[0]}-${color.split('.')[1] ?? '6'})`, display: 'inline-flex' }}>
            <Icon size={14} />
          </Box>
          <Text size="sm" truncate flex={1}>{node.name}</Text>
        </Group>
      </UnstyledButton>
      {isDir && open && node.children.map((c) => (
        <AuthRow key={c.path} node={c} depth={depth + 1} activePath={activePath} onOpenFile={onOpenFile} />
      ))}
    </>
  )
}

function EmptyState({ kind }: { kind: 'requests' | 'auth' }) {
  const message = kind === 'requests'
    ? 'No collections yet — create one with +.'
    : 'No auth profiles yet — create one with +.'
  return (
    <Text size="xs" c="dimmed" ta="center" py="xl" px="md">{message}</Text>
  )
}

function filterByKind(nodes: TreeNode[], kind: WorkspaceFileKind): TreeNode[] {
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
