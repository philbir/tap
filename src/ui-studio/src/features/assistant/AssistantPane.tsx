import { ActionIcon, Alert, Badge, Box, Button, Code, Group, Loader, ScrollArea, Select, Stack, Text, Textarea, ThemeIcon, Tooltip } from '@mantine/core'
import {
  IconAlertCircle, IconArrowUp, IconCheck, IconRefresh, IconSettings, IconSparkles, IconTool, IconUser, IconX,
} from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../../api/client'
import type { AiModelOption, AiStatus, AiToolCall, RequestSpec } from '../../api/types'
import { SETTINGS_TAB_PATH, useTapStore } from '../../store'
import { Markdown } from './Markdown'

interface Turn {
  role: 'user' | 'assistant'
  /** Display text — for assistant turns the raw ```tap-request block is stripped out. */
  text: string
  /** Present on assistant turns when the model proposed an applyable request. */
  proposal?: RequestSpec | null
  /** True once the user clicks Apply for this turn's proposal. */
  applied?: boolean
  /** Tool calls the assistant ran while producing this turn. */
  toolCalls?: AiToolCall[]
}

interface Props {
  requestPath: string
  currentSpec: RequestSpec | null
  onApply: (spec: RequestSpec) => void
  onClose: () => void
}

/** localStorage key for the last model the user picked, scoped per provider. */
const modelKey = (provider: string) => `tap.ai.model.${provider}`

/**
 * The request assistant pane. Lives in the right slot of the RequestEditor. Sends the live
 * editor spec as context to `/api/ai/assist`; when the model returns a `tap-request`
 * proposal it is applied to the editor spec immediately (marking it dirty — the file is NOT
 * written; the user saves with the normal flow). The proposal card is a passive summary of
 * what changed, with a Re-apply affordance in case the user has since edited the form.
 */
export function AssistantPane({ requestPath, currentSpec, onApply, onClose }: Props) {
  const openTab = useTapStore((s) => s.openTab)
  const [status, setStatus] = useState<AiStatus | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [models, setModels] = useState<AiModelOption[]>([])
  const [model, setModel] = useState<string | null>(null)
  const [turns, setTurns] = useState<Turn[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const viewportRef = useRef<HTMLDivElement>(null)

  const loadStatus = async () => {
    setStatusError(null)
    try {
      const s = await api.aiStatus()
      setStatus(s)
      // Restore the user's last model for this provider, else the provider default.
      const saved = localStorage.getItem(modelKey(s.provider))
      setModel((cur) => cur ?? saved ?? s.model)
    } catch (e) { setStatusError(e instanceof ApiError ? e.message : String(e)) }
  }
  useEffect(() => { void loadStatus() }, [])

  // Once configured, fetch the model catalog for the picker.
  useEffect(() => {
    if (!status?.configured) return
    let cancelled = false
    void (async () => {
      try {
        const res = await api.aiModels()
        if (!cancelled) setModels(res.models)
      } catch { /* picker falls back to the default model; non-fatal */ }
    })()
    return () => { cancelled = true }
  }, [status?.configured])

  useEffect(() => {
    // Keep the latest turn in view as the conversation grows.
    viewportRef.current?.scrollTo({ top: viewportRef.current.scrollHeight, behavior: 'smooth' })
  }, [turns, busy])

  const configured = status?.configured ?? false

  function pickModel(value: string | null) {
    setModel(value)
    if (value && status) localStorage.setItem(modelKey(status.provider), value)
  }

  async function send(prompt: string) {
    const trimmed = prompt.trim()
    if (!trimmed || busy) return
    setError(null)
    setInput('')
    const history = turns.map((t) => ({ role: t.role, content: t.text }))
    setTurns((cur) => [...cur, { role: 'user', text: trimmed }])
    setBusy(true)
    try {
      const res = await api.aiAssist({
        prompt: trimmed,
        requestPath,
        currentSpec: currentSpec ?? undefined,
        conversation: history,
        model: model ?? undefined,
      })
      const proposal = res.proposal ?? null
      setTurns((cur) => [...cur, {
        role: 'assistant',
        text: stripProposalBlock(res.reply) || (proposal ? 'Applied the proposed update.' : '(no response)'),
        proposal,
        applied: !!proposal,
        toolCalls: res.toolCalls,
      }])
      // Auto-apply onto the live editor spec (dirty, unsaved) — no manual Apply step.
      if (proposal) onApply({ ...proposal, path: requestPath })
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : String(e)
      setError(msg)
      setTurns((cur) => [...cur, { role: 'assistant', text: `⚠️ ${msg}` }])
    } finally {
      setBusy(false)
    }
  }

  function reapply(spec: RequestSpec) {
    // Proposals are auto-applied on arrival; this lets the user push the same proposal back
    // onto the form if they've since hand-edited it. Keeps the proposal's path aligned.
    onApply({ ...spec, path: requestPath })
  }

  const modelData = (() => {
    const opts = models.map((m) => ({ value: m.id, label: m.name || m.id }))
    // Keep the current value selectable even if the catalog hasn't loaded yet.
    if (model && !opts.some((o) => o.value === model)) opts.unshift({ value: model, label: model })
    return opts
  })()

  const header = (
    <Stack gap={0} style={{ flexShrink: 0, borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Group justify="space-between" px="md" py="sm">
        <Group gap="xs">
          <IconSparkles size={18} color="var(--mantine-color-tap-6)" />
          <Text fw={600} size="sm">Assistant</Text>
          {status && (
            <Badge variant="light" color={configured ? 'teal' : 'gray'} size="xs">
              {status.provider}
            </Badge>
          )}
        </Group>
        <Group gap={4}>
          <Tooltip label="Reload status" withArrow>
            <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => void loadStatus()}><IconRefresh size={14} /></ActionIcon>
          </Tooltip>
          <Tooltip label="Close" withArrow>
            <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose}><IconX size={14} /></ActionIcon>
          </Tooltip>
        </Group>
      </Group>
      {configured && (
        <Box px="md" pb="sm">
          <Select
            size="xs"
            data={modelData}
            value={model}
            onChange={pickModel}
            placeholder="Model"
            searchable
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            leftSection={<IconSparkles size={12} />}
            disabled={busy}
            aria-label="Model"
          />
        </Box>
      )}
    </Stack>
  )

  if (status && !configured) {
    return (
      <Box style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
        {header}
        <Stack p="md" gap="sm">
          <Alert color="yellow" variant="light" icon={<IconAlertCircle size={16} />} title="AI not configured">
            <Text size="sm">{status.setupHint}</Text>
          </Alert>
          <Button
            variant="light"
            leftSection={<IconSettings size={14} />}
            onClick={() => openTab({ path: SETTINGS_TAB_PATH, kind: 'settings', label: 'Settings' })}
          >
            Open AI settings
          </Button>
        </Stack>
      </Box>
    )
  }

  return (
    <Box style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {header}

      <ScrollArea style={{ flex: 1, minHeight: 0 }} viewportRef={viewportRef} type="hover">
        <Stack p="md" gap="md">
          {statusError && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{statusError}</Alert>}

          {turns.length === 0 && (
            <Text size="sm" c="dimmed">
              Describe the request you want and I’ll craft it. I can see the request you’re editing,
              so ask for incremental changes too — I’ll apply them straight to the editor.
            </Text>
          )}

          {turns.map((t, i) => (
            <TurnBubble key={i} turn={t} onReapply={(spec) => reapply(spec)} />
          ))}

          {busy && (
            <Group gap="xs">
              <Loader size="xs" />
              <Text size="sm" c="dimmed">Thinking…</Text>
            </Group>
          )}
        </Stack>
      </ScrollArea>

      {error && (
        <Box px="md" pb="xs"><Text size="xs" c="red">{error}</Text></Box>
      )}

      <Box p="md" style={{ borderTop: '1px solid var(--mantine-color-default-border)', flexShrink: 0 }}>
        <Textarea
          placeholder="Ask the assistant to craft or edit this request…"
          value={input}
          onChange={(e) => setInput(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void send(input) }
          }}
          autosize
          minRows={2}
          maxRows={6}
          disabled={busy}
          rightSection={
            <ActionIcon variant="filled" color="tap" disabled={!input.trim() || busy} onClick={() => void send(input)} aria-label="Send">
              <IconArrowUp size={16} />
            </ActionIcon>
          }
        />
        <Text size="10px" c="dimmed" mt={4}>Enter to send · Shift+Enter for a new line</Text>
      </Box>
    </Box>
  )
}

function TurnBubble({ turn, onReapply }: { turn: Turn; onReapply: (spec: RequestSpec) => void }) {
  const isUser = turn.role === 'user'
  return (
    <Stack gap={6}>
      <Group gap={6} align="center">
        {isUser ? <IconUser size={13} /> : <IconSparkles size={13} color="var(--mantine-color-tap-6)" />}
        <Text size="xs" fw={600} c={isUser ? undefined : 'tap'}>{isUser ? 'You' : 'Assistant'}</Text>
      </Group>
      {turn.toolCalls && turn.toolCalls.length > 0 && (
        <Stack gap={4}>
          {turn.toolCalls.map((tc, i) => <ToolCallRow key={i} call={tc} />)}
        </Stack>
      )}
      <Box
        style={{
          background: isUser ? 'var(--mantine-color-default-hover)' : 'transparent',
          borderRadius: 8,
          padding: isUser ? '8px 10px' : 0,
        }}
      >
        {isUser
          ? <Text size="sm" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{turn.text}</Text>
          : <Markdown>{turn.text}</Markdown>}
      </Box>
      {turn.proposal && <ProposalCard spec={turn.proposal} onReapply={() => onReapply(turn.proposal!)} />}
    </Stack>
  )
}

function ToolCallRow({ call }: { call: AiToolCall }) {
  const color = call.success === false ? 'red' : call.success === true ? 'teal' : 'gray'
  return (
    <Group gap={8} wrap="nowrap" align="center">
      <ThemeIcon size="sm" radius="sm" variant="light" color={color}>
        <IconTool size={12} />
      </ThemeIcon>
      <Text size="xs" fw={600} style={{ flexShrink: 0 }}>{call.name}</Text>
      {call.summary && (
        <Code fz="10px" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {call.summary}
        </Code>
      )}
    </Group>
  )
}

function ProposalCard({ spec, onReapply }: { spec: RequestSpec; onReapply: () => void }) {
  return (
    <Box
      p="sm"
      style={{
        border: '1px solid var(--mantine-color-tap-4)',
        borderRadius: 8,
        background: 'var(--mantine-color-tap-light)',
      }}
    >
      <Group gap="xs" mb={6} wrap="nowrap">
        <Badge color="tap" variant="filled" size="sm" style={{ fontFamily: 'var(--mono)' }}>
          {spec.protocol === 'websocket' ? 'WS' : spec.method}
        </Badge>
        <Code fz="xs" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {spec.url || '(no url)'}
        </Code>
      </Group>
      {(() => {
        const bits: string[] = []
        if (spec.headers && spec.headers.length > 0) bits.push(`${spec.headers.length} header${spec.headers.length === 1 ? '' : 's'}`)
        if (spec.requestBody) bits.push('body')
        if (spec.auth) bits.push(`auth: ${spec.auth}`)
        if (spec.body) bits.push('docs')
        return bits.length > 0 ? <Text size="xs" c="dimmed" mb={6}>{bits.join(' · ')}</Text> : null
      })()}
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Group gap={4} c="tap" wrap="nowrap">
          <IconCheck size={14} />
          <Text size="xs" fw={600}>Applied to editor · unsaved</Text>
        </Group>
        <Button size="compact-xs" variant="subtle" color="tap" leftSection={<IconRefresh size={12} />} onClick={onReapply}>
          Re-apply
        </Button>
      </Group>
    </Box>
  )
}

/** Remove the ```tap-request fenced JSON from the visible reply — it's shown as an Apply card. */
function stripProposalBlock(reply: string): string {
  return reply.replace(/```tap-request\s*[\s\S]*?```/gi, '').replace(/\n{3,}/g, '\n\n').trim()
}
