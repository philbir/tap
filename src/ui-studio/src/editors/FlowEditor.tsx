import {
  ActionIcon, Badge, Box, Button, Collapse, Group, Paper, Select, Stack, Tabs, TagsInput, Text,
  TextInput, Tooltip,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconArrowsSplit2, IconChevronDown, IconChevronRight, IconChevronUp, IconCircleMinus, IconCode, IconFileText,
  IconPlayerPlayFilled, IconPlus, IconVariable, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type {
  FlowDetail, FlowSpec, FlowStepSpec, RequestSummary, VariableContext,
} from '../api/types'
import { useActiveEnv, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { AdaptiveTabsList } from './AdaptiveTabsList'
import { AssertsPanel } from './AssertsPanel'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount } from './EditorShell'
import { ExtractTable } from './ExtractTable'
import { KvTable, type KvRow } from './KvTable'
import { SourceTab } from './SourceTab'
import { TestRunPanel } from './TestRunPanel'
import { matchRefOptionGrouped, requestSelectGroups, resolveRef } from './testingOptions'
import { useTabView } from './useTabView'
import { useSpecEditor } from './useSpecEditor'
import { useTestRun } from './useTestRun'
import { flatVarsToRows, rowsToFlatVars } from './varRows'
import { VariablesPanel } from './VariablesPanel'
import { labelForPath } from '../shell/tapFiles'

interface Props { path: string }

/**
 * Editor for a `*.flow.tap` — an ordered sequence of requests that passes values between
 * steps.
 *
 * The step list is the whole point, so it's the default tab and each step is a card that
 * reads top-down in the order things happen: which request, what variables it goes in with,
 * what it binds out of the response, what has to be true about it. Everything below the
 * header collapses, because a six-step flow you're only editing one step of shouldn't fill
 * the screen.
 */
export function FlowEditor({ path }: Props) {
  const collections = useTapStore((s) => s.collections)
  const generation = useTapStore((s) => s.generation)
  const activeEnv = useActiveEnv()
  const tagSuggestions = useTagDictionary()

  const [tab, setTab] = useTabView<string | null>(path, 'tab', 'steps')
  const [requests, setRequests] = useState<RequestSummary[]>([])
  const [varsOpened, varsCtl] = useDisclosure(false)

  const editor = useSpecEditor<FlowDetail, FlowSpec>({
    key: path,
    fetchDetail: api.flow,
    specFromDetail: specFromFlow,
    saveSpec: api.saveFlowSpec,
  })
  const { detail, spec, setSpec, update, dirty, saving, errorMessage } = editor

  const run = useTestRun(path)

  useEffect(() => {
    let cancelled = false
    api.requests()
      .then((rows) => { if (!cancelled) setRequests(rows) })
      .catch(() => !cancelled && setRequests([]))
    return () => { cancelled = true }
  }, [generation])

  const requestOptions = useMemo(
    () => requestSelectGroups({ requests, collections, fromPath: path }),
    [requests, collections, path],
  )

  // A step's own variables resolve against the request it targets, but the flow doesn't know
  // which collection that is until the step picks one — so the token autocomplete is scoped
  // to the active environment, which is what every step shares.
  const variableContext = useMemo<VariableContext>(
    () => ({ envPath: activeEnv ?? undefined }),
    [activeEnv],
  )

  if (!spec) {
    return (
      <EditorShell title={basename(path)} kindLabel="Flow" dirty={false} saving={saving}
        errorMessage={errorMessage} onSave={() => {}}>
        <Text c="dimmed" size="sm">Loading…</Text>
      </EditorShell>
    )
  }

  const steps = spec.steps
  const setSteps = (next: FlowStepSpec[]) => update('steps', next)
  const patchStep = (index: number, patch: Partial<FlowStepSpec>) =>
    setSteps(steps.map((s, i) => (i === index ? { ...s, ...patch } : s)))

  function move(index: number, by: number) {
    const to = index + by
    if (to < 0 || to >= steps.length) return
    const next = steps.slice()
    const [moved] = next.splice(index, 1)
    next.splice(to, 0, moved)
    setSteps(next)
  }

  return (
    <>
      <EditorShell
        title={spec.name || basename(path)}
        kindLabel="Flow"
        dirty={dirty}
        saving={saving}
        errorMessage={errorMessage}
        onSave={editor.save}
        onDiscard={editor.discard}
        onTitleChange={(n) => update('name', n)}
        toolbarExtras={
          <Tooltip label={dirty ? 'Save first — a run always uses what is on disk' : 'Run this flow'} withArrow>
            <Button
              size="xs"
              leftSection={<IconPlayerPlayFilled size={13} />}
              onClick={() => run.run(activeEnv, null)}
              loading={run.state.running}
              disabled={dirty || steps.length === 0}
            >
              Run
            </Button>
          </Tooltip>
        }
        bottomPane={run.active ? (
          <TestRunPanel state={run.state} kind="flow" onStop={run.stop} onClose={run.clear} />
        ) : undefined}
      >
        <Tabs value={tab} onChange={setTab} keepMounted={false}>
          <AdaptiveTabsList
            tabs={[
              {
                value: 'steps',
                label: 'Steps',
                icon: <IconArrowsSplit2 size={14} />,
                adornment: <TabCount count={steps.length} />,
              },
              {
                value: 'vars',
                label: 'Variables',
                icon: <IconVariable size={14} />,
                adornment: <TabCount count={Object.keys(spec.vars ?? {}).length} />,
              },
              { value: 'docs', label: 'Docs', icon: <IconFileText size={14} /> },
              { value: 'source', label: 'Source', icon: <IconCode size={14} /> },
            ]}
          />

          <Tabs.Panel value="steps" pt="md">
            <Stack gap="sm" maw={1100}>
              {steps.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="lg">
                  No steps yet. Add the first request this flow sends — then extract a value from
                  its response and the next step can use it.
                </Text>
              )}

              {steps.map((step, index) => (
                <StepCard
                  key={index}
                  index={index}
                  count={steps.length}
                  step={step}
                  requestOptions={requestOptions}
                  flowPath={path}
                  variableContext={variableContext}
                  onOpenVariables={varsCtl.open}
                  onPatch={(patch) => patchStep(index, patch)}
                  onMove={(by) => move(index, by)}
                  onRemove={() => setSteps(steps.filter((_, i) => i !== index))}
                />
              ))}

              <Group>
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconPlus size={14} />}
                  onClick={() => setSteps([...steps, { request: '', extract: [], assertions: [] }])}
                >
                  Add step
                </Button>
              </Group>
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="vars" pt="md">
            <Box maw={760}>
              <Text size="xs" c="dimmed" mb="xs">
                Flow-scoped variables. They override every file scope for this run, and anything a
                step extracts overrides them in turn — that ordering is what lets a flow supply a
                starting value that later steps replace.
              </Text>
              <KvTable
                rows={flatVarsToRows(spec.vars, spec.secrets)}
                onChange={(rows) => setSpec((cur) => cur ? { ...cur, ...rowsToFlatVars(rows) } : cur)}
                keyPlaceholder="name"
                valuePlaceholder="value"
                allowSecretToggle
                emptyHint="No flow variables. Add one to give every step a shared starting value."
                variableContext={{ envPath: activeEnv ?? undefined }}
                onOpenVariables={varsCtl.open}
              />
              <Box mt="lg">
                <TagsInput
                  label="Tags"
                  value={spec.tags ?? []}
                  onChange={(v) => update('tags', v)}
                  data={tagSuggestions}
                  placeholder="Add a tag…"
                  maw={420}
                />
              </Box>
            </Box>
          </Tabs.Panel>

          <Tabs.Panel value="docs" pt="md">
            <DocsEditor
              value={spec.body ?? ''}
              onChange={(v) => update('body', v)}
              emptyHint="Describe what this flow proves and what has to be true before it runs."
            />
          </Tabs.Panel>

          <Tabs.Panel value="source" pt="md">
            <SourceTab path={path} source={detail?.source ?? ''} />
          </Tabs.Panel>
        </Tabs>
      </EditorShell>

      <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={{ envPath: activeEnv ?? undefined }} />
    </>
  )
}

function StepCard({
  index, count, step, requestOptions, flowPath, variableContext, onOpenVariables, onPatch, onMove, onRemove,
}: {
  index: number
  count: number
  step: FlowStepSpec
  requestOptions: ReturnType<typeof requestSelectGroups>
  flowPath: string
  variableContext: VariableContext
  onOpenVariables: () => void
  onPatch: (patch: Partial<FlowStepSpec>) => void
  onMove: (by: number) => void
  onRemove: () => void
}) {
  // A brand-new step opens itself; established ones stay collapsed so a long flow reads as
  // a list of steps rather than a wall of forms.
  const [open, setOpen] = useState(step.request === '')

  const detailCount =
    (step.extract?.length ?? 0) + (step.assertions?.length ?? 0) + Object.keys(step.vars ?? {}).length

  return (
    <Paper withBorder p="sm" radius="sm" opacity={step.skip ? 0.6 : 1}>
      <Group gap="xs" wrap="nowrap" align="center">
        <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => setOpen(!open)}
          aria-label={open ? 'Collapse step' : 'Expand step'}>
          {open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
        </ActionIcon>

        <Badge size="sm" variant="light" color="gray" style={{ flexShrink: 0 }}>{index + 1}</Badge>

        <TextInput
          size="xs"
          placeholder="Step name (optional)"
          value={step.name ?? ''}
          onChange={(e) => onPatch({ name: e.currentTarget.value || null })}
          w={180}
          aria-label="Step name"
        />

        <Select
          size="xs"
          flex={1}
          searchable
          allowDeselect={false}
          placeholder="Pick the request this step sends…"
          data={requestOptions}
          value={matchRefOptionGrouped(flowPath, step.request, requestOptions)}
          onChange={(v) => v && onPatch({ request: v })}
          nothingFoundMessage="No requests match"
          comboboxProps={{ withinPortal: true }}
          aria-label="Request"
        />

        {detailCount > 0 && !open && (
          <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>{detailCount}</Text>
        )}

        <Group gap={2} wrap="nowrap" style={{ flexShrink: 0 }}>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === 0}
            onClick={() => onMove(-1)} aria-label="Move step up">
            <IconChevronUp size={14} />
          </ActionIcon>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === count - 1}
            onClick={() => onMove(1)} aria-label="Move step down">
            <IconChevronDown size={14} />
          </ActionIcon>
          <Tooltip label={step.skip ? 'Enable this step' : 'Skip this step'} withArrow>
            <ActionIcon
              variant={step.skip ? 'light' : 'subtle'}
              color={step.skip ? 'yellow' : 'gray'}
              size="sm"
              onClick={() => onPatch({ skip: !step.skip })}
              aria-label="Toggle skip"
            >
              <IconCircleMinus size={14} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon variant="subtle" color="red" size="sm" onClick={onRemove} aria-label="Remove step">
            <IconX size={14} />
          </ActionIcon>
        </Group>
      </Group>

      <Collapse expanded={open}>
        <Stack gap="md" mt="md" pl={34}>
          <Section
            title="Variables"
            hint="Sent in with the request. Values are templates — use {{name}} to read what an earlier step bound."
          >
            <KvTable
              rows={Object.entries(step.vars ?? {}).map(([key, value]) => ({ key, value }))}
              onChange={(rows) => onPatch({ vars: rowsToVars(rows) })}
              keyPlaceholder="name"
              valuePlaceholder="value or {{bound}}"
              emptyHint="No overrides — the request runs with the variables it already resolves."
              variableContext={variableContext}
              onOpenVariables={onOpenVariables}
            />
          </Section>

          <Section
            title="Extract"
            hint="Values bound out of this step's response for the steps below."
          >
            <ExtractTable
              extractions={step.extract ?? []}
              onChange={(next) => onPatch({ extract: next })}
              variableContext={variableContext}
              onOpenVariables={onOpenVariables}
            />
          </Section>

          <Section
            title="Assertions"
            hint="Checked on top of whatever the request file already asserts."
          >
            <AssertsPanel
              assertions={step.assertions ?? []}
              onChange={(next) => onPatch({ assertions: next })}
              variableContext={variableContext}
              onOpenVariables={onOpenVariables}
            />
          </Section>

          <Group gap="xs">
            <Button
              size="xs"
              variant={step.continueOnFailure ? 'light' : 'default'}
              color={step.continueOnFailure ? 'yellow' : 'gray'}
              onClick={() => onPatch({ continueOnFailure: !step.continueOnFailure })}
            >
              {step.continueOnFailure ? 'Continues on failure' : 'Stops the flow on failure'}
            </Button>
            {step.request && (
              <Text size="xs" c="dimmed" ff="var(--mono)">{resolveRef(flowPath, step.request)}</Text>
            )}
          </Group>
        </Stack>
      </Collapse>
    </Paper>
  )
}

function Section({ title, hint, children }: { title: string; hint: string; children: React.ReactNode }) {
  return (
    <Box>
      <Text size="xs" fw={600} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.4 }}>{title}</Text>
      <Text size="xs" c="dimmed" mb={6}>{hint}</Text>
      {children}
    </Box>
  )
}

function specFromFlow(detail: FlowDetail): FlowSpec {
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const [key, v] of Object.entries(detail.vars ?? {})) {
    vars[key] = v.default ?? ''
    if (v.secret) secrets.push(key)
  }
  return {
    path: detail.path,
    id: detail.id,
    name: detail.name,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
    tags: detail.tags.length > 0 ? detail.tags : undefined,
    body: detail.body || undefined,
    steps: detail.steps,
  }
}

function rowsToVars(rows: KvRow[]): Record<string, string> | null {
  const vars: Record<string, string> = {}
  for (const r of rows) {
    if (!r.key.trim()) continue
    vars[r.key] = r.value
  }
  return Object.keys(vars).length > 0 ? vars : null
}

function basename(path: string): string {
  return labelForPath(path)
}
