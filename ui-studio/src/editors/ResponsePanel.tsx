import {
  ActionIcon, Badge, Box, Center, Code, Group, Loader, ScrollArea, Stack, Table, Tabs, Text, Tooltip,
} from '@mantine/core'
import {
  IconAlertCircle, IconArrowDown, IconArrowUp, IconBolt, IconKey, IconPlugConnected, IconPlugX, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { ExecutionResult, RenderedRequest, SseEvent, WsFrame } from '../api/types'
import { CodeBlock } from './CodeBlock'

interface Props {
  rendered: RenderedRequest | null
  execution: ExecutionResult | null
  error: string | null
  busy: boolean
  /** Called when the user clicks the close (×) button — parent should hide the pane. */
  onClose?: () => void
}

/**
 * Response panel that lives in the bottom split of the RequestEditor. Mirrors dreamr's
 * layout:
 *
 *   ┌─ Body | Cookies (n) | Headers (n) | Request ──── 200 OK · 68 ms · 412 KB · json ──┐
 *   │                                                                                    │
 *   │  CodeMirror response viewer (JSON / XML / YAML / plain)                            │
 *   │                                                                                    │
 *   └────────────────────────────────────────────────────────────────────────────────────┘
 *
 * The status pill on the right is color-coded by status class (2xx green / 3xx orange /
 * 4xx yellow / 5xx red). The "Request" sub-tab shows what was sent on the wire (replaces
 * the old separate Preview pane).
 */
export function ResponsePanel({ rendered, execution, error, busy, onClose }: Props) {
  const sseEvents = execution?.sseEvents
  const hasSse = !!sseEvents && sseEvents.length > 0
  const wsFrames = execution?.wsFrames
  // WebSocket execution doesn't have a meaningful HTTP body, so default the active tab
  // to Frames for ws requests. SSE keeps its existing auto-switch behavior on first event.
  const isWs = execution?.protocol === 'websocket' || rendered?.protocol === 'websocket'
  const [tab, setTab] = useState<string | null>(isWs ? 'frames' : 'body')

  // First time SSE frames appear, snap the user to the Events tab — they almost
  // certainly want to watch them stream in.
  const lastSeenCountRef = useRef(0)
  useEffect(() => {
    const count = sseEvents?.length ?? 0
    if (count > 0 && lastSeenCountRef.current === 0) setTab('events')
    lastSeenCountRef.current = count
  }, [sseEvents])

  // Same idea for ws frames — flip to the Frames tab when the first one arrives.
  const lastWsCountRef = useRef(0)
  useEffect(() => {
    const count = wsFrames?.length ?? 0
    if (count > 0 && lastWsCountRef.current === 0) setTab('frames')
    lastWsCountRef.current = count
  }, [wsFrames])

  const cookies = useMemo(() => parseSetCookies(execution?.responseHeaders), [execution?.responseHeaders])

  // While busy we still fall through to the tabs IF execution has started — otherwise
  // SSE frames accumulate invisibly until `done` fires (the entire reason the user
  // ran a streaming request was to watch them arrive). Only show the bare loader when
  // we haven't received the `meta` event yet.
  if (busy && !execution) {
    return (
      <Stack h="100%" gap={0}>
        <CompactHeader onClose={onClose}>
          <Group gap={6}>
            <Loader size="xs" />
            <Text size="xs" c="dimmed">Executing request…</Text>
          </Group>
        </CompactHeader>
        <Box style={{ flex: 1 }} />
      </Stack>
    )
  }
  if (error && !execution) {
    return (
      <Stack h="100%" gap={0}>
        <CompactHeader onClose={onClose}>
          <Group gap={6}>
            <IconAlertCircle size={14} color="var(--mantine-color-red-6)" />
            <Text c="red" size="xs">{error}</Text>
          </Group>
        </CompactHeader>
        <Box style={{ flex: 1 }} />
      </Stack>
    )
  }

  const headerCount = execution ? Object.keys(execution.responseHeaders).length : 0
  const secretCount = (execution?.variablesUsed ?? rendered?.variablesUsed ?? []).filter(v => v.isSecret).length

  return (
    <Tabs value={tab} onChange={setTab} keepMounted={false} variant="default" style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <Group
        justify="space-between"
        wrap="nowrap"
        px="md"
        gap="xs"
        style={{
          minHeight: 32,
          borderBottom: '1px solid var(--mantine-color-default-border)',
          flexShrink: 0,
        }}
      >
        <Tabs.List style={{ border: 'none', flexShrink: 0 }}>
          {!isWs && <Tabs.Tab value="body" py={6}>Body</Tabs.Tab>}
          {hasSse && (
            <Tabs.Tab value="events" py={6} leftSection={<IconBolt size={12} />}>
              Events <Text component="span" c="dimmed" ml={6}>{sseEvents!.length}</Text>
            </Tabs.Tab>
          )}
          {isWs && (
            <Tabs.Tab value="frames" py={6} leftSection={<IconBolt size={12} />}>
              Frames <Text component="span" c="dimmed" ml={6}>{wsFrames?.length ?? 0}</Text>
            </Tabs.Tab>
          )}
          <Tabs.Tab value="headers" py={6}>
            Headers <Text component="span" c="dimmed" ml={6}>{headerCount}</Text>
          </Tabs.Tab>
          {cookies.length > 0 && (
            <Tabs.Tab value="cookies" py={6}>
              Cookies <Text component="span" c="dimmed" ml={6}>{cookies.length}</Text>
            </Tabs.Tab>
          )}
          <Tabs.Tab value="request" py={6}>Request</Tabs.Tab>
          <Tabs.Tab value="secrets" py={6}>
            Secrets {secretCount > 0 && <Text component="span" c="dimmed" ml={6}>{secretCount}</Text>}
          </Tabs.Tab>
        </Tabs.List>
        <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
          {execution && <StatusStrip execution={execution} busy={busy} />}
          {onClose && (
            <Tooltip label="Close response" withArrow>
              <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose} aria-label="Close response panel">
                <IconX size={14} />
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
      </Group>

      <Box style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
        {!isWs && (
          <Tabs.Panel value="body" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <BodyView execution={execution} />
          </Tabs.Panel>
        )}
        {hasSse && (
          <Tabs.Panel value="events" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <EventsView events={sseEvents!} busy={busy} />
          </Tabs.Panel>
        )}
        {isWs && (
          <Tabs.Panel value="frames" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <FramesView frames={wsFrames ?? []} busy={busy} />
          </Tabs.Panel>
        )}
        {cookies.length > 0 && (
          <Tabs.Panel value="cookies" h="100%">
            <CookiesView cookies={cookies} />
          </Tabs.Panel>
        )}
        <Tabs.Panel value="headers" h="100%">
          <HeaderTable headers={execution?.responseHeaders ?? {}} />
        </Tabs.Panel>
        <Tabs.Panel value="request" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <RequestView rendered={rendered} execution={execution} />
        </Tabs.Panel>
        <Tabs.Panel value="secrets" h="100%">
          <SecretsView rendered={rendered} execution={execution} />
        </Tabs.Panel>
      </Box>
    </Tabs>
  )
}

/** Skinny header with just a Close button — used by the busy / error states so they
 *  match the laid-out tab bar height and offer the same dismiss affordance. */
function CompactHeader({ children, onClose }: { children?: React.ReactNode; onClose?: () => void }) {
  return (
    <Group
      justify="space-between"
      wrap="nowrap"
      px="md"
      gap="xs"
      style={{
        minHeight: 32,
        borderBottom: '1px solid var(--mantine-color-default-border)',
        flexShrink: 0,
      }}
    >
      <Box>{children}</Box>
      {onClose && (
        <Tooltip label="Close response" withArrow>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose} aria-label="Close response panel">
            <IconX size={14} />
          </ActionIcon>
        </Tooltip>
      )}
    </Group>
  )
}

// ---- Status strip --------------------------------------------------------------------

function StatusStrip({ execution, busy }: { execution: ExecutionResult; busy?: boolean }) {
  return (
    <Group gap="sm" wrap="nowrap">
      <Tooltip label={execution.statusText ?? ''} withArrow disabled={!execution.statusText}>
        <Badge color={statusColor(execution.status)} variant="filled" size="sm" radius="sm">
          {execution.status || '—'} {execution.statusText ?? ''}
        </Badge>
      </Tooltip>
      <Group gap={6} wrap="nowrap">
        {busy
          ? <Group gap={4} wrap="nowrap"><Loader size={10} /><Text size="xs" c="dimmed">streaming…</Text></Group>
          : <Text size="xs" c="dimmed">{execution.durationMs.toFixed(0)} ms</Text>}
        <Text size="xs" c="dimmed">·</Text>
        <Text size="xs" c="dimmed">{formatBytes(execution.responseBodyBytes)}</Text>
        {execution.contentType && (
          <>
            <Text size="xs" c="dimmed">·</Text>
            <Text size="xs" c="dimmed">{shortContentType(execution.contentType)}</Text>
          </>
        )}
      </Group>
    </Group>
  )
}

// ---- Body view (CodeMirror) ----------------------------------------------------------

function BodyView({ execution }: { execution: ExecutionResult | null }) {
  if (!execution) {
    return <Center h="100%"><Text size="sm" c="dimmed">Nothing to show.</Text></Center>
  }
  if (execution.error) {
    return (
      <Stack p="md" gap="xs">
        <Group gap={6}>
          <IconAlertCircle size={14} color="var(--mantine-color-red-6)" />
          <Text size="sm" c="red">{execution.error}</Text>
        </Group>
      </Stack>
    )
  }
  // SSE responses have no traditional body — point the user at the Events tab.
  if (execution.contentType?.toLowerCase().includes('text/event-stream')) {
    return (
      <Center h="100%">
        <Stack align="center" gap={4} maw={320} ta="center">
          <Text size="sm" c="dimmed">This response is a live SSE stream.</Text>
          <Text size="xs" c="dimmed">Switch to the <strong>Events</strong> tab to watch frames arrive.</Text>
        </Stack>
      </Center>
    )
  }

  if (!execution.responseBody) {
    return <Center h="100%"><Text size="sm" c="dimmed">Empty body.</Text></Center>
  }

  // Image preview — server hands us a `data:image/...;base64,...` URL.
  if (execution.contentType?.toLowerCase().startsWith('image/') && execution.responseBody.startsWith('data:')) {
    return (
      <Center h="100%" p="md" style={{ overflow: 'auto' }}>
        <img
          src={execution.responseBody}
          alt={execution.contentType}
          style={{ maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', boxShadow: '0 1px 4px rgba(0,0,0,0.12)' }}
        />
      </Center>
    )
  }

  // Binary placeholder (server returns "[binary N bytes — …]") — render plainly.
  if (execution.responseBody.startsWith('[binary ')) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={320} ta="center">
          <Text size="sm" c="dimmed" ff="var(--mono)">{execution.responseBody}</Text>
          <Text size="xs" c="dimmed">No text preview available for this content type.</Text>
        </Stack>
      </Center>
    )
  }

  return (
    <ScrollArea h="100%" type="auto" scrollbarSize={8}>
      <CodeBlock
        value={execution.responseBody}
        contentType={execution.contentType}
        readOnly
      />
    </ScrollArea>
  )
}

// ---- Live SSE events ------------------------------------------------------------------

/**
 * Live-updating list of parsed SSE frames. Each frame shows seq + event name + a
 * preview line; clicking expands to the full data payload (rendered via CodeBlock with
 * detected language — JSON-shaped payloads get full JSON highlighting).
 *
 * Auto-scrolls to the newest event while the stream is busy; once the stream finishes
 * we stop auto-scrolling so the user can keep their place.
 */
function EventsView({ events, busy }: { events: SseEvent[]; busy: boolean }) {
  const viewportRef = useRef<HTMLDivElement>(null)
  const [openSeq, setOpenSeq] = useState<number | null>(null)

  useEffect(() => {
    if (!busy) return
    const el = viewportRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [events.length, busy])

  if (events.length === 0) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={280}>
          <IconBolt size={20} color="var(--mantine-color-dimmed)" />
          <Text size="sm" c="dimmed">Waiting for the first event…</Text>
        </Stack>
      </Center>
    )
  }
  return (
    <ScrollArea h="100%" viewportRef={viewportRef} type="auto" scrollbarSize={8}>
      <Stack gap={2} p={0}>
        {events.map((ev) => {
          const open = openSeq === ev.seq
          const preview = oneLinePreview(ev.data)
          return (
            <Stack
              key={`${ev.seq}`}
              gap={0}
              style={{ borderBottom: '1px solid var(--mantine-color-default-border)', cursor: 'pointer' }}
              onClick={() => setOpenSeq(open ? null : ev.seq)}
            >
              <Group gap="xs" wrap="nowrap" px="md" py={6}>
                <Text size="xs" c="dimmed" ff="var(--mono)" style={{ minWidth: 38 }}>#{ev.seq}</Text>
                <Text size="xs" c="dimmed" ff="var(--mono)" style={{ minWidth: 56 }}>{(ev.timestampMs / 1000).toFixed(2)}s</Text>
                <Badge size="xs" variant="light" color={ev.event === 'message' ? 'gray' : 'tap'} radius="sm" style={{ textTransform: 'none' }}>
                  {ev.event}
                </Badge>
                {ev.id && (
                  <Text size="xs" c="dimmed" ff="var(--mono)">id={ev.id}</Text>
                )}
                <Text size="xs" ff="var(--mono)" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {preview}
                </Text>
              </Group>
              {open && (
                <Box px="md" pb="sm" onClick={(e) => e.stopPropagation()}>
                  <CodeBlock
                    value={ev.data}
                    language={guessEventLanguage(ev.data)}
                    readOnly
                  />
                </Box>
              )}
            </Stack>
          )
        })}
      </Stack>
    </ScrollArea>
  )
}

// ---- Live WebSocket frames -----------------------------------------------------------

/**
 * Live-updating list of captured WebSocket frames. Mirrors the EventsView layout, but
 * with direction icons (↑ client, ↓ server, ⚡ system markers like open/close/error) and
 * the close-status code spelled out when the upstream sends one.
 *
 * Auto-scrolls to the newest frame while the executor is still pumping; once it stops,
 * scrolling is left to the user.
 */
function FramesView({ frames, busy }: { frames: WsFrame[]; busy: boolean }) {
  const viewportRef = useRef<HTMLDivElement>(null)
  const [openSeq, setOpenSeq] = useState<number | null>(null)

  useEffect(() => {
    if (!busy) return
    const el = viewportRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [frames.length, busy])

  if (frames.length === 0) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={320} ta="center">
          <IconPlugConnected size={20} color="var(--mantine-color-dimmed)" />
          <Text size="sm" c="dimmed">Waiting for the WebSocket handshake…</Text>
        </Stack>
      </Center>
    )
  }
  return (
    <ScrollArea h="100%" viewportRef={viewportRef} type="auto" scrollbarSize={8}>
      <Stack gap={2} p={0}>
        {frames.map((f) => {
          const open = openSeq === f.seq
          const expandable = f.type === 'text' || f.type === 'binary'
          const preview = framePreview(f)
          return (
            <Stack
              key={`${f.seq}`}
              gap={0}
              style={{
                borderBottom: '1px solid var(--mantine-color-default-border)',
                cursor: expandable ? 'pointer' : 'default',
              }}
              onClick={() => expandable && setOpenSeq(open ? null : f.seq)}
            >
              <Group gap="xs" wrap="nowrap" px="md" py={6}>
                <Text size="xs" c="dimmed" ff="var(--mono)" style={{ minWidth: 38 }}>#{f.seq}</Text>
                <Text size="xs" c="dimmed" ff="var(--mono)" style={{ minWidth: 56 }}>{(f.timestampMs / 1000).toFixed(2)}s</Text>
                <FrameDirectionBadge frame={f} />
                <Text size="xs" ff="var(--mono)" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {preview}
                </Text>
                {f.size > 0 && (
                  <Text size="xs" c="dimmed" ff="var(--mono)">{formatBytes(f.size)}</Text>
                )}
              </Group>
              {open && expandable && (
                <Box px="md" pb="sm" onClick={(e) => e.stopPropagation()}>
                  <CodeBlock
                    value={f.text ?? `(binary, ${f.size} bytes — base64)\n${f.base64 ?? ''}`}
                    language={guessFrameLanguage(f.text ?? '')}
                    readOnly
                  />
                </Box>
              )}
            </Stack>
          )
        })}
      </Stack>
    </ScrollArea>
  )
}

function FrameDirectionBadge({ frame }: { frame: WsFrame }) {
  if (frame.type === 'open') {
    return <Badge size="xs" variant="light" color="green" radius="sm" leftSection={<IconPlugConnected size={10} />}>open</Badge>
  }
  if (frame.type === 'close') {
    const label = frame.closeStatus ? `close ${frame.closeStatus}` : 'close'
    return <Badge size="xs" variant="light" color="gray" radius="sm" leftSection={<IconPlugX size={10} />}>{label}</Badge>
  }
  if (frame.type === 'error') {
    return <Badge size="xs" variant="light" color="red" radius="sm" leftSection={<IconAlertCircle size={10} />}>error</Badge>
  }
  // text / binary — show direction arrow + type
  const isOut = frame.direction === 'client'
  return (
    <Badge
      size="xs"
      variant="light"
      color={isOut ? 'blue' : 'teal'}
      radius="sm"
      leftSection={isOut ? <IconArrowUp size={10} /> : <IconArrowDown size={10} />}
      style={{ textTransform: 'none' }}
    >
      {frame.type}
    </Badge>
  )
}

function framePreview(f: WsFrame): string {
  if (f.type === 'open') return 'connection opened'
  if (f.type === 'close') return f.closeDescription ?? '(no description)'
  if (f.type === 'error') return f.text ?? '(no message)'
  if (f.type === 'binary') return `(${f.size} bytes binary)`
  return oneLinePreview(f.text ?? '')
}

function guessFrameLanguage(data: string): 'json' | 'text' {
  const trimmed = data.trimStart()
  return (trimmed.startsWith('{') || trimmed.startsWith('[')) ? 'json' : 'text'
}

function oneLinePreview(s: string): string {
  if (!s) return '(empty)'
  const idx = s.indexOf('\n')
  const head = idx === -1 ? s : s.slice(0, idx)
  return head.length > 200 ? head.slice(0, 200) + '…' : head
}

/** Cheap heuristic: if the payload starts with `{` or `[` we treat it as JSON;
 *  otherwise text. Good enough for the expanded inline view. */
function guessEventLanguage(data: string): 'json' | 'text' {
  const trimmed = data.trimStart()
  return (trimmed.startsWith('{') || trimmed.startsWith('[')) ? 'json' : 'text'
}

// ---- Headers table -------------------------------------------------------------------

function HeaderTable({ headers }: { headers: Record<string, string> }) {
  const entries = Object.entries(headers)
  if (entries.length === 0) {
    return <Center h="100%"><Text size="sm" c="dimmed">No headers.</Text></Center>
  }
  return (
    <ScrollArea h="100%">
      <Table verticalSpacing={4} horizontalSpacing="md" striped withRowBorders={false} fz="xs">
        <Table.Tbody>
          {entries.map(([k, v]) => (
            <Table.Tr key={k}>
              <Table.Td style={{ width: '32%' }} ff="var(--mono)" fw={500} c="dimmed">{k}</Table.Td>
              <Table.Td ff="var(--mono)" style={{ wordBreak: 'break-word' }}>{v}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  )
}

// ---- Request view (what was sent) ----------------------------------------------------

function RequestView({ rendered, execution }: { rendered: RenderedRequest | null; execution: ExecutionResult | null }) {
  // Prefer execution data (most recent Send); fall back to rendered (Preview).
  const method = execution?.method ?? rendered?.method
  const url = execution?.url ?? rendered?.url
  const reqHeaders = execution?.requestHeaders ?? rendered?.headers
  const reqBody = execution?.requestBody ?? rendered?.body

  if (!method || !url) {
    return (
      <Center h="100%">
        <Text size="sm" c="dimmed">No request data yet. Click Preview or Send.</Text>
      </Center>
    )
  }

  const wire = formatHttpRequest(method, url, reqHeaders ?? {}, reqBody ?? null)

  return (
    <ScrollArea h="100%" type="auto" scrollbarSize={8}>
      <CodeBlock value={wire} language="http" readOnly />
    </ScrollArea>
  )
}

/** Render a request in the .http / REST Client format:
 *
 *   METHOD url
 *   Header: value
 *
 *   body
 */
function formatHttpRequest(
  method: string,
  url: string,
  headers: Record<string, string>,
  body: string | null,
): string {
  const lines = [`${method.toUpperCase()} ${url}`]
  for (const [k, v] of Object.entries(headers)) lines.push(`${k}: ${v}`)
  if (body && body.length > 0) {
    lines.push('')
    lines.push(body)
  }
  return lines.join('\n')
}

// ---- Cookies view --------------------------------------------------------------------

interface ParsedCookie {
  name: string
  value: string
  size: number
  path?: string
  domain?: string
  expires?: string
  maxAge?: string
  sameSite?: string
  secure: boolean
  httpOnly: boolean
  partitioned: boolean
}

function CookiesView({ cookies }: { cookies: ParsedCookie[] }) {
  if (cookies.length === 0) {
    return <Center h="100%"><Text size="sm" c="dimmed">No Set-Cookie headers.</Text></Center>
  }
  const check = (on: boolean) => on ? <Text size="xs" c="green" fw={600}>✓</Text> : <Text size="xs" c="dimmed">—</Text>
  const cell = (v: string | undefined) => v
    ? <Text size="xs" ff="var(--mono)">{v}</Text>
    : <Text size="xs" c="dimmed">—</Text>
  return (
    <ScrollArea h="100%" type="auto" scrollbars="xy">
      <Table verticalSpacing={6} horizontalSpacing="md" striped withRowBorders={false} fz="xs" style={{ minWidth: 'max-content' }}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Name</Table.Th>
            <Table.Th>Value</Table.Th>
            <Table.Th style={{ textAlign: 'right' }}>Size</Table.Th>
            <Table.Th>Path</Table.Th>
            <Table.Th>Domain</Table.Th>
            <Table.Th>Expires</Table.Th>
            <Table.Th>Max-Age</Table.Th>
            <Table.Th>SameSite</Table.Th>
            <Table.Th style={{ textAlign: 'center' }}>Secure</Table.Th>
            <Table.Th style={{ textAlign: 'center' }}>HttpOnly</Table.Th>
            <Table.Th style={{ textAlign: 'center' }}>Partitioned</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {cookies.map((c, i) => (
            <Table.Tr key={i}>
              <Table.Td ff="var(--mono)" fw={500}>{c.name}</Table.Td>
              <Table.Td ff="var(--mono)" style={{ wordBreak: 'break-all', maxWidth: 280 }} c="dimmed">{c.value}</Table.Td>
              <Table.Td ta="right" c="dimmed" ff="var(--mono)">{c.size}</Table.Td>
              <Table.Td>{cell(c.path)}</Table.Td>
              <Table.Td>{cell(c.domain)}</Table.Td>
              <Table.Td>{cell(c.expires)}</Table.Td>
              <Table.Td>{cell(c.maxAge)}</Table.Td>
              <Table.Td>{cell(c.sameSite)}</Table.Td>
              <Table.Td ta="center">{check(c.secure)}</Table.Td>
              <Table.Td ta="center">{check(c.httpOnly)}</Table.Td>
              <Table.Td ta="center">{check(c.partitioned)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  )
}

/**
 * Parse Set-Cookie header(s) into structured cookies. The server may have joined
 * multiple Set-Cookie values with ", " — split only on commas that begin a new
 * `name=value` pair (skipping commas inside `Expires=…` dates).
 */
function parseSetCookies(headers: Record<string, string> | undefined): ParsedCookie[] {
  if (!headers) return []
  const raw = Object.entries(headers).find(([k]) => k.toLowerCase() === 'set-cookie')?.[1]
  if (!raw) return []
  // Split on `, ` only when followed by a cookie-name token + `=`. Cookie names use
  // RFC 6265 token chars; Expires dates have a space after the comma, then a digit,
  // so they're safely skipped.
  const parts = raw.split(/,\s*(?=[A-Za-z0-9!#$%&'*+\-.^_`|~]+=)/)
  return parts.map(parseOneCookie).filter((c): c is ParsedCookie => c !== null)
}

function parseOneCookie(raw: string): ParsedCookie | null {
  const trimmed = raw.trim()
  if (!trimmed) return null
  const segments = trimmed.split(';').map(s => s.trim())
  const first = segments[0]
  const eq = first.indexOf('=')
  if (eq < 0) return null
  const name = first.slice(0, eq).trim()
  const value = first.slice(eq + 1).trim()
  const cookie: ParsedCookie = {
    name, value,
    size: new TextEncoder().encode(trimmed).length,
    secure: false, httpOnly: false, partitioned: false,
  }
  for (let i = 1; i < segments.length; i++) {
    const seg = segments[i]
    if (!seg) continue
    const sEq = seg.indexOf('=')
    const k = (sEq < 0 ? seg : seg.slice(0, sEq)).trim().toLowerCase()
    const v = sEq < 0 ? '' : seg.slice(sEq + 1).trim()
    switch (k) {
      case 'path': cookie.path = v; break
      case 'domain': cookie.domain = v; break
      case 'expires': cookie.expires = v; break
      case 'max-age': cookie.maxAge = v; break
      case 'samesite': cookie.sameSite = v; break
      case 'secure': cookie.secure = true; break
      case 'httponly': cookie.httpOnly = true; break
      case 'partitioned': cookie.partitioned = true; break
    }
  }
  return cookie
}

// ---- Secrets view --------------------------------------------------------------------

function SecretsView({ rendered, execution }: { rendered: RenderedRequest | null; execution: ExecutionResult | null }) {
  const traces = useMemo(
    () => (execution?.variablesUsed ?? rendered?.variablesUsed ?? []).filter(v => v.isSecret),
    [execution, rendered])

  if (traces.length === 0) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={280}>
          <IconKey size={20} color="var(--mantine-color-dimmed)" />
          <Text size="sm" c="dimmed">No secret references touched.</Text>
        </Stack>
      </Center>
    )
  }
  return (
    <ScrollArea h="100%">
      <Table verticalSpacing={6} horizontalSpacing="md" withRowBorders={false}>
        <Table.Tbody>
          {traces.map((t, i) => (
            <Table.Tr key={i}>
              <Table.Td style={{ width: 110 }}>
                <Badge size="sm" variant="light" color={t.resolved ? 'green' : 'red'}>
                  {t.resolved ? 'resolved' : 'failed'}
                </Badge>
              </Table.Td>
              <Table.Td>
                <Group gap="xs"><Code fz="xs">{t.variableProvider}:{t.name}</Code><Text size="xs" c="dimmed">{t.durationMs.toFixed(1)} ms</Text></Group>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  )
}

// ---- Helpers -------------------------------------------------------------------------

function statusColor(status: number): string {
  if (status === 0 || status >= 500) return 'red'
  if (status >= 400) return 'yellow'
  if (status >= 300) return 'orange'
  if (status >= 200) return 'green'
  return 'gray'
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}

/** Trim a verbose `Content-Type` like `application/json; charset=utf-8` to just `json`. */
function shortContentType(ct: string): string {
  const main = ct.split(';')[0].trim().toLowerCase()
  // application/json → json, application/xml → xml, text/html → html, application/x-www-form-urlencoded → form
  if (main === 'application/x-www-form-urlencoded') return 'form'
  if (main.includes('/')) return main.split('/')[1]!.replace(/^x-/, '')
  return main
}
