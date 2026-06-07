import { Box, Group, ScrollArea, Text, UnstyledButton } from '@mantine/core'
import {
  IconChevronRight, IconFile, IconFolder, IconFolderOpen, IconFolders, IconLayoutDashboard,
  IconLock, IconSend, IconWorld, type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useState } from 'react'
import type { TreeNode } from '../api/types'

interface Props {
  tree: TreeNode[]
  search: string
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

const FILE_ICON: Record<string, TablerIcon> = {
  request: IconSend,
  auth: IconLock,
  env: IconWorld,
  collection: IconFolders,
  workspace: IconLayoutDashboard,
}

const FILE_COLOR: Record<string, string> = {
  request: 'var(--mantine-color-tap-6)',
  auth: 'var(--mantine-color-orange-6)',
  env: 'var(--mantine-color-grape-6)',
  collection: 'var(--mantine-color-blue-6)',
  workspace: 'var(--mantine-color-dimmed)',
}

/** Raw filesystem view of the `.tap/` directory. Renders every directory and every
 *  file as the workspace tree from the server reports it — no kind-aware reshuffling,
 *  no hiding of structural directories. Useful for sanity-checking the on-disk layout
 *  alongside the kind-aware Collections / API / Auth / Tags tabs. */
export function FilesystemView({ tree, search, activePath, onOpenFile }: Props) {
  const filtered = search ? filterTreeByQuery(tree, search.toLowerCase()) : tree
  if (filtered.length === 0) {
    return <Text size="xs" c="dimmed" ta="center" py="xl" px="md">Empty .tap/ directory.</Text>
  }
  return (
    <ScrollArea.Autosize>
      <Box>
        {filtered.map((n) => (
          <FsRow key={n.path} node={n} depth={0} activePath={activePath} onOpenFile={onOpenFile} />
        ))}
      </Box>
    </ScrollArea.Autosize>
  )
}

interface RowProps {
  node: TreeNode
  depth: number
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

function FsRow({ node, depth, activePath, onOpenFile }: RowProps) {
  const isDir = node.kind === 'directory'
  const [open, setOpen] = useState(depth < 1) // top-level dirs start expanded
  const isActive = !isDir && activePath === node.path
  const indent = 8 + depth * 14

  const Icon = isDir
    ? (open ? IconFolderOpen : IconFolder)
    : (FILE_ICON[node.kind] ?? IconFile)
  const color = isDir
    ? 'var(--mantine-color-dimmed)'
    : (FILE_COLOR[node.kind] ?? 'var(--mantine-color-dimmed)')

  const onClick = () => {
    if (isDir) setOpen(!open)
    else onOpenFile(node)
  }

  // Show the on-disk filename (last segment of path) rather than the parsed display
  // name — the whole point of this view is the literal filesystem layout.
  const label = node.path.split('/').pop() ?? node.path

  return (
    <>
      <UnstyledButton
        onClick={onClick}
        w="100%"
        px={indent}
        py={4}
        bg={isActive ? 'var(--mantine-color-default-hover)' : undefined}
        style={{
          borderLeft: `3px solid ${isActive ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
          color: isActive ? 'var(--mantine-color-tap-light-color)' : undefined,
          fontSize: 13,
          fontFamily: 'var(--mono, ui-monospace)',
        }}
        className="tap-tree-row"
      >
        <Group gap={6} wrap="nowrap">
          {isDir
            ? <IconChevronRight
                size={11}
                style={{
                  transform: open ? 'rotate(90deg)' : 'none',
                  transition: 'transform 0.12s',
                  color: 'var(--mantine-color-dimmed)',
                }}
              />
            : <Box w={11} />}
          <Box style={{ color, display: 'inline-flex' }}>
            <Icon size={13} />
          </Box>
          <Text size="xs" ff="var(--mono)" truncate flex={1}>{label}</Text>
        </Group>
      </UnstyledButton>
      {isDir && open && node.children.map((c) => (
        <FsRow key={c.path} node={c} depth={depth + 1} activePath={activePath} onOpenFile={onOpenFile} />
      ))}
    </>
  )
}

function filterTreeByQuery(nodes: TreeNode[], q: string): TreeNode[] {
  const recurse = (n: TreeNode): TreeNode | null => {
    const selfHit = n.path.toLowerCase().includes(q) || n.name.toLowerCase().includes(q)
    if (n.kind !== 'directory') return selfHit ? n : null
    const kids = n.children.map(recurse).filter((x): x is TreeNode => x !== null)
    if (!selfHit && kids.length === 0) return null
    return { ...n, children: kids }
  }
  return nodes.map(recurse).filter((x): x is TreeNode => x !== null)
}
