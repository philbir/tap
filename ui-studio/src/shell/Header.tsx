import { ActionIcon, Badge, Box, Button, Divider, Group, Modal, Select, Stack, Text, TextInput, Tooltip } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { IconChevronDown, IconFolders, IconPencil, IconPlus, IconStack2 } from '@tabler/icons-react'
import { useState, type ReactNode } from 'react'
import { api, ApiError } from '../api/client'
import { MANIFEST_TAB_PATH, useActiveEnv, useTapStore } from '../store'

const ADD_WORKSPACE_SENTINEL = '__add_workspace__'
const ADD_ENV_SENTINEL = '__add_env__'

interface Props {
  /** Slot in the top-right of the header (theme toggle lives here). */
  rightAction: ReactNode
}

/**
 * App header — brand on the far left, then the workspace switcher (with an edit button
 * that opens the workspace manifest in a tab), then the environment switcher (with an
 * edit button that opens the active env file in a tab), then the theme toggle.
 */
export function Header({ rightAction }: Props) {
  const [addOpened, addControls] = useDisclosure(false)
  const [addEnvOpened, addEnvControls] = useDisclosure(false)

  const info = useTapStore((s) => s.info)
  const knownWorkspaces = useTapStore((s) => s.knownWorkspaces)
  const envs = useTapStore((s) => s.envs)
  const activateWorkspace = useTapStore((s) => s.activateWorkspace)
  const addAndActivateWorkspace = useTapStore((s) => s.addAndActivateWorkspace)
  const setActiveEnv = useTapStore((s) => s.setActiveEnv)
  const reload = useTapStore((s) => s.reload)
  const openTab = useTapStore((s) => s.openTab)
  const activeEnv = useActiveEnv()

  const activeWs = knownWorkspaces.find((w) => w.isActive) ?? null
  const activeEnvSummary = envs.find((e) => e.path === activeEnv) ?? null
  const hasWorkspace = activeWs?.available === true

  const wsOptions = [
    ...knownWorkspaces.map((w) => ({
      value: w.path,
      label: w.label + (w.available ? '' : ' (missing)'),
    })),
    { value: ADD_WORKSPACE_SENTINEL, label: '+ Add workspace…' },
  ]
  const envOptions = [
    { value: '', label: 'No environment' },
    ...envs.map((e) => ({ value: e.path, label: e.name })),
    { value: ADD_ENV_SENTINEL, label: '+ Create environment…' },
  ]

  async function handleWorkspacePick(value: string | null) {
    if (!value) return
    if (value === ADD_WORKSPACE_SENTINEL) { addControls.open(); return }
    if (value === activeWs?.path) return
    try { await activateWorkspace(value) }
    catch (e) { console.error(e) }
  }

  function openWorkspaceTab() {
    openTab({ path: MANIFEST_TAB_PATH, kind: 'workspace', label: 'Workspace' })
  }

  function openEnvTab() {
    if (!activeEnvSummary) return
    openTab({ path: activeEnvSummary.path, kind: 'env', label: activeEnvSummary.name })
  }

  function handleEnvPick(value: string | null) {
    if (value === ADD_ENV_SENTINEL) { addEnvControls.open(); return }
    setActiveEnv(value && value !== '' ? value : null)
  }

  async function createEnv(name: string) {
    const slug = nameToSlug(name)
    if (!slug) throw new Error('Pick a name.')
    const path = `environments/${slug}.env.md`
    await api.saveEnvSpec({ path, id: null, name })
    await reload()
    setActiveEnv(path)
  }

  return (
    <Group h="100%" px="md" gap="md" justify="space-between" wrap="nowrap">
      <Group gap="md" wrap="nowrap">
        <Group gap={8} className="tap-brand" wrap="nowrap">
          <img className="tap-brand__icon" src="/tap-studio-icon.svg" alt="Tap Studio" />
        </Group>

        <Divider orientation="vertical" />

        <Group gap={6} wrap="nowrap">
          <Select
            aria-label="Workspace"
            placeholder="No workspace"
            data={wsOptions}
            value={activeWs?.path ?? null}
            onChange={handleWorkspacePick}
            w={260}
            allowDeselect={false}
            leftSection={<IconFolders size={16} stroke={1.7} />}
            rightSectionWidth={52}
            rightSectionPointerEvents="auto"
            rightSection={
              <Group gap={2} wrap="nowrap" pr={4}>
                <Tooltip label="Edit workspace" withArrow>
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    size="sm"
                    onClick={(e) => { e.stopPropagation(); openWorkspaceTab() }}
                    onMouseDown={(e) => e.stopPropagation()}
                    disabled={!hasWorkspace}
                    aria-label="Edit workspace"
                  >
                    <IconPencil size={14} />
                  </ActionIcon>
                </Tooltip>
                <IconChevronDown size={14} stroke={1.6} style={{ color: 'var(--mantine-color-dimmed)', pointerEvents: 'none' }} />
              </Group>
            }
          />
          {info?.errors && info.errors.length > 0 && (
            <Badge color="red" variant="light" size="sm" title={`${info.errors.length} workspace error(s)`}>
              {info.errors.length} err
            </Badge>
          )}
        </Group>
      </Group>

      <Group gap="sm" wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <Select
            aria-label="Environment"
            data={envOptions}
            value={activeEnv ?? ''}
            onChange={handleEnvPick}
            w={240}
            allowDeselect={false}
            disabled={!hasWorkspace}
            leftSection={<IconStack2 size={16} stroke={1.7} />}
            rightSectionWidth={52}
            rightSectionPointerEvents="auto"
            rightSection={
              <Group gap={2} wrap="nowrap" pr={4}>
                <Tooltip label={activeEnvSummary ? 'Edit environment' : 'No environment selected'} withArrow>
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    size="sm"
                    onClick={(e) => { e.stopPropagation(); openEnvTab() }}
                    onMouseDown={(e) => e.stopPropagation()}
                    disabled={!activeEnvSummary}
                    aria-label="Edit environment"
                  >
                    <IconPencil size={14} />
                  </ActionIcon>
                </Tooltip>
                <IconChevronDown size={14} stroke={1.6} style={{ color: 'var(--mantine-color-dimmed)', pointerEvents: 'none' }} />
              </Group>
            }
          />
        </Group>

        <Divider orientation="vertical" />

        {rightAction}
      </Group>

      <AddWorkspaceModal opened={addOpened} onClose={addControls.close} onAdd={addAndActivateWorkspace} />
      <AddEnvModal opened={addEnvOpened} onClose={addEnvControls.close} onAdd={createEnv} />
    </Group>
  )
}

function AddEnvModal({ opened, onClose, onAdd }: {
  opened: boolean
  onClose: () => void
  onAdd: (name: string) => Promise<void>
}) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit() {
    if (!name.trim()) return
    setBusy(true); setError(null)
    try {
      await onAdd(name.trim())
      setName('')
      onClose()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  const slug = nameToSlug(name)

  return (
    <Modal
      opened={opened}
      onClose={() => { setName(''); setError(null); onClose() }}
      title="Create environment"
      size="md"
    >
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Adds a new environment file under <code>.tap/environments/</code> and switches to it.</Text>
        <TextInput
          label="Name"
          placeholder="e.g. Local"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void submit() }}
          autoFocus
        />
        {slug && <Text size="xs" c="dimmed">Created at <code>.tap/environments/{slug}.env.md</code></Text>}
        {error && <Box c="red" fz="xs">{error}</Box>}
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button leftSection={<IconPlus size={14} />} onClick={submit} loading={busy} disabled={!name.trim()}>
            Create & switch
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function nameToSlug(name: string): string {
  return name.trim().toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/-+/g, '-')
    .slice(0, 60)
}

function AddWorkspaceModal({ opened, onClose, onAdd }: {
  opened: boolean
  onClose: () => void
  onAdd: (path: string) => Promise<void>
}) {
  const [path, setPath] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit() {
    if (!path.trim()) return
    setBusy(true); setError(null)
    try {
      await onAdd(path.trim())
      setPath('')
      onClose()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  return (
    <Modal
      opened={opened}
      onClose={() => { setPath(''); setError(null); onClose() }}
      title="Add workspace"
      size="md"
    >
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Absolute path to a folder containing a <code>.tap/</code> subdirectory.</Text>
        <TextInput
          placeholder="/Users/you/projects/myapp"
          value={path}
          onChange={(e) => setPath(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void submit() }}
          autoFocus
          styles={{ input: { fontFamily: 'var(--mono)' } }}
        />
        {error && <Box c="red" fz="xs">{error}</Box>}
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button leftSection={<IconPlus size={14} />} onClick={submit} loading={busy} disabled={!path.trim()}>
            Add & switch
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
