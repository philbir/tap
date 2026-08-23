import {
  ActionIcon, Alert, Badge, Button, Group, Loader, ScrollArea, Stack, Text, Tooltip, UnstyledButton,
} from '@mantine/core'
import { modals } from '@mantine/modals'
import {
  IconAdjustments, IconAlertTriangle, IconHistory, IconLock, IconRefresh, IconTrash,
} from '@tabler/icons-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { HistorySummary } from '../api/types'
import { useTapStore } from '../store'

interface Props {
  /** Stable id of the request whose history this is. Null when the request has never been
   *  saved through the Studio, which is the one case with nothing to file entries under. */
  requestId: string | null
  /** Whether recording is on for this request after the workspace → collection → request
   *  merge. Drives the empty state's explanation. */
  enabled: boolean
  /** The entry currently open in the response panel, so the row can show as selected. */
  selectedId: string | null
  onSelect: (summary: HistorySummary) => void
  /** Reported up so the tab can carry a count without the editor fetching the list twice. */
  onCountChange?: (count: number) => void
  /** Jumps to where recording is configured — the request's Meta tab. The settings are a tab
   *  away from the list they govern, and "recording is off" is not a useful thing to be told
   *  without a way to act on it. */
  onOpenSettings?: () => void
}

/**
 * One request's recorded exchanges. Rows are summaries — the bodies stay on disk until a row is
 * picked, which is what keeps this instant for a request with a hundred entries.
 *
 * <p>The empty state is doing real work here: "nothing yet" and "recording is off" and "this
 * request has no id" are three different situations with three different fixes, and a single
 * blank panel would leave the user guessing which one they are in.</p>
 */
export function HistoryPanel({ requestId, enabled, selectedId, onSelect, onCountChange, onOpenSettings }: Props) {
  const generation = useTapStore((s) => s.generation)
  const [rows, setRows] = useState<HistorySummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const listRef = useRef<HTMLDivElement>(null)

  const load = useCallback(async () => {
    if (!requestId) { setRows([]); onCountChange?.(0); return }
    try {
      const next = await api.requestHistory(requestId)
      setRows(next)
      onCountChange?.(next.length)
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      setRows([])
      onCountChange?.(0)
    }
  }, [requestId, onCountChange])

  // `generation` bumps after every save and every watcher-driven reload — which is also when a
  // Send has just finished, so the list refreshes without its own subscription.
  useEffect(() => { void load() }, [load, generation])

  // Bring the selected row into view. It matters when the selection came from somewhere else —
  // the sidebar timeline picks an entry that may be the fortieth row here, and a highlight you
  // have to scroll to find is not a selection the user can see.
  useEffect(() => {
    if (!selectedId || !rows?.length) return
    const row = listRef.current?.querySelector(`[data-entry-id="${CSS.escape(selectedId)}"]`)
    row?.scrollIntoView({ block: 'nearest' })
  }, [selectedId, rows])

  function confirmClear() {
    if (!requestId || !rows?.length) return
    modals.openConfirmModal({
      title: 'Clear this request’s history?',
      children: (
        <Text size="sm">
          {rows.length === 1 ? '1 recorded exchange' : `${rows.length} recorded exchanges`} will be
          deleted from disk. This can’t be undone.
        </Text>
      ),
      labels: { confirm: 'Clear', cancel: 'Keep' },
      confirmProps: { color: 'red' },
      onConfirm: () => { void api.clearRequestHistory(requestId).then(load) },
    })
  }

  if (rows === null) {
    return <Group gap="xs" p="md"><Loader size="xs" /><Text size="sm" c="dimmed">Loading history…</Text></Group>
  }

  return (
    <Stack gap="xs">
      <Group justify="space-between" wrap="nowrap">
        <Text size="sm" c="dimmed">
          {rows.length === 0
            ? 'No recorded exchanges'
            : `${rows.length} recorded exchange${rows.length === 1 ? '' : 's'}, newest first`}
        </Text>
        <Group gap={4} wrap="nowrap">
          {onOpenSettings && (
            <Tooltip label="History settings" withArrow>
              <ActionIcon variant="subtle" color="gray" onClick={onOpenSettings} aria-label="History settings">
                <IconAdjustments size={15} />
              </ActionIcon>
            </Tooltip>
          )}
          <Tooltip label="Refresh" withArrow>
            <ActionIcon variant="subtle" color="gray" onClick={() => void load()} aria-label="Refresh history">
              <IconRefresh size={15} />
            </ActionIcon>
          </Tooltip>
          <Button
            size="compact-xs" variant="default" leftSection={<IconTrash size={13} />}
            onClick={confirmClear} disabled={rows.length === 0}
          >Clear</Button>
        </Group>
      </Group>

      {error && (
        <Alert color="red" variant="light" icon={<IconAlertTriangle size={14} />} p="xs">
          <Text size="xs">{error}</Text>
        </Alert>
      )}

      {rows.length === 0 && (
        <EmptyState requestId={requestId} enabled={enabled} onOpenSettings={onOpenSettings} />
      )}

      {rows.length > 0 && (
        <ScrollArea.Autosize mah={420} type="hover" scrollbarSize={8}>
          <Stack gap={2} ref={listRef}>
            {rows.map((row) => (
              <HistoryRow
                key={row.id}
                row={row}
                selected={row.id === selectedId}
                onSelect={() => onSelect(row)}
                onDelete={() => void api.deleteHistoryEntry(row.requestId, row.id).then(load)}
              />
            ))}
          </Stack>
        </ScrollArea.Autosize>
      )}
    </Stack>
  )
}

/** Which of the three "nothing here" situations this is, and what to do about it. */
function EmptyState({ requestId, enabled, onOpenSettings }: {
  requestId: string | null
  enabled: boolean
  onOpenSettings?: () => void
}) {
  const message = requestId === null
    ? 'This request has no id yet, so there is nothing stable to file its exchanges under. Save it once and recording starts.'
    : enabled
      ? 'Nothing recorded yet. The next Send lands here.'
      : 'Recording is off for this request — turn it on here, on its collection, or on the workspace.'

  return (
    <Stack align="center" gap={6} py="lg" px="md">
      <IconHistory size={26} stroke={1.5} color="var(--mantine-color-dimmed)" />
      <Text size="sm" c="dimmed" ta="center" maw={420}>{message}</Text>
      {!enabled && requestId !== null && onOpenSettings && (
        <Button
          size="compact-xs" variant="default" leftSection={<IconAdjustments size={13} />}
          onClick={onOpenSettings} mt={4}
        >History settings</Button>
      )}
    </Stack>
  )
}

function HistoryRow({ row, selected, onSelect, onDelete }: {
  row: HistorySummary
  selected: boolean
  onSelect: () => void
  onDelete: () => void
}) {
  return (
    <UnstyledButton
      onClick={onSelect}
      data-entry-id={row.id}
      style={{
        display: 'block',
        padding: '6px 8px',
        borderRadius: 'var(--mantine-radius-sm)',
        background: selected ? 'var(--mantine-color-default-hover)' : 'transparent',
        border: '1px solid',
        borderColor: selected ? 'var(--mantine-color-default-border)' : 'transparent',
      }}
    >
      <Group gap="xs" wrap="nowrap">
        <StatusChip row={row} />
        <Text size="xs" ff="var(--mono)" c="dimmed" style={{ width: 46, flexShrink: 0 }}>{row.method}</Text>
        <Text size="xs" truncate="end" style={{ flex: 1, minWidth: 0 }} title={row.url}>{row.url}</Text>
        {row.env && <Badge size="xs" variant="light" color="grape">{envLabel(row.env)}</Badge>}
        {row.encrypted && (
          <Tooltip label={row.locked ? 'Encrypted — no key on this machine' : 'Encrypted at rest'} withArrow>
            <IconLock size={12} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />
          </Tooltip>
        )}
        <Text size="xs" c="dimmed" style={{ flexShrink: 0, fontVariantNumeric: 'tabular-nums' }}>
          {formatWhen(row.at)}
        </Text>
        <ActionIcon
          component="div" role="button" size="xs" variant="subtle" color="gray"
          onClick={(e) => { e.stopPropagation(); onDelete() }}
          aria-label="Delete entry"
        >
          <IconTrash size={12} />
        </ActionIcon>
      </Group>
    </UnstyledButton>
  )
}

/** Status pill, colour-coded the same way the response panel's is. A locked entry has no status
 *  to show — its file can't be opened — so it says so instead of implying a zero. */
function StatusChip({ row }: { row: HistorySummary }) {
  if (row.locked) return <Badge size="xs" variant="light" color="gray" style={{ flexShrink: 0 }}>locked</Badge>
  if (row.error !== null || row.status === null) {
    return <Badge size="xs" variant="light" color="red" style={{ flexShrink: 0 }}>failed</Badge>
  }
  const color = row.status >= 500 ? 'red'
    : row.status >= 400 ? 'yellow'
      : row.status >= 300 ? 'orange'
        : 'green'
  return (
    <Badge size="xs" variant="light" color={color} style={{ flexShrink: 0, fontVariantNumeric: 'tabular-nums' }}>
      {row.status}
    </Badge>
  )
}

/** Relative for anything recent, absolute once "3 days ago" stops being useful. */
export function formatWhen(iso: string): string {
  const at = new Date(iso)
  const seconds = Math.floor((Date.now() - at.getTime()) / 1000)
  if (!Number.isFinite(seconds)) return ''
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`
  if (seconds < 604_800) return `${Math.floor(seconds / 86_400)}d ago`
  return at.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

/** Milliseconds, in the units a person reads. */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(2)} s`
}

/** Bytes, in the units a person reads. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** The env badge on a history row shows the file's stem — a full workspace-relative path
 *  would swamp the row, and the stem is what the picker calls it anyway. */
function envLabel(path: string): string {
  const name = path.split('/').pop() ?? path
  return name.replace(/\.env\.(tap|md)$/i, '')
}
