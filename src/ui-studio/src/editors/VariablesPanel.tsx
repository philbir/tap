import {
  ActionIcon, Badge, Box, Center, Code, Group, Loader, Modal, Paper, Progress, Stack, Table, Text, TextInput, Textarea, Tooltip, UnstyledButton,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import {
  IconArrowDown, IconCode, IconFlask, IconKey, IconRefresh, type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { CompileResult, Variable, VariableContext, VariableScope, VariableView } from '../api/types'
import { useTapStore } from '../store'
import { useVariableView } from '../workspace/useVariables'
import { ProviderTypeIcon } from './providerMeta'

const SCOPE_ORDER: VariableScope[] = ['provider', 'workspace', 'collection', 'stage', 'env', 'request']

// The first cascade stage is the configured-provider layer (system.json, azkv, file,
// env, …). Providers can be declared at system OR workspace scope, so the tier is
// labelled PROVIDERS and its detail view groups variables per provider with an origin
// badge — a flat "SYSTEM" pile hid which vault a value came from.
const SCOPE_LABEL: Record<VariableScope, string> = {
  provider: 'PROVIDERS',
  workspace: 'WORKSPACE',
  collection: 'COLLECTION',
  stage: 'STAGE',
  env: 'ENV',
  request: 'REQUEST',
}

const SCOPE_COLOR: Record<VariableScope, string> = {
  provider: 'blue',
  workspace: 'tap',
  collection: 'indigo',
  stage: 'cyan',
  env: 'grape',
  request: 'orange',
}

interface Props {
  opened: boolean
  onClose: () => void
  context: VariableContext | null
}

/**
 * Modal that shows the scope cascade (WORKSPACE → API → STAGE → ENV → REQUEST →
 * Result) for the active editor context, plus a "Test it" pane that runs a template through
 * the server's `VariableCompiler` and displays the rendered output + replacement annotations.
 *
 * Layout matches dreamr's VariableViewer but uses Tap's scope names.
 */
export function VariablesPanel({ opened, onClose, context }: Props) {
  // Fetch only while open (the view can hit slow providers like Azure Key Vault), but
  // RETAIN the last loaded view: the hook clears its state the instant `opened` flips
  // false, while Mantine keeps the content mounted through the exit fade — without the
  // retained copy every close flashes the "Open a request or env…" fallback (all counts
  // 0) over the populated panel, and a quick reopen starts empty. Keyed by context so a
  // different editor's panel never shows this one's cascade.
  const liveView = useVariableView(opened ? context : null)
  const contextKey = context ? JSON.stringify(context) : null
  const [retained, setRetained] = useState<{ key: string; view: VariableView } | null>(null)
  useEffect(() => {
    if (liveView && contextKey) setRetained({ key: contextKey, view: liveView })
  }, [liveView, contextKey])
  const view = liveView ?? (retained && retained.key === contextKey ? retained.view : null)
  const [selectedScope, setSelectedScope] = useState<VariableScope | 'result'>('result')
  const loading = opened && !!context && view === null

  // Reset to 'result' when the context changes so we always land somewhere meaningful.
  useEffect(() => { setSelectedScope('result') }, [context?.requestPath, context?.collectionPath])

  // Every mounted editor renders one of these panels, and a Mantine Modal portals an
  // (empty) root into <body> even while closed. Skip the portal entirely until the panel
  // is first opened — fewer stray portals, and the open panel's portal is appended last,
  // so it always paints above older ones.
  const [everOpened, setEverOpened] = useState(opened)
  useEffect(() => { if (opened) setEverOpened(true) }, [opened])
  if (!everOpened && !opened) return null

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size="xl"
      title={<Text fw={600}>Variables</Text>}
    >
      {/* Top-of-modal progress bar — indeterminate. Shows whenever the view is still being
          assembled (slow providers like Azure Key Vault dominate the first call; later
          calls land instantly thanks to the server-side provider cache). */}
      {loading && (
        <Stack gap={6} mb="md">
          <Progress value={100} striped animated size="xs" radius="xs" color="tap" />
          <Group gap={6} c="dimmed">
            <Loader size="xs" />
            <Text size="xs">Resolving variables — first call after a restart can take a few seconds.</Text>
          </Group>
        </Stack>
      )}
      {/* Active provider binding — which provider bare tokens hit first, and any env
          alias → provider bindings (kv → kv-prod). Only shown when a binding exists. */}
      {view && (view.defaultProvider || (view.aliases && Object.keys(view.aliases).length > 0)) && (
        <Group gap={6} mb="sm">
          {view.defaultProvider && (
            <Tooltip label="Bare {{name}} tokens resolve against this provider first." withArrow>
              <Badge size="sm" variant="light" color="blue">default · {view.defaultProvider}</Badge>
            </Tooltip>
          )}
          {view.aliases && Object.entries(view.aliases).map(([alias, target]) => (
            <Tooltip key={alias} label={`{{${alias}:name}} resolves against '${target}' in the active env.`} withArrow>
              <Badge size="sm" variant="light" color="grape">{alias} → {target}</Badge>
            </Tooltip>
          ))}
        </Group>
      )}
      <Box style={{ display: 'grid', gridTemplateColumns: '160px 1fr', gap: 24 }}>
        {/* Cascade renders immediately. While loading, counts are zero (we don't know
            them yet) but the structure is on screen so the user sees what they're
            waiting for. Once view arrives, badges fill in. */}
        <ScopeCascade view={view} selected={selectedScope} onSelect={setSelectedScope} />
        <Stack gap="md">
          {loading ? (
            <Center mih={140}><Loader size="sm" /></Center>
          ) : view ? (
            <SelectedScopeTable view={view} scope={selectedScope} />
          ) : (
            <Text size="sm" c="dimmed">Open a request or env to see variables.</Text>
          )}
          <TestItSection context={context} loading={loading} />
        </Stack>
      </Box>
    </Modal>
  )
}

// -----------------------------------------------------------------------------------------
// Scope cascade — left column of pill-buttons connected by ↓ arrows.
// -----------------------------------------------------------------------------------------

function ScopeCascade({
  view, selected, onSelect,
}: {
  view: VariableView | null
  selected: VariableScope | 'result'
  onSelect: (s: VariableScope | 'result') => void
}) {
  const counts = useMemo(() => {
    const out = new Map<VariableScope, number>()
    if (!view) return out
    for (const s of view.sets) out.set(s.scope, (out.get(s.scope) ?? 0) + s.count)
    return out
  }, [view])

  return (
    <Stack gap={4} align="center">
      {SCOPE_ORDER.map((s, i) => (
        <Box key={s} w="100%">
          <ScopePill
            label={SCOPE_LABEL[s]}
            count={counts.get(s) ?? 0}
            color={SCOPE_COLOR[s]}
            active={selected === s}
            onClick={() => onSelect(s)}
          />
          {i < SCOPE_ORDER.length - 1 && (
            <Box ta="center" my={2}>
              <IconArrowDown size={14} color="var(--mantine-color-orange-6)" />
            </Box>
          )}
        </Box>
      ))}
      <Box ta="center" my={2}>
        <IconArrowDown size={14} color="var(--mantine-color-orange-6)" />
      </Box>
      <ScopePill
        label="Result"
        count={view?.result.length ?? 0}
        color="green"
        active={selected === 'result'}
        onClick={() => onSelect('result')}
        outline
      />
    </Stack>
  )
}

function ScopePill({ label, count, color, active, onClick, outline }: {
  label: string
  count: number
  color: string
  active: boolean
  onClick: () => void
  outline?: boolean
}) {
  return (
    <UnstyledButton
      onClick={onClick}
      w="100%"
      px="md"
      py={8}
      style={{
        background: active
          ? `var(--mantine-color-${color}-light)`
          : outline ? 'transparent' : 'var(--mantine-color-default)',
        border: outline
          ? `1.5px solid var(--mantine-color-${color}-filled)`
          : '1px solid var(--mantine-color-default-border)',
        borderRadius: 999,
        color: active ? `var(--mantine-color-${color}-light-color)` : 'var(--mantine-color-text)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: 8,
        transition: 'background 0.12s, border-color 0.12s',
      }}
    >
      <Text size="sm" fw={600} tt="uppercase" lts={0.4}>{label}</Text>
      <Badge size="sm" color={color} variant={active ? 'filled' : 'light'} radius="xl">{count}</Badge>
    </UnstyledButton>
  )
}

// -----------------------------------------------------------------------------------------
// Right column — selected scope's variable table.
// -----------------------------------------------------------------------------------------

function SelectedScopeTable({ view, scope }: { view: VariableView; scope: VariableScope | 'result' }) {
  const variables = useMemo(() => {
    if (scope === 'result') return view.result
    return view.sets.filter((s) => s.scope === scope).flatMap((s) => s.variables)
  }, [view, scope])

  const header = scope === 'result' ? 'RESULT' : SCOPE_LABEL[scope]
  const generation = useTapStore((s) => s.generation)
  const [, force] = useState(0)

  return (
    <Box>
      <Group justify="space-between" mb="xs">
        <Text fw={700} size="sm" tt="uppercase" lts={0.5}>{header}</Text>
        <Tooltip label="Refresh">
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => force(generation)} aria-label="Refresh">
            <IconRefresh size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {scope === 'provider' ? (
        <ProviderSetGroups view={view} />
      ) : variables.length === 0 ? (
        <Text size="sm" c="dimmed" ta="center" py="md">No variables in this scope.</Text>
      ) : (
        <Paper withBorder radius="md" p="xs">
          <VariableRows variables={variables} showScope={scope === 'result'} />
        </Paper>
      )}
    </Box>
  )
}

/**
 * The PROVIDERS tier, one labelled group per configured provider: type icon, instance
 * name, type display name, declaration origin, plus the active env's binding — a
 * `default` badge on the provider bare tokens hit first and `alias → name` chips for
 * every alias pointing at it. This is where "which vault is this env using?" is answered.
 */
function ProviderSetGroups({ view }: { view: VariableView }) {
  const sets = view.sets.filter((s) => s.scope === 'provider')
  if (sets.length === 0) {
    return <Text size="sm" c="dimmed" ta="center" py="md">No variable providers configured.</Text>
  }
  const eq = (a: string | null | undefined, b: string | null | undefined) =>
    !!a && !!b && a.toLowerCase() === b.toLowerCase()

  return (
    <Stack gap="sm">
      {sets.map((s) => {
        const isDefault = eq(view.defaultProvider, s.providerName)
        const aliases = Object.entries(view.aliases ?? {}).filter(([, target]) => eq(target, s.providerName))
        return (
          <Paper withBorder radius="md" p="xs" key={s.sourcePath}>
            <Group gap={6} mb={4} wrap="wrap">
              <ProviderTypeIcon icon={s.icon} size={15} />
              <Text size="sm" fw={600} ff="var(--mono)">{s.label}</Text>
              {s.typeDisplayName && <Text size="xs" c="dimmed">{s.typeDisplayName}</Text>}
              {s.origin && (
                <Badge size="xs" variant="light" color={s.origin === 'system' ? 'blue' : 'tap'}>{s.origin}</Badge>
              )}
              {isDefault && (
                <Tooltip label="Bare {{name}} tokens resolve against this provider first in the active env." withArrow>
                  <Badge size="xs" variant="light" color="blue">default</Badge>
                </Tooltip>
              )}
              {aliases.map(([alias]) => (
                <Tooltip key={alias} label={`{{${alias}:name}} targets this provider in the active env.`} withArrow>
                  <Badge size="xs" variant="light" color="grape">{alias} → {s.providerName}</Badge>
                </Tooltip>
              ))}
              <Badge size="xs" variant="light" color="gray" ml="auto">{s.count}</Badge>
            </Group>
            {s.variables.length === 0 ? (
              <Text size="xs" c="dimmed" pl={4}>No variables visible right now.</Text>
            ) : (
              <VariableRows variables={s.variables} showScope={false} />
            )}
          </Paper>
        )
      })}
    </Stack>
  )
}

function VariableRows({ variables, showScope }: { variables: Variable[]; showScope: boolean }) {
  return (
    <Table verticalSpacing={6} horizontalSpacing="md" withRowBorders={false}>
      <Table.Tbody>
        {variables.map((v) => {
          const Icon: TablerIcon = v.isSensitive ? IconKey : IconCode
          return (
            <Table.Tr key={v.name + '-' + v.sourcePath}>
              <Table.Td style={{ width: '40%' }}>
                <Group gap={6} wrap="nowrap">
                  <Icon size={12} color={v.isSensitive ? 'var(--mantine-color-yellow-6)' : 'var(--mantine-color-tap-6)'} />
                  <Text ff="var(--mono)" size="sm" truncate>{v.name}</Text>
                </Group>
              </Table.Td>
              <Table.Td>
                {v.isSensitive ? (
                  <Group gap={6} wrap="nowrap">
                    <IconKey size={11} color="var(--mantine-color-yellow-6)" />
                    <Text ff="var(--mono)" size="sm" c="dimmed">***</Text>
                  </Group>
                ) : (
                  <Text ff="var(--mono)" size="sm">{v.value ?? <Text component="em" c="dimmed">(empty)</Text>}</Text>
                )}
              </Table.Td>
              {showScope && (
                <Table.Td style={{ width: 110 }}>
                  {/* Provider-scope rows name the actual provider — "kv-dev" says more
                      than a generic tier label ever could. */}
                  <Badge size="xs" color={SCOPE_COLOR[v.scope]} variant="light" style={{ textTransform: 'none' }}>
                    {v.scope === 'provider' && v.providerName ? v.providerName : SCOPE_LABEL[v.scope]}
                  </Badge>
                </Table.Td>
              )}
            </Table.Tr>
          )
        })}
      </Table.Tbody>
    </Table>
  )
}

// -----------------------------------------------------------------------------------------
// "Test it" pane — send a template to the server, show rendered + per-token replacements.
// -----------------------------------------------------------------------------------------

function TestItSection({ context, loading }: { context: VariableContext | null; loading: boolean }) {
  const [template, setTemplate] = useState('')
  const [debounced] = useDebouncedValue(template, 250)
  const [result, setResult] = useState<CompileResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [compiling, setCompiling] = useState(false)

  useEffect(() => {
    if (!context || !debounced) { setResult(null); setError(null); setCompiling(false); return }
    let cancelled = false
    setCompiling(true)
    api.compileTemplate(debounced, context).then((r) => {
      if (!cancelled) { setResult(r); setError(null); setCompiling(false) }
    }).catch((e: Error) => {
      if (!cancelled) { setError(e.message); setCompiling(false) }
    })
    return () => { cancelled = true }
  }, [debounced, context])

  return (
    <Paper withBorder p="md" radius="md">
      <Stack gap="xs">
        <Group gap={6} justify="space-between">
          <Group gap={6}>
            <IconFlask size={14} color="var(--mantine-color-tap-6)" />
            <Text fw={600} size="sm">Test it</Text>
          </Group>
          {(loading || compiling) && <Loader size="xs" />}
        </Group>
        <TextInput
          placeholder="Type a template — e.g. /api/echo/{{ClientId}}"
          value={template}
          onChange={(e) => setTemplate(e.currentTarget.value)}
          styles={{ input: { fontFamily: 'var(--mono)' } }}
        />
        <Text size="xs" fw={600} c="dimmed" tt="uppercase" lts={0.4} mt={4}>Compiled Output</Text>
        {error ? (
          <Code block c="red" fz="xs">{error}</Code>
        ) : result ? (
          <Box>
            <Textarea
              value={result.value}
              readOnly autosize minRows={2}
              styles={{ input: { fontFamily: 'var(--mono)', fontSize: 12 } }}
            />
            {result.replacements.length > 0 && (
              <Group gap={4} mt="xs">
                {result.replacements.map((r, i) => (
                  <Badge
                    key={i}
                    size="xs"
                    color={r.resolved ? (r.isSensitive ? 'yellow' : 'tap') : 'red'}
                    variant="light"
                    title={r.scope ?? 'unknown'}
                  >
                    {r.name}
                  </Badge>
                ))}
              </Group>
            )}
          </Box>
        ) : (
          <Code block c="dimmed" fz="xs">Compiled output will appear here.</Code>
        )}
      </Stack>
    </Paper>
  )
}
