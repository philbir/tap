import { ActionIcon, Autocomplete, Box, Button, Group, Table, TextInput, Tooltip } from '@mantine/core'
import { IconEye, IconEyeOff, IconPlus, IconX } from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { VariableContext } from '../api/types'
import { passwordManagerOptOut } from './passwordManagerOptOut'
import { VariableInput } from './VariableInput'

/**
 * Editable key/value table — env vars, default headers, header overrides, query params,
 * custom auth headers. Owns its own row state with stable ids so the "+ Add row" button
 * can introduce empty-key drafts without the parent filtering them away (the parent only
 * receives committed rows — entries with a non-empty key).
 *
 * The value column is a {@link VariableInput} so `{{var}}` chips + autocomplete work
 * everywhere a value can hold a template (URLs, header values, env values, etc.).
 *
 * Why internal state: parents flatten `KvRow[]` into `Record<string, string>` for the wire
 * spec — empty-key rows have nowhere to live there. Maintaining the working list locally
 * lets the user keep typing in a half-finished row without losing it on every keystroke.
 */
export interface KvRow {
  key: string
  value: string
  secret?: boolean
}

interface InternalRow extends KvRow { id: string }

export interface KvTableProps {
  rows: KvRow[]
  /** Fired with the row list as it currently exists — INCLUDING rows still being typed
   *  in (empty key). Callers should ignore empty-key rows when serializing to disk; the
   *  table holds onto them so the user doesn't lose focus mid-typing. */
  onChange: (rows: KvRow[]) => void
  keyPlaceholder?: string
  valuePlaceholder?: string
  allowSecretToggle?: boolean
  emptyHint?: ReactNode
  /** Optional variable context passed through to the value column's `VariableInput` so
   *  the user gets `{{var}}` autocomplete inside table values too. */
  variableContext?: VariableContext | null
  /** Click handler for the value-input's flask icon (opens Variables panel). */
  onOpenVariables?: () => void
  /** When provided, the key field renders as a Mantine Autocomplete with these options
   *  (free text is still accepted — typical use is well-known HTTP header names). */
  keySuggestions?: readonly string[]
  /** When provided, returns a list of suggested values for the row's current key. The
   *  value field's VariableInput shows these as static suggestions in its dropdown. */
  getValueSuggestions?: (key: string) => string[]
}

let nextIdCounter = 0
const nextId = () => `kvr-${++nextIdCounter}-${Math.random().toString(36).slice(2, 6)}`

export function KvTable({
  rows: externalRows, onChange,
  keyPlaceholder = 'key',
  valuePlaceholder = 'value',
  allowSecretToggle,
  emptyHint,
  variableContext,
  onOpenVariables,
  keySuggestions,
  getValueSuggestions,
}: KvTableProps) {
  // Local state mirrors `externalRows` but adds a stable id per row so React keys stay
  // consistent across edits and the "+ Add" button can introduce drafts with empty keys.
  const [internal, setInternal] = useState<InternalRow[]>(() =>
    externalRows.map((r) => ({ ...r, id: nextId() })),
  )

  // Detect external prop changes (parent reset / refetched from server) and re-seed.
  // We use a content-stable key so the effect only fires when the actual data changed.
  const externalKey = useMemo(
    () => externalRows.map((r) => `${r.key}\x1f${r.value}\x1f${r.secret ? '1' : '0'}`).join('\x1e'),
    [externalRows],
  )
  const lastExternalKey = useRef(externalKey)
  useEffect(() => {
    if (lastExternalKey.current === externalKey) return
    lastExternalKey.current = externalKey
    // Only re-seed if the external rows differ from what we already have committed.
    const committedKey = internal
      .filter((r) => r.key.trim())
      .map((r) => `${r.key}\x1f${r.value}\x1f${r.secret ? '1' : '0'}`)
      .join('\x1e')
    if (committedKey !== externalKey) {
      setInternal(externalRows.map((r) => ({ ...r, id: nextId() })))
    }
  }, [externalKey])

  function commit(next: InternalRow[]) {
    setInternal(next)
    onChange(next.map(({ id: _id, ...r }) => r))
  }
  function patch(id: string, p: Partial<KvRow>) {
    commit(internal.map((r) => (r.id === id ? { ...r, ...p } : r)))
  }
  function remove(id: string) {
    commit(internal.filter((r) => r.id !== id))
  }
  function add() {
    commit([...internal, { id: nextId(), key: '', value: '' }])
  }

  const showTable = internal.length > 0

  return (
    <Box>
      {!showTable && emptyHint && (
        <Box ta="center" py="md" c="dimmed" fz="sm">{emptyHint}</Box>
      )}

      {showTable && (
        <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false}>
          <Table.Tbody>
            {internal.map((r) => (
              <Table.Tr key={r.id}>
                <Table.Td style={{ width: '40%' }}>
                  {keySuggestions ? (
                    <Autocomplete
                      size="xs"
                      placeholder={keyPlaceholder}
                      value={r.key}
                      data={keySuggestions as string[]}
                      onChange={(v) => patch(r.id, { key: v })}
                      limit={10}
                      styles={{ input: { fontFamily: 'var(--mono)' } }}
                      {...passwordManagerOptOut}
                    />
                  ) : (
                    <TextInput
                      size="xs"
                      placeholder={keyPlaceholder}
                      value={r.key}
                      onChange={(e) => patch(r.id, { key: e.currentTarget.value })}
                      styles={{ input: { fontFamily: 'var(--mono)' } }}
                      {...passwordManagerOptOut}
                    />
                  )}
                </Table.Td>
                <Table.Td>
                  <ValueCell
                    row={r}
                    placeholder={valuePlaceholder}
                    context={variableContext ?? null}
                    onOpenVariables={onOpenVariables}
                    onChange={(value) => patch(r.id, { value })}
                    staticSuggestions={getValueSuggestions?.(r.key)}
                  />
                </Table.Td>
                {allowSecretToggle && (
                  <Table.Td style={{ width: 32 }}>
                    <Tooltip label={r.secret ? 'Plain text' : 'Mark as secret'} withArrow>
                      <ActionIcon
                        variant="subtle"
                        color={r.secret ? 'yellow' : 'gray'}
                        size="sm"
                        onClick={() => patch(r.id, { secret: !r.secret })}
                        aria-label="Toggle secret"
                      >
                        {r.secret ? <IconEyeOff size={14} /> : <IconEye size={14} />}
                      </ActionIcon>
                    </Tooltip>
                  </Table.Td>
                )}
                <Table.Td style={{ width: 32 }}>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    size="sm"
                    onClick={() => remove(r.id)}
                    aria-label="Remove row"
                  >
                    <IconX size={14} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Group justify="flex-start" mt="xs">
        <Button
          size="xs"
          variant="default"
          color="gray"
          leftSection={<IconPlus size={14} />}
          onClick={add}
        >
          Add
        </Button>
      </Group>
    </Box>
  )
}

/**
 * Value-column cell — wraps a VariableInput so {{var}} chips render inline. Secrets are
 * displayed redacted in the cell unless the user opens the row's secret-toggle to reveal
 * (handled at the row level via the secret prop on the row, which the parent flips).
 */
function ValueCell({
  row, placeholder, context, onChange, onOpenVariables, staticSuggestions,
}: {
  row: KvRow
  placeholder: string
  context: VariableContext | null
  onChange: (v: string) => void
  onOpenVariables?: () => void
  staticSuggestions?: string[]
}) {
  return (
    <VariableInput
      value={row.value}
      onChange={onChange}
      placeholder={placeholder}
      context={context}
      onOpenVariables={onOpenVariables}
      size="xs"
      staticSuggestions={staticSuggestions}
    />
  )
}
