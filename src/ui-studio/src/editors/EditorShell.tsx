import { ActionIcon, Alert, Badge, Box, Button, Group, ScrollArea, TextInput, Title, Tooltip } from '@mantine/core'
import { useHotkeys } from '@mantine/hooks'
import {
  IconAlertCircle, IconArrowsSplit2, IconChecklist, IconDeviceFloppy, IconFileCode, IconFolders,
  IconLayoutDashboard, IconLock, IconPencil, IconPlug, IconSend, IconWorld,
  type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Panel, PanelGroup, PanelResizeHandle } from 'react-resizable-panels'
import { ErrorBoundary } from '../shell/ErrorBoundary'
import styles from './EditorShell.module.css'

/**
 * Shared chrome for every per-kind editor. Layout slots:
 *   - `children`: primary editor pane (always present)
 *   - `rightPane`: optional secondary pane to the RIGHT (e.g. the request Assistant)
 *   - `bottomPane`: optional secondary pane BELOW (e.g. RequestEditor Response panel)
 * Both secondary panes use react-resizable-panels so the user can drag the divider. The
 * right pane defaults to ~32% width; the bottom pane to ~45% height. When both are present
 * the bottom pane nests inside the left column, so the editor + response stay usable while
 * the right pane is open. Persisting the split sizes is wired through localStorage keyed by
 * editor kind.
 *
 * Ctrl/Cmd+S triggers save when dirty.
 */
export interface EditorShellProps {
  title: string
  kindLabel: string
  dirty: boolean
  saving: boolean
  errorMessage: string | null
  onSave: () => void
  onDiscard?: () => void
  /** Optional: when present, clicking the title (or its pencil icon) flips it into a
   *  text-input. Enter / blur commits, Esc reverts. Empty values are rejected. */
  onTitleChange?: (next: string) => void
  toolbarExtras?: ReactNode
  children: ReactNode
  /** Pane attached to the right of the primary editor (used by AuthEditor). */
  rightPane?: ReactNode
  /** Pane below the primary editor (used by RequestEditor for the Response viewer). */
  bottomPane?: ReactNode
}

const KIND_COLOR: Record<string, string> = {
  Request: 'tap',
  API: 'teal',
  Auth: 'orange',
  Environment: 'grape',
  Workspace: 'tap',
  Flow: 'violet',
  'Test set': 'teal',
  'HTTP file': 'blue',
}

const KIND_ICON: Record<string, TablerIcon> = {
  Request: IconSend,
  API: IconPlug,
  Auth: IconLock,
  Environment: IconWorld,
  Workspace: IconLayoutDashboard,
  Collection: IconFolders,
  Flow: IconArrowsSplit2,
  'Test set': IconChecklist,
  'HTTP file': IconFileCode,
}

export function EditorShell(props: EditorShellProps) {
  const { title, kindLabel, dirty, saving, errorMessage, onSave, onDiscard, onTitleChange, toolbarExtras, children, rightPane, bottomPane } = props

  useHotkeys([
    ['mod+S', () => { if (dirty && !saving) onSave() }],
  ])

  const chipColor = KIND_COLOR[kindLabel] ?? 'tap'
  const KindIcon = KIND_ICON[kindLabel] ?? IconSend
  // Stable localStorage key so each editor kind remembers its preferred split sizes.
  const autoSaveId = `tap-studio:split:${kindLabel.toLowerCase()}`

  return (
    <Box style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Group
        justify="space-between"
        wrap="nowrap"
        px="lg"
        py="sm"
        style={{ borderBottom: '1px solid var(--mantine-color-default-border)', flexShrink: 0 }}
      >
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0, flex: 1 }}>
          <Tooltip label={kindLabel} openDelay={400} withArrow>
            <KindIcon size={20} stroke={1.7} color={`var(--mantine-color-${chipColor}-6)`} aria-label={kindLabel} />
          </Tooltip>
          <EditableTitle title={title} onChange={onTitleChange} />
          {dirty && (
            <Box
              w={8} h={8}
              style={{ background: 'var(--mantine-color-orange-6)', borderRadius: '50%', flexShrink: 0 }}
              title="Unsaved changes"
            />
          )}
        </Group>
        <Group gap="xs" wrap="nowrap">
          {toolbarExtras}
          {onDiscard && dirty && (
            <Button variant="default" size="xs" onClick={onDiscard} disabled={saving}>Discard</Button>
          )}
          <Button
            size="xs"
            leftSection={<IconDeviceFloppy size={14} />}
            onClick={onSave}
            disabled={!dirty || saving}
            loading={saving}
          >Save</Button>
        </Group>
      </Group>

      {errorMessage && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />} radius={0} p="xs" px="lg">
          <Box ff="var(--mono)" fz="xs">{errorMessage}</Box>
        </Alert>
      )}

      <Box style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
        {/*
          Layout composes two independent splits:
            - bottomPane → vertical split inside the editor column (editor on top, secondary below).
            - rightPane  → horizontal split wrapping that column (column on left, secondary on right).
          They can be active at the same time: e.g. the request editor keeps its Response viewer
          below while the Assistant occupies the right pane.
        */}
        {rightPane ? (
          <PanelGroup direction="horizontal" autoSaveId={`${autoSaveId}:right`} style={{ height: '100%' }}>
            <Panel defaultSize={68} minSize={30}>
              <EditorColumn autoSaveId={autoSaveId} bottomPane={bottomPane} resetKey={title}>{children}</EditorColumn>
            </Panel>
            <PanelResizeHandle className={styles.handleVertical} />
            <Panel defaultSize={32} minSize={20}>
              <Box style={{ height: '100%', overflow: 'hidden', display: 'flex', flexDirection: 'column', background: 'var(--mantine-color-body)' }}>
                <ErrorBoundary label={`${kindLabel} side panel`} resetKeys={[title]}>{rightPane}</ErrorBoundary>
              </Box>
            </Panel>
          </PanelGroup>
        ) : (
          <EditorColumn autoSaveId={autoSaveId} bottomPane={bottomPane} resetKey={title}>{children}</EditorColumn>
        )}
      </Box>
    </Box>
  )
}

/**
 * The editor column: the primary pane, optionally split vertically with a bottom pane
 * (the request Response viewer). Shared by the bare layout and the right-pane layout so the
 * editor + response keep working when a right pane (Assistant) is open.
 */
function EditorColumn({ autoSaveId, bottomPane, children, resetKey }: { autoSaveId: string; bottomPane?: ReactNode; children: ReactNode; resetKey: string }) {
  // Each pane gets its own boundary rather than one around the pair: a crash while rendering
  // a response body should still leave the request editor above it editable.
  const editor = (
    <PrimaryPane>
      <ErrorBoundary label="Editor" resetKeys={[resetKey]}>{children}</ErrorBoundary>
    </PrimaryPane>
  )
  if (!bottomPane) return editor
  return (
    <PanelGroup direction="vertical" autoSaveId={`${autoSaveId}:bottom`} style={{ height: '100%' }}>
      <Panel defaultSize={55} minSize={20}>
        {editor}
      </Panel>
      <PanelResizeHandle className={styles.handleHorizontal} />
      <Panel defaultSize={45} minSize={15}>
        <Box style={{ height: '100%', overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          <ErrorBoundary label="Response panel" resetKeys={[resetKey]}>{bottomPane}</ErrorBoundary>
        </Box>
      </Panel>
    </PanelGroup>
  )
}

/**
 * Title rendered inside the editor header. When `onChange` is supplied, clicking the
 * title (or its pencil affordance) flips it into a TextInput; Enter / blur commits and
 * Escape reverts. When `onChange` is omitted, the title is plain read-only text.
 */
function EditableTitle({ title, onChange }: { title: string; onChange?: (next: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(title)
  const inputRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => { if (!editing) setDraft(title) }, [title, editing])
  useEffect(() => {
    if (editing) {
      // Focus + select on entry so the user can type to replace.
      inputRef.current?.focus()
      inputRef.current?.select()
    }
  }, [editing])

  function commit() {
    setEditing(false)
    const next = draft.trim()
    if (!onChange) return
    if (next.length === 0 || next === title) { setDraft(title); return }
    onChange(next)
  }
  function cancel() {
    setEditing(false)
    setDraft(title)
  }

  if (editing && onChange) {
    return (
      <TextInput
        ref={inputRef}
        value={draft}
        onChange={(e) => setDraft(e.currentTarget.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') { e.preventDefault(); commit() }
          else if (e.key === 'Escape') { e.preventDefault(); cancel() }
        }}
        size="sm"
        styles={{ input: { fontWeight: 600, fontSize: 18, height: 32, minHeight: 32 } }}
        style={{ flex: 1, minWidth: 0, maxWidth: 480 }}
      />
    )
  }

  return (
    <Group gap={4} wrap="nowrap" style={{ minWidth: 0 }}>
      <Title
        order={4}
        fw={600}
        onClick={onChange ? () => setEditing(true) : undefined}
        style={{
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          cursor: onChange ? 'text' : 'default',
        }}
        title={onChange ? 'Click to rename' : undefined}
      >
        {title}
      </Title>
      {onChange && (
        <Tooltip label="Rename" openDelay={400} withArrow>
          <ActionIcon
            variant="subtle"
            size="sm"
            color="gray"
            onClick={() => setEditing(true)}
            aria-label="Rename"
          >
            <IconPencil size={12} />
          </ActionIcon>
        </Tooltip>
      )}
    </Group>
  )
}

/** The scroll-wrapped main editor pane. Extracted so all three layout branches use the
 *  same chrome and overflow rules. The `primaryPane` class full-bleeds a direct-child
 *  Tabs list so its underline meets the editor edges (see EditorShell.module.css). */
function PrimaryPane({ children }: { children: ReactNode }) {
  return (
    <ScrollArea h="100%" type="hover" scrollbarSize={8}>
      <Box className={styles.primaryPane} px="lg" pt={4} pb="md">{children}</Box>
    </ScrollArea>
  )
}

/** Small count badge shown on a Tabs.Tab (params, headers, variables, …). `miw` equal to the
 *  xs badge height makes a single digit a circle; longer counts grow into a pill rather than
 *  clip (which is why this isn't Badge's own `circle`). Renders nothing when the count is zero
 *  so empty sections stay uncluttered. Pass `active` on the selected tab to tint it brand. */
export function TabCount({ count, active = false }: { count: number; active?: boolean }) {
  if (count <= 0) return null
  return (
    <Badge
      size="xs"
      variant="light"
      color={active ? 'tap' : 'gray'}
      ml={6}
      px={3}
      miw={16}
      fz={10}
      style={{ fontVariantNumeric: 'tabular-nums' }}
    >
      {count}
    </Badge>
  )
}

/** Tiny presence dot shown on a Tabs.Tab when a section has content but no meaningful count
 *  (e.g. an auth profile is selected, or docs have been written). */
export function TabDot({ active, color = 'tap' }: { active: boolean; color?: string }) {
  if (!active) return null
  return (
    <Box
      component="span"
      ml={6}
      w={6}
      h={6}
      style={{ display: 'inline-block', borderRadius: '50%', background: `var(--mantine-color-${color}-6)`, flexShrink: 0 }}
    />
  )
}

/** Tiny helper hook: dirty tracker that resets when externals identity changes. */
export function useDirty<T>(initial: T, reset: number): [T, (next: T) => void, boolean, () => void] {
  const [value, setValue] = useState<T>(initial)
  const [savedSnapshot, setSavedSnapshot] = useState<T>(initial)
  useEffect(() => {
    setValue(initial)
    setSavedSnapshot(initial)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reset])
  const dirty = value !== savedSnapshot
  const discard = () => setValue(savedSnapshot)
  return [value, setValue, dirty, discard]
}
