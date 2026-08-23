import { ActionIcon, Anchor, Badge, Box, Button, Divider, Group, Modal, Select, Stack, Text, TextInput, Tooltip } from '@mantine/core'
import type { ComboboxItem, ComboboxLikeRenderOptionInput } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { IconBrandGit, IconCheck, IconChevronDown, IconDeviceDesktop, IconFolders, IconPencil, IconPlugConnected, IconPlus, IconStack2 } from '@tabler/icons-react'
import { useState, type ReactNode } from 'react'
import { api, ApiError } from '../api/client'
import { MANIFEST_TAB_PATH, useActiveCollection, useEnvSelection, useTapStore } from '../store'
import { DirectoryPicker } from './DirectoryPicker'
import { fileNameFor } from './tapFiles'

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
 *
 * <p>The environment switcher is context-aware, and it is the same control the base-URL chip
 * on a request exposes. With a collection's file in front of you it lists that collection's
 * environments and sets the one that collection runs under; with nothing collection-shaped
 * open there is nothing to narrow by, so it lists every environment and sets the workspace
 * default. See `useEnvSelection`.</p>
 */
export function Header({ rightAction }: Props) {
  const [addOpened, addControls] = useDisclosure(false)
  const [addEnvOpened, addEnvControls] = useDisclosure(false)

  const info = useTapStore((s) => s.info)
  const knownWorkspaces = useTapStore((s) => s.knownWorkspaces)
  const envs = useTapStore((s) => s.envs)
  const activateWorkspace = useTapStore((s) => s.activateWorkspace)
  const addAndActivateWorkspace = useTapStore((s) => s.addAndActivateWorkspace)
  const reload = useTapStore((s) => s.reload)
  const openTab = useTapStore((s) => s.openTab)

  // The collection whose editor is open decides what this switcher is choosing *for*.
  const activeCollection = useActiveCollection()
  const collectionName = useTapStore(
    (s) => s.collections.find((c) => c.slug === activeCollection)?.name ?? activeCollection,
  )
  const { value: activeEnv, options: envChoices, select: selectEnv } = useEnvSelection(activeCollection)

  const isPinned = info?.mode === 'aspire'

  const activeWs = knownWorkspaces.find((w) => w.isActive) ?? null
  const activeEnvSummary = envs.find((e) => e.path === activeEnv) ?? null
  const hasWorkspace = isPinned || activeWs?.available === true

  // Pinned mode bypasses the known-workspace list entirely, so reading the switcher's label
  // from that list would name a workspace this process is not serving. Show what is loaded.
  const wsOptions = isPinned
    ? [{ value: info!.root, label: info!.name }]
    : [
        ...knownWorkspaces.map((w) => ({
          value: w.path,
          label: w.name + (w.available ? '' : ' (missing)'),
        })),
        { value: ADD_WORKSPACE_SENTINEL, label: '+ Add workspace…' },
      ]
  // Grouped so it stays obvious which choices belong to the collection at hand and which are
  // workspace-wide — the same split the base-URL chip draws.
  const scoped = envChoices.filter((e) => e.collections.length > 0)
  const globals = envChoices.filter((e) => e.collections.length === 0)
  const envOptions = [
    { value: '', label: activeCollection ? 'Workspace default' : 'No environment' },
    ...(scoped.length > 0
      ? [{
          group: activeCollection ? (collectionName ?? activeCollection) : 'Assigned to a collection',
          items: scoped.map((e) => ({ value: e.path, label: e.name })),
        }]
      : []),
    ...(globals.length > 0
      ? [{ group: 'Global', items: globals.map((e) => ({ value: e.path, label: e.name })) }]
      : []),
    { value: ADD_ENV_SENTINEL, label: '+ Create environment…' },
  ]

  async function handleWorkspacePick(value: string | null) {
    if (!value) return
    if (value === ADD_WORKSPACE_SENTINEL) { addControls.open(); return }
    if (value === activeWs?.path) return
    try { await activateWorkspace(value) }
    catch (e) { console.error(e) }
  }

  /** Dropdown row: the workspace's own name, with the folder holding it underneath. The closed
   *  input shows the name alone — the folder is a disambiguator you only need while choosing,
   *  and it says nothing extra when a manifest never got a name of its own. Providing this
   *  replaces Mantine's default row, check icon included, so the selected row draws its own. */
  function renderWorkspaceOption({ option, checked }: ComboboxLikeRenderOptionInput<ComboboxItem>) {
    const ws = knownWorkspaces.find((w) => w.path === option.value)
    return (
      <Group gap="xs" wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
        <Box style={{ flex: 1, minWidth: 0 }}>
          <Text size="sm" truncate="end">{option.label}</Text>
          {ws && ws.label !== ws.name && (
            <Text size="xs" c="dimmed" truncate="end">{ws.label}</Text>
          )}
        </Box>
        {checked && <IconCheck size={14} stroke={2} />}
      </Group>
    )
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
    selectEnv(value && value !== '' ? value : null)
  }

  /** Creates an environment and selects it. With a collection open the new env is assigned to
   *  it — that is what "create one from here" means — and lands beside the collection file so
   *  deleting the collection takes it along. */
  async function createEnv(name: string) {
    const slug = nameToSlug(name)
    if (!slug) throw new Error('Pick a name.')
    const path = activeCollection
      ? `collections/${activeCollection}/${fileNameFor('env', slug)}`
      : `environments/${fileNameFor('env', slug)}`
    await api.saveEnvSpec({
      path, id: null, name,
      collections: activeCollection ? [{ collection: activeCollection, baseUrl: null, defaultAuth: null }] : undefined,
    })
    await reload()
    selectEnv(path)
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
            renderOption={renderWorkspaceOption}
            value={isPinned ? (info?.root ?? null) : (activeWs?.path ?? null)}
            onChange={handleWorkspacePick}
            w={260}
            allowDeselect={false}
            // An AppHost owns this choice. Disabling beats leaving it clickable and letting
            // the request come back 409.
            disabled={isPinned}
            leftSection={<IconFolders size={16} stroke={1.7} />}
            rightSectionWidth={52}
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
                    style={{ pointerEvents: 'auto' }}
                  >
                    <IconPencil size={14} />
                  </ActionIcon>
                </Tooltip>
                <IconChevronDown size={14} stroke={1.6} style={{ color: 'var(--mantine-color-dimmed)' }} />
              </Group>
            }
          />
          {isPinned && (
            <Tooltip
              label="Workspace pinned by the Aspire AppHost. Change it in WithWorkspaceFolder(...)."
              withArrow
            >
              <Badge color="grape" variant="light" size="sm" leftSection={<IconPlugConnected size={12} />}>
                Aspire
              </Badge>
            </Tooltip>
          )}
          {(() => {
            const problems = info?.errors ?? []
            const errorCount = problems.filter((e) => e.severity === 'error').length
            const warningCount = problems.length - errorCount
            return (
              <>
                {errorCount > 0 && (
                  <Badge color="red" variant="light" size="sm" title={`${errorCount} workspace error(s)`}>
                    {errorCount} err
                  </Badge>
                )}
                {warningCount > 0 && (
                  <Badge color="yellow" variant="light" size="sm" title={`${warningCount} workspace warning(s)`}>
                    {warningCount} warn
                  </Badge>
                )}
              </>
            )
          })()}
          {activeWs?.git && (
            <Tooltip
              label={
                <Box>
                  <Text size="xs">{activeWs.path}</Text>
                  <Text size="xs">git root: {activeWs.git.root}</Text>
                  {activeWs.git.originUrl && <Text size="xs">origin: {activeWs.git.originUrl}</Text>}
                </Box>
              }
              withArrow
            >
              <Badge
                color="orange" variant="light" size="sm"
                leftSection={<IconBrandGit size={11} />}
                style={{ maxWidth: 180, overflow: 'hidden', textOverflow: 'ellipsis' }}
              >
                {activeWs.git.branch}
              </Badge>
            </Tooltip>
          )}
        </Group>
      </Group>

      <Group gap="sm" wrap="nowrap">
        <Group gap={6} wrap="nowrap">
          <Select
            aria-label="Environment"
            data={envOptions}
            value={envChoices.some((e) => e.path === activeEnv) ? activeEnv! : ''}
            onChange={handleEnvPick}
            w={240}
            allowDeselect={false}
            disabled={!hasWorkspace}
            leftSection={<IconStack2 size={16} stroke={1.7} />}
            rightSectionWidth={52}
            rightSection={
              <Group gap={2} wrap="nowrap" pr={4}>
                <Tooltip
                  label={activeEnvSummary
                    ? `Edit environment${activeCollection ? ` — chosen for ${collectionName}` : ''}`
                    : 'No environment selected'}
                  withArrow
                >
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    size="sm"
                    onClick={(e) => { e.stopPropagation(); openEnvTab() }}
                    onMouseDown={(e) => e.stopPropagation()}
                    disabled={!activeEnvSummary}
                    aria-label="Edit environment"
                    style={{ pointerEvents: 'auto' }}
                  >
                    <IconPencil size={14} />
                  </ActionIcon>
                </Tooltip>
                <IconChevronDown size={14} stroke={1.6} style={{ color: 'var(--mantine-color-dimmed)' }} />
              </Group>
            }
          />
        </Group>

        {isPinned && <DesktopAppLink />}

        <Divider orientation="vertical" />

        {rightAction}
      </Group>

      <DirectoryPicker
        opened={addOpened}
        onClose={addControls.close}
        onPick={(path) => addAndActivateWorkspace(path)}
      />
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
        {slug && <Text size="xs" c="dimmed">Created at <code>.tap/environments/{slug}.env.tap</code></Text>}
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

/** Where the download lives. Points at the docs' install section rather than straight at the
 *  release assets, so the reader gets the per-platform picker and the auto-update note instead
 *  of a bare file list. */
const STUDIO_INSTALL_URL = 'https://philbir.github.io/tap/#studio-install'

/**
 * Desktop-app pointer, shown only when an Aspire AppHost is hosting this Studio.
 *
 * That is the one context where the recommendation is unambiguous: a Studio started by an
 * AppHost lives in a browser tab that dies with `aspire run`, and its workspace is a folder in
 * the repo the developer already has checked out. The desktop app opens the same workspace
 * without the AppHost having to be up. Outside aspire mode the user already chose how they
 * launched Studio, and advertising at them would just be noise.
 */
function DesktopAppLink() {
  return (
    <Tooltip
      label="Tap Studio also ships as a desktop app — same workspace, no AppHost required."
      withArrow
      multiline
      w={260}
    >
      <Anchor
        href={STUDIO_INSTALL_URL}
        target="_blank"
        rel="noreferrer noopener"
        underline="never"
        c="dimmed"
      >
        <Group gap={6} wrap="nowrap">
          <IconDeviceDesktop size={15} stroke={1.7} />
          <Text size="xs" visibleFrom="md">Get the desktop app</Text>
        </Group>
      </Anchor>
    </Tooltip>
  )
}
