import { FileTree, useFileTree, useFileTreeSelector } from '@pierre/trees/react'
import type { FileTreeDropTarget, FileTreeIcons, GitStatusEntry } from '@pierre/trees'
import { useEffect, useMemo, useRef } from 'react'

export interface TreeDragAndDrop {
  /** Gate which rows may start a drag. Paths are display paths; directories keep
   *  their trailing `/` so hosts can tell them apart from files. Default: all. */
  canDrag?: (paths: readonly string[]) => boolean
  /** Gate drop targets while dragging. `targetDir` is the display path of the hovered
   *  directory (no trailing slash), or null when hovering the tree root. Default: all. */
  canDrop?: (draggedPaths: readonly string[], targetDir: string | null) => boolean
  /** Fired after the tree has visually applied the move. The host is responsible for
   *  persisting it (and reloading to revert if persistence fails). */
  onDrop: (draggedPaths: readonly string[], targetDir: string | null) => void
  onError?: (message: string) => void
}

interface Props {
  /** Slash-separated paths. Directories are inferred from path segments. */
  paths: readonly string[]
  /** Optional git status overlay (file decoration). */
  gitStatus?: readonly GitStatusEntry[]
  /** Path to highlight as selected. Updates the tree's selection imperatively when changed. */
  selectedPath?: string | null
  /** Fired when the user changes selection by clicking / arrow-keying. */
  onSelectionChange?: (paths: readonly string[]) => void
  /** Convenience: fired with the single newly selected leaf path (skipped for directories). */
  onActivateFile?: (path: string) => void
  /** Optional initial expansion ('open', 'closed', or a depth — default: closed). */
  initialExpansion?: 'open' | 'closed' | number
  /** Built-in icon set name, or a full FileTreeIcons config (sprite, remaps, etc.). */
  icons?: FileTreeIcons
  /** CSS string injected into the tree's shadow DOM. Use it to theme css vars and
   *  to color custom icons. See TAP_TREE_UNSAFE_CSS in `treeIcons.ts`. */
  unsafeCSS?: string
  /** Fired when the user right-clicks a row. The handler is responsible for showing
   *  a context menu — we deliberately don't go through pierre's renderContextMenu
   *  because its slot-based portal doesn't play well with React's event delegation
   *  (clicks inside pierre's menu surface get swallowed before reaching React).
   *
   *  `path` is the row's display path (matches what `onActivateFile` would get).
   *  `kind` is the row's tree kind: 'directory' for folder rows, 'file' for leaves.
   *  `event.clientX/Y` is the cursor anchor for positioning. The default browser
   *  context menu is suppressed automatically. */
  onContextMenuRequest?: (path: string, kind: 'directory' | 'file', event: { clientX: number; clientY: number }) => void
  /** Enables row drag-and-drop. The tree applies moves to its own model optimistically;
   *  `onDrop` fires afterwards so the host can persist (or reload to revert). Like
   *  `composition`, enabled-ness is fixed at mount — pass it from the first render. */
  dragAndDrop?: TreeDragAndDrop
  /** When true, the host stretches to fill its parent and the tree virtualizes within it.
   *  When false (default), the host's height is sized to fit all currently-visible rows so
   *  the tree behaves like a static block embedded in document flow. */
  fillParent?: boolean
  /** Pierre's "compact folders" behaviour: a directory that holds no files but exactly one
   *  subdirectory gets merged into a single `parent / child` row. Defaults to `false` here
   *  because it merges a collection with its sole subfolder, which hides the collection row's
   *  own context menu (e.g. "Edit collection…"). Set `true` to opt back in. */
  flattenSingleChildDirectories?: boolean
  className?: string
  style?: React.CSSProperties
}

const DEFAULT_ROW_HEIGHT = 24

/**
 * Thin wrapper over `@pierre/trees` for the path-first usage we need across the
 * Studio sidebar: a flat list of slash-separated paths in, click → onActivateFile,
 * git status decoration optional, controlled selection via `selectedPath`.
 */
export function PierreFileTree(props: Props) {
  const {
    paths, gitStatus, selectedPath, onSelectionChange, onActivateFile,
    initialExpansion = 'closed', icons, unsafeCSS, onContextMenuRequest,
    dragAndDrop, fillParent = false, flattenSingleChildDirectories = false, className, style,
  } = props

  // useFileTree creates the model ONCE per component lifetime; we feed updates via
  // model.resetPaths / setGitStatus on prop changes.
  const onSelChangeRef = useRef(onSelectionChange)
  const onActivateRef = useRef(onActivateFile)
  const onContextRef = useRef(onContextMenuRequest)
  const dragRef = useRef(dragAndDrop)
  useEffect(() => { onSelChangeRef.current = onSelectionChange }, [onSelectionChange])
  useEffect(() => { onActivateRef.current = onActivateFile }, [onActivateFile])
  useEffect(() => { onContextRef.current = onContextMenuRequest }, [onContextMenuRequest])
  useEffect(() => { dragRef.current = dragAndDrop }, [dragAndDrop])

  // Snapshot the initial paths so useFileTree mounts with them. Later changes go
  // through model.resetPaths in the effect below.
  const initialPaths = useMemo(() => Array.from(paths), [])
  const initialGitStatus = useMemo(
    () => gitStatus ? Array.from(gitStatus) : undefined,
    [],
  )

  const { model } = useFileTree({
    paths: initialPaths,
    gitStatus: initialGitStatus,
    initialExpansion,
    flattenEmptyDirectories: flattenSingleChildDirectories,
    icons,
    unsafeCSS,
    // Enable pierre's context-menu composition so it renders the per-row ⋯ trigger
    // and exposes both right-click and button activations through `onOpen`. We
    // hijack the open: instead of letting pierre mount its slot-based surface
    // (which doesn't deliver click events back to React reliably), we read the
    // anchor coords and immediately close pierre's surface, then surface the event
    // to the host via `onContextMenuRequest` so the host can render its own menu.
    composition: onContextMenuRequest ? {
      contextMenu: {
        enabled: true,
        triggerMode: 'both',
        // 'always' is required for pierre to actually render the ⋯ icon in the row's
        // action lane — with 'when-needed' it leaves the lane empty and only marks
        // a `data-…-visibility="when-needed"` attribute for the consumer to style.
        // We still want hover-only display, so TAP_TREE_UNSAFE_CSS hides the lane
        // by default and fades it in on row hover.
        buttonVisibility: 'always',
        onOpen: (item, context) => {
          // Close pierre's empty surface synchronously — we never want it visible.
          context.close({ restoreFocus: false })
          const itemPath = item.path.replace(/\/$/, '')
          onContextRef.current?.(itemPath, item.kind, {
            clientX: context.anchorRect.right,
            clientY: context.anchorRect.bottom,
          })
        },
      },
    } : undefined,
    // Like composition, drag-and-drop enabled-ness is locked in at model creation.
    // The handlers read through dragRef so the host can swap callbacks per render.
    dragAndDrop: dragAndDrop ? {
      canDrag: (dragPaths) => dragRef.current?.canDrag?.(dragPaths) ?? true,
      canDrop: (ctx) => dragRef.current?.canDrop?.(ctx.draggedPaths, dropTargetDir(ctx.target)) ?? true,
      onDropComplete: (result) => dragRef.current?.onDrop(result.draggedPaths, dropTargetDir(result.target)),
      onDropError: (error) => dragRef.current?.onError?.(error),
    } : undefined,
    initialSelectedPaths: selectedPath ? [selectedPath] : undefined,
    onSelectionChange: (selected) => {
      onSelChangeRef.current?.(selected)
      // Treat a single-leaf selection as "activate this file" — directories
      // can't be activated (no onclick semantics in the file tree itself).
      if (selected.length === 1) {
        const handle = model.getItem(selected[0])
        if (handle && !handle.isDirectory()) onActivateRef.current?.(selected[0])
      }
    },
  })

  // Push prop updates into the imperative model.
  useEffect(() => { model.resetPaths(Array.from(paths)) }, [model, paths])
  useEffect(() => { model.setGitStatus(gitStatus ? Array.from(gitStatus) : undefined) }, [model, gitStatus])
  useEffect(() => {
    // Keep selection in sync with the controlled prop. If the host clears it
    // (selectedPath === null), deselect the currently-selected item.
    if (selectedPath) {
      const handle = model.getItem(selectedPath)
      if (handle && !handle.isSelected()) handle.select()
    } else {
      for (const p of model.getSelectedPaths()) model.getItem(p)?.deselect()
    }
  }, [model, selectedPath])

  // Subscribe to selection for re-renders that depend on it (e.g. external
  // "stage selected" affordances reading from this component's parent).
  useFileTreeSelector(model, (m) => m.getSelectedPaths(), arraysShallowEqual)

  // The FileTree custom element virtualises within whatever height its host gives it.
  // When `fillParent` is set we let CSS stretch the host; otherwise we estimate a height
  // big enough to render every visible row inline (so multiple sections can stack in a
  // scroll parent without each tree introducing its own scroller).
  // The custom element uses % heights for its inner virtualized layout, so it needs an
  // explicit `height` (not just `min-height`) on the host or its children collapse to 0.
  const sizingStyle: React.CSSProperties = fillParent
    ? { flex: 1, minHeight: 0, display: 'block' }
    : { display: 'block', height: estimateMinHeight(paths.length, initialExpansion) }

  return (
    <FileTree
      model={model}
      className={className}
      style={{ ...sizingStyle, ...style }}
    />
  )
}

/** Resolve a pierre drop target to the display path of the destination directory
 *  (canonical dir paths carry a trailing `/` we strip), or null for the tree root. */
function dropTargetDir(target: FileTreeDropTarget): string | null {
  if (target.kind === 'root' || !target.directoryPath) return null
  return target.directoryPath.replace(/\/$/, '')
}

function estimateMinHeight(pathCount: number, initialExpansion: 'open' | 'closed' | number) {
  // The tree's "visible row" count depends on expansion state, which we can't compute
  // without the model. Use a generous upper bound: count every path as one row when
  // expanded, plus a couple of header/expand rows. The custom element only paints what
  // it has room for, so an overestimate is harmless aside from extra empty space.
  const expandedHint = initialExpansion === 'closed' ? Math.min(pathCount, 6) : pathCount + 4
  return Math.max(expandedHint * DEFAULT_ROW_HEIGHT, DEFAULT_ROW_HEIGHT)
}

function arraysShallowEqual<T>(a: readonly T[], b: readonly T[]) {
  if (a === b) return true
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false
  return true
}
