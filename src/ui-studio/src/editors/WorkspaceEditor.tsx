import {
  ActionIcon, Anchor, Badge, Box, Button, Code, Collapse, Group, Select, Stack, Tabs, Text, TextInput, Tooltip,
  UnstyledButton,
} from '@mantine/core'
import {
  IconBrandGit, IconChevronDown, IconChevronRight, IconCode, IconFolder, IconLayoutDashboard, IconPlug, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { KnownWorkspace, ProviderConfig, ProviderTypeDescriptor, WorkspaceDetail, WorkspaceSpec } from '../api/types'
import { modeForProviderType } from '../api/types'
import { useTapStore } from '../store'
import { EditorShell, TabCount } from './EditorShell'
import { SourceTab } from './SourceTab'
import {
  BrowseProviderControl, ProviderSettingsFields, ProviderTypeIcon, ProviderTypeSelect,
  TestProviderControl, descriptorFor, useProviderTypes,
} from './providerMeta'

/** Workspace manifest editor — `workspace.tap`. Typed state; server emits canonical YAML. */
export function WorkspaceEditor() {
  const generation = useTapStore((s) => s.generation)
  const envs = useTapStore((s) => s.envs)
  const activeWs = useTapStore((s) => s.knownWorkspaces.find((w) => w.isActive) ?? null)
  const reload = useTapStore((s) => s.reload)

  const [detail, setDetail] = useState<WorkspaceDetail | null>(null)
  const [spec, setSpec] = useState<WorkspaceSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<WorkspaceSpec | null>(null)
  const [tab, setTab] = useState<string | null>('general')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  const { types: providerTypes } = useProviderTypes()
  // Which provider rows are expanded, by position in `spec.variableProviders`. This lives
  // here rather than inside ProviderRow so renaming a provider can't reset it: the row is
  // keyed by index, and index survives an edit to the name.
  const [expandedRows, setExpandedRows] = useState<ReadonlySet<number>>(new Set())

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.workspaceManifest().then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d)
      setSpec(initial); setSavedSpec(initial)
      setExpandedRows(initialExpandedRows(initial))
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

  function update<K extends keyof WorkspaceSpec>(key: K, value: WorkspaceSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
  }

  function updateProvider(idx: number, next: ProviderConfig) {
    const list = [...(spec?.variableProviders ?? [])]
    list[idx] = next
    update('variableProviders', list)
  }

  function removeProvider(idx: number) {
    const list = (spec?.variableProviders ?? []).filter((_, i) => i !== idx)
    update('variableProviders', list.length > 0 ? list : undefined)
    // Rows below the removed one shift up by one — carry their expanded state with them.
    setExpandedRows((cur) => {
      const next = new Set<number>()
      for (const i of cur) {
        if (i < idx) next.add(i)
        else if (i > idx) next.add(i - 1)
      }
      return next
    })
  }

  function toggleProviderRow(idx: number) {
    setExpandedRows((cur) => {
      const next = new Set(cur)
      if (next.has(idx)) next.delete(idx); else next.add(idx)
      return next
    })
  }

  async function save() {
    if (!spec) return
    // A provider row without a name would be silently skipped by the parser on the next
    // load — block the save instead of letting the row vanish from workspace.tap.
    if ((spec.variableProviders ?? []).some((p) => p.origin !== 'system' && !p.name.trim())) {
      setError('Every variable provider needs a name — fill in the empty Name field before saving.')
      return
    }
    setSaving(true); setError(null)
    try {
      await api.saveWorkspaceSpec(spec)
      setSavedSpec(spec)
    } catch (e) { setError(e instanceof ApiError ? e.message : String(e)) }
    finally { setSaving(false) }
  }

  if (!detail || !spec) {
    return (
      <EditorShell title={detail?.name ?? 'Workspace'} kindLabel="Workspace" dirty={false} saving={saving} errorMessage={errorMessage} onSave={save}>
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  const providers = spec.variableProviders ?? []
  // Filter out system-origin providers from the workspace editor — they're managed in
  // app config, not workspace.tap. Workspace-origin providers stay editable.
  const workspaceProviders = providers.filter(p => p.origin !== 'system')
  const isWritable = (p: ProviderConfig) =>
    (descriptorFor(providerTypes, p.type)?.mode ?? modeForProviderType(p.type)) === 'readwrite'
  const writableProviderOptions = [
    { value: '', label: '(auto — first writable)' },
    ...providers.filter(isWritable).map(p => ({ value: p.name, label: p.name })),
  ]

  return (
    <EditorShell
      title={spec.name} kindLabel="Workspace"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      // Discard can change the row count, so the index-keyed expansion state has to be
      // rebuilt for the restored list rather than left pointing at whatever was open.
      onDiscard={() => { setSpec(savedSpec); setExpandedRows(initialExpandedRows(savedSpec)) }}
      onTitleChange={(n) => update('name', n)}
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="md">
          <Tabs.Tab value="general" leftSection={<IconLayoutDashboard size={14} />}>General</Tabs.Tab>
          <Tabs.Tab value="providers" leftSection={<IconPlug size={14} />}>
            Variable Providers <TabCount count={workspaceProviders.length} />
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="general">
          <Stack gap="md" maw={760}>
            {activeWs && <LocationCard workspace={activeWs} onWorkspaceChanged={() => void reload()} />}
            <TextInput
              label="Name"
              value={spec.name}
              onChange={(e) => update('name', e.currentTarget.value)}
            />
            <Select
              label="Default Environment"
              description="Used by tap render and the Studio Render button when no env is explicit."
              data={[{ value: '', label: '(none)' }, ...envs.map((e) => ({ value: e.path, label: e.name }))]}
              value={spec.defaultEnv ?? ''}
              onChange={(v) => update('defaultEnv', v && v !== '' ? v : undefined)}
              allowDeselect={false}
            />
            <Select
              label="Default Variable Provider"
              description="Provider used when the API sets a variable without naming one. Falls back to the first read/write provider when unset."
              data={writableProviderOptions}
              value={spec.defaultVariableProvider ?? ''}
              onChange={(v) => update('defaultVariableProvider', v && v !== '' ? v : null)}
              allowDeselect={false}
            />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="providers">
          <Stack gap="md" maw={840}>
            <Text size="xs" c="dimmed">
              Variable providers registered for this workspace. References like
              {' '}<Code>{'{{azkv:my-secret}}'}</Code> target a specific provider; unprefixed
              {' '}<Code>{'{{name}}'}</Code> walks providers in registration order. Mode
              (read vs. read/write) is a static property of the type. System-level providers
              (from app config) are listed read-only.
            </Text>

            <Stack gap="xs">
              {providers.map((p, i) => (
                <ProviderRow
                  key={i}
                  provider={p}
                  types={providerTypes}
                  readOnly={p.origin === 'system'}
                  expanded={expandedRows.has(i)}
                  onToggle={() => toggleProviderRow(i)}
                  onChange={(next) => updateProvider(i, next)}
                  onRemove={() => removeProvider(i)}
                />
              ))}
              {providers.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="sm">No variable providers registered yet.</Text>
              )}
            </Stack>

            <Group>
              <ProviderTypeSelect
                types={providerTypes}
                placeholder="+ Add variable provider…"
                value={null}
                onChange={(v) => {
                  if (!v) return
                  const name = uniqueName(v, providers)
                  const next: ProviderConfig = { name, type: v, settings: {}, origin: 'workspace' }
                  update('variableProviders', [...providers, next])
                  // Open the new row so its settings are right there to fill in.
                  setExpandedRows((cur) => new Set(cur).add(providers.length))
                }}
                w={360}
              />
            </Group>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab path="workspace.tap" source={detail.source} label="workspace.tap" />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
  )
}

/** Read-only summary of where this workspace lives on disk and the enclosing git repo,
 *  if any. The path comes from the active <see cref="KnownWorkspace"/>; git info was
 *  resolved server-side via LibGit2Sharp and refreshed on every <c>/api/workspaces</c>
 *  list call, so branch + remote URL stay current as the user works. */
function LocationCard({ workspace, onWorkspaceChanged }: { workspace: KnownWorkspace; onWorkspaceChanged: () => Promise<void> | void }) {
  const git = workspace.git
  const [gitError, setGitError] = useState<string | null>(null)
  const [initBusy, setInitBusy] = useState(false)
  const [remoteBusy, setRemoteBusy] = useState(false)
  const [remoteName, setRemoteName] = useState('origin')
  const [remoteUrl, setRemoteUrl] = useState('')
  const [remoteNameTouched, setRemoteNameTouched] = useState(false)
  const [remoteUrlTouched, setRemoteUrlTouched] = useState(false)

  useEffect(() => {
    const origin = git?.remotes.find((r) => r.name === 'origin') ?? git?.remotes[0] ?? null
    setRemoteName(origin?.name ?? 'origin')
    setRemoteUrl(origin?.url ?? '')
    setRemoteNameTouched(false)
    setRemoteUrlTouched(false)
  }, [workspace.path, git?.root, git?.branch, git?.remotes])

  async function initRepository() {
    setInitBusy(true); setGitError(null)
    try {
      await api.gitInit()
      await onWorkspaceChanged()
    } catch (e) {
      setGitError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setInitBusy(false)
    }
  }

  async function saveRemote() {
    const name = remoteName.trim()
    const url = remoteUrl.trim()
    if (!name || !url) return
    setRemoteBusy(true); setGitError(null)
    try {
      await api.gitSetRemote(name, url)
      await onWorkspaceChanged()
      setRemoteUrlTouched(false)
      setRemoteNameTouched(false)
    } catch (e) {
      setGitError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setRemoteBusy(false)
    }
  }

  const hasRemoteWithName = git?.remotes.some((r) => r.name === remoteName.trim()) ?? false
  const remoteDirty = remoteNameTouched || remoteUrlTouched
  const canSaveRemote = remoteName.trim().length > 0 && remoteUrl.trim().length > 0 && remoteDirty

  return (
    <Box
      p="sm"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 6,
        background: 'var(--mantine-color-default)',
      }}
    >
      <Stack gap={6}>
        <Group gap="xs" wrap="nowrap">
          <IconFolder size={14} stroke={1.7} />
          <Text size="xs" c="dimmed" fw={600}>Path</Text>
        </Group>
        <Code fz="xs" style={{ background: 'transparent', padding: 0, wordBreak: 'break-all' }}>
          {workspace.path}
        </Code>
        {git ? (
          <>
            <Group gap="xs" mt={6} wrap="nowrap">
              <IconBrandGit size={14} stroke={1.7} color="var(--mantine-color-orange-6)" />
              <Text size="xs" c="dimmed" fw={600}>Git</Text>
              <Badge size="xs" variant="light" color="orange">{git.branch}</Badge>
              {git.isDetached && <Badge size="xs" variant="light" color="gray">detached</Badge>}
            </Group>
            {git.root !== workspace.path && (
              <Text size="xs" c="dimmed">
                Repository root:{' '}
                <Code fz="xs" style={{ background: 'transparent', padding: 0 }}>{git.root}</Code>
              </Text>
            )}
            {git.remotes.length > 0 ? (
              <Stack gap={2} mt={2}>
                {git.remotes.map((r) => (
                  <Group key={r.name} gap="xs" wrap="nowrap">
                    <Badge size="xs" variant="outline" color="gray">{r.name}</Badge>
                    {isHttpUrl(r.url) ? (
                      <Tooltip label="Open remote" withArrow>
                        <Anchor href={r.url} target="_blank" rel="noopener noreferrer" size="xs">
                          {r.url}
                        </Anchor>
                      </Tooltip>
                    ) : (
                      <Code fz="xs" style={{ background: 'transparent', padding: 0, wordBreak: 'break-all' }}>
                        {r.url}
                      </Code>
                    )}
                  </Group>
                ))}
              </Stack>
            ) : (
              <Text size="xs" c="dimmed">No remotes configured.</Text>
            )}
            <Stack gap={6} mt={8}>
              <Group gap="xs" wrap="nowrap">
                <TextInput
                  label="Remote name"
                  size="xs"
                  value={remoteName}
                  onChange={(e) => { setRemoteName(e.currentTarget.value); setRemoteNameTouched(true) }}
                  w={120}
                />
                <TextInput
                  label="Remote URL"
                  size="xs"
                  placeholder="https://github.com/org/repo.git"
                  value={remoteUrl}
                  onChange={(e) => { setRemoteUrl(e.currentTarget.value); setRemoteUrlTouched(true) }}
                  style={{ flex: 1 }}
                />
              </Group>
              <Group justify="flex-end">
                <Button size="xs" variant="light" onClick={() => void saveRemote()} loading={remoteBusy} disabled={!canSaveRemote}>
                  {hasRemoteWithName ? 'Update remote' : 'Add remote'}
                </Button>
              </Group>
            </Stack>
          </>
        ) : (
          <Stack gap={6} mt={6}>
            <Group gap="xs" wrap="nowrap">
              <IconBrandGit size={14} stroke={1.7} style={{ opacity: 0.4 }} />
              <Text size="xs" c="dimmed">Not under git version control.</Text>
            </Group>
            <Group justify="flex-end">
              <Button size="xs" variant="light" onClick={() => void initRepository()} loading={initBusy}>
                Init git repository
              </Button>
            </Group>
          </Stack>
        )}
        {gitError && (
          <Text size="xs" c="red">{gitError}</Text>
        )}
      </Stack>
    </Box>
  )
}

function isHttpUrl(url: string): boolean {
  return url.startsWith('http://') || url.startsWith('https://')
}

/** One provider row: a collapsed summary line that expands to the name + settings form.
 *  `expanded` is owned by the parent — keeping it local would tie it to the row's mount,
 *  and any re-key (a rename) would silently collapse the row mid-edit. */
function ProviderRow({
  provider, types, readOnly, expanded, onToggle, onChange, onRemove,
}: {
  provider: ProviderConfig
  types: ProviderTypeDescriptor[]
  readOnly: boolean
  expanded: boolean
  onToggle: () => void
  onChange: (next: ProviderConfig) => void
  onRemove: () => void
}) {
  const descriptor = descriptorFor(types, provider.type)
  const mode = descriptor?.mode ?? modeForProviderType(provider.type)
  const summary = descriptor?.fields
    .find((f) => f.kind === 'text' && (provider.settings[f.key] ?? '').trim() !== '')
  const summaryValue = summary ? provider.settings[summary.key] : null

  return (
    <Box
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 6,
        background: 'var(--mantine-color-default)',
      }}
    >
      <Group justify="space-between" wrap="nowrap" p="sm">
        <UnstyledButton
          onClick={onToggle}
          style={{ flex: 1, minWidth: 0 }}
          aria-expanded={expanded}
          aria-label={`${expanded ? 'Collapse' : 'Expand'} provider ${provider.name || '(unnamed)'}`}
        >
          <Group gap="xs" wrap="nowrap">
            {expanded ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
            <ProviderTypeIcon icon={descriptor?.icon} size={16} />
            {provider.name.trim() === '' ? (
              <Text size="sm" c="red" fs="italic">(unnamed)</Text>
            ) : (
              <Text size="sm" fw={600} ff="var(--mono)">{provider.name}</Text>
            )}
            <Text size="sm" c="dimmed">{descriptor?.displayName ?? provider.type}</Text>
            {summaryValue && (
              <Text size="xs" c="dimmed" ff="var(--mono)" truncate>· {summaryValue}</Text>
            )}
            <Badge size="xs" variant="light" color={mode === 'readwrite' ? 'green' : 'gray'}>
              {mode === 'readwrite' ? 'read/write' : 'read-only'}
            </Badge>
            <Badge size="xs" variant="light" color={provider.origin === 'system' ? 'blue' : 'tap'}>
              {provider.origin}
            </Badge>
          </Group>
        </UnstyledButton>
        <Group gap="xs" wrap="nowrap">
          <TestProviderControl
            name={provider.name || null}
            type={provider.type}
            settings={provider.settings}
          />
          {provider.name && (
            <BrowseProviderControl
              providerName={provider.name}
              writable={descriptor?.mode === 'readwrite'}
            />
          )}
          {!readOnly && (
            <ActionIcon variant="subtle" color="red" size="sm" onClick={onRemove} title="Remove variable provider" aria-label="Remove variable provider">
              <IconX size={14} />
            </ActionIcon>
          )}
        </Group>
      </Group>

      <Collapse expanded={expanded}>
        <Stack gap="xs" px="sm" pb="sm">
          {!readOnly && (
            <TextInput
              label="Name" size="xs" value={provider.name}
              onChange={(e) => onChange({ ...provider, name: e.currentTarget.value })}
              required
              error={provider.name.trim() === '' ? 'Name is required' : undefined}
              styles={{ input: { fontFamily: 'var(--mono)' } }}
            />
          )}
          <ProviderSettingsFields
            descriptor={descriptor}
            settings={provider.settings}
            onChange={(settings) => onChange({ ...provider, settings })}
            disabled={readOnly}
            providerName={provider.name || null}
            size="xs"
          />
        </Stack>
      </Collapse>
    </Box>
  )
}

/** Provider rows to open when a spec is (re)loaded: only the unnamed ones. An unnamed row
 *  would be dropped by the parser on the next load, so its required-name error should be
 *  visible without hunting for it. */
function initialExpandedRows(spec: WorkspaceSpec | null): ReadonlySet<number> {
  return new Set((spec?.variableProviders ?? []).flatMap((p, i) => (p.name.trim() === '' ? [i] : [])))
}

function uniqueName(type: string, existing: ProviderConfig[]): string {
  if (!existing.some(p => p.name === type)) return type
  for (let i = 2; i < 99; i++) {
    const candidate = `${type}-${i}`
    if (!existing.some(p => p.name === candidate)) return candidate
  }
  return `${type}-${Date.now()}`
}

function specFromDetail(d: WorkspaceDetail): WorkspaceSpec {
  const vars: Record<string, string> = {}
  for (const [k, v] of Object.entries(d.vars ?? {})) {
    if (v?.default) vars[k] = v.default
  }
  return {
    id: d.id,
    name: d.name,
    defaultEnv: d.defaultEnv ?? undefined,
    variableProviders: d.variableProviders && d.variableProviders.length > 0 ? d.variableProviders : undefined,
    defaultVariableProvider: d.defaultVariableProvider ?? null,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
  }
}
