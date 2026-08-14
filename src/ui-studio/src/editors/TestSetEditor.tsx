import {
  ActionIcon, Badge, Box, Button, Collapse, Group, Paper, SegmentedControl, Select, Stack, Tabs,
  TagsInput, Text, TextInput, Tooltip,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconArrowsSplit2, IconChecklist, IconChevronDown, IconChevronRight, IconChevronUp,
  IconCircleMinus, IconCode, IconFileText, IconPlayerPlayFilled, IconPlus, IconSend, IconVariable, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type {
  RequestSummary, TestEntrySpec, TestSetDetail, TestSetSpec, VariableContext,
} from '../api/types'
import { useActiveEnv, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { AdaptiveTabsList } from './AdaptiveTabsList'
import { AssertsPanel } from './AssertsPanel'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount } from './EditorShell'
import { KvTable, type KvRow } from './KvTable'
import { SourceTab } from './SourceTab'
import { TestRunPanel } from './TestRunPanel'
import {
  flowSelectItems, matchRefOption, matchRefOptionGrouped, requestSelectGroups, resolveRef,
} from './testingOptions'
import { useSpecEditor } from './useSpecEditor'
import { useTestRun } from './useTestRun'
import { flatVarsToRows, rowsToFlatVars } from './varRows'
import { VariablesPanel } from './VariablesPanel'

interface Props { path: string }

/**
 * Editor for a `*.test.md` — a named group of checks, each running one request or one flow.
 *
 * Where the flow editor is a composer, this is a runner: the list is what to check, the Run
 * button is the point, and each row can be re-run on its own so a single failure doesn't
 * mean re-firing everything else at someone's API.
 */
export function TestSetEditor({ path }: Props) {
  const collections = useTapStore((s) => s.collections)
  const flows = useTapStore((s) => s.flows)
  const generation = useTapStore((s) => s.generation)
  const openTab = useTapStore((s) => s.openTab)
  const activeEnv = useActiveEnv()
  const tagSuggestions = useTagDictionary()

  const [tab, setTab] = useState<string | null>('tests')
  const [requests, setRequests] = useState<RequestSummary[]>([])
  const [varsOpened, varsCtl] = useDisclosure(false)

  const editor = useSpecEditor<TestSetDetail, TestSetSpec>({
    key: path,
    fetchDetail: api.testSet,
    specFromDetail: specFromTestSet,
    saveSpec: api.saveTestSetSpec,
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
  const flowOptions = useMemo(() => flowSelectItems(flows, path), [flows, path])
  const variableContext = useMemo<VariableContext>(() => ({ envPath: activeEnv ?? undefined }), [activeEnv])

  if (!spec) {
    return (
      <EditorShell title={basename(path)} kindLabel="Test set" dirty={false} saving={saving}
        errorMessage={errorMessage} onSave={() => {}}>
        <Text c="dimmed" size="sm">Loading…</Text>
      </EditorShell>
    )
  }

  const tests = spec.tests
  const setTests = (next: TestEntrySpec[]) => update('tests', next)
  const patchTest = (index: number, patch: Partial<TestEntrySpec>) =>
    setTests(tests.map((t, i) => (i === index ? { ...t, ...patch } : t)))

  function move(index: number, by: number) {
    const to = index + by
    if (to < 0 || to >= tests.length) return
    const next = tests.slice()
    const [moved] = next.splice(index, 1)
    next.splice(to, 0, moved)
    setTests(next)
  }

  return (
    <>
      <EditorShell
        title={spec.name || basename(path)}
        kindLabel="Test set"
        dirty={dirty}
        saving={saving}
        errorMessage={errorMessage}
        onSave={editor.save}
        onDiscard={editor.discard}
        onTitleChange={(n) => update('name', n)}
        toolbarExtras={
          <Tooltip label={dirty ? 'Save first — a run always uses what is on disk' : 'Run every test'} withArrow>
            <Button
              size="xs"
              leftSection={<IconPlayerPlayFilled size={13} />}
              onClick={() => run.run(activeEnv, null)}
              loading={run.state.running}
              disabled={dirty || tests.length === 0}
            >
              Run
            </Button>
          </Tooltip>
        }
        bottomPane={run.active ? (
          <TestRunPanel
            state={run.state}
            kind="test"
            onStop={run.stop}
            onClose={run.clear}
            onRunOne={(index) => run.run(activeEnv, null, index)}
          />
        ) : undefined}
      >
        <Tabs value={tab} onChange={setTab} keepMounted={false}>
          <AdaptiveTabsList
            tabs={[
              {
                value: 'tests',
                label: 'Tests',
                icon: <IconChecklist size={14} />,
                adornment: <TabCount count={tests.length} />,
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

          <Tabs.Panel value="tests" pt="md">
            <Stack gap="sm" maw={1100}>
              <Group gap="xs" align="center">
                <Text size="xs" c="dimmed">When a test fails</Text>
                <SegmentedControl
                  size="xs"
                  value={spec.onFailure ?? 'continue'}
                  onChange={(v) => update('onFailure', v as TestSetSpec['onFailure'])}
                  data={[
                    { value: 'continue', label: 'Keep going' },
                    { value: 'stop', label: 'Stop the set' },
                  ]}
                />
                <Text size="xs" c="dimmed">
                  {(spec.onFailure ?? 'continue') === 'continue'
                    ? 'Independent checks — one broken endpoint won’t hide the others.'
                    : 'For a set whose tests build on each other.'}
                </Text>
              </Group>

              {tests.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="lg">
                  No tests yet. Add one that runs a request, or one that runs a whole flow.
                </Text>
              )}

              {tests.map((test, index) => (
                <TestCard
                  key={index}
                  index={index}
                  count={tests.length}
                  test={test}
                  requestOptions={requestOptions}
                  flowOptions={flowOptions}
                  setPath={path}
                  variableContext={variableContext}
                  onOpenVariables={varsCtl.open}
                  onPatch={(patch) => patchTest(index, patch)}
                  onMove={(by) => move(index, by)}
                  onRemove={() => setTests(tests.filter((_, i) => i !== index))}
                  onOpenTarget={(targetPath, kind) =>
                    openTab({ path: targetPath, kind, label: basename(targetPath) })}
                />
              ))}

              <Group gap="xs">
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconPlus size={14} />}
                  onClick={() => setTests([...tests, { request: '', assertions: [] }])}
                >
                  Add request test
                </Button>
                <Button
                  size="xs"
                  variant="light"
                  color="violet"
                  leftSection={<IconPlus size={14} />}
                  onClick={() => setTests([...tests, { flow: '', assertions: [] }])}
                  disabled={flowOptions.length === 0}
                  title={flowOptions.length === 0 ? 'No flows in this workspace yet' : undefined}
                >
                  Add flow test
                </Button>
              </Group>
            </Stack>
          </Tabs.Panel>

          <Tabs.Panel value="vars" pt="md">
            <Box maw={760}>
              <Text size="xs" c="dimmed" mb="xs">
                Set-scoped variables. These override every file scope for the whole run — the
                last word on what the requests below see, short of what a flow step binds.
              </Text>
              <KvTable
                rows={flatVarsToRows(spec.vars, spec.secrets)}
                onChange={(rows) => setSpec((cur) => cur ? { ...cur, ...rowsToFlatVars(rows) } : cur)}
                keyPlaceholder="name"
                valuePlaceholder="value"
                allowSecretToggle
                emptyHint="No set variables. Add one to pin a value for every test in this set."
                variableContext={variableContext}
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
              emptyHint="Describe what this set covers and when it should be run."
            />
          </Tabs.Panel>

          <Tabs.Panel value="source" pt="md">
            <SourceTab path={path} source={detail?.source ?? ''} />
          </Tabs.Panel>
        </Tabs>
      </EditorShell>

      <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    </>
  )
}

function TestCard({
  index, count, test, requestOptions, flowOptions, setPath, variableContext, onOpenVariables,
  onPatch, onMove, onRemove, onOpenTarget,
}: {
  index: number
  count: number
  test: TestEntrySpec
  requestOptions: ReturnType<typeof requestSelectGroups>
  flowOptions: ReturnType<typeof flowSelectItems>
  setPath: string
  variableContext: VariableContext
  onOpenVariables: () => void
  onPatch: (patch: Partial<TestEntrySpec>) => void
  onMove: (by: number) => void
  onRemove: () => void
  onOpenTarget: (path: string, kind: 'request' | 'flow') => void
}) {
  const isFlow = test.flow !== undefined && test.flow !== null
  const ref = (isFlow ? test.flow : test.request) ?? ''
  const [open, setOpen] = useState(ref === '')

  const detailCount = (test.assertions?.length ?? 0) + Object.keys(test.vars ?? {}).length

  return (
    <Paper withBorder p="sm" radius="sm" opacity={test.skip ? 0.6 : 1}>
      <Group gap="xs" wrap="nowrap" align="center">
        <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => setOpen(!open)}
          aria-label={open ? 'Collapse test' : 'Expand test'}>
          {open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
        </ActionIcon>

        <Tooltip label={isFlow ? 'Runs a flow' : 'Runs one request'} withArrow>
          <Box style={{ display: 'flex', flexShrink: 0 }}>
            {isFlow
              ? <IconArrowsSplit2 size={15} color="var(--mantine-color-violet-6)" />
              : <IconSend size={15} color="var(--mantine-color-tap-6)" />}
          </Box>
        </Tooltip>

        <TextInput
          size="xs"
          placeholder="Test name (optional)"
          value={test.name ?? ''}
          onChange={(e) => onPatch({ name: e.currentTarget.value || null })}
          w={200}
          aria-label="Test name"
        />

        <Select
          size="xs"
          flex={1}
          searchable
          allowDeselect={false}
          placeholder={isFlow ? 'Pick a flow…' : 'Pick a request…'}
          data={isFlow ? flowOptions : requestOptions}
          value={isFlow
            ? matchRefOption(setPath, ref, flowOptions)
            : matchRefOptionGrouped(setPath, ref, requestOptions)}
          onChange={(v) => v && onPatch(isFlow ? { flow: v } : { request: v })}
          nothingFoundMessage={isFlow ? 'No flows match' : 'No requests match'}
          comboboxProps={{ withinPortal: true }}
          aria-label={isFlow ? 'Flow' : 'Request'}
        />

        {detailCount > 0 && !open && (
          <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>{detailCount}</Text>
        )}

        <Group gap={2} wrap="nowrap" style={{ flexShrink: 0 }}>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === 0}
            onClick={() => onMove(-1)} aria-label="Move test up">
            <IconChevronUp size={14} />
          </ActionIcon>
          <ActionIcon variant="subtle" color="gray" size="sm" disabled={index === count - 1}
            onClick={() => onMove(1)} aria-label="Move test down">
            <IconChevronDown size={14} />
          </ActionIcon>
          <Tooltip label={test.skip ? 'Enable this test' : 'Skip this test'} withArrow>
            <ActionIcon
              variant={test.skip ? 'light' : 'subtle'}
              color={test.skip ? 'yellow' : 'gray'}
              size="sm"
              onClick={() => onPatch({ skip: !test.skip })}
              aria-label="Toggle skip"
            >
              <IconCircleMinus size={14} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon variant="subtle" color="red" size="sm" onClick={onRemove} aria-label="Remove test">
            <IconX size={14} />
          </ActionIcon>
        </Group>
      </Group>

      <Collapse expanded={open}>
        <Stack gap="md" mt="md" pl={34}>
          <Box>
            <Text size="xs" fw={600} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.4 }}>Variables</Text>
            <Text size="xs" c="dimmed" mb={6}>
              Applied to this test only. Values are templates, expanded against the set's variables first.
            </Text>
            <KvTable
              rows={Object.entries(test.vars ?? {}).map(([key, value]) => ({ key, value }))}
              onChange={(rows) => onPatch({ vars: rowsToVars(rows) })}
              keyPlaceholder="name"
              valuePlaceholder="value"
              emptyHint="No overrides — this test runs with what the set and the files already resolve."
              variableContext={variableContext}
              onOpenVariables={onOpenVariables}
            />
          </Box>

          <Box>
            <Text size="xs" fw={600} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.4 }}>Assertions</Text>
            <Text size="xs" c="dimmed" mb={6}>
              {isFlow
                ? "Checked against the flow's last response — the one a caller of the flow sees."
                : 'Checked on top of whatever the request file already asserts.'}
            </Text>
            <AssertsPanel
              assertions={test.assertions ?? []}
              onChange={(next) => onPatch({ assertions: next })}
              variableContext={variableContext}
              onOpenVariables={onOpenVariables}
            />
          </Box>

          {ref && (
            <Group gap="xs">
              <Badge size="xs" variant="light" color={isFlow ? 'violet' : 'tap'}>
                {isFlow ? 'flow' : 'request'}
              </Badge>
              <Text
                size="xs"
                c="dimmed"
                ff="var(--mono)"
                style={{ cursor: 'pointer', textDecoration: 'underline dotted' }}
                onClick={() => onOpenTarget(resolveRef(setPath, ref), isFlow ? 'flow' : 'request')}
                title="Open it"
              >
                {resolveRef(setPath, ref)}
              </Text>
            </Group>
          )}
        </Stack>
      </Collapse>
    </Paper>
  )
}

function specFromTestSet(detail: TestSetDetail): TestSetSpec {
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
    onFailure: detail.onFailure,
    tags: detail.tags.length > 0 ? detail.tags : undefined,
    body: detail.body || undefined,
    tests: detail.tests,
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
  return path.split('/').pop()?.replace(/\.(test|flow|req)\.md$/i, '') ?? path
}
