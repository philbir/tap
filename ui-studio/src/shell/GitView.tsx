import {
  ActionIcon, Alert, Badge, Box, Button, Checkbox, Code, Divider, Group, Modal, ScrollArea,
  Select, Stack, Text, Textarea, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconArrowDown, IconArrowUp, IconBrandGit, IconCheck, IconCloudDownload, IconGitBranch,
  IconGitCommit, IconGitPullRequest, IconMinus, IconPlus, IconRefresh,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { GitBranch, GitFileChange, GitStatus } from '../api/types'
import { encodeGitDiffTabPath, gitDiffTabLabel } from '../editors/GitDiffEditor'
import { useTapStore } from '../store'

/**
 * Git tab content for the sidebar. Renders staged + unstaged changes as flat checkbox
 * lists (the checkbox is the staging-selection control); clicking a row opens the
 * file's diff as an editor tab via `<GitDiffEditor>`.
 */
export function GitView() {
  const generation = useTapStore((s) => s.generation)
  const openTab = useTapStore((s) => s.openTab)
  const activeTab = useTapStore((s) => s.activeTab)

  const [status, setStatus] = useState<GitStatus | null | undefined>(undefined)
  const [changes, setChanges] = useState<GitFileChange[]>([])
  const [branches, setBranches] = useState<GitBranch[]>([])
  const [selected, setSelected] = useState<Set<string>>(() => new Set())
  const [commitMsg, setCommitMsg] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [createOpen, createControls] = useDisclosure(false)

  const refresh = useCallback(async () => {
    try {
      const s = await api.gitStatus()
      setStatus(s)
      if (s) {
        const [c, b] = await Promise.all([api.gitChanges(), api.gitBranches()])
        setChanges(c)
        setBranches(b)
      }
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh, generation])

  const staged = useMemo(() => changes.filter((c) => c.indexStatus !== null), [changes])
  const unstaged = useMemo(() => changes.filter((c) => c.workingStatus !== null), [changes])

  // Prune stale selection entries when files leave both sections (e.g. after commit).
  useEffect(() => {
    const present = new Set(changes.map((c) => c.path))
    setSelected((cur) => {
      const next = new Set<string>()
      for (const p of cur) if (present.has(p)) next.add(p)
      return next.size === cur.size ? cur : next
    })
  }, [changes])

  function toggle(path: string) {
    setSelected((cur) => {
      const next = new Set(cur)
      if (next.has(path)) next.delete(path); else next.add(path)
      return next
    })
  }
  function toggleAll(paths: string[], on: boolean) {
    setSelected((cur) => {
      const next = new Set(cur)
      for (const p of paths) on ? next.add(p) : next.delete(p)
      return next
    })
  }

  const selectedStaged = useMemo(
    () => staged.filter((c) => selected.has(c.path)).map((c) => c.path),
    [staged, selected],
  )
  const selectedUnstaged = useMemo(
    () => unstaged.filter((c) => selected.has(c.path)).map((c) => c.path),
    [unstaged, selected],
  )

  function openDiffTab(change: GitFileChange, preferredSide: 'working' | 'staged') {
    const side = preferredSide === 'working'
      ? (change.workingStatus !== null ? 'working' : 'staged')
      : (change.indexStatus !== null ? 'staged' : 'working')
    const tabPath = encodeGitDiffTabPath(side, change.path)
    openTab({ path: tabPath, kind: 'git-diff', label: gitDiffTabLabel(side, change.path) })
  }

  async function run(label: string, fn: () => Promise<unknown>) {
    setBusy(label); setError(null)
    try {
      await fn()
      await refresh()
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : String(e)
      setError(msg)
      notifications.show({ color: 'red', title: `${label} failed`, message: msg })
    } finally { setBusy(null) }
  }

  async function handleStage() {
    if (selectedUnstaged.length === 0) return
    await run('Stage', () => api.gitStage(selectedUnstaged))
  }
  async function handleUnstage() {
    if (selectedStaged.length === 0) return
    await run('Unstage', () => api.gitUnstage(selectedStaged))
  }
  async function handleStageAll() {
    const all = unstaged.map((c) => c.path)
    if (all.length === 0) return
    await run('Stage all', () => api.gitStage(all))
  }
  async function handleCommit() {
    const msg = commitMsg.trim()
    if (!msg) return
    await run('Commit', async () => {
      const result = await api.gitCommit(msg)
      setCommitMsg('')
      notifications.show({
        color: 'green',
        title: `Committed ${result.shortSha}`,
        message: result.message,
      })
    })
  }
  async function handleCheckout(name: string) {
    if (!status || name === status.branch) return
    await run('Checkout', () => api.gitCheckout(name))
  }
  async function handleCreateBranch(name: string) {
    await run('Create branch', async () => {
      await api.gitCreateBranch(name, true)
      createControls.close()
    })
  }
  async function handleFetch() {
    await run('Fetch', async () => {
      const r = await api.gitFetch()
      reportRemote('Fetch', r)
    })
  }
  async function handlePull() {
    await run('Pull', async () => {
      const r = await api.gitPull()
      reportRemote('Pull', r)
    })
  }
  async function handlePush() {
    await run('Push', async () => {
      const r = await api.gitPush(true)
      reportRemote('Push', r)
    })
  }

  if (status === undefined) {
    return <Box p="md"><Text size="sm" c="dimmed">Loading git…</Text></Box>
  }
  if (status === null) {
    return (
      <Box p="md">
        <Alert color="gray" variant="light" icon={<IconBrandGit size={14} />}>
          This workspace is not inside a git repository.
        </Alert>
      </Box>
    )
  }

  const branchOptions = branches
    .filter((b) => !b.isRemote)
    .map((b) => ({ value: b.name, label: b.upstream ? `${b.name} → ${b.upstream}` : b.name }))

  return (
    <Stack gap={0} h="100%">
      <Box p="sm">
        <Group gap={4} wrap="nowrap" mb="xs">
          <Select
            flex={1}
            size="xs"
            data={branchOptions}
            value={status.branch}
            onChange={(v) => v && void handleCheckout(v)}
            allowDeselect={false}
            leftSection={<IconGitBranch size={13} />}
            disabled={busy !== null}
          />
          <Tooltip label="New branch" withArrow>
            <ActionIcon size="lg" variant="default" onClick={createControls.open} aria-label="New branch">
              <IconPlus size={14} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="Refresh" withArrow>
            <ActionIcon size="lg" variant="default" onClick={() => void refresh()} aria-label="Refresh git status">
              <IconRefresh size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>

        <Group gap={4} wrap="wrap">
          {status.ahead !== null && status.ahead > 0 && (
            <Badge size="xs" color="blue" variant="light" leftSection={<IconArrowUp size={10} />}>
              {status.ahead}
            </Badge>
          )}
          {status.behind !== null && status.behind > 0 && (
            <Badge size="xs" color="orange" variant="light" leftSection={<IconArrowDown size={10} />}>
              {status.behind}
            </Badge>
          )}
          {status.upstream && (
            <Badge size="xs" color="gray" variant="light" title={status.upstream}>
              {status.upstream}
            </Badge>
          )}
          {!status.upstream && status.hasRemote && (
            <Badge size="xs" color="yellow" variant="light">no upstream</Badge>
          )}
          {!status.hasRemote && (
            <Badge size="xs" color="gray" variant="light">local only</Badge>
          )}
        </Group>

        {status.hasRemote && (
          <Group gap={4} mt="xs" wrap="nowrap">
            <Button
              size="xs" variant="default" flex={1}
              leftSection={<IconCloudDownload size={13} />}
              loading={busy === 'Fetch'}
              onClick={() => void handleFetch()}
            >
              Fetch
            </Button>
            <Button
              size="xs" variant="default" flex={1}
              leftSection={<IconArrowDown size={13} />}
              loading={busy === 'Pull'}
              disabled={!status.upstream}
              onClick={() => void handlePull()}
            >
              Pull
            </Button>
            <Button
              size="xs" variant="default" flex={1}
              leftSection={<IconArrowUp size={13} />}
              loading={busy === 'Push'}
              onClick={() => void handlePush()}
            >
              Push
            </Button>
          </Group>
        )}
      </Box>

      <Divider />

      <ScrollArea flex={1} type="hover" scrollbarSize={8}>
        <Box pb="md">
          <ChangeSection
            title={`Staged (${staged.length})`}
            files={staged}
            selected={selected}
            activeTab={activeTab}
            onToggle={toggle}
            onToggleAll={(on) => toggleAll(staged.map((c) => c.path), on)}
            onActivate={(c) => openDiffTab(c, 'staged')}
            getStatus={(c) => c.indexStatus}
            sideForRow="staged"
            rightAction={
              selectedStaged.length > 0 && (
                <Tooltip label="Unstage selected" withArrow>
                  <ActionIcon
                    size="sm" variant="subtle" color="gray"
                    onClick={() => void handleUnstage()}
                    aria-label="Unstage selected"
                    loading={busy === 'Unstage'}
                  >
                    <IconMinus size={13} />
                  </ActionIcon>
                </Tooltip>
              )
            }
          />

          <ChangeSection
            title={`Changes (${unstaged.length})`}
            files={unstaged}
            selected={selected}
            activeTab={activeTab}
            onToggle={toggle}
            onToggleAll={(on) => toggleAll(unstaged.map((c) => c.path), on)}
            onActivate={(c) => openDiffTab(c, 'working')}
            getStatus={(c) => c.workingStatus}
            sideForRow="working"
            rightAction={
              <Group gap={2} wrap="nowrap">
                {selectedUnstaged.length > 0 && (
                  <Tooltip label="Stage selected" withArrow>
                    <ActionIcon
                      size="sm" variant="subtle" color="gray"
                      onClick={() => void handleStage()}
                      aria-label="Stage selected"
                      loading={busy === 'Stage'}
                    >
                      <IconPlus size={13} />
                    </ActionIcon>
                  </Tooltip>
                )}
                {unstaged.length > 0 && (
                  <Tooltip label="Stage all" withArrow>
                    <ActionIcon
                      size="sm" variant="subtle" color="gray"
                      onClick={() => void handleStageAll()}
                      aria-label="Stage all"
                      loading={busy === 'Stage all'}
                    >
                      <IconCheck size={13} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            }
          />

          {staged.length === 0 && unstaged.length === 0 && (
            <Text size="xs" c="dimmed" ta="center" py="xl" px="md">
              Working tree clean.
            </Text>
          )}
        </Box>
      </ScrollArea>

      <Divider />

      <Box p="sm">
        <Textarea
          size="xs"
          autosize
          minRows={2}
          maxRows={5}
          placeholder="Commit message…"
          value={commitMsg}
          onChange={(e) => setCommitMsg(e.currentTarget.value)}
        />
        <Button
          mt="xs"
          fullWidth
          size="xs"
          leftSection={<IconGitCommit size={14} />}
          loading={busy === 'Commit'}
          disabled={staged.length === 0 || commitMsg.trim().length === 0}
          onClick={() => void handleCommit()}
        >
          Commit {staged.length > 0 && `(${staged.length})`}
        </Button>
        {error && (
          <Alert mt="xs" color="red" variant="light" p="xs">
            <Text size="xs">{error}</Text>
          </Alert>
        )}
      </Box>

      <CreateBranchModal
        opened={createOpen}
        onClose={createControls.close}
        onCreate={handleCreateBranch}
        busy={busy === 'Create branch'}
      />
    </Stack>
  )
}

interface ChangeSectionProps {
  title: string
  files: GitFileChange[]
  selected: Set<string>
  activeTab: string | null
  onToggle: (path: string) => void
  onToggleAll: (on: boolean) => void
  onActivate: (c: GitFileChange) => void
  getStatus: (c: GitFileChange) => string | null
  sideForRow: 'working' | 'staged'
  rightAction?: React.ReactNode
}

function ChangeSection(props: ChangeSectionProps) {
  const { title, files, selected, activeTab, onToggle, onToggleAll, onActivate, getStatus, sideForRow, rightAction } = props
  if (files.length === 0) return null
  const allSelected = files.every((f) => selected.has(f.path))
  const someSelected = files.some((f) => selected.has(f.path))
  return (
    <Box>
      <Group justify="space-between" wrap="nowrap" px="sm" py={6} bg="var(--mantine-color-default-hover)">
        <Group gap={6} wrap="nowrap">
          <Checkbox
            size="xs"
            checked={allSelected}
            indeterminate={!allSelected && someSelected}
            onChange={(e) => onToggleAll(e.currentTarget.checked)}
            aria-label={`Toggle all in ${title}`}
          />
          <Text size="xs" fw={600} c="dimmed" tt="uppercase">{title}</Text>
        </Group>
        {rightAction}
      </Group>
      {files.map((f) => {
        const tabId = encodeGitDiffTabPath(sideForRow, f.path)
        const isActive = activeTab === tabId
        return (
          <Group
            key={f.path}
            gap={6}
            wrap="nowrap"
            px="sm"
            py={4}
            className="tap-tree-row"
            bg={isActive ? 'var(--mantine-color-default-hover)' : undefined}
            style={{
              borderLeft: `3px solid ${isActive ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
              cursor: 'pointer',
            }}
          >
            <Checkbox
              size="xs"
              checked={selected.has(f.path)}
              onChange={(e) => { e.stopPropagation(); onToggle(f.path) }}
              onClick={(e) => e.stopPropagation()}
              aria-label={`Select ${f.path}`}
            />
            <UnstyledButton
              onClick={() => onActivate(f)}
              style={{ display: 'flex', alignItems: 'center', gap: 6, flex: 1, minWidth: 0 }}
            >
              <StatusBadge status={getStatus(f)} />
              <Text size="xs" ff="var(--mono)" truncate flex={1} title={f.path}>
                {f.path}
              </Text>
            </UnstyledButton>
          </Group>
        )
      })}
    </Box>
  )
}

const STATUS_COLOR: Record<string, string> = {
  added: 'green',
  modified: 'yellow',
  deleted: 'red',
  renamed: 'blue',
  typechange: 'grape',
  untracked: 'gray',
  conflicted: 'red',
}

const STATUS_LETTER: Record<string, string> = {
  added: 'A',
  modified: 'M',
  deleted: 'D',
  renamed: 'R',
  typechange: 'T',
  untracked: '?',
  conflicted: '!',
}

function StatusBadge({ status }: { status: string | null }) {
  if (!status) return null
  const color = STATUS_COLOR[status]
  return (
    <Code
      style={{
        background: `var(--mantine-color-${color}-light)`,
        color: `var(--mantine-color-${color}-light-color)`,
        width: 16, textAlign: 'center', fontSize: 10, padding: '0 2px',
      }}
      title={status}
    >
      {STATUS_LETTER[status]}
    </Code>
  )
}

function CreateBranchModal({ opened, onClose, onCreate, busy }: {
  opened: boolean; onClose: () => void
  onCreate: (name: string) => Promise<void>
  busy: boolean
}) {
  const [name, setName] = useState('')
  useEffect(() => { if (opened) setName('') }, [opened])
  return (
    <Modal opened={opened} onClose={onClose} title="Create branch" size="sm">
      <Stack gap="sm">
        <TextInput
          label="Branch name"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && name.trim()) void onCreate(name.trim()) }}
          autoFocus
        />
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button
            leftSection={<IconGitPullRequest size={14} />}
            loading={busy}
            disabled={!name.trim()}
            onClick={() => void onCreate(name.trim())}
          >
            Create & switch
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function reportRemote(label: string, r: { exitCode: number; stdout: string; stderr: string }) {
  if (r.exitCode === 0) {
    const text = (r.stdout || r.stderr || '').trim()
    notifications.show({
      color: 'green',
      title: `${label} OK`,
      message: text.length > 0 ? text : 'Done.',
    })
  } else {
    const text = (r.stderr || r.stdout || `git exited with ${r.exitCode}`).trim()
    notifications.show({ color: 'red', title: `${label} failed`, message: text, autoClose: false })
  }
}
