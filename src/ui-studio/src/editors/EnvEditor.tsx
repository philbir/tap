import {
  ActionIcon, Alert, Badge, Box, Button, Code, Group, Paper, Select, Stack, Switch, Tabs, Text, TextInput, Tooltip,
} from '@mantine/core'
import { IconCode, IconFolders, IconInfoCircle, IconPlug, IconPlus, IconVariable, IconX } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { AuthSummary, CollectionSummary, EnvCollection, EnvDetail, EnvSpec, ProviderSummary } from '../api/types'
import { useTapStore } from '../store'
import { authSelectGroups } from './authOptions'
import { EditorShell, TabCount } from './EditorShell'
import { KvTable } from './KvTable'
import { ProviderTypeIcon } from './providerMeta'
import { SourceTab } from './SourceTab'
import { useTabView } from './useTabView'
import { useSpecEditor } from './useSpecEditor'
import { VariableInput } from './VariableInput'
import { envSpecFromDetail } from './envSpec'
import { flatVarsToRows, rowsToFlatVars } from './varRows'

interface Props {
  path: string
}

/**
 * Environment editor.
 *
 * <p>An environment is either global — selectable anywhere — or scoped to a list of
 * collections, which is what collection stages became. The base URL and default auth
 * overrides on the Scope tab only mean something narrow for a scoped env; on a global one
 * they move every collection at once, so the tab says so.</p>
 */
export function EnvEditor({ path }: Props) {
  const editor = useSpecEditor<EnvDetail, EnvSpec>({
    key: path,
    fetchDetail: (p) => api.envDetail(p),
    specFromDetail: (d) => envSpecFromDetail(d, path),
    saveSpec: (s) => api.saveEnvSpec(s),
  })
  const { detail, spec, setSpec, update, dirty, saving, errorMessage, save, discard } = editor
  const [tab, setTab] = useTabView<string | null>(path, 'tab', 'variables')
  const collections = useTapStore((s) => s.collections)
  const auths = useTapStore((s) => s.auths)

  if (!detail || !spec) {
    return (
      <EditorShell
        title={detail?.name ?? basename(path)}
        kindLabel="Environment"
        dirty={false} saving={saving} errorMessage={errorMessage}
        onSave={save}
      >
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  const rows = flatVarsToRows(spec.vars, spec.secrets)
  const aliasCount = Object.keys(spec.providerAliases ?? {}).length
  const bindingCount = aliasCount + (spec.defaultVariableProvider ? 1 : 0)
  const scopeCount = spec.collections?.length ?? 0

  return (
    <EditorShell
      title={spec.name || basename(path)}
      kindLabel="Environment"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={discard}
      onTitleChange={(n) => update('name', n)}
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="md">
          <Tabs.Tab value="variables" leftSection={<IconVariable size={14} />}>
            Variables <TabCount count={rows.length} />
          </Tabs.Tab>
          <Tabs.Tab value="scope" leftSection={<IconFolders size={14} />}>
            Scope <TabCount count={scopeCount} />
          </Tabs.Tab>
          <Tabs.Tab value="providers" leftSection={<IconPlug size={14} />}>
            Providers <TabCount count={bindingCount} />
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="variables">
          <Stack gap="md" maw={880}>
            <TextInput
              label="Name"
              value={spec.name}
              onChange={(e) => update('name', e.currentTarget.value)}
            />
            <Box>
              <Text size="sm" mb={4}>Variables</Text>
              <Text size="xs" c="dimmed" mb="xs">
                Resolve at execute time. Values can be literals or secret references like
                {' '}<Code>${'{{azkv:vault/key}}'}</Code>.
              </Text>
              <KvTable
                rows={rows}
                onChange={(next) => {
                  const { vars, secrets } = rowsToFlatVars(next)
                  setSpec((cur) => cur ? { ...cur, vars, secrets } : cur)
                }}
                keyPlaceholder="var.name"
                valuePlaceholder="value"
                allowSecretToggle
                variableContext={{ envPath: path }}
                emptyHint="No variables defined for this environment yet."
              />
            </Box>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="scope">
          <ScopeTab spec={spec} update={update} envPath={path} collections={collections} auths={auths} />
        </Tabs.Panel>

        <Tabs.Panel value="providers">
          <ProviderBindingTab spec={spec} setSpec={setSpec} />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab
            path={path}
            source={detail.source}
            deletable={{ kind: 'env', path, name: spec.name || basename(path) }}
          />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
  )
}

/**
 * Per-env variable-provider binding: which provider bare `{{name}}` tokens hit first,
 * optional strict mode (no fall-through past that provider), and alias → provider
 * bindings so requests can use a stable prefix like `{{kv:secret}}` whose target vault
 * follows the selected environment. Providers themselves are declared in Settings
 * (system scope) or workspace.tap (workspace scope) — this tab only points at them.
 */
interface AliasRow { id: number; alias: string; target: string }

let aliasRowId = 0
function rowsFromSpec(spec: EnvSpec): AliasRow[] {
  return Object.entries(spec.providerAliases ?? {}).map(([alias, target]) => ({ id: ++aliasRowId, alias, target }))
}

/** Rows → the map that actually gets saved: blank alias names / targets are edit-in-progress
 *  states and stay out of the spec until filled in. */
function cleanAliasMap(rows: AliasRow[]): Record<string, string> {
  const map: Record<string, string> = {}
  for (const r of rows) {
    if (r.alias.trim() && r.target) map[r.alias.trim()] = r.target
  }
  return map
}

function ProviderBindingTab({
  spec, setSpec,
}: {
  spec: EnvSpec
  setSpec: (fn: (cur: EnvSpec | null) => EnvSpec | null) => void
}) {
  const [providers, setProviders] = useState<ProviderSummary[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [aliasRows, setAliasRows] = useState<AliasRow[]>(() => rowsFromSpec(spec))

  useEffect(() => {
    let cancelled = false
    api.listVariableProviders()
      .then((p) => { if (!cancelled) setProviders(p) })
      .catch((e: Error) => { if (!cancelled) setLoadError(e.message) })
    return () => { cancelled = true }
  }, [])

  // Resync rows when the spec's alias map changes underneath us (discard, reload). An
  // in-progress row (blank alias) cleans to the same map as the spec, so it survives.
  useEffect(() => {
    const specMap = spec.providerAliases ?? {}
    if (JSON.stringify(specMap) !== JSON.stringify(cleanAliasMap(aliasRows))) {
      setAliasRows(rowsFromSpec(spec))
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [spec.providerAliases])

  function updateAliasRows(next: AliasRow[]) {
    setAliasRows(next)
    const map = cleanAliasMap(next)
    setSpec((cur) => cur
      ? { ...cur, providerAliases: Object.keys(map).length > 0 ? map : undefined }
      : cur)
  }

  const providerOptions = providers.map((p) => ({ value: p.name, label: p.name }))
  const renderProviderOption = ({ option }: { option: { value: string; label: string } }) => {
    const p = providers.find((x) => x.name === option.value)
    return (
      <Group gap="sm" wrap="nowrap">
        <ProviderTypeIcon icon={p?.icon} size={16} />
        <Stack gap={0}>
          <Text size="sm" ff="var(--mono)">{option.value}</Text>
          {p?.typeDisplayName && <Text size="xs" c="dimmed">{p.typeDisplayName}</Text>}
        </Stack>
      </Group>
    )
  }
  const selectedProvider = (name: string | null | undefined) =>
    name ? providers.find((p) => p.name === name) ?? null : null

  return (
    <Stack gap="md" maw={880}>
      <Alert color="grape" variant="light" icon={<IconInfoCircle size={14} />}>
        <Text size="xs">
          Bind this environment to variable providers: bare <Code fz="xs">{'{{name}}'}</Code> tokens
          hit the default provider first, and aliases let requests use a stable prefix
          (<Code fz="xs">{'{{kv:secret}}'}</Code>) whose target vault changes with the environment.
          Providers are declared in Settings or the workspace manifest.
        </Text>
      </Alert>

      {loadError && (
        <Alert color="red" variant="light"><Text size="xs">{loadError}</Text></Alert>
      )}

      <Select
        label="Default variable provider"
        description="Bare {{name}} tokens resolve here first while this env is active. Overrides the workspace / system default."
        placeholder="(inherit workspace / system default)"
        data={providerOptions}
        value={spec.defaultVariableProvider ?? null}
        onChange={(v) => setSpec((cur) => cur
          ? { ...cur, defaultVariableProvider: v ?? undefined, strictVariables: v ? cur.strictVariables : undefined }
          : cur)}
        renderOption={renderProviderOption}
        leftSection={<ProviderTypeIcon icon={selectedProvider(spec.defaultVariableProvider)?.icon} size={14} />}
        clearable
        w={420}
      />

      <Switch
        label="Strict resolution"
        description="When the default provider doesn't have a bare {{name}}, fail instead of falling through to other providers. Recommended with one vault per environment — prevents silently reading another environment's secret."
        checked={spec.strictVariables === true}
        disabled={!spec.defaultVariableProvider}
        onChange={(e) => {
          const on = e.currentTarget.checked
          setSpec((cur) => cur ? { ...cur, strictVariables: on ? true : undefined } : cur)
        }}
        maw={620}
      />

      <Box>
        <Group gap="xs" mb={4}>
          <Text size="sm">Provider aliases</Text>
          {aliasRows.length > 0 && <Badge size="xs" variant="light" color="grape">{aliasRows.length}</Badge>}
        </Group>
        <Text size="xs" c="dimmed" mb="xs">
          Requests reference the alias (<Code fz="xs">{'{{kv:clientSecret}}'}</Code>); each environment
          points the alias at its own provider — <Code fz="xs">kv → kv-dev</Code> here,{' '}
          <Code fz="xs">kv → kv-prod</Code> in prod.
        </Text>

        <Stack gap={6}>
          {aliasRows.map((row) => (
            <Group key={row.id} gap="xs" wrap="nowrap">
              <TextInput
                size="xs"
                value={row.alias}
                placeholder="alias (e.g. kv)"
                onChange={(e) => updateAliasRows(
                  aliasRows.map((r) => r.id === row.id ? { ...r, alias: e.currentTarget.value } : r))}
                styles={{ input: { fontFamily: 'var(--mono)' } }}
                w={200}
              />
              <Text size="sm" c="dimmed">→</Text>
              <Select
                size="xs"
                data={providerOptions}
                value={row.target || null}
                placeholder="provider"
                onChange={(v) => updateAliasRows(
                  aliasRows.map((r) => r.id === row.id ? { ...r, target: v ?? '' } : r))}
                renderOption={renderProviderOption}
                leftSection={<ProviderTypeIcon icon={selectedProvider(row.target)?.icon} size={13} />}
                w={280}
                allowDeselect={false}
              />
              <ActionIcon
                variant="subtle" color="red" size="sm"
                onClick={() => updateAliasRows(aliasRows.filter((r) => r.id !== row.id))}
                aria-label={`Remove alias ${row.alias || '(new)'}`}
              >
                <IconX size={14} />
              </ActionIcon>
            </Group>
          ))}
          {aliasRows.length === 0 && (
            <Text size="xs" c="dimmed">No aliases bound for this environment.</Text>
          )}
        </Stack>

        <Group mt="xs">
          <Button
            size="xs"
            variant="default"
            leftSection={<IconPlus size={12} />}
            onClick={() => updateAliasRows([...aliasRows, { id: ++aliasRowId, alias: '', target: providers[0]?.name ?? '' }])}
          >
            Add alias
          </Button>
        </Group>
      </Box>
    </Stack>
  )
}

/**
 * Scope tab: which collections this environment is assigned to, and what it changes about
 * each one while it is active.
 *
 * <p>Assigning no collection keeps the environment global — offered everywhere, overriding
 * nothing. That is the right shape for a `dev`/`prod` pair every collection shares. Assigning
 * one is the case a stage used to cover, where `uat` only means something to one API.</p>
 *
 * <p>The base URL and default auth sit on each assignment rather than on the environment,
 * because they are only ever true of one collection: the same `uat` points `orders` and
 * `billing` at different hosts.</p>
 */
function ScopeTab({ spec, update, envPath, collections, auths }: {
  spec: EnvSpec
  update: <K extends keyof EnvSpec>(key: K, value: EnvSpec[K]) => void
  envPath: string
  collections: CollectionSummary[]
  auths: AuthSummary[]
}) {
  const assigned = spec.collections ?? []
  const nameOf = (slug: string) => collections.find((c) => c.slug === slug)?.name ?? slug

  function setAssignments(next: EnvCollection[]) {
    update('collections', next.length > 0 ? next : undefined)
  }

  function patch(slug: string, p: Partial<EnvCollection>) {
    setAssignments(assigned.map((b) => (b.collection === slug ? { ...b, ...p } : b)))
  }

  const unassigned = collections
    .filter((c) => c.exists && !assigned.some((b) => b.collection === c.slug))
    .map((c) => ({ value: c.slug, label: c.name }))

  return (
    <Stack gap="md" maw={760}>
      {assigned.length === 0 ? (
        <Alert color="blue" icon={<IconInfoCircle size={16} />} variant="light">
          <Text size="sm">
            This environment is <b>global</b> — offered in every collection&rsquo;s picker, and
            contributing its variables wherever it is selected.
          </Text>
          <Text size="sm" mt={4}>
            Assign it to a collection below to also override that collection&rsquo;s base URL or
            default auth. Those overrides belong to the assignment, so the same environment can
            point each collection somewhere different.
          </Text>
        </Alert>
      ) : (
        <Stack gap="sm">
          {assigned.map((binding) => (
            <Paper key={binding.collection} withBorder p="md" radius="sm">
              <Group justify="space-between" wrap="nowrap" mb="sm">
                <Group gap={6} wrap="nowrap">
                  <IconFolders size={14} opacity={0.6} />
                  <Text size="sm" fw={600}>{nameOf(binding.collection)}</Text>
                  <Code fz="xs">{binding.collection}</Code>
                </Group>
                <Tooltip label="Unassign — this environment stops being offered here" withArrow>
                  <ActionIcon
                    variant="subtle" color="red" size="sm"
                    aria-label={`Unassign ${binding.collection}`}
                    onClick={() => setAssignments(assigned.filter((b) => b.collection !== binding.collection))}
                  >
                    <IconX size={14} />
                  </ActionIcon>
                </Tooltip>
              </Group>

              <Stack gap="sm">
                <Box>
                  <Text size="sm" fw={500} mb={4}>Base URL</Text>
                  <VariableInput
                    value={binding.baseUrl ?? ''}
                    onChange={(v) => patch(binding.collection, { baseUrl: v && v.length > 0 ? v : null })}
                    placeholder={`Inherit ${nameOf(binding.collection)}'s base URL`}
                    context={{ envPath }}
                  />
                </Box>

                <Select
                  label="Default auth"
                  // Grouped against the *assigned* collection so its own profiles read as
                  // "This collection", while the ref itself is written relative to this env
                  // file — which is where it lives on disk.
                  data={authSelectGroups({
                    auths, collections, fromPath: envPath, forCollection: binding.collection,
                  })}
                  placeholder={`Inherit ${nameOf(binding.collection)}'s default auth`}
                  value={binding.defaultAuth ?? ''}
                  onChange={(v) => patch(binding.collection, { defaultAuth: v && v.length > 0 ? v : null })}
                  searchable
                  clearable
                />
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}

      <Select
        label="Assign a collection"
        description="The environment appears in that collection's picker, and may override its base URL and default auth."
        placeholder={unassigned.length > 0 ? 'Pick a collection…' : 'Every collection is already assigned'}
        data={unassigned}
        value={null}
        disabled={unassigned.length === 0}
        onChange={(slug) => {
          if (slug) setAssignments([...assigned, { collection: slug, baseUrl: null, defaultAuth: null }])
        }}
        searchable
        leftSection={<IconPlus size={14} />}
      />
    </Stack>
  )
}


function basename(p: string): string {
  return p.split('/').pop() ?? p
}
