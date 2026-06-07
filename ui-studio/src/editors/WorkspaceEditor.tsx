import {
  ActionIcon, Badge, Code, Group, Select, Stack, Tabs, Text, TextInput,
} from '@mantine/core'
import {
  IconCode, IconLayoutDashboard, IconPlug, IconPlus, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { ProviderConfig, WorkspaceDetail, WorkspaceSpec } from '../api/types'
import { modeForProviderType } from '../api/types'
import { useTapStore } from '../store'
import { EditorShell } from './EditorShell'
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
            Variable Providers {workspaceProviders.length > 0 && <Text component="span" c="dimmed" ml={6}>{workspaceProviders.length}</Text>}
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="general">
          <Stack gap="md" maw={760}>
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
