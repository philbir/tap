import {
  ActionIcon, Anchor, Badge, Box, Button, Code, Group, Select, Stack, Tabs, Text, TextInput, Tooltip,
} from '@mantine/core'
import {
  IconBrandGit, IconCode, IconFolder, IconLayoutDashboard, IconPlug, IconPlus, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { KnownWorkspace, ProviderConfig, WorkspaceDetail, WorkspaceSpec } from '../api/types'
import { modeForProviderType } from '../api/types'
import { useTapStore } from '../store'
import { EditorShell, TabCount } from './EditorShell'
import { SourceTab } from './SourceTab'

/** Variable-provider types the UI offers as quick-add. Custom types are still allowed
 *  by hand-editing the YAML — the server's factory registry is the source of truth. */
const KNOWN_TYPES = [
  { value: 'env', label: 'env — process environment variables (read)' },
  { value: 'file', label: 'file — encrypted at-rest store (read/write)' },
  { value: 'azkv', label: 'azkv — Azure Key Vault (read/write)' },
]

/** Workspace manifest editor — `tap.md`. Typed state; server emits canonical YAML. */
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

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.workspaceManifest().then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d)
      setSpec(initial); setSavedSpec(initial)
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
  }

  async function save() {
    if (!spec) return
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
  // app config, not tap.md. Workspace-origin providers stay editable.
  const workspaceProviders = providers.filter(p => p.origin !== 'system')
  const writableProviderOptions = [
    { value: '', label: '(auto — first writable)' },
    ...providers.filter(p => modeForProviderType(p.type) === 'readwrite').map(p => ({ value: p.name, label: p.name })),
  ]

  return (
    <EditorShell
      title={spec.name} kindLabel="Workspace"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={() => setSpec(savedSpec)}
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
                  key={`${p.origin}:${p.name}`}
                  provider={p}
                  readOnly={p.origin === 'system'}
                  onChange={(next) => updateProvider(i, next)}
                  onRemove={() => removeProvider(i)}
                />
              ))}
              {providers.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="sm">No variable providers registered yet.</Text>
              )}
            </Stack>

            <Group>
              <Select
                placeholder="+ Add variable provider…"
                data={KNOWN_TYPES}
                value={null}
                onChange={(v) => {
                  if (!v) return
                  const name = uniqueName(v, providers)
                  const next: ProviderConfig = { name, type: v, settings: {}, origin: 'workspace' }
                  update('variableProviders', [...providers, next])
                }}
                leftSection={<IconPlus size={14} />}
                w={360}
              />
            </Group>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab path="tap.md" source={detail.source} label="tap.md" />
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

function ProviderRow({
  provider, readOnly, onChange, onRemove,
}: {
  provider: ProviderConfig
  readOnly: boolean
  onChange: (next: ProviderConfig) => void
  onRemove: () => void
}) {
  const settingFields = settingFieldsFor(provider.type)
  const mode = modeForProviderType(provider.type)
  return (
    <Stack
      gap="xs"
      p="sm"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 6,
        background: 'var(--mantine-color-default)',
      }}
    >
      <Group justify="space-between" wrap="nowrap">
        <Group gap="xs" wrap="nowrap">
          <Code fz="sm" fw={600} c="tap.6">{provider.type}</Code>
          <Text size="sm">{provider.name}</Text>
          <Badge size="xs" variant="light" color={mode === 'readwrite' ? 'green' : 'gray'}>
            {mode}
          </Badge>
          <Badge size="xs" variant="light" color={provider.origin === 'system' ? 'blue' : 'tap'}>
            {provider.origin}
          </Badge>
        </Group>
        {!readOnly && (
          <ActionIcon variant="subtle" color="red" size="sm" onClick={onRemove} title="Remove variable provider" aria-label="Remove variable provider">
            <IconX size={14} />
          </ActionIcon>
        )}
      </Group>
      {!readOnly && (
        <TextInput
          label="Name" size="xs" value={provider.name}
          onChange={(e) => onChange({ ...provider, name: e.currentTarget.value })}
        />
      )}
      {settingFields.map((f) => (
        <TextInput
          key={f.key}
          label={f.label}
          description={f.help}
          placeholder={f.placeholder}
          type={f.sensitive ? 'password' : 'text'}
          size="xs"
          value={provider.settings[f.key] ?? ''}
          disabled={readOnly}
          onChange={(e) => onChange({
            ...provider,
            settings: { ...provider.settings, [f.key]: e.currentTarget.value || null },
          })}
        />
      ))}
    </Stack>
  )
}

function settingFieldsFor(type: string): Array<{ key: string; label: string; help?: string; placeholder?: string; sensitive?: boolean }> {
  switch (type) {
    case 'env':
      return []
    case 'file':
      return [
        { key: 'encryptionKey', label: 'Encryption key', help: 'Passphrase used to encrypt secret values. Echoed as *** after save; clearing this field on a saved provider preserves the on-disk key.', sensitive: true, placeholder: '••••••••' },
      ]
    case 'azkv':
      return [
        { key: 'vaultName', label: 'Vault name', help: 'Short name; expanded to https://<name>.vault.azure.net/.', placeholder: 'my-team-kv' },
        { key: 'tenantId', label: 'Tenant ID (optional)' },
        { key: 'prefix', label: 'Key prefix (optional)', help: 'Prepended to each lookup. Tokens still use the unprefixed name.' },
      ]
    default:
      return []
  }
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
