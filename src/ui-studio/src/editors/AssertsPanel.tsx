import { ActionIcon, Badge, Box, Button, Group, Select, Table, TagsInput, Text, Tooltip } from '@mantine/core'
import {
  IconCircleCheckFilled, IconCircleMinus, IconCircleXFilled, IconLetterCase, IconPlus, IconX,
} from '@tabler/icons-react'
import { type ReactNode } from 'react'
import {
  ASSERT_JSON_TYPES, MATCHERS_FOR_SOURCE, OP_TAKES_LIST, OP_TAKES_NO_VALUE, SOURCE_TAKES_SELECTOR,
  type AssertOp, type AssertResult, type AssertSpec, type AssertSource, type AssertSummary,
  type VariableContext,
} from '../api/types'
import { VariableInput } from './VariableInput'

/**
 * Editor for a request's assertions — one row per (extractor, matcher, expected) triple.
 *
 * The row is built from dropdowns rather than free text on purpose: the matcher list is
 * filtered to what the chosen extractor actually supports, so the editor can't produce an
 * assertion the server would refuse to save. Expected values go through {@link VariableInput}
 * so `{{var}}` works on the expectation exactly as it does in a URL or header.
 *
 * `results` paints last-run (or live re-check) verdicts into the leading column, which is
 * what turns this from a form into a feedback loop: Send once, then shape the assertions
 * against the real response and watch each row flip.
 */
export interface AssertsPanelProps {
  assertions: AssertSpec[]
  onChange: (next: AssertSpec[]) => void
  /** Verdicts keyed by their `index` into `assertions`. Null while none are known. */
  results?: AssertResult[] | null
  summary?: AssertSummary | null
  variableContext?: VariableContext | null
  onOpenVariables?: () => void
  /** Shown above the table — used to explain where the live verdicts come from. */
  hint?: ReactNode
}

const SOURCE_OPTIONS: { value: AssertSource; label: string }[] = [
  { value: 'status', label: 'Status' },
  { value: 'duration', label: 'Duration' },
  { value: 'header', label: 'Header' },
  { value: 'body', label: 'Body' },
  { value: 'jsonpath', label: 'JSONPath' },
  { value: 'xpath', label: 'XPath' },
]

const OP_LABELS: Record<AssertOp, string> = {
  equals: 'equals',
  notEquals: 'not equals',
  contains: 'contains',
  notContains: 'not contains',
  startsWith: 'starts with',
  endsWith: 'ends with',
  matches: 'matches regex',
  notMatches: 'no regex match',
  lt: 'less than',
  lte: 'at most',
  gt: 'greater than',
  gte: 'at least',
  between: 'between',
  in: 'one of',
  exists: 'exists',
  count: 'node count',
  length: 'length',
  type: 'is type',
}

/** Matchers where a case-insensitive comparison is meaningful. */
const CASEABLE_OPS: ReadonlySet<AssertOp> = new Set<AssertOp>([
  'equals', 'notEquals', 'contains', 'notContains', 'startsWith', 'endsWith', 'matches', 'notMatches', 'in',
])

const SELECTOR_PLACEHOLDER: Partial<Record<AssertSource, string>> = {
  header: 'content-type',
  jsonpath: '$.data.id',
  xpath: '/root/item',
}

const EXPECTED_PLACEHOLDER: Partial<Record<AssertOp, string>> = {
  matches: '^ord-\\d+$',
  notMatches: '^tmp-',
  between: 'low and high',
  in: 'add values…',
}

export function AssertsPanel({
  assertions, onChange, results, summary, variableContext, onOpenVariables, hint,
}: AssertsPanelProps) {
  function patch(index: number, next: Partial<AssertSpec>) {
    onChange(assertions.map((a, i) => (i === index ? { ...a, ...next } : a)))
  }

  function remove(index: number) {
    onChange(assertions.filter((_, i) => i !== index))
  }

  function add() {
    onChange([...assertions, { source: 'status', op: 'equals', expected: '200' }])
  }

  /** Changing the extractor can strand the matcher (a `count` on a body, say). Snap to the
   *  first matcher the new extractor supports rather than leaving an unsavable row. */
  function changeSource(index: number, source: AssertSource) {
    const current = assertions[index]
    const allowed = MATCHERS_FOR_SOURCE[source]
    const op = allowed.includes(current.op) ? current.op : allowed[0]
    patch(index, {
      source,
      op,
      selector: SOURCE_TAKES_SELECTOR[source] ? (current.selector ?? '') : null,
      ...resetValues(op, current),
    })
  }

  function changeOp(index: number, op: AssertOp) {
    patch(index, { op, ...resetValues(op, assertions[index]) })
  }

  const resultFor = (index: number) => results?.find((r) => r.index === index) ?? null

  return (
    <Box maw={1100}>
      <Group justify="space-between" mb="xs" align="center">
        <Box>
          {hint && <Text size="xs" c="dimmed">{hint}</Text>}
        </Box>
        {summary && <SummaryBadge summary={summary} />}
      </Group>

      {assertions.length === 0 ? (
        <Box ta="center" py="lg" c="dimmed" fz="sm">
          No assertions yet. Add one to describe what a passing response looks like — the
          verdict shows up here and in the response panel after every Send.
        </Box>
      ) : (
        <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false}>
          <Table.Tbody>
            {assertions.map((assertion, index) => {
              const takesSelector = SOURCE_TAKES_SELECTOR[assertion.source]
              const result = resultFor(index)
              return (
                // Index keys: rows are never reordered, and removal is a click that moves
                // focus to the button anyway, so stable ids would buy nothing here.
                <Table.Tr key={index} opacity={assertion.skip ? 0.55 : 1}>
                  <Table.Td w={30}>
                    <ResultDot result={result} />
                  </Table.Td>

                  <Table.Td w={122}>
                    <Select
                      size="xs"
                      allowDeselect={false}
                      value={assertion.source}
                      data={SOURCE_OPTIONS}
                      onChange={(v) => v && changeSource(index, v as AssertSource)}
                      aria-label="What to check"
                    />
                  </Table.Td>

                  {takesSelector && (
                    <Table.Td>
                      <VariableInput
                        size="xs"
                        value={assertion.selector ?? ''}
                        onChange={(v) => patch(index, { selector: v })}
                        placeholder={SELECTOR_PLACEHOLDER[assertion.source]}
                        context={variableContext ?? null}
                        onOpenVariables={onOpenVariables}
                      />
                    </Table.Td>
                  )}

                  <Table.Td w={148}>
                    <Select
                      size="xs"
                      allowDeselect={false}
                      value={assertion.op}
                      data={MATCHERS_FOR_SOURCE[assertion.source].map((op) => ({ value: op, label: OP_LABELS[op] }))}
                      onChange={(v) => v && changeOp(index, v as AssertOp)}
                      aria-label="How to compare"
                    />
                  </Table.Td>

                  <Table.Td colSpan={takesSelector ? 1 : 2}>
                    <ExpectedCell
                      assertion={assertion}
                      onChange={(next) => patch(index, next)}
                      variableContext={variableContext}
                      onOpenVariables={onOpenVariables}
                    />
                  </Table.Td>

                  <Table.Td w={30}>
                    {CASEABLE_OPS.has(assertion.op) && (
                      <Tooltip label={assertion.ignoreCase ? 'Case-insensitive' : 'Case-sensitive'} withArrow>
                        <ActionIcon
                          variant={assertion.ignoreCase ? 'light' : 'subtle'}
                          color={assertion.ignoreCase ? 'tap' : 'gray'}
                          size="sm"
                          onClick={() => patch(index, { ignoreCase: !assertion.ignoreCase })}
                          aria-label="Toggle case sensitivity"
                        >
                          <IconLetterCase size={14} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </Table.Td>

                  <Table.Td w={30}>
                    <Tooltip label={assertion.skip ? 'Enable this assertion' : 'Skip this assertion'} withArrow>
                      <ActionIcon
                        variant={assertion.skip ? 'light' : 'subtle'}
                        color={assertion.skip ? 'yellow' : 'gray'}
                        size="sm"
                        onClick={() => patch(index, { skip: !assertion.skip })}
                        aria-label="Toggle skip"
                      >
                        <IconCircleMinus size={14} />
                      </ActionIcon>
                    </Tooltip>
                  </Table.Td>

                  <Table.Td w={30}>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      size="sm"
                      onClick={() => remove(index)}
                      aria-label="Remove assertion"
                    >
                      <IconX size={14} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      )}

      <Group mt="xs">
        <Button size="xs" variant="light" leftSection={<IconPlus size={14} />} onClick={add}>
          Add assertion
        </Button>
      </Group>
    </Box>
  )
}

/** Clears expected values that don't carry over when the matcher changes shape. */
function resetValues(op: AssertOp, current: AssertSpec): Partial<AssertSpec> {
  if (OP_TAKES_NO_VALUE.has(op)) return { expected: 'true', expectedList: null }
  if (OP_TAKES_LIST.has(op)) return { expected: null, expectedList: current.expectedList ?? [] }
  if (op === 'type') return { expected: ASSERT_JSON_TYPES.includes(current.expected as never) ? current.expected : 'string', expectedList: null }
  // Coming from exists/list, the old value is meaningless; from another scalar matcher it isn't.
  const carried = OP_TAKES_LIST.has(current.op) || OP_TAKES_NO_VALUE.has(current.op) ? '' : (current.expected ?? '')
  return { expected: carried, expectedList: null }
}

function ExpectedCell({
  assertion, onChange, variableContext, onOpenVariables,
}: {
  assertion: AssertSpec
  onChange: (next: Partial<AssertSpec>) => void
  variableContext?: VariableContext | null
  onOpenVariables?: () => void
}) {
  if (assertion.op === 'exists') {
    return (
      <Select
        size="xs"
        allowDeselect={false}
        value={assertion.expected === 'false' ? 'false' : 'true'}
        data={[{ value: 'true', label: 'is present' }, { value: 'false', label: 'is absent' }]}
        onChange={(v) => onChange({ expected: v ?? 'true' })}
        aria-label="Expected presence"
      />
    )
  }

  if (assertion.op === 'type') {
    return (
      <Select
        size="xs"
        allowDeselect={false}
        value={assertion.expected ?? 'string'}
        data={ASSERT_JSON_TYPES as unknown as string[]}
        onChange={(v) => onChange({ expected: v ?? 'string' })}
        aria-label="Expected JSON type"
      />
    )
  }

  if (OP_TAKES_LIST.has(assertion.op)) {
    return (
      <TagsInput
        size="xs"
        value={assertion.expectedList ?? []}
        onChange={(v) => onChange({ expectedList: v })}
        placeholder={EXPECTED_PLACEHOLDER[assertion.op]}
        maxTags={assertion.op === 'between' ? 2 : undefined}
        aria-label="Expected values"
      />
    )
  }

  return (
    <VariableInput
      size="xs"
      value={assertion.expected ?? ''}
      onChange={(v) => onChange({ expected: v })}
      placeholder={EXPECTED_PLACEHOLDER[assertion.op] ?? 'expected value or {{var}}'}
      context={variableContext ?? null}
      onOpenVariables={onOpenVariables}
    />
  )
}

function ResultDot({ result }: { result: AssertResult | null }) {
  if (!result) return <Text c="dimmed" fz="xs" ta="center">·</Text>

  if (result.skipped) {
    return (
      <Tooltip label={result.message ?? 'Skipped'} withArrow>
        <Text c="dimmed" component="span" style={{ display: 'flex' }}><IconCircleMinus size={16} /></Text>
      </Tooltip>
    )
  }

  if (result.ok) {
    return (
      <Tooltip label={`Passed — got ${result.actual ?? 'no value'}`} withArrow>
        <Text c="green" component="span" style={{ display: 'flex' }}><IconCircleCheckFilled size={16} /></Text>
      </Tooltip>
    )
  }

  return (
    <Tooltip label={result.message ?? `Expected ${result.expected ?? '—'}, got ${result.actual ?? 'nothing'}`} withArrow multiline maw={340}>
      <Text c="red" component="span" style={{ display: 'flex' }}><IconCircleXFilled size={16} /></Text>
    </Tooltip>
  )
}

export function SummaryBadge({ summary, size = 'sm' }: { summary: AssertSummary; size?: string }) {
  const total = summary.passed + summary.failed
  const color = summary.failed > 0 ? 'red' : summary.passed > 0 ? 'green' : 'gray'
  const label = summary.failed > 0
    ? `${summary.failed} failed`
    : total > 0 ? `${summary.passed}/${total} passed` : 'all skipped'
  return (
    <Badge size={size as never} variant="light" color={color}>
      {label}{summary.skipped > 0 ? ` · ${summary.skipped} skipped` : ''}
    </Badge>
  )
}
