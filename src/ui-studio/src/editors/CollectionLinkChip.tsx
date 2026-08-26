import { Group, Menu, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core'
import { IconCheck, IconChevronDown, IconFolders } from '@tabler/icons-react'
import { useMemo } from 'react'
import { envBindingFor } from '../api/types'
import type { CollectionSummary, EnvSummary, VariableContext } from '../api/types'
import { useEnvsFor } from '../store'
import { useVariableView, variableMap } from '../workspace/useVariables'

/**
 * The baseUrl template a request under `summary` resolves against, or `''` when the
 * collection has none configured and no environment supplies one.
 *
 * <p>An environment can override the collection's baseUrl outright — on the *assignment* to
 * this collection, since the same env points elsewhere in another one. Summaries carry the
 * assignments, so this moves with the picker without a second round trip.</p>
 *
 * <p>Exported because the chip is not the only thing that turns on the answer: a caller with
 * nothing to show for a base URL hides the chip rather than rendering "(no baseUrl)".</p>
 */
export function effectiveBaseUrl(summary: CollectionSummary, env: EnvSummary | null): string {
  return ((env && envBindingFor(env, summary.slug)?.baseUrl) || summary.baseUrl) ?? ''
}

/**
 * The collection a request inherits from, rendered as a compact two-part chip: the resolved
 * base URL (click to open the collection) and an environment menu.
 *
 * Shared by the request editor and the `.http` editor. Both need it for the same reason — a
 * relative request line only means something once you know which base URL it resolves
 * against, and which environment is in effect is a choice made per collection rather than a
 * property of the file.
 *
 * <p>The menu lists every environment in scope here: the workspace's global ones plus the
 * ones assigned to this collection. Picking one is remembered for the collection, so every
 * request under it follows — which is what the old per-collection stage picker did, minus
 * the second concept. It is the same control the header shows.</p>
 */
export function CollectionLinkChip({ summary, env, onEnvChange, variableContext, onOpen }: {
  summary: CollectionSummary
  /** Path of the environment currently in effect for this collection, or null. */
  env: string | null
  /** Null means "fall back to the workspace default". */
  onEnvChange: (next: string | null) => void
  variableContext: VariableContext
  onOpen: () => void
}) {
  const view = useVariableView(variableContext)
  const vars = useMemo(() => variableMap(view), [view])
  const envs = useEnvsFor(summary.slug)
  const selected = envs.find((e) => e.path === env) ?? null

  const baseTemplate = effectiveBaseUrl(summary, selected)
  const display = useMemo(() => resolveTokens(baseTemplate, vars), [baseTemplate, vars])
  const hasUnresolved = /\{\{[^}]+\}\}/.test(display)
  // The chip is width-capped so a long base URL can't crowd out the path input, so the
  // tooltip carries the full URL (and the raw template when tokens were substituted).
  const tooltip = (
    <Stack gap={2}>
      <Text size="xs">Open collection · {summary.name}</Text>
      <Text size="xs" ff="var(--mono)">{display || '(no baseUrl)'}</Text>
      {baseTemplate !== display && (
        <Text size="xs" c="dimmed" ff="var(--mono)">template: {baseTemplate}</Text>
      )}
      {selected && envBindingFor(selected, summary.slug)?.baseUrl && (
        <Text size="xs" c="dimmed">baseUrl from environment “{selected.name}”</Text>
      )}
    </Stack>
  )

  const globals = envs.filter((e) => e.collections.length === 0)
  const scoped = envs.filter((e) => e.collections.length > 0)

  return (
    <Group
      gap={0}
      wrap="nowrap"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        overflow: 'hidden',
      }}
    >
      <Tooltip label={tooltip} withArrow openDelay={400}>
        <UnstyledButton
          onClick={onOpen}
          px="sm"
          style={{
            display: 'flex', alignItems: 'center', gap: 6,
            height: 34, color: 'var(--mantine-color-text)',
          }}
        >
          <IconFolders size={13} opacity={0.6} style={{ flexShrink: 0 }} />
          <Text
            component="span"
            size="sm"
            ff="var(--mono)"
            w={100}
            truncate
            c={hasUnresolved ? 'dimmed' : undefined}
          >
            {display || '(no baseUrl)'}
          </Text>
        </UnstyledButton>
      </Tooltip>
      {envs.length > 0 && (
        <Menu shadow="md" position="bottom-end" withinPortal>
          <Menu.Target>
            <UnstyledButton
              aria-label="Select environment"
              title={selected ? `Environment: ${selected.name}` : 'Select environment'}
              style={{
                display: 'flex', alignItems: 'center', gap: 4,
                height: 34, padding: '0 8px',
                borderLeft: '1px solid var(--mantine-color-default-border)',
                color: 'var(--mantine-color-dimmed)',
              }}
            >
              {selected && <Text size="xs" c="dimmed" ff="var(--mono)">{selected.name}</Text>}
              <IconChevronDown size={12} />
            </UnstyledButton>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item
              onClick={() => onEnvChange(null)}
              rightSection={env === null ? <IconCheck size={12} /> : null}
            >
              <Text size="sm" c="dimmed">Workspace default</Text>
            </Menu.Item>
            {scoped.length > 0 && <Menu.Label>{summary.name}</Menu.Label>}
            {scoped.map((e) => (
              <EnvItem key={e.path} name={e.name} baseUrl={envBindingFor(e, summary.slug)?.baseUrl ?? null}
                       checked={env === e.path} onClick={() => onEnvChange(e.path)} />
            ))}
            {globals.length > 0 && <Menu.Label>Global</Menu.Label>}
            {globals.map((e) => (
              <EnvItem key={e.path} name={e.name} baseUrl={null}
                       checked={env === e.path} onClick={() => onEnvChange(e.path)} />
            ))}
          </Menu.Dropdown>
        </Menu>
      )}
    </Group>
  )
}

/** One row in the environment menu. Shows the baseUrl an env would move the request to, since
 *  that is the difference the user is usually choosing between. */
function EnvItem({ name, baseUrl, checked, onClick }: {
  name: string
  baseUrl: string | null
  checked: boolean
  onClick: () => void
}) {
  return (
    <Menu.Item onClick={onClick} rightSection={checked ? <IconCheck size={12} /> : null}>
      <Stack gap={0}>
        <Text size="sm">{name}</Text>
        {baseUrl && <Text size="xs" c="dimmed" ff="var(--mono)">{baseUrl}</Text>}
      </Stack>
    </Menu.Item>
  )
}

function resolveTokens(text: string, vars: Map<string, { value: string | null; isSensitive: boolean }>): string {
  return text.replace(/(\$?)\{\{\s*([^}]+?)\s*\}\}/g, (_, dollar: string, name: string) => {
    if (dollar === '$') return '***'
    const v = vars.get(name)
    if (!v) return `{{${name}}}`
    return v.isSensitive ? '***' : (v.value ?? '')
  })
}
