import {
  ActionIcon, Alert, Badge, Box, Button, Code, Group, Select, Stack, Switch, Tabs, Text, TextInput,
} from '@mantine/core'
import { IconCode, IconInfoCircle, IconPlug, IconPlus, IconVariable, IconX } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { EnvDetail, EnvSpec, ProviderSummary } from '../api/types'
import { EditorShell, TabCount } from './EditorShell'
import { KvTable } from './KvTable'
import { ProviderTypeIcon } from './providerMeta'
import { SourceTab } from './SourceTab'
import { useSpecEditor } from './useSpecEditor'
import { flatVarsToRows, rowsToFlatVars } from './varRows'

interface Props {
  path: string
}

/** Environment editor — typed local state. */
export function EnvEditor({ path }: Props) {
  const editor = useSpecEditor<EnvDetail, EnvSpec>({
    key: path,
    fetchDetail: (p) => api.envDetail(p),
    specFromDetail: (d) => specFromDetail(d, path),
    saveSpec: (s) => api.saveEnvSpec(s),
  })
  const { detail, spec, setSpec, update, dirty, saving, errorMessage, save, discard } = editor
  const [tab, setTab] = useState<string | null>('variables')

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

        <Tabs.Panel value="providers">
          <ProviderBindingTab spec={spec} setSpec={setSpec} />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab path={path} source={detail.source} />
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

function specFromDetail(d: EnvDetail, path: string): EnvSpec {
  // Flatten the VarSpec map: keep the default value in vars, and collect the names of
  // secret vars in a separate `secrets` array (the wire shape the emitter expects).
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const [k, spec] of Object.entries(d.vars ?? {})) {
    if (spec?.default != null) vars[k] = spec.default
    if (spec?.secret) secrets.push(k)
  }
  return {
    path,
    id: d.id,
    name: d.name,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
    defaultVariableProvider: d.defaultVariableProvider ?? undefined,
    providerAliases: d.providerAliases && Object.keys(d.providerAliases).length > 0
      ? d.providerAliases
      : undefined,
    strictVariables: d.strictVariables ? true : undefined,
  }
}

function basename(p: string): string {
  return p.split('/').pop() ?? p
}
