import { ActionIcon, Box, Group, Menu, ScrollArea, Text, UnstyledButton } from '@mantine/core'
import {
  IconArrowsSplit2,
  IconBrandGit,
  IconChecklist,
  IconDotsVertical,
  IconFileCode,
  IconFolder, IconFolders, IconLayoutDashboard, IconLock, IconSend, IconServer, IconSettings, IconWorld, IconX,
  type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import type { WorkspaceFileKind } from '../api/types'
import { useTapStore } from '../store'
import classes from './TabBar.module.css'

const KIND_ICON: Record<WorkspaceFileKind, TablerIcon> = {
  workspace: IconLayoutDashboard,
  request: IconSend,
  auth: IconLock,
  env: IconWorld,
  collection: IconFolders,
  flow: IconArrowsSplit2,
  test: IconChecklist,
  folder: IconFolder,
  settings: IconSettings,
  'git-diff': IconBrandGit,
  httpfile: IconFileCode,
  provider: IconServer,
}
const KIND_COLOR: Partial<Record<WorkspaceFileKind, string>> = {
  request: 'var(--mantine-color-tap-6)',
  collection: 'var(--mantine-color-blue-6)',
  auth: 'var(--mantine-color-orange-6)',
  env: 'var(--mantine-color-grape-6)',
  flow: 'var(--mantine-color-violet-6)',
  test: 'var(--mantine-color-teal-6)',
  settings: 'var(--mantine-color-gray-6)',
  provider: 'var(--mantine-color-blue-6)',
}

const TAB_HEIGHT = 36
const TAB_LABEL_MAX = 180

/** Horizontal tab bar — one tab per open file. Middle-click closes. */
export function TabBar() {
  const tabs = useTapStore((s) => s.tabs)
  const active = useTapStore((s) => s.activeTab)
  const onSelect = useTapStore((s) => s.selectTab)
  const onClose = useTapStore((s) => s.closeTab)
  const onCloseOthers = useTapStore((s) => s.closeOtherTabs)
  const onCloseAll = useTapStore((s) => s.closeAllTabs)

  const viewportRef = useRef<HTMLDivElement>(null)
  const activeRef = useRef<HTMLButtonElement>(null)
  const [menu, setMenu] = useState<{ path: string; x: number; y: number } | null>(null)

  useEffect(() => {
    const el = activeRef.current
    if (!el) return
    el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' })
  }, [active])

  // Translate vertical wheel into horizontal scroll for natural overflow nav.
  useEffect(() => {
    const vp = viewportRef.current
    if (!vp) return
    const onWheel = (e: WheelEvent) => {
      if (e.deltaY === 0 || e.shiftKey) return
      if (vp.scrollWidth <= vp.clientWidth) return
      e.preventDefault()
      vp.scrollBy({ left: e.deltaY, behavior: 'auto' })
    }
    vp.addEventListener('wheel', onWheel, { passive: false })
    return () => vp.removeEventListener('wheel', onWheel)
  }, [])

  if (tabs.length === 0) return null

  return (
    <Box
      style={{
        borderBottom: '1px solid var(--mantine-color-default-border)',
        flexShrink: 0,
        height: TAB_HEIGHT,
        display: 'flex',
        alignItems: 'stretch',
      }}
    >
      <ScrollArea
        type="never"
        viewportRef={viewportRef}
        viewportProps={{ style: { scrollBehavior: 'smooth' } }}
        styles={{ viewport: { height: TAB_HEIGHT } }}
        style={{ flex: 1, minWidth: 0 }}
      >
        <Group gap={0} wrap="nowrap" style={{ height: TAB_HEIGHT }}>
          {tabs.map((t) => {
            const isActive = t.path === active
            const KindIcon = KIND_ICON[t.kind] ?? IconLayoutDashboard
            const color = KIND_COLOR[t.kind] ?? 'var(--mantine-color-dimmed)'
            return (
              <UnstyledButton
                key={t.path}
                ref={isActive ? activeRef : undefined}
                onClick={() => onSelect(t.path)}
                onMouseDown={(e) => { if (e.button === 1) { e.preventDefault(); onClose(t.path) } }}
                onContextMenu={(e) => {
                  e.preventDefault()
                  setMenu({ path: t.path, x: e.clientX, y: e.clientY })
                }}
                className={classes.tab}
                data-active={isActive}
                style={{
                  height: TAB_HEIGHT,
                  flex: '0 0 auto',
                  paddingLeft: 10,
                  paddingRight: 6,
                  borderRight: '1px solid var(--mantine-color-default-border)',
                  borderBottom: isActive ? '2px solid var(--mantine-color-tap-filled)' : '2px solid transparent',
                  marginBottom: -1,
                  background: isActive ? 'var(--mantine-color-body)' : 'transparent',
                  color: isActive ? 'var(--mantine-color-text)' : 'var(--mantine-color-dimmed)',
                  fontSize: 13,
                }}
              >
                <Group gap={6} wrap="nowrap" style={{ height: '100%' }}>
                  <KindIcon size={13} color={color} style={{ flexShrink: 0 }} />
                  <Box
                    style={{
                      maxWidth: TAB_LABEL_MAX,
                      whiteSpace: 'nowrap',
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                    }}
                    title={t.label}
                  >
                    {t.label}
                  </Box>
                  <span className={classes.closeSlot}>
                    <ActionIcon
                      component="div"
                      role="button"
                      size={16}
                      variant="subtle"
                      color="gray"
                      onClick={(e) => { e.stopPropagation(); onClose(t.path) }}
                      aria-label="Close tab"
                      className={classes.close}
                    >
                      <IconX size={11} />
                    </ActionIcon>
                  </span>
                </Group>
              </UnstyledButton>
            )
          })}
        </Group>
      </ScrollArea>
      <TabsOverflowMenu
        tabs={tabs}
        active={active}
        onSelect={onSelect}
        onClose={onClose}
        onCloseAll={onCloseAll}
      />
      {/* Right-click context menu, anchored to cursor position. */}
      {menu && <TabContextMenu
        path={menu.path}
        x={menu.x}
        y={menu.y}
        onlyTab={tabs.length <= 1}
        onClose={() => { onClose(menu.path); setMenu(null) }}
        onCloseOthers={() => { onCloseOthers(menu.path); setMenu(null) }}
        onCloseAll={() => { onCloseAll(); setMenu(null) }}
        onDismiss={() => setMenu(null)}
      />}
    </Box>
  )
}

interface TabsOverflowMenuProps {
  tabs: ReturnType<typeof useTapStore.getState>['tabs']
  active: string | null
  onSelect: (path: string) => void
  onClose: (path: string) => void
  onCloseAll: () => void
}

/** Right-aligned chevron menu — lists every open tab and offers "Close all". */
function TabsOverflowMenu({ tabs, active, onSelect, onClose, onCloseAll }: TabsOverflowMenuProps) {
  return (
    <Menu position="bottom-end" shadow="md" width={360} withinPortal>
      <Menu.Target>
        <ActionIcon
          variant="subtle"
          color="gray"
          size={TAB_HEIGHT - 8}
          aria-label="Open tabs menu"
          style={{
            flexShrink: 0,
            alignSelf: 'center',
            marginRight: 4,
            marginLeft: 4,
            borderRadius: 4,
          }}
        >
          <IconDotsVertical size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item onClick={onCloseAll} disabled={tabs.length === 0}>
          Close all tabs
        </Menu.Item>
        {tabs.length > 0 && <Menu.Divider />}
        {tabs.length > 0 && <Menu.Label>Open tabs</Menu.Label>}
        <ScrollArea.Autosize mah={320} type="auto" scrollbars="y" style={{ overflowX: 'hidden' }}>
          {tabs.map((t) => {
            const isActive = t.path === active
            const KindIcon = KIND_ICON[t.kind] ?? IconLayoutDashboard
            const color = KIND_COLOR[t.kind] ?? 'var(--mantine-color-dimmed)'
            return (
              <Menu.Item
                key={t.path}
                onClick={() => onSelect(t.path)}
                leftSection={<KindIcon size={14} color={color} />}
                rightSection={
                  <ActionIcon
                    component="div"
                    role="button"
                    size="xs"
                    variant="subtle"
                    color="gray"
                    onClick={(e) => {
                      e.stopPropagation()
                      onClose(t.path)
                    }}
                    aria-label={`Close ${t.label}`}
                  >
                    <IconX size={12} />
                  </ActionIcon>
                }
                style={{
                  background: isActive ? 'var(--mantine-color-default-hover)' : undefined,
                  fontWeight: isActive ? 500 : 400,
                }}
                styles={{ itemLabel: { minWidth: 0, overflow: 'hidden' } }}
              >
                <Text size="sm" truncate="end" style={{ display: 'block' }}>
                  {t.label}
                </Text>
              </Menu.Item>
            )
          })}
        </ScrollArea.Autosize>
      </Menu.Dropdown>
    </Menu>
  )
}

interface TabContextMenuProps {
  path: string
  x: number
  y: number
  onlyTab: boolean
  onClose: () => void
  onCloseOthers: () => void
  onCloseAll: () => void
  onDismiss: () => void
}

/** Floating right-click menu — uses a portaled <div> for cursor-anchored positioning
 * instead of Mantine's Menu (which needs a real layout-participating target). */
function TabContextMenu({ x, y, onlyTab, onClose, onCloseOthers, onCloseAll, onDismiss }: TabContextMenuProps) {
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      const t = e.target as HTMLElement
      if (!t.closest('[data-tab-context-menu]')) onDismiss()
    }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onDismiss() }
    window.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
    }
  }, [onDismiss])

  return createPortal(
    <div
      data-tab-context-menu
      role="menu"
      style={{
        position: 'fixed',
        left: x,
        top: y,
        zIndex: 1000,
        minWidth: 160,
        padding: 4,
        background: 'var(--mantine-color-body)',
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        boxShadow: 'var(--mantine-shadow-md)',
        fontSize: 13,
      }}
    >
      <MenuRow label="Close" onClick={onClose} />
      <MenuRow label="Close others" disabled={onlyTab} onClick={onCloseOthers} />
      <MenuRow label="Close all" onClick={onCloseAll} />
    </div>,
    document.body,
  )
}

function MenuRow({ label, onClick, disabled }: { label: string; onClick: () => void; disabled?: boolean }) {
  return (
    <UnstyledButton
      role="menuitem"
      disabled={disabled}
      onClick={onClick}
      style={{
        display: 'block',
        width: '100%',
        textAlign: 'left',
        padding: '6px 10px',
        borderRadius: 4,
        color: disabled ? 'var(--mantine-color-dimmed)' : 'var(--mantine-color-text)',
        cursor: disabled ? 'not-allowed' : 'pointer',
        fontSize: 13,
      }}
      onMouseEnter={(e) => { if (!disabled) (e.currentTarget as HTMLElement).style.background = 'var(--mantine-color-default-hover)' }}
      onMouseLeave={(e) => { (e.currentTarget as HTMLElement).style.background = 'transparent' }}
    >
      {label}
    </UnstyledButton>
  )
}
