import { ActionIcon, Box, Center, Group, Stack, Text, Tooltip } from '@mantine/core'
import { IconLayoutDashboard, IconMoon, IconSettings, IconSun } from '@tabler/icons-react'
import { useCallback, useEffect, useState } from 'react'
import { Panel, PanelGroup, PanelResizeHandle } from 'react-resizable-panels'
import type { TreeNode, WorkspaceFileKind } from './api/types'
import { useDesktopUpdater } from './desktop/desktopUpdater'
import { AuthEditor } from './editors/AuthEditor'
import { CollectionEditor } from './editors/CollectionEditor'
import { CreateNewDialog } from './editors/CreateNewDialog'
import { EnvEditor } from './editors/EnvEditor'
import { FlowEditor } from './editors/FlowEditor'
import { GitDiffEditor } from './editors/GitDiffEditor'
import shellStyles from './editors/EditorShell.module.css'
import { RequestEditor } from './editors/RequestEditor'
import { SettingsEditor } from './editors/SettingsEditor'
import { TestSetEditor } from './editors/TestSetEditor'
import { WorkspaceEditor } from './editors/WorkspaceEditor'
import { collectionDirOf } from './shell/explorerTree'
import { Header } from './shell/Header'
import { Sidebar } from './shell/Sidebar'
import { TabBar } from './shell/TabBar'
import { bootstrapStore, MANIFEST_TAB_PATH, SETTINGS_TAB_PATH, useHasActiveWorkspace, useTapStore } from './store'
import { useTheme } from './workspace/useTheme'
import { labelForPath } from './shell/tapFiles'
import { HttpFileEditor } from './editors/HttpFileEditor'

const HEADER_HEIGHT = 52

/**
 * Top-level layout. A fixed header sits above a horizontal PanelGroup that splits the
 * sidebar (left) from the main workspace (right). The split is draggable; the chosen
 * size persists per-user via the `autoSaveId` localStorage key.
 *
 * Below the workspace's TabBar, each editor renders its own internal split (e.g. the
 * RequestEditor adds a horizontal divider with the Response panel underneath).
 */
export function App() {
  const { theme, toggle: toggleTheme } = useTheme()
  const [createOpen, setCreateOpen] = useState(false)

  // One-shot store bootstrap: kicks off the initial reload and subscribes to SSE.
  useEffect(() => bootstrapStore(), [])

  // Desktop-only: check for app updates once on mount (no-op in the browser).
  useDesktopUpdater()

  const activeTabPath = useTapStore((s) => s.activeTab)
  const tabs = useTapStore((s) => s.tabs)
  const loadError = useTapStore((s) => s.loadError)
  const openTab = useTapStore((s) => s.openTab)
  const hasActiveWorkspace = useHasActiveWorkspace()

  const openFromTree = useCallback((node: TreeNode) => {
    if (node.kind === 'directory') return
    if (node.kind === 'workspace') {
      openTab({ path: MANIFEST_TAB_PATH, kind: 'workspace', label: 'Workspace' })
      return
    }
    if (node.kind === 'httpfile') {
      // The file node opens the raw editor; its request children open the request editor.
      openTab({ path: node.path, kind: 'httpfile', label: node.name })
      return
    }
    if (node.kind === 'collection') {
      // The tree hands us the `collections/<slug>/_collection.tap` metadata file, but the
      // collection editor is keyed on the collection *directory* — open the same tab the
      // Requests view opens for that collection instead of a second, broken one.
      const dir = collectionDirOf(node.path)
      openTab({ path: dir, kind: 'collection', label: node.name || (dir.split('/').pop() ?? dir) })
      return
    }
    openTab({ path: node.path, kind: node.kind, label: node.name })
  }, [openTab])

  const openByPathKind = useCallback((path: string, kind: WorkspaceFileKind) => {
    const label = labelForPath(path)
    openTab({ path, kind, label })
  }, [openTab])

  const active = tabs.find((t) => t.path === activeTabPath) ?? null

  return (
    <Box style={{ display: 'flex', flexDirection: 'column', height: '100vh', overflow: 'hidden' }}>
      <Box
        component="header"
        h={HEADER_HEIGHT}
        style={{
          flexShrink: 0,
          borderBottom: '1px solid var(--mantine-color-default-border)',
          background: 'var(--mantine-color-body)',
        }}
      >
        <Header
          rightAction={
            <Group gap={4} wrap="nowrap">
              <Tooltip label="Settings" withArrow>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="lg"
                  onClick={() =>
                    openTab({ path: SETTINGS_TAB_PATH, kind: 'settings', label: 'Settings' })}
                  aria-label="Open settings"
                >
                  <IconSettings size={18} />
                </ActionIcon>
              </Tooltip>
              <ActionIcon
                variant="subtle"
                color="gray"
                size="lg"
                onClick={toggleTheme}
                aria-label="Toggle color scheme"
                title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
              >
                {theme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
              </ActionIcon>
            </Group>
          }
        />
      </Box>

      <Box style={{ flex: 1, minHeight: 0 }}>
        <PanelGroup direction="horizontal" autoSaveId="tap-studio:shell" style={{ height: '100%' }}>
          <Panel
            defaultSize={20}
            minSize={12}
            maxSize={40}
            style={{ borderRight: '1px solid var(--mantine-color-default-border)' }}
          >
            <Sidebar
              hasActiveWorkspace={hasActiveWorkspace}
              onOpenFile={openFromTree}
              onCreateNew={() => setCreateOpen(true)}
            />
          </Panel>

          <PanelResizeHandle className={shellStyles.handleVertical} />

          <Panel>
            <Box style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
              <TabBar />
              <Box style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
                {!active && !hasActiveWorkspace && <NoWorkspace />}
                {!active && hasActiveWorkspace && <EmptyEditor onCreate={() => setCreateOpen(true)} />}
                {active?.kind === 'workspace' && <WorkspaceEditor />}
                {active?.kind === 'request' && <RequestEditor key={active.path} path={active.path} />}
                {active?.kind === 'auth' && <AuthEditor key={active.path} path={active.path} />}
                {active?.kind === 'env' && <EnvEditor key={active.path} path={active.path} />}
                {active?.kind === 'collection' && <CollectionEditor key={active.path} path={active.path} />}
                {active?.kind === 'test' && <TestSetEditor key={active.path} path={active.path} />}
                {active?.kind === 'flow' && <FlowEditor key={active.path} path={active.path} />}
                {active?.kind === 'settings' && <SettingsEditor />}
                {active?.kind === 'git-diff' && <GitDiffEditor key={active.path} path={active.path} />}
                {active?.kind === 'httpfile' && <HttpFileEditor key={active.path} path={active.path} />}
              </Box>
            </Box>
          </Panel>
        </PanelGroup>
      </Box>

      <CreateNewDialog
        open={createOpen}
        onOpenChange={setCreateOpen}
        onCreated={openByPathKind}
      />

      {loadError && (
        <Box
          pos="fixed"
          bottom={16}
          left={16}
          right={16}
          p="md"
          style={{ background: 'var(--mantine-color-red-light)', color: 'var(--mantine-color-red-9)', borderRadius: 8 }}
        >
          {loadError}
        </Box>
      )}
    </Box>
  )
}

function EmptyEditor({ onCreate }: { onCreate: () => void }) {
  return (
    <Center h="100%">
      <Stack align="center" gap="xs" maw={380} ta="center">
        <IconLayoutDashboard size={36} stroke={1.5} color="var(--mantine-color-dimmed)" />
        <Text fw={600} size="md">Open a file or create one</Text>
        <Text size="sm" c="dimmed">
          Pick a request, collection, auth, or environment from the sidebar — or start fresh.
        </Text>
        <ActionIcon variant="filled" size="lg" onClick={onCreate} mt="xs" aria-label="Create new">
          +
        </ActionIcon>
      </Stack>
    </Center>
  )
}

function NoWorkspace() {
  return (
    <Center h="100%">
      <Stack align="center" gap="xs" maw={380} ta="center">
        <IconLayoutDashboard size={36} stroke={1.5} color="var(--mantine-color-dimmed)" />
        <Text fw={600} size="md">No workspace selected</Text>
        <Text size="sm" c="dimmed">
          Open the workspace switcher in the top-left to pick or add one.
        </Text>
      </Stack>
    </Center>
  )
}
