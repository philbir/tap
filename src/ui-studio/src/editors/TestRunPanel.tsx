import {
  ActionIcon, Badge, Box, Code, Collapse, Group, Loader, ScrollArea, Stack, Text, Tooltip,
} from '@mantine/core'
import {
  IconArrowRight, IconChevronDown, IconChevronRight, IconCircleCheckFilled, IconCircleMinus,
  IconCircleXFilled, IconPlayerStopFilled, IconX,
} from '@tabler/icons-react'
import { useState } from 'react'
import type {
  AssertResult, ExtractedValue, TestEntryResult, TestRunPlanEntry, TestRunResult, TestStepResult,
} from '../api/types'

/**
 * Results pane for a test-set or flow run, mounted as the editor's bottom pane.
 *
 * Rows appear from the run's `start` plan and fill in as `step` / `entry` events land, so a
 * ten-entry set reports progress instead of sitting blank until the last request returns.
 * Everything shown here is server-produced — the UI never decides whether something passed.
 */
export interface RunState {
  /** Plan rows, known before anything executes. */
  plan: TestRunPlanEntry[]
  /** Completed entries, keyed by plan index. */
  entries: Map<number, TestEntryResult>
  /** Steps as they land, keyed by entry index — the live view before an entry completes. */
  steps: Map<number, TestStepResult[]>
  result: TestRunResult | null
  error: string | null
  running: boolean
}

export const emptyRunState = (): RunState => ({
  plan: [], entries: new Map(), steps: new Map(), result: null, error: null, running: false,
})

interface Props {
  state: RunState
  /** Kind of the file being run — a flow's single entry is noise, so its steps are shown flat. */
  kind: 'test' | 'flow'
  onStop?: () => void
  onClose: () => void
  /** Re-run one entry. Absent for a flow, where there is only ever the one. */
  onRunOne?: (index: number) => void
}

export function TestRunPanel({ state, kind, onStop, onClose, onRunOne }: Props) {
  const { plan, entries, steps, result, error, running } = state

  // A flow has exactly one entry, and a collapsible row wrapping the whole run buys nothing.
  const flat = kind === 'flow'

  return (
    <Box style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Group
        justify="space-between"
        wrap="nowrap"
        px="lg"
        py={6}
        style={{ borderBottom: '1px solid var(--mantine-color-default-border)', flexShrink: 0 }}
      >
        <Group gap="xs" wrap="nowrap">
          {running && <Loader size={14} />}
          <Text size="sm" fw={600}>{running ? 'Running…' : 'Run'}</Text>
          {result && <RunSummary result={result} />}
          {result && (
            <Text size="xs" c="dimmed" ff="var(--mono)">{Math.round(result.durationMs)} ms</Text>
          )}
        </Group>
        <Group gap={4} wrap="nowrap">
          {running && onStop && (
            <Tooltip label="Stop the run" withArrow>
              <ActionIcon variant="subtle" color="red" size="sm" onClick={onStop} aria-label="Stop the run">
                <IconPlayerStopFilled size={14} />
              </ActionIcon>
            </Tooltip>
          )}
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose} aria-label="Close results">
            <IconX size={14} />
          </ActionIcon>
        </Group>
      </Group>

      {error && (
        <Box px="lg" py="xs" c="red" fz="sm" ff="var(--mono)" style={{ flexShrink: 0 }}>{error}</Box>
      )}

      <ScrollArea flex={1} type="hover" scrollbarSize={8}>
        <Box px="lg" py="xs">
          {plan.length === 0 && !error && (
            <Text size="sm" c="dimmed" py="md">Waiting for the run to start…</Text>
          )}

          {flat
            ? <FlatSteps entry={entries.get(0)} live={steps.get(0) ?? []} />
            : plan.map((row) => (
                <EntryRow
                  key={row.index}
                  plan={row}
                  entry={entries.get(row.index) ?? null}
                  live={steps.get(row.index) ?? []}
                  onRunOne={onRunOne}
                />
              ))}
        </Box>
      </ScrollArea>
    </Box>
  )
}

function FlatSteps({ entry, live }: { entry: TestEntryResult | undefined; live: TestStepResult[] }) {
  const steps = entry?.steps ?? live
  if (steps.length === 0) return null
  return (
    <Stack gap={2}>
      {steps.map((s) => <StepRow key={s.index} step={s} />)}
      {entry?.error && !steps.some((s) => !s.ok && !s.skipped) && (
        <Text size="xs" c="red" ff="var(--mono)" pl={26} py={4}>{entry.error}</Text>
      )}
    </Stack>
  )
}

function EntryRow({
  plan, entry, live, onRunOne,
}: {
  plan: TestRunPlanEntry
  entry: TestEntryResult | null
  live: TestStepResult[]
  onRunOne?: (index: number) => void
}) {
  // Failures open themselves — that's the row you came to read.
  const [open, setOpen] = useState<boolean | null>(null)
  const steps = entry?.steps ?? live
  const expanded = open ?? (entry !== null && !entry.ok && !entry.skipped)

  const pending = entry === null && live.length === 0
  const running = entry === null && live.length > 0

  return (
    <Box mb={2}>
      <Group gap={6} wrap="nowrap" style={{ cursor: 'pointer' }} onClick={() => setOpen(!expanded)} py={3}>
        <Box style={{ display: 'flex', flexShrink: 0, width: 14 }}>
          {steps.length > 0 && (expanded ? <IconChevronDown size={13} /> : <IconChevronRight size={13} />)}
        </Box>

        {pending
          ? <Box w={16} style={{ flexShrink: 0 }}><Text c="dimmed" fz="xs">·</Text></Box>
          : running
            ? <Loader size={12} />
            : <Verdict ok={entry!.ok} skipped={entry!.skipped} />}

        <Text size="sm" fw={500} truncate style={{ minWidth: 0, flex: 1 }} title={plan.name}>
          {plan.name}
        </Text>

        {plan.targetKind === 'flow' && (
          <Badge size="xs" variant="light" color="violet" style={{ flexShrink: 0 }}>flow</Badge>
        )}
        {entry && !entry.skipped && (
          <Text size="xs" c="dimmed" ff="var(--mono)" style={{ flexShrink: 0 }}>
            {Math.round(entry.durationMs)} ms
          </Text>
        )}
        {onRunOne && (
          <Tooltip label="Run just this test" withArrow>
            <ActionIcon
              variant="subtle" color="gray" size="sm"
              onClick={(e) => { e.stopPropagation(); onRunOne(plan.index) }}
              aria-label={`Run ${plan.name}`}
            >
              <IconArrowRight size={13} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      {entry?.error && !expanded && (
        <Text size="xs" c="red" pl={40} pb={4} lineClamp={1} title={entry.error}>{entry.error}</Text>
      )}

      <Collapse expanded={expanded}>
        <Box pl={20} pb={4}>
          {steps.map((s) => <StepRow key={s.index} step={s} />)}
        </Box>
      </Collapse>
    </Box>
  )
}

function StepRow({ step }: { step: TestStepResult }) {
  const [open, setOpen] = useState<boolean | null>(null)
  const expanded = open ?? (!step.ok && !step.skipped)
  const hasDetail = step.assertions.length > 0 || step.extracted.length > 0 || step.responseBody !== null

  return (
    <Box>
      <Group gap={6} wrap="nowrap" py={2} style={{ cursor: hasDetail ? 'pointer' : 'default' }}
        onClick={() => hasDetail && setOpen(!expanded)}>
        <Box style={{ display: 'flex', flexShrink: 0, width: 14 }}>
          {hasDetail && (expanded ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />)}
        </Box>
        <Verdict ok={step.ok} skipped={step.skipped} size={14} />
        <Text size="xs" truncate style={{ minWidth: 0, flex: 1 }} title={step.name}>{step.name}</Text>

        {step.status > 0 && (
          <Badge size="xs" variant="light" color={statusColor(step.status)} style={{ flexShrink: 0 }}>
            {step.status}
          </Badge>
        )}
        {step.assertSummary && (
          <Text size="xs" c={step.assertSummary.failed > 0 ? 'red' : 'dimmed'} ff="var(--mono)" style={{ flexShrink: 0 }}>
            {step.assertSummary.passed}/{step.assertSummary.passed + step.assertSummary.failed}
          </Text>
        )}
        {!step.skipped && step.durationMs > 0 && (
          <Text size="xs" c="dimmed" ff="var(--mono)" style={{ flexShrink: 0 }}>
            {Math.round(step.durationMs)} ms
          </Text>
        )}
      </Group>

      {step.error && (
        <Text size="xs" c={step.skipped ? 'dimmed' : 'red'} pl={34} pb={2}>{step.error}</Text>
      )}

      <Collapse expanded={expanded}>
        <Box pl={34} pb="xs">
          {step.method !== '—' && (
            <Text size="xs" c="dimmed" ff="var(--mono)" mb={4} style={{ wordBreak: 'break-all' }}>
              {step.method} {step.url}
            </Text>
          )}

          {step.extracted.length > 0 && (
            <Stack gap={1} mb={6}>
              {step.extracted.map((e) => <BoundValue key={e.var} value={e} />)}
            </Stack>
          )}

          {step.assertions.length > 0 && (
            <Stack gap={1} mb={6}>
              {step.assertions.map((a) => <AssertionRow key={a.index} result={a} />)}
            </Stack>
          )}

          {step.responseBody && (
            <Code block fz="xs" mah={220} style={{ overflow: 'auto', whiteSpace: 'pre-wrap' }}>
              {step.responseBody}
            </Code>
          )}
        </Box>
      </Collapse>
    </Box>
  )
}

function BoundValue({ value }: { value: ExtractedValue }) {
  if (value.error) {
    return (
      <Group gap={6} wrap="nowrap">
        <IconCircleXFilled size={12} color="var(--mantine-color-red-6)" style={{ flexShrink: 0 }} />
        <Text size="xs" c="red">{value.error}</Text>
      </Group>
    )
  }
  return (
    <Group gap={6} wrap="nowrap">
      <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>bound</Text>
      <Text size="xs" ff="var(--mono)" c="violet" style={{ flexShrink: 0 }}>{value.var}</Text>
      <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>=</Text>
      <Text size="xs" ff="var(--mono)" truncate title={value.value ?? ''}>
        {value.value ?? <Text component="span" c="dimmed">nothing (optional)</Text>}
      </Text>
    </Group>
  )
}

function AssertionRow({ result }: { result: AssertResult }) {
  const detail = result.ok
    ? null
    : result.message ?? `expected ${result.expected ?? '—'}, got ${result.actual ?? 'nothing'}`
  return (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <Box style={{ display: 'flex', flexShrink: 0, marginTop: 2 }}>
        <Verdict ok={result.ok} skipped={result.skipped} size={12} />
      </Box>
      <Box style={{ minWidth: 0 }}>
        <Text size="xs" c={result.ok ? undefined : 'red'}>{result.name}</Text>
        {detail && <Text size="xs" c="dimmed">{detail}</Text>}
      </Box>
    </Group>
  )
}

function Verdict({ ok, skipped, size = 16 }: { ok: boolean; skipped: boolean; size?: number }) {
  if (skipped) {
    return <Text c="dimmed" component="span" style={{ display: 'flex', flexShrink: 0 }}><IconCircleMinus size={size} /></Text>
  }
  return ok
    ? <Text c="green" component="span" style={{ display: 'flex', flexShrink: 0 }}><IconCircleCheckFilled size={size} /></Text>
    : <Text c="red" component="span" style={{ display: 'flex', flexShrink: 0 }}><IconCircleXFilled size={size} /></Text>
}

export function RunSummary({ result }: { result: TestRunResult }) {
  const total = result.passed + result.failed
  const color = result.failed > 0 ? 'red' : result.passed > 0 ? 'green' : 'gray'
  const label = result.failed > 0
    ? `${result.failed} failed`
    : total > 0 ? `${result.passed}/${total} passed` : 'nothing ran'
  return (
    <Badge size="sm" variant="light" color={color}>
      {label}{result.skipped > 0 ? ` · ${result.skipped} skipped` : ''}
    </Badge>
  )
}

function statusColor(status: number): string {
  if (status >= 500) return 'red'
  if (status >= 400) return 'orange'
  if (status >= 300) return 'yellow'
  if (status >= 200) return 'green'
  return 'gray'
}
