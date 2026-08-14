import { Badge, Box, Group, ScrollArea, Stack, Text, UnstyledButton } from '@mantine/core'
import { IconArrowsSplit2, IconChecklist } from '@tabler/icons-react'
import { useMemo } from 'react'
import { useTapStore } from '../store'

/**
 * The Testing tab's list: test sets first, then flows.
 *
 * Two sections rather than one merged list because they answer different questions — a test
 * set is "do these still pass", a flow is "does this sequence still work" — and a flow is
 * usually reached *through* a set. Flows stay directly visible because they're runnable and
 * editable on their own.
 */
interface Props {
  search: string
  activePath: string | null
  onOpen: (path: string, kind: 'test' | 'flow', name: string) => void
}

export function TestingView({ search, activePath, onOpen }: Props) {
  const testSets = useTapStore((s) => s.testSets)
  const flows = useTapStore((s) => s.flows)

  const q = search.trim().toLowerCase()
  const matches = (name: string, path: string, tags: string[]) =>
    q.length === 0
    || name.toLowerCase().includes(q)
    || path.toLowerCase().includes(q)
    || tags.some((t) => t.toLowerCase().includes(q))

  const visibleSets = useMemo(
    () => testSets.filter((t) => matches(t.name, t.path, t.tags)),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `matches` closes over `q` only
    [testSets, q],
  )
  const visibleFlows = useMemo(
    () => flows.filter((f) => matches(f.name, f.path, f.tags)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [flows, q],
  )

  if (testSets.length === 0 && flows.length === 0) {
    return (
      <Text size="xs" c="dimmed" ta="center" py="xl" px="md">
        No test sets or flows yet — create one with +. A test set groups checks; a flow chains
        requests and carries values between them.
      </Text>
    )
  }

  if (visibleSets.length === 0 && visibleFlows.length === 0) {
    return <Text size="xs" c="dimmed" ta="center" py="xl" px="md">Nothing matches “{search}”.</Text>
  }

  return (
    <ScrollArea flex={1} type="hover" scrollbarSize={8}>
      <Box pb="md">
        {visibleSets.length > 0 && (
          <Section label="Test sets">
            {visibleSets.map((t) => (
              <Row
                key={t.path}
                name={t.name}
                detail={`${t.testCount} ${t.testCount === 1 ? 'test' : 'tests'}`}
                tags={t.tags}
                active={activePath === t.path}
                icon={<IconChecklist size={14} color="var(--mantine-color-teal-6)" />}
                onClick={() => onOpen(t.path, 'test', t.name)}
              />
            ))}
          </Section>
        )}

        {visibleFlows.length > 0 && (
          <Section label="Flows">
            {visibleFlows.map((f) => (
              <Row
                key={f.path}
                name={f.name}
                detail={`${f.stepCount} ${f.stepCount === 1 ? 'step' : 'steps'}`}
                tags={f.tags}
                active={activePath === f.path}
                icon={<IconArrowsSplit2 size={14} color="var(--mantine-color-violet-6)" />}
                onClick={() => onOpen(f.path, 'flow', f.name)}
              />
            ))}
          </Section>
        )}
      </Box>
    </ScrollArea>
  )
}

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Box mb="sm">
      <Text size="10px" fw={700} c="dimmed" tt="uppercase" px="sm" pb={4} style={{ letterSpacing: 0.6 }}>
        {label}
      </Text>
      <Stack gap={0}>{children}</Stack>
    </Box>
  )
}

function Row({
  name, detail, tags, active, icon, onClick,
}: {
  name: string
  detail: string
  tags: string[]
  active: boolean
  icon: React.ReactNode
  onClick: () => void
}) {
  return (
    <UnstyledButton
      onClick={onClick}
      px="sm"
      py={5}
      className="tap-tree-row"
      style={{
        background: active ? 'var(--mantine-color-default-hover)' : undefined,
        borderLeft: `2px solid ${active ? 'var(--mantine-color-tap-6)' : 'transparent'}`,
      }}
    >
      <Group gap={8} wrap="nowrap">
        <Box style={{ display: 'flex', flexShrink: 0 }}>{icon}</Box>
        <Box style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" truncate title={name}>{name}</Text>
        </Box>
        {tags.length > 0 && (
          <Badge size="xs" variant="light" color="gray" style={{ flexShrink: 0 }}>{tags[0]}</Badge>
        )}
        <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>{detail}</Text>
      </Group>
    </UnstyledButton>
  )
}
