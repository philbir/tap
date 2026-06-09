import { Text } from '@mantine/core'
import { useMemo } from 'react'
import type { TreeNode } from '../api/types'
import { PierreFileTree } from './PierreFileTree'
import { TAP_TREE_ICONS, TAP_TREE_UNSAFE_CSS } from './treeIcons'

interface Props {
  tree: TreeNode[]
  search: string
  activePath: string | null
  onOpenFile: (n: TreeNode) => void
}

/** Raw filesystem view of the `.tap/` directory, rendered via `@pierre/trees`. The
 *  pierre tree groups by `/` segments natively, so we just hand it the flat list of
 *  workspace-relative file paths the server returns. */
export function FilesystemView({ tree, search, activePath, onOpenFile }: Props) {
  const fileByPath = useMemo(() => {
    const map = new Map<string, TreeNode>()
    collectFiles(tree, map)
    return map
  }, [tree])

  const allPaths = useMemo(() => [...fileByPath.keys()].sort(), [fileByPath])
  const paths = useMemo(() => {
    if (!search) return allPaths
    const q = search.toLowerCase()
    return allPaths.filter((p) => p.toLowerCase().includes(q))
  }, [allPaths, search])

  if (paths.length === 0) {
    return <Text size="xs" c="dimmed" ta="center" py="xl" px="md">Empty .tap/ directory.</Text>
  }

  return (
    <PierreFileTree
      paths={paths}
      selectedPath={activePath}
      initialExpansion="open"
      icons={TAP_TREE_ICONS}
      unsafeCSS={TAP_TREE_UNSAFE_CSS}
      fillParent
      onActivateFile={(p) => {
        const node = fileByPath.get(p)
        if (node) onOpenFile(node)
      }}
    />
  )
}

function collectFiles(nodes: TreeNode[], sink: Map<string, TreeNode>) {
  for (const n of nodes) {
    if (n.kind === 'directory') {
      collectFiles(n.children, sink)
    } else {
      sink.set(n.path, n)
    }
  }
}
