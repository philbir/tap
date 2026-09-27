import { ActionIcon, Anchor, Badge, Box, Button, Group, ScrollArea, Stack, Table, Text, Tooltip } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import {
  IconBrandGit, IconExternalLink, IconFolderOpen, IconPencil, IconPlus, IconRefresh, IconTrash,
} from '@tabler/icons-react'
import { useState } from 'react'
import { api, ApiError } from '../api/client'
import type { GitInfo, KnownWorkspace } from '../api/types'
import { MANIFEST_TAB_PATH, useTapStore } from '../store'
import { DirectoryPicker } from '../shell/DirectoryPicker'

/**
 * Manage workspaces — opens as a tab from the workspace switcher. Lists every known
 * workspace with its folder and git ref, and lets you switch to one, edit its manifest,
 * or forget it. Removing only drops the entry from the known list; nothing on disk is
 * touched, which is why the active workspace cannot be removed — switch away first.
 */
export function WorkspacesEditor() {
  const info = useTapStore((s) => s.info)
  const knownWorkspaces = useTapStore((s) => s.knownWorkspaces)
  const activateWorkspace = useTapStore((s) => s.activateWorkspace)
  const addAndActivateWorkspace = useTapStore((s) => s.addAndActivateWorkspace)
  const reload = useTapStore((s) => s.reload)
  const openTab = useTapStore((s) => s.openTab)
  const [addOpened, addControls] = useDisclosure(false)
  const [busyPath, setBusyPath] = useState<string | null>(null)

  // An AppHost owns the workspace choice; every mutating endpoint answers 409, so say so up
  // front instead of letting each button fail.
  const isPinned = info?.mode === 'aspire'
  // Pinned mode serves the AppHost's folder, whatever the known list last had active.
  const isOpen = (ws: KnownWorkspace) => (isPinned ? ws.path === info?.root : ws.isActive)

  async function run(path: string, action: () => Promise<void>, failTitle: string) {
    setBusyPath(path)
    try { await action() }
    catch (e) {
      notifications.show({ title: failTitle, message: e instanceof ApiError ? e.message : String(e), color: 'red' })
    } finally { setBusyPath(null) }
  }

  function open(ws: KnownWorkspace) {
    void run(ws.path, () => activateWorkspace(ws.path), 'Could not open workspace')
  }

  function edit(ws: KnownWorkspace) {
    void run(ws.path, async () => {
      if (!isOpen(ws)) await activateWorkspace(ws.path)
      openTab({ path: MANIFEST_TAB_PATH, kind: 'workspace', label: 'Workspace' })
    }, 'Could not open workspace')
  }

  function remove(ws: KnownWorkspace) {
    modals.openConfirmModal({
      title: `Remove “${ws.name}”?`,
      children: (
        <Text size="sm">
          Removes it from the workspace list. The folder <Text span ff="monospace" size="xs">{ws.path}</Text> and
          its files stay on disk — add it again any time.
        </Text>
      ),
      labels: { confirm: 'Remove', cancel: 'Keep' },
      confirmProps: { color: 'red' },
      onConfirm: () => void run(ws.path, async () => {
        await api.removeWorkspace(ws.path)
        await reload()
      }, 'Could not remove workspace'),
    })
  }

  return (
    <Box h="100%" style={{ display: 'flex', flexDirection: 'column' }}>
      <Group justify="space-between" align="center" p="md" pb="xs">
        <Stack gap={0}>
          <Text fw={600} size="lg">Workspaces</Text>
          <Text c="dimmed" size="xs">
            {isPinned
              ? 'Pinned by the Aspire AppHost — change it in WithWorkspaceFolder(...).'
              : 'Every workspace Studio knows about. Removing one forgets it; nothing is deleted.'}
          </Text>
        </Stack>
        <Group gap="xs">
          <Button variant="default" leftSection={<IconRefresh size={14} />} onClick={() => void reload()}>
            Reload
          </Button>
          <Button leftSection={<IconPlus size={14} />} onClick={addControls.open} disabled={isPinned}>
            Add workspace
          </Button>
        </Group>
      </Group>

      <ScrollArea style={{ flex: 1 }} px="md" pb="md">
        {knownWorkspaces.length === 0 ? (
          <Text size="sm" c="dimmed" mt="md">No workspaces yet. Add a folder to get started.</Text>
        ) : (
          <Table verticalSpacing="sm" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th>Path</Table.Th>
                <Table.Th>Git</Table.Th>
                <Table.Th w={1} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {knownWorkspaces.map((ws) => (
                <Table.Tr key={ws.path}>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Text size="sm" fw={500}>{ws.name}</Text>
                      {isOpen(ws) && <Badge size="xs" variant="light" color="tap" style={{ flexShrink: 0 }}>Active</Badge>}
                      {!ws.available && <Badge size="xs" variant="light" color="red" style={{ flexShrink: 0 }}>Missing</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Text size="xs" ff="monospace" style={{ wordBreak: 'break-all' }}>{ws.path}</Text>
                  </Table.Td>
                  <Table.Td>
                    {ws.git ? <GitCell git={ws.git} /> : <Text size="xs" c="dimmed">—</Text>}
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      <Tooltip label={isOpen(ws) ? 'Already open' : 'Open'} withArrow>
                        <ActionIcon
                          variant="subtle" color="gray" aria-label={`Open ${ws.name}`}
                          onClick={() => open(ws)}
                          disabled={isPinned || isOpen(ws) || !ws.available}
                          loading={busyPath === ws.path}
                        >
                          <IconFolderOpen size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label="Edit workspace" withArrow>
                        <ActionIcon
                          variant="subtle" color="gray" aria-label={`Edit ${ws.name}`}
                          onClick={() => edit(ws)}
                          disabled={!ws.available || (isPinned && !isOpen(ws))}
                        >
                          <IconPencil size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip
                        label={isOpen(ws) ? 'Switch to another workspace before removing this one' : 'Remove from list'}
                        withArrow
                      >
                        <ActionIcon
                          variant="subtle" color="red" aria-label={`Remove ${ws.name}`}
                          onClick={() => remove(ws)}
                          disabled={isPinned || isOpen(ws)}
                        >
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </ScrollArea>

      <DirectoryPicker
        opened={addOpened}
        onClose={addControls.close}
        onPick={(path) => addAndActivateWorkspace(path)}
      />
    </Box>
  )
}

function GitCell({ git }: { git: GitInfo }) {
  const web = originWebUrl(git.originUrl)
  return (
    <Stack gap={2}>
      <Group gap={4} wrap="nowrap">
        <Badge color="orange" variant="light" size="sm" leftSection={<IconBrandGit size={11} />}>
          {git.isDetached ? `detached @ ${git.branch}` : git.branch}
        </Badge>
      </Group>
      {git.originUrl && (
        web ? (
          <Anchor href={web} target="_blank" rel="noreferrer noopener" size="xs" style={{ wordBreak: 'break-all' }}>
            {git.originUrl} <IconExternalLink size={10} />
          </Anchor>
        ) : (
          <Text size="xs" c="dimmed" style={{ wordBreak: 'break-all' }}>{git.originUrl}</Text>
        )
      )}
    </Stack>
  )
}

/** Browser URL for a git remote: https remotes as-is, scp-style `git@host:owner/repo.git`
 *  rewritten to `https://host/owner/repo`. Anything else gets no link. */
function originWebUrl(url: string | null): string | null {
  if (!url) return null
  // Strip any userinfo — a token embedded in the remote must not end up in a link.
  if (/^https?:\/\//.test(url)) return url.replace(/\/\/[^@/]+@/, '//').replace(/\.git$/, '')
  const scp = /^[\w.-]+@([\w.-]+):(.+?)(\.git)?$/.exec(url)
  return scp ? `https://${scp[1]}/${scp[2]}` : null
}
