import {
  ActionIcon, Alert, Anchor, Badge, Box, Button, Code, Divider, Group, Loader, ScrollArea, Select, Stack, Table, Tabs,
  Text, TextInput, Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import Editor, { type BeforeMount, type OnMount } from '@monaco-editor/react'
import * as monaco from 'monaco-editor'
import {
  IconAlertCircle, IconCheck, IconCode, IconDeviceFloppy, IconEye, IconEyeOff, IconKey, IconPlus,
  IconRefresh, IconSearch, IconServer, IconSparkles, IconTrash, IconVariable, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  AiConfig, AiModelOption, AiProviderName, SaveAiConfig, SaveSystemSettings, SystemProvider, SystemSettings, SystemVariable,
} from '../api/types'
import { ensureThemes, useMonacoTheme } from './monacoSetup'
import { TabCount } from './EditorShell'

/**
 * Settings editor — opens as a tab. Two sub-tabs:
 *
 *  - **Variable providers** — system-scope provider configs persisted to
 *    `$TAP_SYSTEM_DIR/system.json`. The built-in `system` provider is hidden; users can
 *    add any of the host's known provider types (env / file / azkv / …).
 *  - **System variables** — flat name/value entries exposed through the implicit `system`
 *    provider. Per-variable `secret` toggle marks the value sensitive; secret values are
 *    redacted on read and round-tripped blank on save (empty + secret = keep as-is).
 */
export function SettingsEditor() {
  const [data, setData] = useState<SystemSettings | null>(null)
  const [defaultProvider, setDefaultProvider] = useState<string | null>(null)
  const [providers, setProviders] = useState<DraftProvider[]>([])
  const [variables, setVariables] = useState<DraftVariable[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true); setError(null)
    try {
      const s = await api.systemSettings()
      setData(s)
      setDefaultProvider(s.defaultVariableProvider)
      setProviders(s.variableProviders.map(toDraftProvider))
      setVariables(s.variables.map(toDraftVariable))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setLoading(false) }
  }

  useEffect(() => { void load() }, [])

  const dirty = useMemo(() => {
    if (!data) return false
    const cleanedProviders = providers.map(({ id: _id, ...p }) => ({
      name: p.name.trim(),
      type: p.type,
      settings: p.settings.reduce<Record<string, string | null>>((acc, kv) => {
        if (kv.key.trim()) acc[kv.key.trim()] = kv.value
        return acc
      }, {}),
    }))
    const cleanedVariables = variables
      .filter((v) => v.name.trim())
      .map((v) => ({ name: v.name.trim(), value: v.value, secret: v.secret }))
    return JSON.stringify({
      defaultVariableProvider: defaultProvider,
      variableProviders: cleanedProviders,
      variables: cleanedVariables,
    }) !== JSON.stringify({
      defaultVariableProvider: data.defaultVariableProvider,
      variableProviders: data.variableProviders.map((p) => ({ name: p.name, type: p.type, settings: p.settings })),
      variables: data.variables.map((v) => ({ name: v.name, value: v.value, secret: v.secret })),
    })
  }, [data, defaultProvider, providers, variables])

  async function save() {
    setBusy(true); setError(null)
    try {
      const body: SaveSystemSettings = {
        defaultVariableProvider: defaultProvider,
        variableProviders: providers
          .filter((p) => p.name.trim() && p.type)
          .map<SystemProvider>((p) => ({
            name: p.name.trim(),
            type: p.type,
            settings: p.settings.reduce<Record<string, string | null>>((acc, kv) => {
              if (kv.key.trim()) acc[kv.key.trim()] = kv.value
              return acc
            }, {}),
          })),
        variables: variables
          .filter((v) => v.name.trim())
          .map<SystemVariable>((v) => ({ name: v.name.trim(), value: v.value, secret: v.secret })),
      }
      await api.saveSystemSettings(body)
      notifications.show({ color: 'teal', title: 'Saved', message: 'System settings updated.' })
      await load()
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : String(e)
      setError(msg)
      notifications.show({ color: 'red', title: 'Save failed', message: msg })
    } finally { setBusy(false) }
  }

  if (loading) {
    return (
      <Stack align="center" justify="center" h="100%" gap="xs">
        <Loader />
        <Text size="sm" c="dimmed">Loading settings…</Text>
      </Stack>
    )
  }
  if (!data) {
    return (
      <Stack align="center" justify="center" h="100%" gap="xs" p="md">
        <Alert color="red" icon={<IconAlertCircle size={16} />}>{error ?? 'Failed to load settings.'}</Alert>
        <Button variant="default" leftSection={<IconRefresh size={14} />} onClick={() => void load()}>Retry</Button>
      </Stack>
    )
  }

  const availableTypes = data.availableProviderTypes

  return (
    <Box h="100%" style={{ display: 'flex', flexDirection: 'column' }}>
      <Group justify="space-between" align="center" p="md" pb="xs">
        <Stack gap={0}>
          <Group gap="xs">
            <Text fw={600} size="lg">Settings</Text>
            <Tooltip label={data.systemDir} withArrow>
              <Code fz="xs">{shortenHome(data.systemDir)}/system.json</Code>
            </Tooltip>
          </Group>
          <Text c="dimmed" size="xs">
            User-level configuration. Set <Code fz="xs">TAP_SYSTEM_DIR</Code> to override the location.
          </Text>
        </Stack>
        <Group gap="xs">
          <Button
            variant="default"
            leftSection={<IconRefresh size={14} />}
            onClick={() => void load()}
            disabled={busy}
          >
            Reload
          </Button>
          <Button
            leftSection={<IconDeviceFloppy size={14} />}
            onClick={() => void save()}
            loading={busy}
            disabled={!dirty}
          >
            Save
          </Button>
        </Group>
      </Group>

      {error && (
        <Box px="md" pb="xs">
          <Alert color="red" icon={<IconAlertCircle size={14} />}>{error}</Alert>
        </Box>
      )}

      <Tabs defaultValue="providers" keepMounted={false} style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <Tabs.List px="md">
          <Tabs.Tab value="providers" leftSection={<IconServer size={14} />}>
            Variable providers <TabCount count={providers.length} />
          </Tabs.Tab>
          <Tabs.Tab value="variables" leftSection={<IconVariable size={14} />}>
            System variables <TabCount count={variables.filter((v) => v.name.trim()).length} />
          </Tabs.Tab>
          <Tabs.Tab value="ai" leftSection={<IconSparkles size={14} />}>
            AI assistant
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>
            Source
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="providers" style={{ flex: 1, minHeight: 0 }}>
          <ScrollArea h="100%" type="auto">
            <Box p="md">
              <ProvidersTab
                providers={providers}
                availableTypes={availableTypes}
                defaultProvider={defaultProvider}
                onDefaultProviderChange={setDefaultProvider}
                onChange={setProviders}
              />
            </Box>
          </ScrollArea>
        </Tabs.Panel>

        <Tabs.Panel value="variables" style={{ flex: 1, minHeight: 0 }}>
          <ScrollArea h="100%" type="auto">
            <Box p="md">
              <VariablesTab variables={variables} onChange={setVariables} />
            </Box>
          </ScrollArea>
        </Tabs.Panel>

        <Tabs.Panel value="ai" style={{ flex: 1, minHeight: 0 }}>
          <ScrollArea h="100%" type="auto">
            <Box p="md">
              <AiSettingsTab />
            </Box>
          </ScrollArea>
        </Tabs.Panel>

        <Tabs.Panel value="source" style={{ flex: 1, minHeight: 0 }}>
          <Box p="md" h="100%">
            <SourceJsonTab
              defaultProvider={defaultProvider}
              providers={providers}
              variables={variables}
              systemDir={data.systemDir}
              onSaved={() => void load()}
            />
          </Box>
        </Tabs.Panel>
      </Tabs>
    </Box>
  )
}

// ----------- Variable providers tab ---------------------------------------------------

interface SettingKv { id: string; key: string; value: string | null }
interface DraftProvider {
  id: string
  name: string
  type: string
  settings: SettingKv[]
}

function toDraftProvider(p: SystemProvider): DraftProvider {
  return {
    id: nextId(),
    name: p.name,
    type: p.type,
    settings: Object.entries(p.settings).map(([k, v]) => ({ id: nextId(), key: k, value: v })),
  }
}

function ProvidersTab({
  providers, availableTypes, defaultProvider, onDefaultProviderChange, onChange,
}: {
  providers: DraftProvider[]
  availableTypes: string[]
  defaultProvider: string | null
  onDefaultProviderChange: (next: string | null) => void
  onChange: (next: DraftProvider[]) => void
}) {
  const typeOptions = availableTypes.map((t) => ({ value: t, label: t }))
  // Only ReadWrite-ish providers (anything the user has defined here) are valid defaults.
  // The implicit `system` provider is always a ReadWrite candidate; expose it explicitly so
  // users can pick it without typing.
  const defaultOptions = useMemo(() => {
    const names = new Set<string>()
    const out: { value: string; label: string }[] = [{ value: 'system', label: 'system (built-in)' }]
    names.add('system')
    for (const p of providers) {
      const name = p.name.trim()
      if (!name || names.has(name)) continue
      names.add(name)
      out.push({ value: name, label: name })
    }
    return out
  }, [providers])

  const update = (id: string, patch: Partial<DraftProvider>) =>
    onChange(providers.map((p) => (p.id === id ? { ...p, ...patch } : p)))
  const remove = (id: string) => onChange(providers.filter((p) => p.id !== id))
  const add = () => onChange([
    ...providers,
    { id: nextId(), name: '', type: availableTypes[0] ?? '', settings: [] },
  ])

  return (
    <Stack gap="md">
      <Group gap="xs" align="flex-end" wrap="nowrap">
        <Select
          label="Default variable provider"
          description="Used when a write doesn't name a target. Workspace-level defaults shadow this."
          placeholder="(none — first writable provider wins)"
          data={defaultOptions}
          value={defaultProvider}
          onChange={onDefaultProviderChange}
          clearable
          w={320}
        />
      </Group>

      {providers.length === 0 && (
        <Alert color="gray" variant="light" icon={<IconServer size={14} />}>
          No system-scope variable providers configured. Add one to make its variables
          available to every workspace. Workspace-level providers in <Code fz="xs">tap.md</Code> always
          shadow same-named system providers.
        </Alert>
      )}

      {providers.map((p) => (
        <ProviderCard
          key={p.id}
          provider={p}
          typeOptions={typeOptions}
          isDefault={defaultProvider !== null && p.name.trim() === defaultProvider}
          onChange={(patch) => update(p.id, patch)}
          onRemove={() => remove(p.id)}
        />
      ))}

      <Group>
        <Button
          variant="default"
          leftSection={<IconPlus size={14} />}
          onClick={add}
          disabled={availableTypes.length === 0}
        >
          Add provider
        </Button>
      </Group>
    </Stack>
  )
}

function ProviderCard({
  provider, typeOptions, isDefault, onChange, onRemove,
}: {
  provider: DraftProvider
  typeOptions: { value: string; label: string }[]
  isDefault: boolean
  onChange: (patch: Partial<DraftProvider>) => void
  onRemove: () => void
}) {
  function patchSetting(id: string, patch: Partial<SettingKv>) {
    onChange({ settings: provider.settings.map((s) => (s.id === id ? { ...s, ...patch } : s)) })
  }
  function addSetting() {
    onChange({ settings: [...provider.settings, { id: nextId(), key: '', value: '' }] })
  }
  function removeSetting(id: string) {
    onChange({ settings: provider.settings.filter((s) => s.id !== id) })
  }

  return (
    <Box
      p="md"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 6,
        background: 'var(--mantine-color-default-hover)',
      }}
    >
      <Group justify="space-between" align="flex-start" mb="sm">
        <Group gap="xs" align="flex-end" wrap="nowrap">
          <TextInput
            label="Name"
            value={provider.name}
            onChange={(e) => onChange({ name: e.currentTarget.value })}
            placeholder="my-provider"
            w={220}
            styles={{ input: { fontFamily: 'var(--mono)' } }}
          />
          <Select
            label="Type"
            value={provider.type}
            data={typeOptions}
            onChange={(v) => v && onChange({ type: v })}
            w={160}
            allowDeselect={false}
          />
          {isDefault && (
            <Tooltip label="Selected as the system default writable provider." withArrow>
              <Code fz="xs" c="tap" mb={8}>default</Code>
            </Tooltip>
          )}
        </Group>
        <Tooltip label="Remove provider" withArrow>
          <ActionIcon
            variant="subtle"
            color="red"
            onClick={onRemove}
            aria-label="Remove provider"
            mt={22}
          >
            <IconTrash size={16} />
          </ActionIcon>
        </Tooltip>
      </Group>

      <Text size="xs" c="dimmed" mb={4}>Settings</Text>
      {provider.settings.length === 0 && (
        <Text size="xs" c="dimmed" mb="xs">No settings — add rows for the keys this provider expects.</Text>
      )}
      {provider.settings.length > 0 && (
        <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false}>
          <Table.Tbody>
            {provider.settings.map((s) => (
              <Table.Tr key={s.id}>
                <Table.Td style={{ width: '40%' }}>
                  <TextInput
                    size="xs"
                    value={s.key}
                    placeholder="key"
                    onChange={(e) => patchSetting(s.id, { key: e.currentTarget.value })}
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                  />
                </Table.Td>
                <Table.Td>
                  <TextInput
                    size="xs"
                    value={s.value ?? ''}
                    placeholder="value"
                    onChange={(e) => patchSetting(s.id, { value: e.currentTarget.value })}
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                  />
                </Table.Td>
                <Table.Td style={{ width: 32 }}>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    size="sm"
                    onClick={() => removeSetting(s.id)}
                    aria-label="Remove setting"
                  >
                    <IconX size={14} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      <Group mt="xs">
        <Button
          size="xs"
          variant="default"
          leftSection={<IconPlus size={12} />}
          onClick={addSetting}
        >
          Add setting
        </Button>
      </Group>
    </Box>
  )
}

// ----------- System variables tab -----------------------------------------------------

interface DraftVariable { id: string; name: string; value: string; secret: boolean }

function toDraftVariable(v: SystemVariable): DraftVariable {
  return { id: nextId(), name: v.name, value: v.value, secret: v.secret }
}

function VariablesTab({
  variables, onChange,
}: {
  variables: DraftVariable[]
  onChange: (next: DraftVariable[]) => void
}) {
  function patch(id: string, p: Partial<DraftVariable>) {
    onChange(variables.map((v) => (v.id === id ? { ...v, ...p } : v)))
  }
  function remove(id: string) { onChange(variables.filter((v) => v.id !== id)) }
  function add() {
    onChange([...variables, { id: nextId(), name: '', value: '', secret: false }])
  }

  return (
    <Stack gap="sm">
      <Alert color="gray" variant="light" icon={<IconKey size={14} />}>
        Variables here are exposed through the built-in <Code fz="xs">system</Code> provider —
        reference them as <Code fz="xs">{'{{system:NAME}}'}</Code> (or just <Code fz="xs">{'{{NAME}}'}</Code> if
        no other provider has it). Secret values are blanked when read back; leave the
        value empty on a secret row to keep the stored value.
      </Alert>

      {variables.length === 0 && (
        <Text size="sm" c="dimmed" ta="center" py="md">No system variables yet — add one below.</Text>
      )}

      {variables.length > 0 && (
        <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Value</Table.Th>
              <Table.Th style={{ width: 40 }}>Secret</Table.Th>
              <Table.Th style={{ width: 40 }} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {variables.map((v) => (
              <Table.Tr key={v.id}>
                <Table.Td style={{ width: '30%' }}>
                  <TextInput
                    size="xs"
                    value={v.name}
                    placeholder="NAME"
                    onChange={(e) => patch(v.id, { name: e.currentTarget.value })}
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                  />
                </Table.Td>
                <Table.Td>
                  <TextInput
                    size="xs"
                    type={v.secret ? 'password' : 'text'}
                    value={v.value}
                    placeholder={v.secret ? '(secret — leave blank to keep stored)' : 'value'}
                    onChange={(e) => patch(v.id, { value: e.currentTarget.value })}
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                  />
                </Table.Td>
                <Table.Td>
                  <Tooltip label={v.secret ? 'Mark as plain text' : 'Mark as secret'} withArrow>
                    <ActionIcon
                      variant="subtle"
                      color={v.secret ? 'yellow' : 'gray'}
                      size="sm"
                      onClick={() => patch(v.id, { secret: !v.secret })}
                      aria-label="Toggle secret"
                    >
                      {v.secret ? <IconEyeOff size={14} /> : <IconEye size={14} />}
                    </ActionIcon>
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    size="sm"
                    onClick={() => remove(v.id)}
                    aria-label="Remove variable"
                  >
                    <IconX size={14} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Group>
        <Button
          variant="default"
          size="sm"
          leftSection={<IconPlus size={14} />}
          onClick={add}
        >
          Add variable
        </Button>
      </Group>
    </Stack>
  )
}

// ----------- Source tab ---------------------------------------------------------------

/**
 * Raw-JSON view of `system.json`, edited in Monaco. The displayed JSON is the same
 * `{providers, variables}` payload the structured PUT accepts — secrets are already
 * masked (`***`) by the server, and the back-end's mask-roundtrip preserves stored
 * secret values when the user saves without retyping them.
 *
 * Note: this Source tab snapshots from the in-memory drafts (so unsaved structured
 * edits show through), not from the on-disk file. That mirrors how the structured
 * tabs work — switching back and forth doesn't lose pending edits in either direction.
 * Switching tabs without saving still discards Source-tab edits because
 * <c>keepMounted=false</c> tears the panel down.
 */
function SourceJsonTab({
  defaultProvider, providers, variables, systemDir, onSaved,
}: {
  defaultProvider: string | null
  providers: DraftProvider[]
  variables: DraftVariable[]
  systemDir: string
  onSaved: () => void
}) {
  const monacoTheme = useMonacoTheme()
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)
  const saveRef = useRef<() => void>(() => {})

  const snapshot = useMemo(() => {
    const payload: SaveSystemSettings = {
      defaultVariableProvider: defaultProvider,
      variableProviders: providers
        .filter((p) => p.name.trim() && p.type)
        .map((p) => ({
          name: p.name.trim(),
          type: p.type,
          settings: p.settings.reduce<Record<string, string | null>>((acc, kv) => {
            if (kv.key.trim()) acc[kv.key.trim()] = kv.value
            return acc
          }, {}),
        })),
      variables: variables
        .filter((v) => v.name.trim())
        .map((v) => ({ name: v.name.trim(), value: v.value, secret: v.secret })),
    }
    return JSON.stringify(payload, null, 2)
  }, [defaultProvider, providers, variables])

  const [draft, setDraft] = useState(snapshot)
  const [saving, setSaving] = useState(false)
  const [parseError, setParseError] = useState<string | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)

  // Re-sync to the latest snapshot whenever the parent drafts change AND the editor isn't
  // dirty. If the user is mid-edit, we preserve their work.
  const dirty = draft !== snapshot
  useEffect(() => {
    if (!dirty) {
      setDraft(snapshot)
      setParseError(null)
      setSaveError(null)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [snapshot])

  async function save() {
    setSaving(true); setParseError(null); setSaveError(null)
    let parsed: SaveSystemSettings
    try {
      const raw: unknown = JSON.parse(draft)
      if (!raw || typeof raw !== 'object' || Array.isArray(raw)
          || !('variableProviders' in raw) || !('variables' in raw)
          || !Array.isArray((raw as Record<string, unknown>).variableProviders)
          || !Array.isArray((raw as Record<string, unknown>).variables)) {
        throw new Error('Expected an object with `variableProviders: []` and `variables: []`.')
      }
      const obj = raw as Record<string, unknown>
      const defaultRaw = obj.defaultVariableProvider
      parsed = {
        defaultVariableProvider: typeof defaultRaw === 'string' && defaultRaw.trim() !== '' ? defaultRaw : null,
        variableProviders: obj.variableProviders as SystemProvider[],
        variables: obj.variables as SystemVariable[],
      }
    } catch (e) {
      setParseError(e instanceof Error ? e.message : String(e))
      setSaving(false)
      return
    }
    try {
      await api.saveSystemSettings(parsed)
      notifications.show({ color: 'teal', title: 'Saved', message: 'system.json updated.' })
      onSaved()
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : String(e)
      setSaveError(msg)
      notifications.show({ color: 'red', title: 'Save failed', message: msg })
    } finally {
      setSaving(false)
    }
  }
  saveRef.current = save

  const beforeMount: BeforeMount = (m) => ensureThemes(m)
  const onMount: OnMount = (editor) => {
    editorRef.current = editor
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current())
  }

  return (
    <Stack gap="xs" h="100%">
      <Group justify="space-between" align="center">
        <Tooltip label={systemDir + '/system.json'} withArrow>
          <Code>system.json</Code>
        </Tooltip>
        <Group gap="xs">
          {dirty && (
            <Button
              variant="default" size="xs"
              onClick={() => { setDraft(snapshot); setParseError(null); setSaveError(null) }}
              disabled={saving}
            >
              Revert
            </Button>
          )}
          <Button
            size="xs"
            leftSection={<IconDeviceFloppy size={12} />}
            onClick={() => void save()}
            disabled={!dirty || saving}
            loading={saving}
          >
            Save source
          </Button>
        </Group>
      </Group>
      <Text size="xs" c="dimmed">
        Edits are validated server-side. Secret provider settings and secret variable
        values are masked as <Code fz="xs">"***"</Code> — leaving the mask in place keeps the stored
        value; replacing it writes a new one.
      </Text>

      {parseError && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />} title="Invalid JSON">
          <Text size="sm">{parseError}</Text>
        </Alert>
      )}
      {saveError && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
          <Text size="sm">{saveError}</Text>
        </Alert>
      )}

      <Box
        style={{
          flex: 1,
          minHeight: 0,
          border: '1px solid var(--mantine-color-default-border)',
          borderRadius: 'var(--mantine-radius-sm)',
          overflow: 'hidden',
        }}
      >
        <Editor
          height="100%"
          language="json"
          value={draft}
          onChange={(v) => setDraft(v ?? '')}
          theme={monacoTheme}
          beforeMount={beforeMount}
          onMount={onMount}
          options={{
            minimap: { enabled: false },
            fontSize: 12,
            fontFamily: 'var(--mono)',
            tabSize: 2,
            insertSpaces: true,
            renderWhitespace: 'selection',
            scrollBeyondLastLine: false,
            scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
            automaticLayout: true,
            wordWrap: 'on',
            lineNumbersMinChars: 3,
            padding: { top: 8, bottom: 8 },
          }}
        />
      </Box>
    </Stack>
  )
}

// ----------- helpers ------------------------------------------------------------------

let idCounter = 0
function nextId(): string { return `s-${++idCounter}-${Math.random().toString(36).slice(2, 6)}` }

function shortenHome(p: string): string {
  // Best-effort tilde — we don't know the user profile path in the browser, but the
  // common-case POSIX home (`/Users/<name>` or `/home/<name>`) is easy to spot.
  const m = p.match(/^(\/Users\/[^/]+|\/home\/[^/]+)(\/.*)?$/)
  return m ? '~' + (m[2] ?? '') : p
}

// ----------- AI assistant tab ---------------------------------------------------------

const AI_PROVIDERS: { value: AiProviderName; label: string; docs: string; install: string }[] = [
  { value: 'copilot', label: 'GitHub Copilot CLI', docs: 'https://docs.github.com/copilot/github-copilot-cli', install: 'copilot' },
  { value: 'claude-code', label: 'Claude Code', docs: 'https://claude.com/claude-code', install: 'claude' },
]

/**
 * Self-contained AI config panel. Unlike the providers/variables tabs (which share the
 * page-level Save button), this manages its own load + save against `/api/ai/*` so the AI
 * settings persist independently of the variable-provider settings.
 */
function AiSettingsTab() {
  const [config, setConfig] = useState<AiConfig | null>(null)
  const [provider, setProvider] = useState<AiProviderName>('copilot')
  const [model, setModel] = useState<string | null>(null)
  const [copilotPath, setCopilotPath] = useState('')
  const [claudePath, setClaudePath] = useState('')
  const [models, setModels] = useState<AiModelOption[]>([])
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [detecting, setDetecting] = useState(false)
  const [testing, setTesting] = useState(false)
  const [testMsg, setTestMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true); setError(null)
    try {
      const c = await api.aiConfig()
      setConfig(c)
      setProvider(c.provider ?? 'copilot')
      setModel(c.model)
      setCopilotPath(c.copilotCliPath ?? '')
      setClaudePath(c.claudeCliPath ?? '')
      try { setModels((await api.aiModels()).models) } catch { /* models are best-effort */ }
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setLoading(false) }
  }
  useEffect(() => { void load() }, [])

  const draft = (): SaveAiConfig => ({
    provider,
    model: model?.trim() || null,
    copilotCliPath: copilotPath.trim() || null,
    claudeCliPath: claudePath.trim() || null,
  })

  const dirty = useMemo(() => {
    if (!config) return false
    return JSON.stringify(draft()) !== JSON.stringify({
      provider: config.provider ?? 'copilot',
      model: config.model,
      copilotCliPath: config.copilotCliPath,
      claudeCliPath: config.claudeCliPath,
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, provider, model, copilotPath, claudePath])

  const pathValue = provider === 'copilot' ? copilotPath : claudePath
  const setPathValue = provider === 'copilot' ? setCopilotPath : setClaudePath
  const meta = AI_PROVIDERS.find((p) => p.value === provider)!

  async function detect() {
    setDetecting(true); setTestMsg(null); setError(null)
    try {
      const r = provider === 'copilot'
        ? await api.detectCopilotCli(pathValue.trim() || null)
        : await api.detectClaudeCli(pathValue.trim() || null)
      if (r.ok && r.path) {
        setPathValue(r.path)
        setTestMsg({ ok: true, text: `Found ${r.version ?? 'CLI'} at ${r.path}` })
      } else {
        setTestMsg({ ok: false, text: r.error ?? 'CLI not found.' })
      }
    } catch (e) {
      setTestMsg({ ok: false, text: e instanceof ApiError ? e.message : String(e) })
    } finally { setDetecting(false) }
  }

  async function test() {
    setTesting(true); setTestMsg(null); setError(null)
    try {
      const r = await api.testAiConfig(draft())
      if (r.ok) {
        const ver = r.diagnostics?.cliVersion ? ` — ${r.diagnostics.cliVersion}` : ''
        setTestMsg({ ok: true, text: `Connected${ver}. ${r.modelCount} model(s) available.` })
      } else {
        setTestMsg({ ok: false, text: r.error ?? 'Test failed.' })
      }
    } catch (e) {
      setTestMsg({ ok: false, text: e instanceof ApiError ? e.message : String(e) })
    } finally { setTesting(false) }
  }

  async function save() {
    setBusy(true); setError(null)
    try {
      const saved = await api.saveAiConfig(draft())
      setConfig(saved)
      notifications.show({ color: 'teal', title: 'Saved', message: 'AI settings updated.' })
      try { setModels((await api.aiModels()).models) } catch { /* best-effort */ }
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : String(e)
      setError(msg)
      notifications.show({ color: 'red', title: 'Save failed', message: msg })
    } finally { setBusy(false) }
  }

  if (loading) {
    return (
      <Stack align="center" justify="center" gap="xs" py="xl">
        <Loader size="sm" />
        <Text size="sm" c="dimmed">Loading AI settings…</Text>
      </Stack>
    )
  }

  const modelData = models.map((m) => ({ value: m.id, label: m.name ?? m.id }))

  return (
    <Stack gap="md" maw={640}>
      <Box>
        <Text fw={600} size="sm">Request assistant</Text>
        <Text c="dimmed" size="xs">
          Tap drives a local AI CLI you already have installed — no API keys stored. Pick a
          provider, point Tap at the binary if it isn't auto-detected, then test the connection.
        </Text>
      </Box>

      {error && <Alert color="red" icon={<IconAlertCircle size={14} />}>{error}</Alert>}

      <Select
        label="Provider"
        data={AI_PROVIDERS.map((p) => ({ value: p.value, label: p.label }))}
        value={provider}
        onChange={(v) => { if (v) { setProvider(v as AiProviderName); setTestMsg(null) } }}
        allowDeselect={false}
      />

      <Box>
        <TextInput
          label={`${meta.label} path`}
          description={`Leave blank to auto-detect the "${meta.install}" binary on PATH and common install locations.`}
          placeholder={provider === 'copilot' ? '/opt/homebrew/bin/copilot' : '~/.claude/local/claude'}
          value={pathValue}
          onChange={(e) => setPathValue(e.currentTarget.value)}
          styles={{ input: { fontFamily: 'var(--mono)' } }}
          rightSectionWidth={96}
          rightSection={
            <Button size="compact-xs" variant="light" leftSection={<IconSearch size={12} />} onClick={() => void detect()} loading={detecting}>
              Detect
            </Button>
          }
        />
      </Box>

      <Select
        label="Default model"
        description="Used when a request doesn't pick a specific model."
        placeholder={config?.model ? undefined : 'Provider default'}
        data={modelData}
        value={model}
        onChange={setModel}
        searchable
        clearable
        nothingFoundMessage="Type a model id"
      />

      {testMsg && (
        <Alert
          color={testMsg.ok ? 'teal' : 'red'}
          icon={testMsg.ok ? <IconCheck size={14} /> : <IconAlertCircle size={14} />}
          variant="light"
        >
          {testMsg.text}
        </Alert>
      )}

      <Divider />

      <Group justify="space-between">
        <Anchor href={meta.docs} target="_blank" size="xs" c="dimmed">
          {meta.label} setup docs ↗
        </Anchor>
        <Group gap="xs">
          <Button variant="default" leftSection={<IconSparkles size={14} />} onClick={() => void test()} loading={testing} disabled={busy}>
            Test connection
          </Button>
          <Button leftSection={<IconDeviceFloppy size={14} />} onClick={() => void save()} loading={busy} disabled={!dirty}>
            Save
          </Button>
        </Group>
      </Group>

      {config?.persisted && (
        <Group gap="xs">
          <Badge variant="light" color="gray" size="sm">Saved</Badge>
          <Anchor
            component="button"
            type="button"
            size="xs"
            c="red"
            onClick={async () => {
              try { await api.clearAiConfig(); await load(); notifications.show({ color: 'gray', message: 'AI settings cleared.' }) }
              catch (e) { setError(e instanceof ApiError ? e.message : String(e)) }
            }}
          >
            Reset to defaults
          </Anchor>
        </Group>
      )}
    </Stack>
  )
}
