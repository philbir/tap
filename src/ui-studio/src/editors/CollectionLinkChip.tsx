import { Group, Menu, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core'
import { IconCheck, IconChevronDown, IconFolders } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { CollectionDetail, CollectionSummary, VariableContext } from '../api/types'
import { useTapStore } from '../store'
import { useVariableView, variableMap } from '../workspace/useVariables'

/**
 * The collection a request inherits from, rendered as a compact two-part chip: the resolved
 * base URL (click to open the collection) and a stage menu.
 *
 * Shared by the request editor and the `.http` editor. Both need it for the same reason — a
 * relative request line only means something once you know which base URL and which stage it
 * resolves against, and the stage is a choice the user makes per send rather than a property
 * of the file.
 */
export function CollectionLinkChip({ summary, stage, onStageChange, variableContext, onOpen }: {
  summary: CollectionSummary
  stage: string | null
  onStageChange: (next: string | null) => void
  variableContext: VariableContext
  onOpen: () => void
}) {
  const generation = useTapStore((s) => s.generation)
  const view = useVariableView(variableContext)
  const vars = useMemo(() => variableMap(view), [view])
  // Stage can override the collection-level baseUrl entirely. Fetch CollectionDetail to
  // pick up per-stage overrides so changing the stage actually moves the URL.
  const [detail, setDetail] = useState<CollectionDetail | null>(null)
  useEffect(() => {
    let cancelled = false
    api.collectionDetail(summary.slug).then((d) => { if (!cancelled) setDetail(d) }).catch(() => {})
    return () => { cancelled = true }
  }, [summary.slug, generation])
  const baseTemplate = useMemo(() => {
    if (!stage || !detail) return summary.baseUrl
    const s = detail.stages.find((x) => x.name === stage)
    return s?.baseUrl ?? summary.baseUrl
  }, [detail, stage, summary.baseUrl])
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
    </Stack>
  )

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
      {summary.stageNames.length > 0 && (
        <Menu shadow="md" position="bottom-end" withinPortal>
          <Menu.Target>
            <UnstyledButton
              aria-label="Select stage"
              title={stage ? `Stage: ${stage}` : 'Select stage'}
              style={{
                display: 'flex', alignItems: 'center', gap: 4,
                height: 34, padding: '0 8px',
                borderLeft: '1px solid var(--mantine-color-default-border)',
                color: 'var(--mantine-color-dimmed)',
              }}
            >
              {stage && <Text size="xs" c="dimmed" ff="var(--mono)">{stage}</Text>}
              <IconChevronDown size={12} />
            </UnstyledButton>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Stage</Menu.Label>
            <Menu.Item
              onClick={() => onStageChange(null)}
              rightSection={stage === null ? <IconCheck size={12} /> : null}
            >
              (no stage)
            </Menu.Item>
            {summary.stageNames.map((s) => (
              <Menu.Item
                key={s}
                onClick={() => onStageChange(s)}
                rightSection={stage === s ? <IconCheck size={12} /> : null}
              >
                {s}
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      )}
    </Group>
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
