import { Badge, Box, Code, Group, Stack, Text, Tooltip } from '@mantine/core'
import { useEffect, useRef, useState } from 'react'
import type { VariableContext } from '../api/types'
import { RawBodyEditor } from './RawBodyEditor'
import { VariableInput } from './VariableInput'
import { parseSoapBody, serializeSoapBody, SOAP_12_NS, type SoapBody } from './body-mode'

interface Props {
  /** Raw wire-format body string: a full SOAP envelope. */
  body: string
  onChange: (body: string) => void
  variableContext?: VariableContext | null
  onOpenVariables?: () => void
}

/**
 * SOAP body editor. The user fills in the operation and its arguments; the envelope around
 * them is generated. Everything the editor doesn't surface — a `<soap:Header>` block, extra
 * attributes on the operation element, a SOAP 1.2 namespace — is carried through
 * {@link parseSoapBody} verbatim, so opening a hand-written envelope here and saving it
 * back doesn't quietly rewrite the parts that aren't on screen.
 *
 * `SOAPAction` is deliberately not handled here: it's an HTTP header, its value is a URI
 * rather than the bare operation name, and services disagree on whether it's required at
 * all — so it belongs on the Headers tab where the user can see what's actually sent.
 */
export function SoapEditor({ body, onChange, variableContext, onOpenVariables }: Props) {
  const [parts, setParts] = useState<SoapBody>(() => parseSoapBody(body))

  // Re-sync when the parent's body changes for a reason other than our own edit (the user
  // switched requests, or hit Discard). Comparing against our last serialization keeps
  // in-flight typing from being clobbered by the echo of the edit we just emitted.
  const lastSerialized = useRef(body)
  useEffect(() => {
    if (body === lastSerialized.current) return
    setParts(parseSoapBody(body))
    lastSerialized.current = body
  }, [body])

  function emit(next: SoapBody) {
    setParts(next)
    const serialized = serializeSoapBody(next)
    lastSerialized.current = serialized
    // Always emit the envelope, even when empty — dropping the body would re-detect the
    // mode as None and snap the selector away from SOAP mid-edit.
    onChange(serialized)
  }

  const version = parts.envelopeNs === SOAP_12_NS ? '1.2' : '1.1'

  return (
    <Stack gap="sm">
      <Group grow align="flex-start" gap="sm">
        <Box>
          <Text size="sm" fw={500} mb={4}>Operation</Text>
          <VariableInput
            value={parts.operation}
            onChange={(v) => emit({ ...parts, operation: v })}
            placeholder="GetWeather"
            context={variableContext}
            onOpenVariables={onOpenVariables}
            nameHint="operation"
          />
          <Text size="xs" c="dimmed" mt={4}>Element wrapping the payload inside the envelope's Body.</Text>
        </Box>
        <Box>
          <Text size="sm" fw={500} mb={4}>Namespace</Text>
          <VariableInput
            value={parts.namespace}
            onChange={(v) => emit({ ...parts, namespace: v })}
            placeholder="http://tempuri.org/"
            context={variableContext}
            onOpenVariables={onOpenVariables}
            nameHint="namespace"
          />
          <Text size="xs" c="dimmed" mt={4}>The operation's <Code>xmlns</Code> — the WSDL's target namespace.</Text>
        </Box>
      </Group>

      <Box>
        <Group justify="space-between" mb={6} wrap="nowrap" gap="xs">
          <Group gap={6} wrap="nowrap">
            <Text size="xs" fw={600} c="dimmed" tt="uppercase">XML payload</Text>
            <Badge size="xs" variant="light" color="gray">SOAP {version}</Badge>
            {parts.header.trim() && (
              <Tooltip
                label={<Box maw={420} style={{ whiteSpace: 'pre-wrap' }}>{parts.header}</Box>}
                withArrow multiline w={440}
              >
                <Badge size="xs" variant="light" color="orange" style={{ cursor: 'help' }}>Header kept</Badge>
              </Tooltip>
            )}
          </Group>
          <Text size="xs" c="dimmed">
            Wrapped in the envelope on send. Set <Code>SOAPAction</Code> on the Headers tab if the service requires it.
          </Text>
        </Group>
        <RawBodyEditor
          value={parts.payload}
          onChange={(v) => emit({ ...parts, payload: v })}
          rawSub="xml"
          height={360}
        />
      </Box>
    </Stack>
  )
}
