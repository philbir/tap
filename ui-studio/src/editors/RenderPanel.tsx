import { Badge, Box, Center, Code, Group, ScrollArea, Stack, Table, Text } from '@mantine/core'
import { IconCheck, IconEye } from '@tabler/icons-react'
import type { RenderedRequest } from '../api/types'

interface Props {
  rendered: RenderedRequest | null
  error: string | null
  busy: boolean
}

/** Resolved-request preview: method/URL line, headers, body, and secret-resolution chips. */
export function RenderPanel({ rendered, error, busy }: Props) {
  if (busy && !rendered) {
    return <Center h="100%"><Text size="sm" c="dimmed">Rendering…</Text></Center>
  }
  if (error) {
    return (
      <Stack p="lg" gap="xs">
        <Text size="sm" c="red">{error}</Text>
      </Stack>
    )
  }
  if (!rendered) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={280} ta="center">
          <IconEye size={20} color="var(--mantine-color-dimmed)" />
          <Text size="sm" c="dimmed">Click <strong>Preview</strong> to resolve this request against the active environment.</Text>
        </Stack>
      </Center>
    )
  }

  return (
    <ScrollArea h="100%">
      <Stack p="md" gap="md">
        <Group gap="xs" wrap="nowrap">
          <Text fw={700} fz="sm" data-method={rendered.method} ff="var(--mono)">{rendered.method}</Text>
          <Code fz="xs" style={{ wordBreak: 'break-all' }}>{rendered.url}</Code>
        </Group>

        <Box>
          <Text size="xs" fw={600} tt="uppercase" c="tap" lts={0.5} mb={6}>Headers</Text>
          <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false} striped>
            <Table.Tbody>
              {Object.entries(rendered.headers).map(([k, v]) => (
                <Table.Tr key={k}>
                  <Table.Td style={{ width: '35%' }} ff="var(--mono)" fz="xs">{k}</Table.Td>
                  <Table.Td ff="var(--mono)" fz="xs" style={{ wordBreak: 'break-word' }}>{v}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Box>

        {rendered.body && (
          <Box>
            <Text size="xs" fw={600} tt="uppercase" c="tap" lts={0.5} mb={6}>Body</Text>
            <Code block fz="xs" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: 320, overflow: 'auto' }}>
              {rendered.body}
            </Code>
          </Box>
        )}

        {rendered.variablesUsed.length > 0 && (
          <Box>
            <Text size="xs" fw={600} tt="uppercase" c="tap" lts={0.5} mb={6}>Variables resolved</Text>
            <Group gap={6}>
              {rendered.variablesUsed.map((s, i) => (
                <Badge
                  key={i}
                  size="sm"
                  variant="light"
                  color={s.resolved ? (s.isSecret ? 'yellow' : 'green') : 'red'}
                  leftSection={s.resolved ? <IconCheck size={10} /> : null}
                  title={`${s.durationMs.toFixed(1)} ms${s.isSecret ? ' (secret)' : ''}`}
                  styles={{ root: { textTransform: 'none', fontFamily: 'var(--mono)' } }}
                >
                  {s.variableProvider}:{s.name}
                </Badge>
              ))}
            </Group>
          </Box>
        )}
      </Stack>
    </ScrollArea>
  )
}
