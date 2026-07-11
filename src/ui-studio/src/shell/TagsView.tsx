import { Badge, Box, Group, Stack, Text, UnstyledButton } from '@mantine/core'
import {
  IconChevronRight, IconFolders, IconLock, IconSend, IconTag, type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useMemo, useState } from 'react'
import type { TaggedItem, TreeNode, WorkspaceFileKind } from '../api/types'

const KIND_ICON: Record<string, TablerIcon> = {
  collection: IconFolders,
  request: IconSend,
  auth: IconLock,
}

const KIND_COLOR: Record<string, string> = {
  collection: 'var(--mantine-color-blue-6)',
  request: 'var(--mantine-color-tap-6)',
  auth: 'var(--mantine-color-orange-6)',
}

interface Props {
  /** Pre-fetched tag rows. The Sidebar owns the fetch so its tag-picker dropdown can
   *  list the same unique tags. */
  items: TaggedItem[]
  /** When non-empty, restrict to these tags. Driven by the MultiSelect in the search row. */
  selectedTags: string[]
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

/** Tags top-level view: groups every (tag, entity) row by tag. Each entry opens the
 *  underlying editor when clicked. Visibility is gated by <see cref="selectedTags"/>;
 *  an empty selection shows everything. */
export function TagsView({ items, selectedTags, activePath, onOpenFile }: Props) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set())

  const grouped = useMemo(() => {
    const allowedTags = selectedTags.length > 0 ? new Set(selectedTags) : null
    const byTag = new Map<string, TaggedItem[]>()
    for (const it of items) {
      if (allowedTags && !allowedTags.has(it.tag)) continue
      if (!byTag.has(it.tag)) byTag.set(it.tag, [])
      byTag.get(it.tag)!.push(it)
    }
    return [...byTag.entries()].sort((a, b) => a[0].localeCompare(b[0], undefined, { sensitivity: 'base' }))
  }, [items, selectedTags])

  const toggle = (tag: string) => setCollapsed((cur) => {
    const next = new Set(cur)
    if (next.has(tag)) next.delete(tag); else next.add(tag)
    return next
  })

  if (grouped.length === 0) {
    const message = selectedTags.length > 0
      ? 'No items match the selected tag(s).'
      : 'No tags yet — add tags to collections, requests, APIs, or auth profiles to group them here.'
    return (
      <Stack align="center" gap={4} py="xl" px="md">
        <IconTag size={20} color="var(--mantine-color-dimmed)" />
        <Text size="xs" c="dimmed" ta="center">{message}</Text>
      </Stack>
    )
  }

  return (
    <Box>
      {grouped.map(([tag, rows]) => {
        const open = !collapsed.has(tag)
        return (
          <Box key={tag}>
            <UnstyledButton
              w="100%"
              px="sm"
              py={6}
              onClick={() => toggle(tag)}
              style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}
            >
              <Group gap={6} wrap="nowrap">
                <IconChevronRight
                  size={11}
                  style={{
                    transform: open ? 'rotate(90deg)' : 'none',
                    transition: 'transform 0.12s',
                    color: 'var(--mantine-color-dimmed)',
                  }}
                />
                <IconTag size={13} color="var(--mantine-color-dimmed)" />
                <Text size="sm" fw={600} flex={1} truncate>{tag}</Text>
                <Badge size="xs" variant="light" color="gray">{rows.length}</Badge>
              </Group>
            </UnstyledButton>
            {open && rows.map((r) => (
              <TagRow key={`${tag}:${r.kind}:${r.path}`} item={r} activePath={activePath} onOpenFile={onOpenFile} />
            ))}
          </Box>
        )
      })}
    </Box>
  )
}

interface TagRowProps {
  item: TaggedItem
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

function TagRow({ item, activePath, onOpenFile }: TagRowProps) {
  const Icon = KIND_ICON[item.kind] ?? IconSend
  const color = KIND_COLOR[item.kind] ?? 'var(--mantine-color-dimmed)'
  const isActive = activePath === item.path
  return (
    <UnstyledButton
      w="100%"
      px="md"
      py={5}
      onClick={() => onOpenFile({
        kind: item.kind as WorkspaceFileKind,
        path: item.path,
        name: item.name,
        id: null,
        children: [],
      })}
      style={{
        background: isActive ? 'var(--mantine-color-default-hover)' : undefined,
        borderLeft: `3px solid ${isActive ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
        fontSize: 13,
      }}
      className="tap-tree-row"
    >
      <Group gap={6} wrap="nowrap" pl={14}>
        <Box style={{ color, display: 'inline-flex' }}>
          <Icon size={13} />
        </Box>
        <Text size="sm" truncate flex={1}>{item.name}</Text>
        <Text size="xs" c="dimmed">{item.kind}</Text>
      </Group>
    </UnstyledButton>
  )
}
