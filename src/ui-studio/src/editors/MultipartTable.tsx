import {
  ActionIcon, Box, Button, FileButton, Group, Select, Stack, Table, Text, TextInput, Tooltip,
} from '@mantine/core'
import { IconFile, IconPlus, IconUpload, IconX } from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { VariableContext } from '../api/types'
import { passwordManagerOptOut } from './passwordManagerOptOut'
import { VariableInput } from './VariableInput'
import type { MultipartPart } from './body-mode'

/**
 * Editable multipart/form-data builder. Each row is a {@link MultipartPart} — a text
 * field or a file field with an explicit Content-Type. Mirrors the shape of
 * {@link KvTable} so the look-and-feel stays consistent, but adds a per-row type
 * toggle, file picker, and content-type input.
 *
 * File reads are best-effort text decodes. Binary files (PNG, etc.) survive a single
 * pick → send → response cycle because the FileReader's `readAsText` keeps a Latin1
 * mapping, but they are NOT round-trip-safe through the on-disk markdown spec — the
 * UI surfaces a hint when the picked file's MIME is non-text.
 */
export interface MultipartTableProps {
  parts: MultipartPart[]
  onChange: (parts: MultipartPart[]) => void
  variableContext?: VariableContext | null
  onOpenVariables?: () => void
}

interface InternalRow extends MultipartPart {
  id: string
  /** Captured at pick time so the row can show "image.png · 1.2 KB · image/png". Not
   *  persisted to the body — disk diffs only care about the part body itself. */
  fileMeta?: { name: string; size: number; type: string }
}

let counter = 0
const nextId = () => `mpt-${++counter}-${Math.random().toString(36).slice(2, 6)}`

export function MultipartTable({ parts, onChange, variableContext, onOpenVariables }: MultipartTableProps) {
  const [rows, setRows] = useState<InternalRow[]>(() =>
    parts.map((p) => ({ ...p, id: nextId() })),
  )

  // Re-seed only when the parent hands us a logically different list — e.g. switching
  // tabs and back, or another panel rewrote the body. The naïve check (any externalKey
  // change → re-seed) fires on every keystroke because typing emits a serialized body
  // upstream, which round-trips back as new `parts`. Re-seeding regenerates row ids,
  // which remounts the <TextInput>s and steals focus mid-word. Guard by comparing the
  // external key against what we already have committed locally.
  const externalKey = useMemo(
    () => parts.map(partKey).join('\x1e'),
    [parts],
  )
  const lastExternalKey = useRef(externalKey)
  useEffect(() => {
    if (lastExternalKey.current === externalKey) return
    lastExternalKey.current = externalKey
    const committed = rows
      .filter((r) => r.name || r.value || r.filename)
      .map(partKey)
      .join('\x1e')
    if (committed !== externalKey) {
      setRows(parts.map((p) => ({ ...p, id: nextId() })))
    }
  }, [externalKey, parts, rows])

  function emit(next: InternalRow[]) {
    setRows(next)
    onChange(next.map(({ id: _id, fileMeta: _fm, ...rest }) => rest))
  }

  function update(id: string, patch: Partial<InternalRow>) {
    emit(rows.map((r) => (r.id === id ? { ...r, ...patch } : r)))
  }

  function remove(id: string) {
    emit(rows.filter((r) => r.id !== id))
  }

  function addRow(kind: MultipartPart['kind']) {
    emit([...rows, { id: nextId(), name: '', kind, value: '', contentType: kind === 'file' ? 'application/octet-stream' : undefined }])
  }

  async function pickFile(id: string, file: File | null) {
    if (!file) return
    const text = await file.text()
    update(id, {
      kind: 'file',
      value: text,
      filename: file.name,
      contentType: file.type || 'application/octet-stream',
      fileMeta: { name: file.name, size: file.size, type: file.type },
    })
  }

  return (
    <Stack gap="xs">
      <Box style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 'var(--mantine-radius-sm)', overflow: 'hidden' }}>
        <Table verticalSpacing={4} horizontalSpacing="sm" striped="even" highlightOnHover withColumnBorders={false}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th style={{ width: 90 }}>Type</Table.Th>
              <Table.Th style={{ width: '22%' }}>Name</Table.Th>
              <Table.Th>Value</Table.Th>
              <Table.Th style={{ width: '22%' }}>Content-Type</Table.Th>
              <Table.Th style={{ width: 36 }} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.length === 0 && (
              <Table.Tr>
                <Table.Td colSpan={5}>
                  <Text size="xs" c="dimmed" ta="center" py="sm">
                    No parts yet. Add a text field or file below.
                  </Text>
                </Table.Td>
              </Table.Tr>
            )}
            {rows.map((r) => (
              <Table.Tr key={r.id}>
                <Table.Td>
                  <Select
                    size="xs"
                    data={[{ value: 'text', label: 'Text' }, { value: 'file', label: 'File' }]}
                    value={r.kind}
                    onChange={(v) => {
                      if (!v || v === r.kind) return
                      // Toggle wipes the value/filename so the row stays consistent —
                      // a text part keeping a stale `filename="…"` would emit a broken
                      // Content-Disposition line.
                      update(r.id, {
                        kind: v as MultipartPart['kind'],
                        value: '',
                        filename: v === 'file' ? '' : undefined,
                        contentType: v === 'file' ? 'application/octet-stream' : undefined,
                        fileMeta: undefined,
                      })
                    }}
                    allowDeselect={false}
                  />
                </Table.Td>
                <Table.Td>
                  <TextInput
                    size="xs"
                    value={r.name}
                    onChange={(e) => update(r.id, { name: e.currentTarget.value })}
                    placeholder="field-name"
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                    {...passwordManagerOptOut}
                  />
                </Table.Td>
                <Table.Td>
                  {r.kind === 'file' ? (
                    <Group gap="xs" wrap="nowrap">
                      <FileButton onChange={(f) => pickFile(r.id, f)}>
                        {(props) => (
                          <Button {...props} size="xs" variant="default" leftSection={<IconUpload size={12} />}>
                            {r.filename ? 'Replace' : 'Pick file'}
                          </Button>
                        )}
                      </FileButton>
                      <Text size="xs" c="dimmed" ff="var(--mono)" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {r.fileMeta
                          ? `${r.fileMeta.name} · ${formatBytes(r.fileMeta.size)}`
                          : r.filename
                            ? r.filename
                            : '(no file picked)'}
                      </Text>
                    </Group>
                  ) : (
                    <VariableInput
                      value={r.value}
                      onChange={(v) => update(r.id, { value: v })}
                      placeholder="value or {{var}}"
                      size="xs"
                      context={variableContext}
                      onOpenVariables={onOpenVariables}
                    />
                  )}
                </Table.Td>
                <Table.Td>
                  <TextInput
                    size="xs"
                    value={r.contentType ?? ''}
                    onChange={(e) => update(r.id, { contentType: e.currentTarget.value || undefined })}
                    placeholder={r.kind === 'file' ? 'auto' : '(none)'}
                    styles={{ input: { fontFamily: 'var(--mono)' } }}
                    {...passwordManagerOptOut}
                  />
                </Table.Td>
                <Table.Td>
                  <Tooltip label="Remove" openDelay={400}>
                    <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => remove(r.id)} aria-label="Remove row">
                      <IconX size={14} />
                    </ActionIcon>
                  </Tooltip>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Box>
      <Group gap="xs">
        <Button size="xs" variant="default" leftSection={<IconPlus size={12} />} onClick={() => addRow('text')}>
          Text field
        </Button>
        <Button size="xs" variant="default" leftSection={<IconFile size={12} />} onClick={() => addRow('file')}>
          File
        </Button>
      </Group>
    </Stack>
  )
}

function partKey(p: MultipartPart): string {
  return `${p.kind}\x1f${p.name}\x1f${p.value}\x1f${p.filename ?? ''}\x1f${p.contentType ?? ''}`
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}
