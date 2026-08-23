import {
  ActionIcon, Badge, Box, Button, Center, Group, Loader, ScrollArea, Select, Stack, Text, Tooltip,
  UnstyledButton,
} from '@mantine/core'
import { modals } from '@mantine/modals'
import { IconHistory, IconLock, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { HistorySummary } from '../api/types'
import { useTapStore } from '../store'
import { labelForPath } from './tapFiles'

/**
 * Every exchange the workspace has recorded, newest first — the view the per-request History tab
 * can't be: "what did I run before lunch", across collections, when you no longer remember which
 * request it was.
 *
 * <p>Rows for requests that have since been deleted stay in the list, tagged. Dropping them would
 * hide exactly the history most likely to be wanted — you deleted the request, and now you want
 * to know what it used to return.</p>
 *
 * <p>Clicking a row opens the exchange it names, not merely the request it belongs to: the tab,
 * its History tab, and that entry in the response pane. Landing on the request's Params tab and
 * leaving the user to find the row again would be answering a different question than the one
 * the click asked.</p>
 */
export function HistoryView({ onOpenEntry }: { onOpenEntry: (row: HistorySummary) => void }) {
  const generation = useTapStore((s) => s.generation)
  const collections = useTapStore((s) => s.collections)
  const [rows, setRows] = useState<HistorySummary[] | null>(null)
  const [collection, setCollection] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setRows(await api.history({
        limit: 200,
        collection: collection ?? undefined,
        status: status ?? undefined,
      }))
    } catch {
      setRows([])
    }
  }, [collection, status])

  useEffect(() => { void load() }, [load, generation])

  const orphanCount = useMemo(() => rows?.filter((r) => r.orphaned).length ?? 0, [rows])

  /** Grouped by calendar day so a long list reads as a timeline rather than a wall of rows. */
  const days = useMemo(() => {
    const out: { key: string; label: string; rows: HistorySummary[] }[] = []
    for (const row of rows ?? []) {
      const key = new Date(row.at).toDateString()
      const last = out[out.length - 1]
      if (last?.key === key) last.rows.push(row)
      else out.push({ key, label: dayLabel(new Date(row.at)), rows: [row] })
    }
    return out
  }, [rows])

  function confirmClearOrphans() {
    modals.openConfirmModal({
      title: 'Clear orphaned history?',
      children: (
        <Text size="sm">
          {orphanCount === 1 ? '1 entry belongs' : `${orphanCount} entries belong`} to requests that
          no longer exist. Deleting them frees the space now; left alone they age out on their own,
          and re-link if the request comes back.
        </Text>
      ),
      labels: { confirm: 'Clear', cancel: 'Keep' },
      confirmProps: { color: 'red' },
      onConfirm: () => { void api.clearOrphanedHistory().then(load) },
    })
  }

  return (
    <Stack gap={0} h="100%">
      <Stack gap="xs" p="sm">
        <Group gap="xs" wrap="nowrap">
          <Select
            flex={1}
            size="xs"
            placeholder="All collections"
            data={collections.map((c) => ({ value: c.slug, label: c.name }))}
            value={collection}
            onChange={setCollection}
            clearable
          />
          <Select
            w={104}
            size="xs"
            placeholder="Any"
            data={[
              { value: 'ok', label: 'OK' },
              { value: 'failed', label: 'Failed' },
              { value: '2xx', label: '2xx' },
              { value: '4xx', label: '4xx' },
              { value: '5xx', label: '5xx' },
            ]}
            value={status}
            onChange={setStatus}
            clearable
          />
          <Tooltip label="Refresh" withArrow>
            <ActionIcon variant="subtle" color="gray" onClick={() => void load()} aria-label="Refresh history">
              <IconRefresh size={15} />
            </ActionIcon>
          </Tooltip>
        </Group>
        {orphanCount > 0 && (
          <Button
            size="compact-xs" variant="subtle" color="gray" leftSection={<IconTrash size={12} />}
            onClick={confirmClearOrphans}
          >
            Clear {orphanCount} orphaned
          </Button>
        )}
      </Stack>

      <Box style={{ flex: 1, minHeight: 0 }}>
        {rows === null && (
          <Center h="100%"><Loader size="xs" /></Center>
        )}
        {rows?.length === 0 && <EmptyState filtered={collection !== null || status !== null} />}
        {rows && rows.length > 0 && (
          <ScrollArea h="100%" type="hover" scrollbarSize={8}>
            <Stack gap={2} px="xs" pb="sm">
              {days.map((day) => (
                <Box key={day.key}>
                  <Text size="xs" c="dimmed" fw={600} px={6} pt="xs" pb={4}>{day.label}</Text>
                  {day.rows.map((row) => (
                    <TimelineRow
                      key={`${row.requestId}/${row.id}`}
                      row={row}
                      onOpen={() => onOpenEntry(row)}
                    />
                  ))}
                </Box>
              ))}
            </Stack>
          </ScrollArea>
        )}
      </Box>
    </Stack>
  )
}

function EmptyState({ filtered }: { filtered: boolean }) {
  return (
    <Center h="100%" px="md">
      <Stack align="center" gap={6} maw={280} ta="center">
        <IconHistory size={26} stroke={1.5} color="var(--mantine-color-dimmed)" />
        <Text size="sm" c="dimmed">
          {filtered
            ? 'Nothing matches those filters.'
            : 'Nothing recorded yet. Turn on history: on the workspace or a collection, then send a request.'}
        </Text>
      </Stack>
    </Center>
  )
}

function TimelineRow({ row, onOpen }: { row: HistorySummary; onOpen: () => void }) {
  const name = row.requestName
    ?? (row.requestPath ? labelForPath(row.requestPath) : row.requestId)

  return (
    <UnstyledButton
      onClick={onOpen}
      disabled={row.orphaned || !row.requestPath}
      style={{
        display: 'block',
        width: '100%',
        padding: '5px 6px',
        borderRadius: 'var(--mantine-radius-sm)',
        cursor: row.orphaned ? 'default' : 'pointer',
        opacity: row.orphaned ? 0.65 : 1,
      }}
      title={row.url}
    >
      <Group gap={6} wrap="nowrap">
        <StatusDot row={row} />
        <Text size="xs" truncate="end" style={{ flex: 1, minWidth: 0 }}>{name}</Text>
        {row.encrypted && <IconLock size={11} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />}
        {row.orphaned && (
          <Badge size="xs" variant="light" color="gray" style={{ flexShrink: 0 }}>deleted</Badge>
        )}
        <Text size="xs" c="dimmed" style={{ flexShrink: 0, fontVariantNumeric: 'tabular-nums' }}>
          {new Date(row.at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
        </Text>
      </Group>
    </UnstyledButton>
  )
}

function StatusDot({ row }: { row: HistorySummary }) {
  const color = row.locked ? 'gray'
    : row.error !== null || row.status === null ? 'red'
      : row.status >= 500 ? 'red'
        : row.status >= 400 ? 'yellow'
          : row.status >= 300 ? 'orange'
            : 'green'
  return (
    <Box
      w={6} h={6}
      style={{ borderRadius: '50%', background: `var(--mantine-color-${color}-6)`, flexShrink: 0 }}
    />
  )
}

function dayLabel(at: Date): string {
  const today = new Date()
  const yesterday = new Date(today)
  yesterday.setDate(today.getDate() - 1)
  if (at.toDateString() === today.toDateString()) return 'Today'
  if (at.toDateString() === yesterday.toDateString()) return 'Yesterday'
  return at.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })
}
