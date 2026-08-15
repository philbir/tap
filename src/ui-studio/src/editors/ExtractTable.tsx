import { ActionIcon, Box, Button, NumberInput, Select, Table, Text, TextInput, Tooltip } from '@mantine/core'
import { IconAsterisk, IconPlus, IconX } from '@tabler/icons-react'
import { EXTRACT_TAKES_SELECTOR, type ExtractSource, type ExtractSpec, type VariableContext } from '../api/types'
import { VariableInput } from './VariableInput'
import { passwordManagerOptOut } from './passwordManagerOptOut'

/**
 * Editor for a flow step's `extract:` list — one row per value the step binds for the steps
 * after it. This is the piece that turns a list of requests into a flow, so the row reads
 * left-to-right as the sentence it is: **variable ← source (selector)**.
 *
 * Rows are built from a source dropdown plus one input rather than free text, because the
 * server validates the same combinations and an editor that can produce an unsavable row is
 * a worse editor.
 */
export interface ExtractTableProps {
  extractions: ExtractSpec[]
  onChange: (next: ExtractSpec[]) => void
  variableContext?: VariableContext | null
  onOpenVariables?: () => void
}

const SOURCE_OPTIONS: { value: ExtractSource; label: string }[] = [
  { value: 'jsonpath', label: 'JSONPath' },
  { value: 'xpath', label: 'XPath' },
  { value: 'regex', label: 'Regex' },
  { value: 'header', label: 'Header' },
  { value: 'body', label: 'Body' },
  { value: 'status', label: 'Status' },
  { value: 'duration', label: 'Duration' },
]

const SELECTOR_PLACEHOLDER: Partial<Record<ExtractSource, string>> = {
  jsonpath: '$.order.id',
  xpath: '/order/id',
  regex: 'session=([^;]+)',
  header: 'etag',
}

export function ExtractTable({
  extractions, onChange, variableContext, onOpenVariables,
}: ExtractTableProps) {
  function patch(index: number, next: Partial<ExtractSpec>) {
    onChange(extractions.map((e, i) => (i === index ? { ...e, ...next } : e)))
  }

  function changeSource(index: number, source: ExtractSource) {
    patch(index, {
      source,
      selector: EXTRACT_TAKES_SELECTOR[source] ? (extractions[index].selector ?? '') : null,
      // A capture group only means something for a regex.
      group: source === 'regex' ? extractions[index].group ?? null : null,
    })
  }

  return (
    <Box>
      {extractions.length === 0 ? (
        <Box ta="center" py="md" c="dimmed" fz="sm">
          Nothing extracted. Bind a value here — an id, a token, an ETag — and the steps below
          read it as <Text component="span" ff="var(--mono)">{'{{name}}'}</Text>.
        </Box>
      ) : (
        <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false}>
          <Table.Tbody>
            {extractions.map((extract, index) => {
              const takesSelector = EXTRACT_TAKES_SELECTOR[extract.source]
              return (
                <Table.Tr key={index}>
                  <Table.Td w={150}>
                    <TextInput
                      size="xs"
                      value={extract.var}
                      placeholder="variableName"
                      onChange={(e) => patch(index, { var: e.currentTarget.value })}
                      styles={{ input: { fontFamily: 'var(--mono)' } }}
                      aria-label="Variable to bind"
                      {...passwordManagerOptOut}
                    />
                  </Table.Td>

                  <Table.Td w={16}>
                    <Text size="xs" c="dimmed" ta="center">←</Text>
                  </Table.Td>

                  <Table.Td w={110}>
                    <Select
                      size="xs"
                      allowDeselect={false}
                      value={extract.source}
                      data={SOURCE_OPTIONS}
                      onChange={(v) => v && changeSource(index, v as ExtractSource)}
                      aria-label="What to read"
                    />
                  </Table.Td>

                  <Table.Td>
                    {takesSelector ? (
                      <VariableInput
                        size="xs"
                        value={extract.selector ?? ''}
                        onChange={(v) => patch(index, { selector: v })}
                        placeholder={SELECTOR_PLACEHOLDER[extract.source]}
                        context={variableContext ?? null}
                        onOpenVariables={onOpenVariables}
                      />
                    ) : (
                      <Text size="xs" c="dimmed">the whole {extract.source}</Text>
                    )}
                  </Table.Td>

                  {extract.source === 'regex' && (
                    <Table.Td w={92}>
                      <NumberInput
                        size="xs"
                        value={extract.group ?? ''}
                        onChange={(v) => patch(index, { group: v === '' ? null : Number(v) })}
                        placeholder="group"
                        min={0}
                        allowDecimal={false}
                        aria-label="Capture group"
                      />
                    </Table.Td>
                  )}

                  <Table.Td w={132}>
                    <TextInput
                      size="xs"
                      value={extract.default ?? ''}
                      placeholder="default…"
                      onChange={(e) => patch(index, { default: e.currentTarget.value || null })}
                      aria-label="Default when nothing matches"
                      {...passwordManagerOptOut}
                    />
                  </Table.Td>

                  <Table.Td w={30}>
                    <Tooltip
                      label={extract.required === false
                        ? 'Optional — a miss binds nothing and the flow carries on'
                        : 'Required — a miss fails the step'}
                      withArrow
                    >
                      <ActionIcon
                        variant={extract.required === false ? 'subtle' : 'light'}
                        color={extract.required === false ? 'gray' : 'tap'}
                        size="sm"
                        onClick={() => patch(index, { required: extract.required === false })}
                        aria-label="Toggle required"
                      >
                        <IconAsterisk size={13} />
                      </ActionIcon>
                    </Tooltip>
                  </Table.Td>

                  <Table.Td w={30}>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      size="sm"
                      onClick={() => onChange(extractions.filter((_, i) => i !== index))}
                      aria-label="Remove extraction"
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

      <Button
        size="xs"
        variant="light"
        mt="xs"
        leftSection={<IconPlus size={14} />}
        onClick={() => onChange([...extractions, { var: '', source: 'jsonpath', selector: '', required: true }])}
      >
        Extract a value
      </Button>
    </Box>
  )
}
