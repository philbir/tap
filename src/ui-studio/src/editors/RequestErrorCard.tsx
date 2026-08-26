import { Box, Button, Group, Paper, ScrollArea, Stack, Text } from '@mantine/core'
import {
  IconAlertCircle, IconAdjustments, IconClockExclamation, IconPlugX, IconShieldX,
  IconStethoscope, IconWorldOff,
} from '@tabler/icons-react'
import { describeRequestError, type RequestErrorKind } from './requestError'

const KIND_ICON: Record<RequestErrorKind, React.ReactNode> = {
  tls: <IconShieldX size={20} color="var(--mantine-color-red-6)" />,
  protocol: <IconShieldX size={20} color="var(--mantine-color-red-6)" />,
  dns: <IconWorldOff size={20} color="var(--mantine-color-red-6)" />,
  connection: <IconPlugX size={20} color="var(--mantine-color-red-6)" />,
  timeout: <IconClockExclamation size={20} color="var(--mantine-color-red-6)" />,
  unknown: <IconAlertCircle size={20} color="var(--mantine-color-red-6)" />,
}

/**
 * The response pane when there is no response — the send died in transport.
 *
 * The raw exception chain is still here verbatim, because it is the only thing that survives
 * being pasted into an issue. But it goes *below* a plain-language reading of the fault and,
 * where one exists, the control that fixes it: for a certificate failure that is a diagnosis of
 * what the server actually presented and the transport setting that can waive validation. The
 * message alone was a dead end — knowing the string says `NotTimeValid` doesn't tell you the
 * switch is two tabs away.
 */
export function RequestErrorCard({ message, onDiagnoseTls, diagnosing, onOpenTransport }: {
  message: string
  /** Offered for certificate failures only — a diagnosis has nothing to add to a DNS miss. */
  onDiagnoseTls?: () => void
  diagnosing?: boolean
  onOpenTransport?: () => void
}) {
  const info = describeRequestError(message)
  const tlsish = info.kind === 'tls' || info.kind === 'protocol'

  return (
    <ScrollArea h="100%" type="auto" scrollbarSize={8}>
      <Stack p="md" gap="sm" maw={680}>
        <Group gap={10} wrap="nowrap" align="flex-start">
          <Box mt={1} style={{ lineHeight: 0 }}>{KIND_ICON[info.kind]}</Box>
          <Box style={{ minWidth: 0 }}>
            <Text size="sm" fw={600} c="red">{info.title}</Text>
            {info.explanation && <Text size="sm" c="dimmed" mt={2}>{info.explanation}</Text>}
          </Box>
        </Group>

        <Paper withBorder radius="sm" p="xs" bg="var(--mantine-color-default)">
          <Text size="xs" ff="var(--mono)" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
            {message}
          </Text>
        </Paper>

        {info.hint && <Text size="xs" c="dimmed">{info.hint}</Text>}

        {(onDiagnoseTls || onOpenTransport) && (tlsish || info.kind === 'timeout') && (
          <Group gap="xs">
            {tlsish && onDiagnoseTls && (
              <Button
                size="xs"
                variant="light"
                color="tap"
                leftSection={<IconStethoscope size={13} />}
                loading={diagnosing}
                onClick={onDiagnoseTls}
              >
                Diagnose TLS
              </Button>
            )}
            {onOpenTransport && (
              <Button
                size="xs"
                variant="default"
                leftSection={<IconAdjustments size={13} />}
                onClick={onOpenTransport}
              >
                Transport settings
              </Button>
            )}
          </Group>
        )}

        {tlsish && onOpenTransport && (
          <Text size="xs" c="dimmed">
            Under <strong>Transport</strong>, setting <strong>TLS certificate validation</strong> to{' '}
            <strong>Ignore certificate errors</strong> lets this request through anyway — fine for a
            local or test endpoint you already trust, and not something to leave on for anything else.
          </Text>
        )}
      </Stack>
    </ScrollArea>
  )
}
