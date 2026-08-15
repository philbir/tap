import {
  ActionIcon, Badge, Box, Breadcrumbs, Button, Center, Group, Loader, Modal,
  ScrollArea, Stack, Text, TextInput, Tooltip,
} from '@mantine/core'
import {
  IconArrowUp, IconBrandGit, IconCheck, IconChevronRight, IconFolder, IconFolderOpen,
  IconFolderPlus, IconHome, IconX,
} from '@tabler/icons-react'
import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { BrowseResponse } from '../api/types'

interface Props {
  opened: boolean
  onClose: () => void
  onPick: (path: string) => Promise<void>
  busyExternal?: boolean
}

/**
 * Folder picker for "Add workspace". Server enumerates subdirectories on demand so the
 * Studio works inside a desktop shell where the native browser file picker isn't reliable.
 *
 * Boots at the user's home directory; the breadcrumb lets the user climb back up, and
 * a single click descends into a child. Any folder can be picked.
 */
export function DirectoryPicker({ opened, onClose, onPick, busyExternal }: Props) {
  const [data, setData] = useState<BrowseResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [newFolderName, setNewFolderName] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(async (path?: string) => {
    setLoading(true); setError(null)
    try {
      const r = await api.browse(path)
      setData(r)
      setNewFolderName(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setLoading(false) }
  }, [])

  useEffect(() => {
    if (!opened) return
    setData(null); setError(null); setNewFolderName(null)
    void load(undefined)
  }, [opened, load])

  async function createFolder() {
    if (!data || newFolderName === null) return
    const name = newFolderName.trim()
    if (!name) return
    setCreating(true); setError(null)
    try {
      const created = await api.createDirectory(data.path, name)
      // Descend into the freshly-created folder so the user can keep building inside it.
      setNewFolderName(null)
      await load(created.path)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setCreating(false) }
  }

  async function confirm() {
    if (!data) return
    setBusy(true); setError(null)
    try {
      await onPick(data.path)
      onClose()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  const canConfirm = !!data
  const totalBusy = busy || busyExternal === true

  return (
    <Modal
      opened={opened}
      onClose={() => { if (!totalBusy) onClose() }}
      title="Add workspace"
      size="lg"
    >
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Pick any folder.
        </Text>

        <Group gap={6} wrap="nowrap" justify="space-between">
          <Group gap={4} wrap="nowrap" style={{ minWidth: 0, flex: 1 }}>
            <Tooltip label="Home" withArrow>
              <ActionIcon variant="light" onClick={() => void load(undefined)} disabled={loading}>
                <IconHome size={16} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Up one folder" withArrow>
              <ActionIcon
                variant="light"
                onClick={() => data?.parent && void load(data.parent)}
                disabled={loading || !data?.parent}
              >
                <IconArrowUp size={16} />
              </ActionIcon>
            </Tooltip>
            <Box style={{ minWidth: 0, overflow: 'hidden', flex: 1 }}>
              <PathBreadcrumbs path={data?.path ?? ''} home={data?.home ?? ''} onJump={(p) => void load(p)} />
            </Box>
            <Tooltip label="New folder in this directory" withArrow>
              <ActionIcon
                variant="light"
                onClick={() => setNewFolderName('')}
                disabled={loading || !data || newFolderName !== null}
              >
                <IconFolderPlus size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>

        {newFolderName !== null && (
          <Group gap={6} wrap="nowrap">
            <TextInput
              autoFocus
              placeholder="folder-name"
              value={newFolderName}
              onChange={(e) => setNewFolderName(e.currentTarget.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') { e.preventDefault(); void createFolder() }
                else if (e.key === 'Escape') { e.preventDefault(); setNewFolderName(null) }
              }}
              disabled={creating}
              style={{ flex: 1 }}
              leftSection={<IconFolderPlus size={14} />}
            />
            <Button size="xs" onClick={() => void createFolder()} loading={creating} disabled={!newFolderName.trim()}>
              Create
            </Button>
            <ActionIcon variant="subtle" onClick={() => setNewFolderName(null)} disabled={creating} aria-label="Cancel new folder">
              <IconX size={14} />
            </ActionIcon>
          </Group>
        )}

        <Box
          style={{
            border: '1px solid var(--mantine-color-default-border)',
            borderRadius: 6,
            background: 'var(--mantine-color-default)',
            minHeight: 280,
          }}
        >
          {loading ? (
            <Center mih={280}><Loader size="sm" /></Center>
          ) : !data ? (
            <Center mih={280}><Text c="dimmed" size="sm">No folder loaded.</Text></Center>
          ) : data.entries.length === 0 ? (
            <Center mih={280}><Text c="dimmed" size="sm">This folder has no visible subdirectories.</Text></Center>
          ) : (
            <ScrollArea h={280} type="auto">
              <Stack gap={0} p={4}>
                {data.entries.map((entry) => (
                  <Group
                    key={entry.path}
                    gap="xs"
                    px="sm" py={6}
                    wrap="nowrap"
                    style={{
                      cursor: 'pointer',
                      borderRadius: 4,
                      userSelect: 'none',
                    }}
                    onClick={() => void load(entry.path)}
                    className="dir-picker__row"
                  >
                    {entry.hasTap
                      ? <IconFolderOpen size={16} stroke={1.7} color="var(--mantine-color-tap-6)" />
                      : <IconFolder size={16} stroke={1.7} />}
                    <Text size="sm" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {entry.name}
                    </Text>
                    {entry.hasTap && (
                      <Badge size="xs" variant="light" color="tap">workspace</Badge>
                    )}
                    <IconChevronRight size={14} stroke={1.4} style={{ opacity: 0.4 }} />
                  </Group>
                ))}
              </Stack>
            </ScrollArea>
          )}
        </Box>

        {data && (
          <Group gap="xs" wrap="nowrap">
            {data.isWorkspace && (
              <Badge color="tap" variant="light" leftSection={<IconCheck size={11} />}>existing workspace</Badge>
            )}
            {data.gitRoot && (
              <Tooltip label={`git root: ${data.gitRoot}`} withArrow>
                <Badge color="orange" variant="light" leftSection={<IconBrandGit size={11} />}>
                  git
                </Badge>
              </Tooltip>
            )}
          </Group>
        )}

        {error && <Box c="red" fz="xs">{error}</Box>}

        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose} disabled={totalBusy}>Cancel</Button>
          <Button
            onClick={confirm}
            loading={totalBusy}
            disabled={!canConfirm}
          >
            Add &amp; switch
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/** Renders the current path as a chain of clickable breadcrumb segments, with the
 *  user's home directory collapsed to a `~` to keep the bar short. */
function PathBreadcrumbs({ path, home, onJump }: {
  path: string
  home: string
  onJump: (path: string) => void
}) {
  if (!path) return null
  const isPosix = path.startsWith('/')
  const sep = isPosix ? '/' : '\\'

  let display = path
  let prefixPath = ''
  if (home && (path === home || path.startsWith(home + sep))) {
    display = '~' + path.slice(home.length)
    prefixPath = home
  }

  const segments: { label: string; full: string }[] = []
  if (display.startsWith('~')) {
    segments.push({ label: '~', full: prefixPath })
    const rest = display.slice(1).split(sep).filter(Boolean)
    let cum = prefixPath
    for (const part of rest) {
      cum = cum + sep + part
      segments.push({ label: part, full: cum })
    }
  } else if (isPosix) {
    segments.push({ label: '/', full: '/' })
    const rest = display.split(sep).filter(Boolean)
    let cum = ''
    for (const part of rest) {
      cum = cum + sep + part
      segments.push({ label: part, full: cum })
    }
  } else {
    // Windows-ish path — just show segments as-is.
    const parts = display.split(sep).filter(Boolean)
    let cum = ''
    for (const part of parts) {
      cum = cum ? cum + sep + part : part
      segments.push({ label: part, full: cum })
    }
  }

  return (
    <Breadcrumbs separator="›" styles={{ root: { flexWrap: 'wrap' } }}>
      {segments.map((seg) => (
        <Text
          key={seg.full}
          size="xs"
          c="dimmed"
          style={{ cursor: 'pointer' }}
          onClick={() => onJump(seg.full)}
        >
          {seg.label}
        </Text>
      ))}
    </Breadcrumbs>
  )
}
