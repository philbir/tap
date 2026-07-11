import {
  ActionIcon, Alert, Badge, Box, Button, Center, Code, Group, Loader, Menu, Paper, ScrollArea, Stack, Table, Tabs, Text, Tooltip,
} from '@mantine/core'
import { useClipboard } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconAlertCircle, IconArrowDown, IconArrowRight, IconArrowUp, IconBolt, IconCheck, IconCopy, IconDots, IconDownload, IconExternalLink,
  IconKey, IconLock, IconLockOpen, IconPlayerPlayFilled, IconPlayerStopFilled, IconPlugConnected, IconPlugX, IconRefresh, IconSend, IconTrash, IconX,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api/client'
import type { AuthExecuteResponse, AuthStatus, AuthSummary, ExecutionResult, RenderedRequest, SseEvent, WsFrame } from '../api/types'
import { openLoginUrl } from '../desktop/desktopUpdater'
import { useTapStore } from '../store'
import { CodeBlock } from './CodeBlock'

interface Props {
  rendered: RenderedRequest | null
  execution: ExecutionResult | null
  error: string | null
  busy: boolean
  /** True when the in-flight request was cancelled by the user — flips the streaming
   *  indicator into a "cancelled" marker and softens the empty-body message. */
  stopped?: boolean
  /** Called to abort the in-flight request. Present only while a Send is streaming. */
  onStop?: () => void
  /** Workspace-relative path of the request being executed. Used to resolve relative
   *  auth paths and look up the owning collection. */
  requestPath?: string
  /** Display name of the request — used to build the download filename. */
  requestName?: string
  /** Raw value of `spec.auth` — `undefined`/empty = inherit, `'none'` = explicit opt-out,
   *  anything else = a relative path or `id:` reference. */
  requestAuth?: string | null
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
export function ResponsePanel({ rendered, execution, error, busy, stopped, onStop, requestPath, requestName, requestAuth, onClose }: Props) {
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

  // When the server reports the request needed auth but didn't have a usable token —
  // flip to the Flow tab so the "Run auth" affordance is front-and-center. Tracked per
  // execution so manually navigating away doesn't snap the user back on every re-render.
  const authNudgedRef = useRef<ExecutionResult | null>(null)
  useEffect(() => {
    if (!execution) { authNudgedRef.current = null; return }
    if (authNudgedRef.current === execution) return
    authNudgedRef.current = execution
    const src = execution.authStatus?.source
    if (src === 'missing' || src === 'expired') setTab('flow')
  }, [execution])

  const cookies = useMemo(() => parseSetCookies(execution?.responseHeaders), [execution?.responseHeaders])

  // Which tabs actually exist for the current response. `body` is HTTP-only; `events` /
  // `frames` / `cookies` are conditional. The rest are always present.
  const availableTabs = useMemo(() => {
    const set = new Set(['headers', 'request', 'flow', 'secrets'])
    if (!isWs) set.add('body')
    if (hasSse) set.add('events')
    if (isWs) set.add('frames')
    if (cookies.length > 0) set.add('cookies')
    return set
  }, [isWs, hasSse, cookies.length])

  // Guard against a stale selection: switching from an SSE/WS result to a plain one (or
  // vice-versa) can leave `tab` pointing at a tab that no longer renders, blanking the
  // panel. Snap back to the natural default when that happens.
  useEffect(() => {
    if (tab && !availableTabs.has(tab)) setTab(isWs ? 'frames' : 'body')
  }, [availableTabs, tab, isWs])

  // While busy we still fall through to the tabs IF execution has started — otherwise
  // SSE frames accumulate invisibly until `done` fires (the entire reason the user
  // ran a streaming request was to watch them arrive). Only show the bare loader when
  // we haven't received the `meta` event yet.
  if (busy && !execution) {
    return (
      <Stack h="100%" gap={0}>
        <CompactHeader onClose={onClose} onStop={onStop}>
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
          <Tabs.Tab value="flow" py={6}>Flow</Tabs.Tab>
          <Tabs.Tab value="secrets" py={6}>
            Secrets {secretCount > 0 && <Text component="span" c="dimmed" ml={6}>{secretCount}</Text>}
          </Tabs.Tab>
        </Tabs.List>
        <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
          {execution && <StatusStrip execution={execution} busy={busy} stopped={stopped} />}
          {busy && onStop && (
            <Tooltip label="Stop request" withArrow>
              <ActionIcon variant="light" color="red" size="sm" onClick={onStop} aria-label="Stop request">
                <IconPlayerStopFilled size={14} />
              </ActionIcon>
            </Tooltip>
          )}
          {execution && !busy && <ResultActionsMenu execution={execution} requestName={requestName} />}
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
            <BodyView execution={execution} stopped={stopped} />
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
        <Tabs.Panel value="flow" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <FlowView
            rendered={rendered}
            execution={execution}
            busy={busy}
            requestPath={requestPath}
            requestAuth={requestAuth}
          />
        </Tabs.Panel>
        <Tabs.Panel value="secrets" h="100%">
          <SecretsView rendered={rendered} execution={execution} />
        </Tabs.Panel>
      </Box>
    </Tabs>
  )
}

/** Skinny header with just a Close button — used by the busy / error states so they
 *  match the laid-out tab bar height and offer the same dismiss affordance. When the
 *  request is still in flight (`onStop`), it also exposes a Stop button. */
function CompactHeader({ children, onClose, onStop }: { children?: React.ReactNode; onClose?: () => void; onStop?: () => void }) {
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
      <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
        {onStop && (
          <Button size="compact-xs" variant="light" color="red" leftSection={<IconPlayerStopFilled size={12} />} onClick={onStop}>
            Stop
          </Button>
        )}
        {onClose && (
          <Tooltip label="Close response" withArrow>
            <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose} aria-label="Close response panel">
              <IconX size={14} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>
    </Group>
  )
}

// ---- Status strip --------------------------------------------------------------------

function StatusStrip({ execution, busy, stopped }: { execution: ExecutionResult; busy?: boolean; stopped?: boolean }) {
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
        {stopped && !busy && (
          <Badge size="xs" color="red" variant="light" radius="sm" style={{ textTransform: 'none' }}>cancelled</Badge>
        )}
      </Group>
    </Group>
  )
}

// ---- Result actions menu (copy / download) -------------------------------------------

/** The `⋯` menu in the response header. Copies or downloads whatever the response holds —
 *  the HTTP body, or the accumulated SSE events / WebSocket frames for streaming requests. */
function ResultActionsMenu({ execution, requestName }: { execution: ExecutionResult; requestName?: string }) {
  const clipboard = useClipboard({ timeout: 1500 })
  const text = useMemo(() => resultToText(execution), [execution])
  const downloadable = useMemo(() => isDownloadableImage(execution) || (text != null && text.length > 0), [execution, text])
  const canCopy = text != null && text.length > 0

  function copy() {
    if (text == null) return
    clipboard.copy(text)
    notifications.show({ message: 'Response copied to clipboard', color: 'green', autoClose: 1500 })
  }

  return (
    <Menu shadow="md" position="bottom-end" withinPortal width={200}>
      <Menu.Target>
        <Tooltip label="Result actions" withArrow>
          <ActionIcon variant="subtle" color="gray" size="sm" aria-label="Result actions">
            <IconDots size={16} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Result</Menu.Label>
        <Menu.Item
          leftSection={clipboard.copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
          disabled={!canCopy}
          onClick={copy}
        >
          Copy response
        </Menu.Item>
        <Menu.Item
          leftSection={<IconDownload size={14} />}
          disabled={!downloadable}
          onClick={() => downloadResult(execution, requestName)}
        >
          Download response
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}

/** Flatten a response into copyable/downloadable text. SSE and WebSocket responses have
 *  no single body, so we serialize their accumulated frames into a readable transcript. */
function resultToText(execution: ExecutionResult): string | null {
  if (execution.sseEvents && execution.sseEvents.length > 0) {
    return execution.sseEvents
      .map((e) => {
        const lines: string[] = []
        if (e.event && e.event !== 'message') lines.push(`event: ${e.event}`)
        if (e.id) lines.push(`id: ${e.id}`)
        for (const d of e.data.split('\n')) lines.push(`data: ${d}`)
        return lines.join('\n')
      })
      .join('\n\n')
  }
  if (execution.wsFrames && execution.wsFrames.length > 0) {
    return execution.wsFrames
      .map((f) => {
        const dir = f.direction === 'client' ? '→' : f.direction === 'server' ? '←' : '•'
        const payload = f.text ?? (f.base64 ? `(binary, ${f.size} bytes)` : framePreview(f))
        return `${dir} [${f.type}] ${payload}`
      })
      .join('\n')
  }
  // A `data:` image URL or a `[binary …]` placeholder isn't useful as copy text.
  if (execution.responseBody && (execution.responseBody.startsWith('data:') || execution.responseBody.startsWith('[binary '))) {
    return null
  }
  return execution.responseBody
}

function isDownloadableImage(execution: ExecutionResult): boolean {
  return !!execution.contentType?.toLowerCase().startsWith('image/') && !!execution.responseBody?.startsWith('data:')
}

/** Save the response to disk. Image responses (stored as `data:` URLs) download as the
 *  original binary; everything else writes its text body with an extension guessed from
 *  the content type. Filename is `<request-name>_response_<timestamp>.<ext>`. */
function downloadResult(execution: ExecutionResult, requestName?: string): void {
  const ext = extForContentType(execution.contentType)
  const filename = buildDownloadName(requestName, ext)
  if (isDownloadableImage(execution)) {
    triggerDownload(execution.responseBody!, filename)
    return
  }
  const text = resultToText(execution)
  if (text == null) return
  const blob = new Blob([text], { type: execution.contentType ?? 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  triggerDownload(url, filename)
  // Revoke after the click has had a chance to start the download.
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/** Compose the download filename: `<sanitized request name>_response_<timestamp>.<ext>`,
 *  falling back to `response_<timestamp>.<ext>` when the request has no usable name. */
function buildDownloadName(requestName: string | undefined, ext: string): string {
  const base = sanitizeFilenamePart(requestName ?? '')
  const ts = fileTimestamp()
  return base ? `${base}_response_${ts}.${ext}` : `response_${ts}.${ext}`
}

/** Reduce a request name to filesystem-safe characters: anything outside
 *  `[A-Za-z0-9._-]` becomes `_`, runs collapse, and leading/trailing separators are
 *  trimmed. Capped so a verbose name can't produce an unwieldy filename. */
function sanitizeFilenamePart(name: string): string {
  return name
    .normalize('NFKD')
    .replace(/[^A-Za-z0-9._-]+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^[_.]+|[_.]+$/g, '')
    .slice(0, 80)
}

/** Local wall-clock timestamp as `YYYY-MM-DD_HH-mm-ss` (filename-safe — no colons). */
function fileTimestamp(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`
}

function triggerDownload(href: string, filename: string): void {
  const a = document.createElement('a')
  a.href = href
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
}

/** Guess a sensible file extension from the response content type. SSE/WS transcripts and
 *  unknown types fall back to `.txt`. */
function extForContentType(ct: string | null): string {
  const main = (ct ?? '').split(';')[0].trim().toLowerCase()
  if (main === 'text/event-stream' || main === 'text/plain') return 'txt'
  if (main.includes('json')) return 'json'
  if (main.includes('xml')) return 'xml'
  if (main.includes('html')) return 'html'
  if (main.includes('csv')) return 'csv'
  if (main.includes('markdown')) return 'md'
  if (main === 'application/x-yaml' || main.includes('yaml')) return 'yaml'
  if (main === 'application/javascript' || main === 'text/javascript') return 'js'
  if (main.startsWith('image/')) return main.split('/')[1]!.replace(/^x-/, '').split('+')[0]
  if (main.startsWith('text/')) {
    const sub = main.split('/')[1]!.replace(/^x-/, '')
    return sub === 'plain' || !sub ? 'txt' : sub
  }
  return 'txt'
}

// ---- Body view (CodeMirror) ----------------------------------------------------------

function BodyView({ execution, stopped }: { execution: ExecutionResult | null; stopped?: boolean }) {
  if (!execution) {
    return <Center h="100%"><Text size="sm" c="dimmed">Nothing to show.</Text></Center>
  }
  // Cancelled before the body arrived — say so rather than implying an empty response.
  if (stopped && !execution.responseBody && !execution.error) {
    return (
      <Center h="100%">
        <Stack align="center" gap={4} maw={320} ta="center">
          <IconPlayerStopFilled size={18} color="var(--mantine-color-red-6)" />
          <Text size="sm" c="dimmed">Request cancelled before the response completed.</Text>
        </Stack>
      </Center>
    )
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

// ---- Flow view (auth → request) ------------------------------------------------------

/**
 * Two-node flow diagram showing the auth step feeding into the request step.
 *
 * When the executor has fired and emitted a `meta` event we trust the server's
 * <code>execution.authStatus</code> snapshot — it's authoritative (it ran the same
 * resolution + cache lookup the executor uses). On the Preview path or before any
 * execute, we fall back to a client-side resolution against the store so the user still
 * sees the bound profile.
 */
function FlowView({ rendered, execution, busy, requestPath, requestAuth }: {
  rendered: RenderedRequest | null
  execution: ExecutionResult | null
  busy: boolean
  requestPath?: string
  requestAuth?: string | null
}) {
  const auths = useTapStore((s) => s.auths)
  const collections = useTapStore((s) => s.collections)

  // Server-side authStatus (preferred). Available after a Send/stream meta has fired.
  const serverStatus = execution?.authStatus ?? null

  // Client-side fallback resolution for the Preview / pre-Send state.
  const [collectionDefaultAuth, setCollectionDefaultAuth] = useState<string | null | undefined>(undefined)
  const collectionSlug = useMemo(() => {
    if (!requestPath) return null
    const parts = requestPath.split('/')
    if (parts.length < 3 || parts[0] !== 'collections') return null
    return parts[1]
  }, [requestPath])

  useEffect(() => {
    if (!collectionSlug) { setCollectionDefaultAuth(null); return }
    if (!collections.find((c) => c.slug === collectionSlug)) { setCollectionDefaultAuth(null); return }
    if (requestAuth && requestAuth !== '') return
    let cancelled = false
    api.collectionDetail(collectionSlug)
      .then((d) => { if (!cancelled) setCollectionDefaultAuth(d.defaultAuth) })
      .catch(() => { if (!cancelled) setCollectionDefaultAuth(null) })
    return () => { cancelled = true }
  }, [collectionSlug, requestAuth, collections])

  const fallback = useMemo(() => {
    return resolveEffectiveAuth({
      requestPath: requestPath ?? '',
      requestAuth,
      collectionDefaultAuth,
      auths,
    })
  }, [requestPath, requestAuth, collectionDefaultAuth, auths])

  // Find the matching AuthSummary by path so we can show the user-facing name. The server
  // status only carries the workspace-relative path + type.
  const summaryForStatus = useMemo(() => {
    if (!serverStatus?.path) return null
    return auths.find((a) => a.path === serverStatus.path) ?? null
  }, [serverStatus, auths])

  const method = execution?.method ?? rendered?.method
  const url = execution?.url ?? rendered?.url
  const protocol = execution?.protocol ?? rendered?.protocol ?? 'http'

  return (
    <ScrollArea h="100%" type="auto" scrollbarSize={8}>
      <Box p="lg">
        {/* Auth and Request cards anchored at the top of the row so the Auth card can
            grow downward (run-auth button + helper text + alerts live inside it) without
            stretching the Request card. */}
        <Group align="flex-start" gap={0} wrap="nowrap" justify="center">
          {serverStatus
            ? <AuthFlowCardFromStatus status={serverStatus} summary={summaryForStatus} />
            : <AuthFlowCard resolved={fallback} />}
          <FlowConnector active={!!execution || busy} />
          <RequestFlowCard
            method={method}
            url={url}
            protocol={protocol}
            execution={execution}
            busy={busy}
          />
        </Group>

        {execution && (
          <AuthHeaderHints
            authHeaders={
              serverStatus?.type
                ? authHeadersFor(serverStatus.type)
                : (fallback.kind === 'profile' && fallback.summary ? authHeadersFor(fallback.summary.type) : [])
            }
            requestHeaders={execution.requestHeaders}
          />
        )}
      </Box>
    </ScrollArea>
  )
}

type EffectiveAuth =
  | { kind: 'profile'; summary: AuthSummary | null; path: string; inherited: boolean }
  | { kind: 'none'; inherited: boolean }
  | { kind: 'inherit-empty' } // collection inherits but has no defaultAuth either
  | { kind: 'unknown' }       // still loading

function resolveEffectiveAuth(opts: {
  requestPath: string
  requestAuth: string | null | undefined
  collectionDefaultAuth: string | null | undefined
  auths: AuthSummary[]
}): EffectiveAuth {
  const { requestPath, requestAuth, collectionDefaultAuth, auths } = opts
  // Explicit on the request — wins outright.
  if (requestAuth === 'none') return { kind: 'none', inherited: false }
  if (requestAuth && requestAuth !== '') {
    const resolved = resolveRelativePath(requestPath, requestAuth)
    return {
      kind: 'profile',
      summary: auths.find((a) => a.path === resolved) ?? null,
      path: resolved,
      inherited: false,
    }
  }
  // Inheriting — wait until we know the collection's value (undefined = loading).
  if (collectionDefaultAuth === undefined) return { kind: 'unknown' }
  if (collectionDefaultAuth === null || collectionDefaultAuth === '') return { kind: 'inherit-empty' }
  if (collectionDefaultAuth === 'none') return { kind: 'none', inherited: true }
  // Collection defaultAuth is relative to the collection file (e.g. `_collection.md`).
  const collectionFile = requestPath.split('/').slice(0, 2).join('/') + '/_collection.md'
  const resolved = resolveRelativePath(collectionFile, collectionDefaultAuth)
  return {
    kind: 'profile',
    summary: auths.find((a) => a.path === resolved) ?? null,
    path: resolved,
    inherited: true,
  }
}

/** POSIX-style relative-path resolver against the directory containing `from`. */
function resolveRelativePath(from: string, rel: string): string {
  const fromDir = from.split('/').slice(0, -1)
  const parts = rel.split('/')
  const stack = [...fromDir]
  for (const p of parts) {
    if (p === '..') stack.pop()
    else if (p === '.' || p === '') continue
    else stack.push(p)
  }
  return stack.join('/')
}

function AuthFlowCard({ resolved }: { resolved: EffectiveAuth }) {
  if (resolved.kind === 'unknown') {
    return (
      <FlowCard>
        <Group gap="xs" wrap="nowrap">
          <Loader size="xs" />
          <Text size="xs" c="dimmed">Resolving auth…</Text>
        </Group>
      </FlowCard>
    )
  }
  if (resolved.kind === 'none') {
    return (
      <FlowCard
        title="Auth"
        icon={<IconLockOpen size={16} color="var(--mantine-color-gray-6)" />}
      >
        <Text size="sm" fw={500}>None</Text>
        <Text size="xs" c="dimmed">
          {resolved.inherited ? 'Inherited — collection opts out.' : 'Request opts out.'}
        </Text>
      </FlowCard>
    )
  }
  if (resolved.kind === 'inherit-empty') {
    return (
      <FlowCard
        title="Auth"
        icon={<IconLockOpen size={16} color="var(--mantine-color-gray-6)" />}
      >
        <Text size="sm" fw={500}>No auth</Text>
        <Text size="xs" c="dimmed">Neither the request nor its collection set one.</Text>
      </FlowCard>
    )
  }
  const { summary, path, inherited } = resolved
  if (!summary) {
    return (
      <FlowCard
        title="Auth"
        icon={<IconAlertCircle size={16} color="var(--mantine-color-yellow-7)" />}
      >
        <Text size="sm" fw={500}>Unknown profile</Text>
        <Code fz="xs" c="dimmed">{path}</Code>
      </FlowCard>
    )
  }
  return (
    <FlowCard
      title={inherited ? 'Auth (inherited)' : 'Auth'}
      icon={<IconLock size={16} color="var(--mantine-color-tap-6)" />}
      accent="tap"
    >
      <Text size="sm" fw={600}>{summary.name}</Text>
      <Badge size="xs" variant="light" color="tap" radius="sm" mt={4} style={{ textTransform: 'none' }}>
        {summary.type}
      </Badge>
    </FlowCard>
  )
}

/** Auth card rendered from the server's authoritative <code>AuthStatus</code> snapshot.
 *  Distinguishes cached / expired / missing / static / none + flags interactive flows. */
function AuthFlowCardFromStatus({ status, summary }: { status: AuthStatus; summary: AuthSummary | null }) {
  // No auth attached to the request at all.
  if (status.source === 'none' || !status.type) {
    return (
      <FlowCard title="Auth" icon={<IconLockOpen size={16} color="var(--mantine-color-gray-6)" />}>
        <Text size="sm" fw={500}>No auth</Text>
        <Text size="xs" c="dimmed">Request does not reference an auth profile.</Text>
      </FlowCard>
    )
  }

  const displayName = summary?.name ?? status.path?.split('/').pop() ?? '(unknown)'
  const badgeColor =
    status.source === 'cached' ? 'green'
    : status.source === 'expired' ? 'yellow'
    : status.source === 'missing' ? 'red'
    : 'tap' // static
  const badgeLabel =
    status.source === 'cached' ? 'cached'
    : status.source === 'expired' ? 'expired'
    : status.source === 'missing' ? 'missing'
    : 'static'

  const icon =
    status.source === 'cached'  ? <IconLock size={16} color="var(--mantine-color-green-7)" />
    : status.source === 'expired' ? <IconAlertCircle size={16} color="var(--mantine-color-yellow-7)" />
    : status.source === 'missing' ? <IconAlertCircle size={16} color="var(--mantine-color-red-7)" />
    : <IconLock size={16} color="var(--mantine-color-tap-6)" />

  return (
    <FlowCard
      title="Auth"
      icon={icon}
      accent={status.source === 'cached' || status.source === 'static' ? 'tap' : undefined}
    >
      <Text size="sm" fw={600}>{displayName}</Text>
      <Group gap={6} mt={4} wrap="nowrap">
        <Badge size="xs" variant="light" color="tap" radius="sm" style={{ textTransform: 'none' }}>
          {status.type}
        </Badge>
        <Badge size="xs" variant="light" color={badgeColor} radius="sm" style={{ textTransform: 'none' }}>
          {badgeLabel}
        </Badge>
      </Group>
      {status.expiresAt && (
        <Text size="xs" c="dimmed" mt={4}>
          {status.source === 'expired' ? 'Expired ' : 'Expires '}{formatRelativeExpiry(status.expiresAt)}
        </Text>
      )}
      {status.source !== 'static' && (
        <Box mt="sm">
          <AuthRunPanel key={status.path ?? 'no-path'} status={status} />
        </Box>
      )}
    </FlowCard>
  )
}

/** Format an ISO timestamp as a short relative phrase: "in 23 min" / "5 min ago". */
function formatRelativeExpiry(iso: string): string {
  const target = new Date(iso).getTime()
  const now = Date.now()
  const deltaMs = target - now
  const abs = Math.abs(deltaMs)
  const minutes = Math.round(abs / 60_000)
  const hours = Math.round(abs / 3_600_000)
  const days = Math.round(abs / 86_400_000)
  let phrase: string
  if (abs < 60_000) phrase = 'just now'
  else if (minutes < 60) phrase = `${minutes} min`
  else if (hours < 36) phrase = `${hours} h`
  else phrase = `${days} d`
  return deltaMs >= 0 ? `in ${phrase}` : `${phrase} ago`
}

/**
 * Auth runner UI shown below the flow cards. Drives <code>POST /api/auth/execute</code>
 * and follows whichever interactive sub-flow the server hands back (popup loginUrl or
 * device-code prompt), polling <code>/api/auth/flows/{id}</code> for completion.
 *
 * Mirrors the auth-editor's "Try it" panel but scoped to the Run-auth-then-resend loop:
 * the success state nudges the user to re-send rather than showing token internals.
 */
function AuthRunPanel({ status }: { status: AuthStatus }) {
  const [result, setResult] = useState<AuthExecuteResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [launchMode, setLaunchMode] = useState<'open' | 'copy' | null>(null)
  const [cleared, setCleared] = useState(false)
  const pollRef = useRef<number | null>(null)
  const clipboard = useClipboard({ timeout: 2000 })

  const stopPolling = useCallback(() => {
    if (pollRef.current !== null) { window.clearInterval(pollRef.current); pollRef.current = null }
  }, [])

  const pollOnce = useCallback(async (flowId: string) => {
    try {
      const r = await api.authFlow(flowId)
      setResult((prev) => ({
        ...r,
        loginUrl: r.loginUrl ?? prev?.loginUrl ?? null,
        userCode: r.userCode ?? prev?.userCode ?? null,
        verificationUri: r.verificationUri ?? prev?.verificationUri ?? null,
        verificationUriComplete: r.verificationUriComplete ?? prev?.verificationUriComplete ?? null,
      }))
      if (r.status !== 'pending') stopPolling()
    } catch { /* keep polling */ }
  }, [stopPolling])

  // postMessage from the OAuth callback popup — same protocol the auth editor uses.
  useEffect(() => {
    const onMessage = (ev: MessageEvent) => {
      const data = ev.data as { type?: string; state?: string } | undefined
      if (data?.type !== 'tap-auth-callback') return
      if (data.state) void pollOnce(data.state)
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [pollOnce])

  useEffect(() => () => stopPolling(), [stopPolling])

  async function run(force: boolean) {
    if (!status.path) return
    setBusy(true); setError(null); setResult(null); setLaunchMode(null); setCleared(false)
    stopPolling()
    try {
      const r = await api.executeAuth(status.path, force)
      setResult(r)
      if (r.status === 'pending' && r.flowId) {
        pollRef.current = window.setInterval(() => { void pollOnce(r.flowId!) }, 800)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  async function clear() {
    if (!status.path) return
    setBusy(true); setError(null); setResult(null); setLaunchMode(null)
    stopPolling()
    try {
      await api.clearAuthToken(status.path)
      setCleared(true)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  function openAuthWindow(loginUrl: string) {
    openLoginUrl(loginUrl)
    setLaunchMode('open')
  }
  function copyLoginUrl(loginUrl: string) {
    clipboard.copy(loginUrl)
    setLaunchMode('copy')
  }

  const runLabel =
    status.source === 'expired' ? 'Refresh auth'
    : status.source === 'missing' ? 'Run auth'
    : 'Re-run auth' // cached path — user explicitly forces

  return (
    <Stack gap="xs">
      {/* Action row — primary button + secondary icons. */}
      <Group gap="xs" wrap="wrap">
        <Button
          size="xs"
          leftSection={<IconPlayerPlayFilled size={12} />}
          onClick={() => run(status.source === 'cached')}
          loading={busy}
          disabled={busy}
          color={status.source === 'cached' ? 'gray' : 'tap'}
          variant={status.source === 'cached' ? 'default' : 'filled'}
        >
          {runLabel}
        </Button>
        {status.source === 'cached' && (
          <Tooltip label="Force re-authentication">
            <ActionIcon variant="default" size="md" onClick={() => run(true)} disabled={busy} aria-label="Force re-auth">
              <IconRefresh size={14} />
            </ActionIcon>
          </Tooltip>
        )}
        {(status.source === 'cached' || status.source === 'expired') && (
          <Tooltip label="Clear cached token (~/.tap/auth-tokens.json)">
            <ActionIcon variant="default" size="md" color="red" onClick={clear} disabled={busy} aria-label="Clear cached token">
              <IconTrash size={14} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      {/* Helper text for the missing/expired cases — sets expectations before the user clicks. */}
      {(status.source === 'missing' || status.source === 'expired') && !result && !busy && !error && (
        <Text size="xs" c="dimmed">
          {status.interactive
            ? 'Sign-in window will open or a device code will be shown. Send the request again after it completes.'
            : 'Acquires a token. Send the request again after it completes.'}
        </Text>
      )}

      {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}><Text size="xs">{error}</Text></Alert>}

      {cleared && (
        <Alert color="gray" variant="light" icon={<IconTrash size={14} />}>
          <Text size="xs">Cached token cleared. Click <strong>Run auth</strong> to acquire a fresh one, then Send the request.</Text>
        </Alert>
      )}

      {/* Device code — RFC 8628 — show the code + verification URL. */}
      {result?.status === 'pending' && result.userCode && (
        <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
          <Stack gap={6}>
            <Text size="sm">Enter this code on the verification URL:</Text>
            <Code fw={700} fz="md" style={{ letterSpacing: 2 }}>{result.userCode}</Code>
            {result.verificationUriComplete ? (
              <Text size="xs"><a href={result.verificationUriComplete} target="_blank" rel="noopener noreferrer">{result.verificationUriComplete}</a></Text>
            ) : result.verificationUri ? (
              <Text size="xs"><a href={result.verificationUri} target="_blank" rel="noopener noreferrer">{result.verificationUri}</a></Text>
            ) : null}
            <Text size="xs" c="dimmed">Polling for completion…</Text>
          </Stack>
        </Alert>
      )}

      {/* Authorization-code chooser — pick popup vs copy. */}
      {result?.status === 'pending' && !result.userCode && result.loginUrl && launchMode === null && (
        <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
          <Stack gap="xs">
            <Text size="sm" fw={500}>Sign in to continue</Text>
            <Text size="xs" c="dimmed">
              Open the auth window here, or copy the URL and paste it into another browser. We'll keep listening for the callback.
            </Text>
            <Group gap="xs" mt={4}>
              <Button size="xs" leftSection={<IconExternalLink size={12} />} onClick={() => openAuthWindow(result.loginUrl!)}>
                Open auth window
              </Button>
              <Button
                size="xs"
                variant="default"
                leftSection={clipboard.copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
                onClick={() => copyLoginUrl(result.loginUrl!)}
              >
                {clipboard.copied ? 'Copied' : 'Copy URL'}
              </Button>
            </Group>
          </Stack>
        </Alert>
      )}

      {/* Post-launch waiting state. */}
      {result?.status === 'pending' && !result.userCode && launchMode !== null && (
        <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
          <Text size="sm">
            {launchMode === 'open'
              ? 'Waiting for sign-in… (the popup should be open)'
              : 'URL copied — waiting for sign-in in your other browser…'}
          </Text>
        </Alert>
      )}

      {/* Success — token acquired in this session. */}
      {result?.status === 'completed' && (
        <Alert color="green" variant="light" icon={<IconCheck size={14} />}>
          <Text size="sm">Auth completed. Click <strong>Send</strong> to re-run the request with the new token.</Text>
        </Alert>
      )}

      {/* Failure from the runner (token endpoint rejection, user denial, etc.) */}
      {result?.status === 'failed' && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
          <Text size="sm">{result.error ?? 'Auth failed.'}</Text>
        </Alert>
      )}
    </Stack>
  )
}

function RequestFlowCard({ method, url, protocol, execution, busy }: {
  method: string | undefined
  url: string | undefined
  protocol: 'http' | 'websocket'
  execution: ExecutionResult | null
  busy: boolean
}) {
  if (!method || !url) {
    return (
      <FlowCard
        title="Request"
        icon={<IconSend size={16} color="var(--mantine-color-gray-6)" />}
      >
        <Text size="xs" c="dimmed">No request yet — click Preview or Send.</Text>
      </FlowCard>
    )
  }
  const isWs = protocol === 'websocket'
  return (
    <FlowCard
      title="Request"
      icon={isWs
        ? <IconBolt size={16} color="var(--mantine-color-tap-6)" />
        : <IconSend size={16} color="var(--mantine-color-tap-6)" />}
      accent="tap"
    >
      <Group gap={6} wrap="nowrap">
        <Badge size="xs" variant="filled" color="tap" radius="sm" style={{ textTransform: 'none', fontFamily: 'var(--mono)' }}>
          {isWs ? 'WS' : method}
        </Badge>
        <Text size="xs" ff="var(--mono)" lineClamp={1} style={{ flex: 1, minWidth: 0 }} title={url}>
          {url}
        </Text>
      </Group>
      {execution && (
        <Group gap={6} wrap="nowrap" mt={6}>
          <Badge size="xs" color={statusColor(execution.status)} variant="filled" radius="sm">
            {execution.status || '—'}
          </Badge>
          <Text size="xs" c="dimmed">
            {busy
              ? 'streaming…'
              : `${execution.durationMs.toFixed(0)} ms · ${formatBytes(execution.responseBodyBytes)}`}
          </Text>
        </Group>
      )}
    </FlowCard>
  )
}

function FlowCard({ title, icon, accent, children }: {
  title?: string
  icon?: React.ReactNode
  accent?: 'tap'
  children: React.ReactNode
}) {
  return (
    <Paper
      withBorder
      p="md"
      radius="md"
      style={{
        minWidth: 220,
        maxWidth: 400,
        flex: '1 1 0',
        borderColor: accent === 'tap' ? 'var(--mantine-color-tap-4)' : undefined,
      }}
    >
      {title && (
        <Group gap={6} mb={6} wrap="nowrap">
          {icon}
          <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{title}</Text>
        </Group>
      )}
      {children}
    </Paper>
  )
}

function FlowConnector({ active }: { active: boolean }) {
  // Vertical offset puts the arrow at the title-row baseline of both cards: card padding
  // 16px (p="md") + roughly half the title icon row (~22/2). Since the row uses
  // align="flex-start", we have to nudge manually — otherwise the connector sticks to the
  // top of the row and reads as disconnected from the card body.
  const TOP_OFFSET = 24
  return (
    <Box
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        padding: '0 12px', position: 'relative',
        paddingTop: TOP_OFFSET,
      }}
    >
      <Box
        style={{
          height: 2, width: 56,
          backgroundColor: active ? 'var(--mantine-color-tap-5)' : 'var(--mantine-color-default-border)',
          borderRadius: 1,
        }}
      />
      <IconArrowRight
        size={20}
        stroke={2.2}
        style={{
          position: 'absolute', right: 6, top: TOP_OFFSET - 9,
          color: active ? 'var(--mantine-color-tap-5)' : 'var(--mantine-color-dimmed)',
        }}
      />
    </Box>
  )
}

function AuthHeaderHints({ authHeaders, requestHeaders }: {
  authHeaders: string[]
  requestHeaders: Record<string, string>
}) {
  const matched = useMemo(() => {
    const keys = Object.keys(requestHeaders).reduce<Record<string, string>>((acc, k) => {
      acc[k.toLowerCase()] = k
      return acc
    }, {})
    return authHeaders
      .map((h) => keys[h.toLowerCase()])
      .filter((k): k is string => !!k)
  }, [authHeaders, requestHeaders])

  if (matched.length === 0) return null
  return (
    <Stack gap={4} mt="md" maw={720} mx="auto">
      <Text size="xs" c="dimmed">Headers contributed by auth</Text>
      <Group gap={6} wrap="wrap">
        {matched.map((k) => (
          <Badge key={k} size="sm" variant="light" color="tap" radius="sm" ff="var(--mono)" style={{ textTransform: 'none' }}>
            {k}
          </Badge>
        ))}
      </Group>
    </Stack>
  )
}

/** Known headers each auth type tends to add — used to highlight the auth's
 *  contribution in the request the user sees. */
function authHeadersFor(type: string): string[] {
  switch (type) {
    case 'basic':
    case 'bearer':
    case 'oauth2':
    case 'azure-cli':
    case 'jwt':
    case 'github':
      return ['Authorization']
    case 'apiKey':
      return ['Authorization', 'X-Api-Key', 'X-API-Key']
    case 'aws-sigv4':
      return ['Authorization', 'X-Amz-Date', 'X-Amz-Security-Token', 'X-Amz-Content-Sha256']
    default:
      return []
  }
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
