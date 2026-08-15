import {
  ActionIcon, Alert, Anchor, Badge, Box, Button, Code, Drawer, Group, Loader, Select, Stack, Table, Text, TextInput, Tooltip,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconAlertCircle, IconBrandAzure, IconEye, IconFileText, IconListSearch, IconPlug, IconPlugConnected,
  IconRefresh, IconSearch, IconSettings, IconShieldLock, IconTerminal2, IconX,
} from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { ProviderSettingField, ProviderTypeDescriptor, ProviderVariable, TestProviderResult } from '../api/types'
import { AzureVaultPicker } from './AzureVaultPicker'
import { OnePasswordVaultPicker } from './OnePasswordVaultPicker'

/**
 * Shared building blocks for variable-provider configuration UIs (Settings editor,
 * Workspace editor, Env editor). Everything renders from the server's
 * `ProviderTypeDescriptor`s — display name, icon key, and the typed settings schema —
 * so adding a provider type on the backend lights up here without UI changes.
 */

// ---- Icon mapping -----------------------------------------------------------------------

/** Semantic icon key (from the descriptor) → Tabler glyph. Unknown keys get a plug. */
export function ProviderTypeIcon({ icon, size = 16 }: { icon: string | null | undefined; size?: number }) {
  switch (icon) {
    case 'azure': return <IconBrandAzure size={size} color="var(--mantine-color-blue-6)" />
    // 1Password ships no Tabler brand glyph — a shield-lock in their blue reads close enough.
    case '1password': return <IconShieldLock size={size} color="var(--mantine-color-blue-5)" />
    case 'terminal': return <IconTerminal2 size={size} color="var(--mantine-color-teal-6)" />
    case 'file': return <IconFileText size={size} color="var(--mantine-color-orange-6)" />
    case 'settings': return <IconSettings size={size} color="var(--mantine-color-gray-6)" />
    default: return <IconPlug size={size} color="var(--mantine-color-gray-6)" />
  }
}

// ---- Descriptor loading -----------------------------------------------------------------

let typesCache: ProviderTypeDescriptor[] | null = null
let typesPromise: Promise<ProviderTypeDescriptor[]> | null = null

function fetchTypes(): Promise<ProviderTypeDescriptor[]> {
  typesPromise ??= api.providerTypes().then((t) => { typesCache = t; return t })
  return typesPromise
}

/** Provider type descriptors, fetched once per app session and shared across mounts. */
export function useProviderTypes(): { types: ProviderTypeDescriptor[]; loading: boolean } {
  const [types, setTypes] = useState<ProviderTypeDescriptor[]>(typesCache ?? [])
  const [loading, setLoading] = useState(typesCache === null)
  useEffect(() => {
    let cancelled = false
    void fetchTypes()
      .then((t) => { if (!cancelled) { setTypes(t); setLoading(false) } })
      .catch(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [])
  return { types, loading }
}

export function descriptorFor(types: ProviderTypeDescriptor[], type: string): ProviderTypeDescriptor | null {
  return types.find((t) => t.type.toLowerCase() === type.toLowerCase()) ?? null
}

// ---- Type picker ------------------------------------------------------------------------

/**
 * Select over provider types showing icon + display name; the dropdown adds the type's
 * one-line description. `value` holds the type key (`azkv`), the UI shows the display name.
 */
export function ProviderTypeSelect({
  types, value, onChange, label, placeholder, w, disabled,
}: {
  types: ProviderTypeDescriptor[]
  value: string | null
  onChange: (type: string | null) => void
  label?: string
  placeholder?: string
  w?: number | string
  disabled?: boolean
}) {
  const current = value ? descriptorFor(types, value) : null
  return (
    <Select
      label={label}
      placeholder={placeholder}
      data={types.map((t) => ({ value: t.type, label: t.displayName }))}
      value={value}
      onChange={onChange}
      leftSection={current ? <ProviderTypeIcon icon={current.icon} size={14} /> : <IconPlug size={14} />}
      renderOption={({ option }) => {
        const d = descriptorFor(types, option.value)
        return (
          <Group gap="sm" wrap="nowrap">
            <ProviderTypeIcon icon={d?.icon} size={18} />
            <Stack gap={0}>
              <Group gap={6}>
                <Text size="sm" fw={500}>{d?.displayName ?? option.value}</Text>
                {d && (
                  <Badge size="xs" variant="light" color={d.mode === 'readwrite' ? 'green' : 'gray'}>
                    {d.mode === 'readwrite' ? 'read/write' : 'read-only'}
                  </Badge>
                )}
              </Group>
              {d?.description && <Text size="xs" c="dimmed">{d.description}</Text>}
            </Stack>
          </Group>
        )
      }}
      w={w}
      disabled={disabled}
      allowDeselect={false}
    />
  )
}

// ---- Typed settings form ----------------------------------------------------------------

/**
 * Generated settings form for one provider config. Descriptor fields render as typed
 * inputs (secret fields as password inputs); setting keys the descriptor doesn't know
 * are preserved and shown as removable "extra" rows so nothing stored is ever dropped.
 */
export function ProviderSettingsFields({
  descriptor, settings, onChange, disabled, size = 'xs', providerName = null,
}: {
  descriptor: ProviderTypeDescriptor | null
  settings: Record<string, string | null>
  onChange: (next: Record<string, string | null>) => void
  disabled?: boolean
  size?: 'xs' | 'sm'
  /** Name of the provider being edited. Pickers pass it to the server so masked (`***`)
   *  secrets can be restored from the stored config instead of being retyped. */
  providerName?: string | null
}) {
  const allFields = descriptor?.fields ?? []
  const knownKeys = new Set(allFields.map((f) => f.key.toLowerCase()))
  const extraKeys = Object.keys(settings).filter((k) => !knownKeys.has(k.toLowerCase()))

  // A field's effective value falls back to its descriptor default, so a mode switch shows
  // the type's preferred mode before the user has ever touched it. Only real edits persist.
  function effective(key: string): string {
    const stored = settings[key]
    if (stored !== null && stored !== undefined && stored !== '') return stored
    return allFields.find((f) => f.key === key)?.defaultValue ?? ''
  }

  // `visibleWhen` lets a type present a mode switch that reveals only that mode's inputs.
  // Hidden fields keep whatever is stored — switching modes and back loses nothing.
  const fields = allFields.filter((f) =>
    !f.visibleWhen || f.visibleWhen.values.includes(effective(f.visibleWhen.key)))

  function setValue(key: string, value: string) {
    onChange({ ...settings, [key]: value === '' ? null : value })
  }
  function removeKey(key: string) {
    const next = { ...settings }
    delete next[key]
    onChange(next)
  }

  return (
    <Stack gap="xs">
      {descriptor && fields.length === 0 && extraKeys.length === 0 && (
        descriptor.type === 'env' ? (
          <Alert color="gray" variant="light" icon={<IconTerminal2 size={14} />} p="xs">
            <Text size="xs">
              No settings — exposure is controlled on the host via the{' '}
              <Code fz="xs">TAP_VARS_ALLOWED</Code> and <Code fz="xs">TAP_SECRETS_ALLOWED</Code>{' '}
              environment variables (comma-separated glob lists).
            </Text>
          </Alert>
        ) : (
          <Text size="xs" c="dimmed">This provider type has no settings.</Text>
        )
      )}

      {fields.map((f) => {
        if (f.kind === 'select') {
          return (
            <SettingSelect
              key={f.key}
              field={f}
              value={effective(f.key)}
              onChange={(v) => setValue(f.key, v)}
              disabled={disabled}
              size={size}
            />
          )
        }
        switch (f.picker) {
          case 'azure-keyvault':
            return (
              <AzureVaultField
                key={f.key} field={f} settings={settings} onChange={onChange}
                disabled={disabled} size={size}
              />
            )
          case '1password-vault':
            return (
              <OnePasswordVaultField
                key={f.key} field={f} settings={settings} onChange={onChange}
                disabled={disabled} size={size} providerName={providerName}
              />
            )
          case '1password-cli':
            return (
              <OnePasswordCliField
                key={f.key} field={f} settings={settings} onChange={onChange}
                disabled={disabled} size={size} providerName={providerName}
              />
            )
          default:
            return (
              <SettingInput
                key={f.key}
                field={f}
                value={settings[f.key] ?? ''}
                onChange={(v) => setValue(f.key, v)}
                disabled={disabled}
                size={size}
              />
            )
        }
      })}

      {extraKeys.length > 0 && (
        <Box>
          <Text size="xs" c="dimmed" mb={4}>Extra settings (not in this type's schema)</Text>
          <Stack gap={4}>
            {extraKeys.map((k) => (
              <Group key={k} gap="xs" wrap="nowrap">
                <Code fz="xs" style={{ minWidth: 120 }}>{k}</Code>
                <TextInput
                  size={size}
                  value={settings[k] ?? ''}
                  onChange={(e) => setValue(k, e.currentTarget.value)}
                  disabled={disabled}
                  style={{ flex: 1 }}
                  styles={{ input: { fontFamily: 'var(--mono)' } }}
                />
                {!disabled && (
                  <ActionIcon variant="subtle" color="red" size="sm" onClick={() => removeKey(k)} aria-label={`Remove setting ${k}`}>
                    <IconX size={14} />
                  </ActionIcon>
                )}
              </Group>
            ))}
          </Stack>
        </Box>
      )}
    </Stack>
  )
}

/**
 * The `vaultName`-style field with an attached Azure browser: the plain input stays
 * editable, and "Browse…" opens the subscription → vault picker. Picking a vault writes
 * the field and back-fills `tenantId` from the subscription when it isn't set yet.
 */
function AzureVaultField({
  field, settings, onChange, disabled, size,
}: {
  field: ProviderSettingField
  settings: Record<string, string | null>
  onChange: (next: Record<string, string | null>) => void
  disabled?: boolean
  size: 'xs' | 'sm'
}) {
  const [opened, { open, close }] = useDisclosure(false)
  return (
    <>
      <Group gap="xs" align="flex-end" wrap="nowrap">
        <Box style={{ flex: 1 }}>
          <SettingInput
            field={field}
            value={settings[field.key] ?? ''}
            onChange={(v) => onChange({ ...settings, [field.key]: v === '' ? null : v })}
            disabled={disabled}
            size={size}
            hideNote
          />
        </Box>
        <Button
          size={size}
          variant="default"
          leftSection={<IconBrandAzure size={13} />}
          onClick={open}
          disabled={disabled}
        >
          Browse…
        </Button>
      </Group>
      <FieldNote note={field.note} />
      <AzureVaultPicker
        opened={opened}
        onClose={close}
        onSelect={(vault, subscription) => {
          const next = { ...settings, [field.key]: vault.name }
          if (subscription.tenantId && !next['tenantId']) next['tenantId'] = subscription.tenantId
          onChange(next)
        }}
      />
    </>
  )
}

/**
 * The `vault` field with an attached 1Password browser. The plain input stays editable —
 * a vault can be named by ID, and service accounts often know one vault without being able
 * to list any — so "Browse…" is a convenience, never the only way in.
 */
function OnePasswordVaultField({
  field, settings, onChange, disabled, size, providerName,
}: {
  field: ProviderSettingField
  settings: Record<string, string | null>
  onChange: (next: Record<string, string | null>) => void
  disabled?: boolean
  size: 'xs' | 'sm'
  providerName: string | null
}) {
  const [opened, { open, close }] = useDisclosure(false)
  return (
    <>
      <Group gap="xs" align="flex-end" wrap="nowrap">
        <Box style={{ flex: 1 }}>
          <SettingInput
            field={field}
            value={settings[field.key] ?? ''}
            onChange={(v) => onChange({ ...settings, [field.key]: v === '' ? null : v })}
            disabled={disabled}
            size={size}
            hideNote
          />
        </Box>
        <Button
          size={size}
          variant="default"
          leftSection={<IconShieldLock size={13} />}
          onClick={open}
          disabled={disabled}
        >
          Browse…
        </Button>
      </Group>
      <FieldNote note={field.note} />
      <OnePasswordVaultPicker
        opened={opened}
        onClose={close}
        providerName={providerName}
        settings={settings}
        onSelect={(vault) => onChange({ ...settings, [field.key]: vault.name })}
      />
    </>
  )
}

/**
 * The `cliPath` field with a Detect button, mirroring the AI assistant's CLI detection:
 * blank means auto-detect, and pressing Detect fills in the resolved absolute path plus the
 * version it found, so a failure names the missing binary instead of surfacing later as a
 * confusing resolution error.
 */
function OnePasswordCliField({
  field, settings, onChange, disabled, size, providerName,
}: {
  field: ProviderSettingField
  settings: Record<string, string | null>
  onChange: (next: Record<string, string | null>) => void
  disabled?: boolean
  size: 'xs' | 'sm'
  providerName: string | null
}) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)

  async function detect() {
    setBusy(true)
    try {
      const r = await api.detectOnePasswordCli(providerName, settings)
      if (r.ok && r.path) {
        // Write the resolved path back: an explicit path is reproducible, where a lucky
        // PATH hit depends on how the Studio process happened to be launched.
        onChange({ ...settings, [field.key]: r.path })
        setResult({ ok: true, text: `Found op ${r.version ?? ''} at ${r.path}`.replace('  ', ' ') })
      } else {
        setResult({ ok: false, text: r.error ?? 'op CLI not found.' })
      }
    } catch (e) {
      setResult({ ok: false, text: e instanceof ApiError ? e.message : String(e) })
    } finally { setBusy(false) }
  }

  return (
    <Stack gap={4}>
      <Group gap="xs" align="flex-end" wrap="nowrap">
        <Box style={{ flex: 1 }}>
          <SettingInput
            field={field}
            value={settings[field.key] ?? ''}
            onChange={(v) => {
              setResult(null)
              onChange({ ...settings, [field.key]: v === '' ? null : v })
            }}
            disabled={disabled}
            size={size}
            hideNote
          />
        </Box>
        <Button
          size={size}
          variant="default"
          leftSection={<IconSearch size={13} />}
          onClick={() => void detect()}
          loading={busy}
          disabled={disabled}
        >
          Detect
        </Button>
      </Group>
      <FieldNote note={field.note} />
      {result && (
        <Text size="xs" c={result.ok ? 'teal' : 'red'} style={{ wordBreak: 'break-word' }}>
          {result.text}
        </Text>
      )}
    </Stack>
  )
}

/** Dropdown for a `select` field: label + description per option, so choosing a mode
 *  explains what it does instead of relying on the reader already knowing. */
function SettingSelect({
  field, value, onChange, disabled, size,
}: {
  field: ProviderSettingField
  value: string
  onChange: (value: string) => void
  disabled?: boolean
  size: 'xs' | 'sm'
}) {
  return (
    <Box>
      <Select
        label={field.label}
        description={field.description ?? undefined}
        data={field.options.map((o) => ({ value: o.value, label: o.label }))}
        value={value || null}
        onChange={(v) => onChange(v ?? '')}
        renderOption={({ option }) => {
          const o = field.options.find((x) => x.value === option.value)
          return (
            <Stack gap={0}>
              <Text size="sm" fw={500}>{o?.label ?? option.value}</Text>
              {o?.description && <Text size="xs" c="dimmed">{o.description}</Text>}
            </Stack>
          )
        }}
        size={size}
        disabled={disabled}
        allowDeselect={false}
      />
      <FieldNote note={field.note} />
    </Box>
  )
}

/** Prerequisite / version-floor guidance under a field, with at most one link. */
function FieldNote({ note }: { note: ProviderSettingField['note'] }) {
  if (!note) return null
  return (
    <Text size="xs" c="dimmed" mt={4} style={{ lineHeight: 1.45 }}>
      {note.text}
      {note.url && (
        <>
          {' '}
          <Anchor href={note.url} target="_blank" rel="noreferrer noopener" size="xs">
            {note.urlLabel ?? note.url}
          </Anchor>
        </>
      )}
    </Text>
  )
}

function SettingInput({
  field, value, onChange, disabled, size, hideNote,
}: {
  field: ProviderSettingField
  value: string
  onChange: (value: string) => void
  disabled?: boolean
  size: 'xs' | 'sm'
  /** Set by picker fields, which sit in a flex row with a button: the note has to render
   *  below the whole row, or `align="flex-end"` drags the button down beside it. */
  hideNote?: boolean
}) {
  return (
    <Box>
      <TextInput
        label={field.label}
        description={field.description ?? undefined}
        placeholder={field.kind === 'secret' && !field.placeholder ? '(leave *** to keep the stored value)' : field.placeholder ?? undefined}
        type={field.kind === 'secret' ? 'password' : 'text'}
        required={field.required}
        size={size}
        value={value}
        onChange={(e) => onChange(e.currentTarget.value)}
        disabled={disabled}
        styles={{ input: { fontFamily: 'var(--mono)' } }}
      />
      {!hideNote && <FieldNote note={field.note} />}
    </Box>
  )
}

// ---- Test button ------------------------------------------------------------------------

/**
 * "Test" button + inline result for one (draft) provider config. The server merges
 * masked (`***`) values from the stored provider with the same name, so testing works
 * without retyping secrets.
 */
export function TestProviderControl({
  name, type, settings,
}: {
  name: string | null
  type: string
  settings: Record<string, string | null>
}) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TestProviderResult | null>(null)

  // A settings/type edit invalidates the last verdict.
  useEffect(() => { setResult(null) }, [type, JSON.stringify(settings)]) // eslint-disable-line react-hooks/exhaustive-deps

  async function run() {
    setBusy(true)
    try {
      setResult(await api.testVariableProvider({ name, type, settings }))
    } catch (e) {
      setResult({ ok: false, message: e instanceof ApiError ? e.message : String(e), durationMs: 0, variableCount: null })
    } finally { setBusy(false) }
  }

  return (
    <Group gap="xs" wrap="nowrap">
      <Button
        size="xs"
        variant="light"
        leftSection={<IconPlugConnected size={14} />}
        onClick={() => void run()}
        loading={busy}
      >
        Test
      </Button>
      {result && (
        <Tooltip label={result.message} withArrow multiline maw={360}>
          <Badge
            size="sm"
            variant="light"
            color={result.ok ? 'green' : 'red'}
            style={{ maxWidth: 320, cursor: 'default' }}
          >
            {result.ok
              ? `OK — ${result.variableCount ?? 0} vars · ${Math.round(result.durationMs)}ms`
              : result.message}
          </Badge>
        </Tooltip>
      )}
    </Group>
  )
}

// ---- Browse drawer ----------------------------------------------------------------------

/**
 * "Browse" button opening a drawer that lists the provider's variables. Secret values
 * are masked; each row has an eye icon that fetches the clear value on demand (azkv
 * values are per-secret GETs anyway, so lazy reveal is also the cheap path).
 */
export function BrowseProviderControl({ providerName, env }: { providerName: string; env?: string | null }) {
  const [opened, { open, close }] = useDisclosure(false)
  return (
    <>
      <Button size="xs" variant="default" leftSection={<IconListSearch size={14} />} onClick={open}>
        Browse
      </Button>
      <Drawer
        opened={opened}
        onClose={close}
        position="right"
        size="lg"
        title={<Group gap="xs"><Text fw={600}>Variables in</Text><Code>{providerName}</Code></Group>}
      >
        {opened && <BrowseDrawerBody providerName={providerName} env={env} />}
      </Drawer>
    </>
  )
}

function BrowseDrawerBody({ providerName, env }: { providerName: string; env?: string | null }) {
  const [rows, setRows] = useState<ProviderVariable[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [revealed, setRevealed] = useState<Record<string, string>>({})
  const [revealBusy, setRevealBusy] = useState<string | null>(null)

  async function load(refresh: boolean) {
    setLoading(true); setError(null)
    try {
      setRows(await api.providerVariables(providerName, refresh, env))
      if (refresh) setRevealed({})
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setLoading(false) }
  }

  useEffect(() => { void load(false) }, [providerName]) // eslint-disable-line react-hooks/exhaustive-deps

  async function reveal(name: string) {
    setRevealBusy(name)
    try {
      const v = await api.providerVariableValue(providerName, name, env)
      setRevealed((cur) => ({ ...cur, [name]: v.value }))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setRevealBusy(null) }
  }

  return (
    <Stack gap="sm">
      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {rows ? `${rows.length} variable(s).` : ''} Secret values stay masked until you reveal them per row.
        </Text>
        <Button
          size="xs" variant="default"
          leftSection={<IconRefresh size={13} />}
          onClick={() => void load(true)}
          loading={loading}
        >
          Refresh
        </Button>
      </Group>

      {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />} p="xs"><Text size="xs">{error}</Text></Alert>}

      {rows === null && !error ? (
        <Group justify="center" py="lg"><Loader size="sm" /></Group>
      ) : rows && rows.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="md">This provider has no variables right now.</Text>
      ) : rows && (
        <Table verticalSpacing={4} horizontalSpacing="sm" withRowBorders={false}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Value</Table.Th>
              <Table.Th style={{ width: 40 }} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((r) => {
              const shown = revealed[r.name]
              return (
                <Table.Tr key={r.name}>
                  <Table.Td style={{ width: '40%' }}>
                    <Group gap={6} wrap="nowrap">
                      <Text ff="var(--mono)" size="sm" truncate>{r.name}</Text>
                      {r.isSecret && <Badge size="xs" variant="light" color="yellow">secret</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {shown !== undefined ? (
                      <Text ff="var(--mono)" size="sm" style={{ wordBreak: 'break-all' }}>{shown || <em>(empty)</em>}</Text>
                    ) : r.isSecret || r.value === null ? (
                      <Text ff="var(--mono)" size="sm" c="dimmed">***</Text>
                    ) : (
                      <Text ff="var(--mono)" size="sm" style={{ wordBreak: 'break-all' }}>{r.value || <em>(empty)</em>}</Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    {(r.isSecret || r.value === null) && shown === undefined && (
                      <Tooltip label="Reveal value" withArrow>
                        <ActionIcon
                          variant="subtle" color="gray" size="sm"
                          onClick={() => void reveal(r.name)}
                          loading={revealBusy === r.name}
                          aria-label={`Reveal ${r.name}`}
                        >
                          <IconEye size={14} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  )
}
