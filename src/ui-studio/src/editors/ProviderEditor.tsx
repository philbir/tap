import {
  ActionIcon, Alert, Badge, Box, Button, Code, Group, Loader, Stack, Table, Text, TextInput, Tooltip,
} from '@mantine/core'
import {
  IconAlertTriangle, IconEye, IconKey, IconPlus, IconRefresh, IconTrash,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, api } from '../api/client'
import type { EncryptionKeyStatus, ProviderSummary } from '../api/types'
import { useTapStore } from '../store'
import { EditorShell } from './EditorShell'
import { ProviderTypeIcon } from './providerMeta'

/**
 * Editor for one variable provider's contents — the manage half of what the Browse drawer
 * only ever read.
 *
 * <p>Same shape as every other editor: a working draft, a dirty flag, one Save. The wire
 * protocol underneath is per-variable REST rather than a whole-document PUT, so Save diffs
 * the draft against what was loaded and issues the writes and deletes that difference
 * implies. A rename is a delete plus a write, in that order.</p>
 *
 * <p>Secret values never arrive here. A secret row loads with `value: null` and stays that way
 * unless the user explicitly reveals it; saving such a row sends `value: null`, which the
 * server reads as "keep what's stored". That is what lets a plain value be flipped to
 * encrypted without its clear text making a round trip through the browser.</p>
 */

interface Row {
  id: string
  /** Key as loaded, or null for a row added in this session. Drives rename detection. */
  originalKey: string | null
  key: string
  /** null = "whatever the server holds" (an unrevealed secret). */
  value: string | null
  secret: boolean
}

let rowSeq = 0
const nextRowId = () => `pv-${++rowSeq}`

export function ProviderEditor({ name }: { name: string }) {
  const activeEnv = useTapStore((s) => s.activeEnvByRoot[s.info?.root ?? ''] ?? null)
  const generation = useTapStore((s) => s.generation)

  const [provider, setProvider] = useState<ProviderSummary | null>(null)
  const [rows, setRows] = useState<Row[]>([])
  const [savedRows, setSavedRows] = useState<Row[]>([])
  const [keyStatus, setKeyStatus] = useState<EncryptionKeyStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busyKey, setBusyKey] = useState<string | null>(null)

  const load = useCallback(async (refresh: boolean) => {
    setLoading(true)
    setError(null)
    try {
      const [providers, variables] = await Promise.all([
        api.listVariableProviders(activeEnv),
        api.providerVariables(name, refresh, activeEnv),
      ])
      setProvider(providers.find((p) => p.name === name) ?? null)
      const loaded: Row[] = variables.map((v) => ({
        id: nextRowId(),
        originalKey: v.name,
        key: v.name,
        value: v.isSecret ? null : (v.value ?? ''),
        secret: v.isSecret,
      }))
      setRows(loaded)
      setSavedRows(loaded)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [name, activeEnv])

  useEffect(() => { void load(false) }, [load, generation])

  // The key banner is only meaningful for providers that encrypt. Fetched separately so a
  // provider that doesn't care never pays for it.
  const encrypts = provider?.type === 'file'
  useEffect(() => {
    if (!encrypts) { setKeyStatus(null); return }
    api.encryptionKey().then(setKeyStatus).catch(() => setKeyStatus(null))
  }, [encrypts, generation])

  const readOnly = provider !== null && provider.mode !== 'readwrite'

  const dirty = useMemo(() => serialize(rows) !== serialize(savedRows), [rows, savedRows])

  const update = (id: string, patch: Partial<Row>) =>
    setRows((cur) => cur.map((r) => (r.id === id ? { ...r, ...patch } : r)))

  async function reveal(row: Row) {
    setBusyKey(row.id)
    try {
      const v = await api.providerVariableValue(name, row.key, activeEnv)
      update(row.id, { value: v.value })
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusyKey(null)
    }
  }

  async function save() {
    // Keys are the identity here, so a duplicate would make one row silently win. Catch it
    // before any write lands — a partially applied save is the worst outcome.
    const committed = rows.filter((r) => r.key.trim() !== '')
    const seen = new Set<string>()
    for (const r of committed) {
      if (seen.has(r.key)) { setError(`Duplicate variable name '${r.key}'.`); return }
      seen.add(r.key)
    }

    setSaving(true)
    setError(null)
    try {
      const liveKeys = new Set(committed.map((r) => r.key))
      // Deletes first: a rename is delete-then-write, and doing it the other way round
      // would delete the row that was just written.
      for (const prev of savedRows) {
        if (prev.originalKey && !liveKeys.has(prev.originalKey)) {
          await api.deleteProviderVariable(name, prev.originalKey, activeEnv)
        }
      }
      for (const row of committed) {
        const before = savedRows.find((r) => r.originalKey === row.originalKey)
        const renamed = row.originalKey !== null && row.originalKey !== row.key
        const changed = !before || renamed || before.value !== row.value || before.secret !== row.secret
        if (!changed) continue
        // A renamed secret the user never revealed has no value to carry across, so the
        // rename has to read it back first — otherwise the new key lands empty.
        let value = row.value
        if (value === null && (renamed || before === undefined)) {
          value = (await api.providerVariableValue(name, row.originalKey ?? row.key, activeEnv)).value
        }
        await api.setProviderVariable(name, row.key, { value, isSecret: row.secret, env: activeEnv })
      }
      await load(true)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      // Reload so the table reflects whatever actually landed rather than the failed draft.
      await load(true)
    } finally {
      setSaving(false)
    }
  }

  const secretsBlocked = encrypts && keyStatus !== null && !keyStatus.configured

  return (
    <EditorShell
      title={name}
      kindLabel="Provider"
      dirty={dirty}
      saving={saving}
      errorMessage={error}
      onSave={() => void save()}
      onDiscard={() => setRows(savedRows)}
      toolbarExtras={
        <Group gap="xs">
          {provider && (
            <Badge size="sm" variant="light" color={readOnly ? 'gray' : 'teal'}>
              {readOnly ? 'read-only' : 'read/write'}
            </Badge>
          )}
          <Tooltip label="Reload from the provider" withArrow>
            <ActionIcon variant="subtle" onClick={() => void load(true)} aria-label="Reload">
              <IconRefresh size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      }
    >
      <Stack gap="md" p="md">
        {provider && (
          <Group gap="xs">
            <ProviderTypeIcon icon={provider.icon} size={16} />
            <Text size="sm" c="dimmed">
              {provider.typeDisplayName ?? provider.type} · {provider.origin} scope
            </Text>
          </Group>
        )}

        {secretsBlocked && <NoKeyBanner status={keyStatus!} onGenerated={setKeyStatus} />}

        {readOnly && (
          <Alert color="gray" variant="light" icon={<IconAlertTriangle size={16} />}>
            <Code fz="xs">{provider?.type}</Code> providers are read-only in Tap. Edit the values
            where they live; this view exists so you can see what resolves.
          </Alert>
        )}

        {loading && rows.length === 0 ? (
          <Group justify="center" p="xl"><Loader size="sm" /></Group>
        ) : (
          <VariableTable
            rows={rows}
            readOnly={readOnly}
            busyRowId={busyKey}
            secretsBlocked={!!secretsBlocked}
            onChange={update}
            onReveal={(row) => void reveal(row)}
            onRemove={(id) => setRows((cur) => cur.filter((r) => r.id !== id))}
            onAdd={() => setRows((cur) => [
              ...cur,
              { id: nextRowId(), originalKey: null, key: '', value: '', secret: false },
            ])}
          />
        )}
      </Stack>
    </EditorShell>
  )
}

/** Content-stable projection used for the dirty check. */
function serialize(rows: readonly Row[]): string {
  return rows
    .filter((r) => r.key.trim() !== '')
    .map((r) => `${r.originalKey ?? ''}\x1f${r.key}\x1f${r.value ?? '\x00'}\x1f${r.secret ? '1' : '0'}`)
    .join('\x1e')
}

function NoKeyBanner({ status, onGenerated }: {
  status: EncryptionKeyStatus
  onGenerated: (s: EncryptionKeyStatus) => void
}) {
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)

  async function generate() {
    setBusy(true)
    setFailed(null)
    try {
      onGenerated(await api.generateEncryptionKey())
    } catch (e) {
      setFailed(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Alert color="yellow" variant="light" icon={<IconKey size={16} />} title="No encryption key on this machine">
      <Stack gap="xs">
        <Text size="sm">
          Secret values can't be written or read until this machine has a key. Set{' '}
          <Code fz="xs">{status.envVarName}</Code>, or generate one at{' '}
          <Code fz="xs">{status.keyFilePath}</Code>.
        </Text>
        {failed && <Text size="sm" c="red">{failed}</Text>}
        <Group>
          <Button size="xs" loading={busy} onClick={() => void generate()} leftSection={<IconKey size={14} />}>
            Generate a key
          </Button>
          <Text size="xs" c="dimmed">
            Back the file up — it is the only thing that can decrypt what it encrypts.
          </Text>
        </Group>
      </Stack>
    </Alert>
  )
}

function VariableTable({
  rows, readOnly, busyRowId, secretsBlocked, onChange, onReveal, onRemove, onAdd,
}: {
  rows: Row[]
  readOnly: boolean
  busyRowId: string | null
  secretsBlocked: boolean
  onChange: (id: string, patch: Partial<Row>) => void
  onReveal: (row: Row) => void
  onRemove: (id: string) => void
  onAdd: () => void
}) {
  // Focus the key field of a newly added row so "+ Add" is one click, not click-then-click.
  const lastCount = useRef(rows.length)
  const addedRef = useRef<HTMLInputElement | null>(null)
  useEffect(() => {
    if (rows.length > lastCount.current) addedRef.current?.focus()
    lastCount.current = rows.length
  }, [rows.length])

  return (
    <Box>
      <Table striped highlightOnHover withTableBorder>
        <Table.Thead>
          <Table.Tr>
            <Table.Th style={{ width: '32%' }}>Name</Table.Th>
            <Table.Th>Value</Table.Th>
            <Table.Th style={{ width: 110 }}>Secret</Table.Th>
            <Table.Th style={{ width: 44 }} />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.length === 0 && (
            <Table.Tr>
              <Table.Td colSpan={4}>
                <Text size="sm" c="dimmed" ta="center" py="md">
                  No variables here yet.
                </Text>
              </Table.Td>
            </Table.Tr>
          )}
          {rows.map((row, i) => (
            <Table.Tr key={row.id}>
              <Table.Td>
                <TextInput
                  ref={i === rows.length - 1 ? addedRef : undefined}
                  size="xs"
                  ff="var(--mono)"
                  placeholder="name"
                  value={row.key}
                  disabled={readOnly}
                  onChange={(e) => onChange(row.id, { key: e.currentTarget.value })}
                />
              </Table.Td>
              <Table.Td>
                {row.value === null ? (
                  <Group gap="xs" wrap="nowrap">
                    <Text size="xs" c="dimmed" ff="var(--mono)" style={{ flex: 1 }}>••••••••</Text>
                    <Tooltip label="Reveal the stored value" withArrow>
                      <ActionIcon
                        size="sm" variant="subtle"
                        loading={busyRowId === row.id}
                        onClick={() => onReveal(row)}
                        aria-label={`Reveal ${row.key}`}
                      >
                        <IconEye size={14} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                ) : (
                  <TextInput
                    size="xs"
                    ff="var(--mono)"
                    placeholder="value"
                    value={row.value}
                    disabled={readOnly}
                    onChange={(e) => onChange(row.id, { value: e.currentTarget.value })}
                  />
                )}
              </Table.Td>
              <Table.Td>
                <Tooltip
                  label={secretsBlocked
                    ? 'Needs an encryption key on this machine'
                    : 'Encrypt this value at rest and mask it everywhere'}
                  withArrow
                >
                  <Badge
                    component="button"
                    type="button"
                    variant={row.secret ? 'filled' : 'outline'}
                    color={row.secret ? 'orange' : 'gray'}
                    size="sm"
                    style={{ cursor: readOnly || secretsBlocked ? 'not-allowed' : 'pointer' }}
                    onClick={() => {
                      if (readOnly || secretsBlocked) return
                      onChange(row.id, { secret: !row.secret })
                    }}
                  >
                    {row.secret ? 'secret' : 'plain'}
                  </Badge>
                </Tooltip>
              </Table.Td>
              <Table.Td>
                {!readOnly && (
                  <Tooltip label="Remove" withArrow>
                    <ActionIcon
                      size="sm" variant="subtle" color="red"
                      onClick={() => onRemove(row.id)}
                      aria-label={`Remove ${row.key}`}
                    >
                      <IconTrash size={14} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {!readOnly && (
        <Button
          mt="xs" size="xs" variant="subtle"
          leftSection={<IconPlus size={14} />}
          onClick={onAdd}
        >
          Add variable
        </Button>
      )}
    </Box>
  )
}
