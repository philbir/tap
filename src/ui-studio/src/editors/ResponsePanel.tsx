import {
  ActionIcon, Alert, Badge, Box, Button, Center, Code, Group, Loader, Menu, Paper, ScrollArea, SegmentedControl, Stack, Table, Tabs, Text, Tooltip,
} from '@mantine/core'
import { useClipboard } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconAlertCircle, IconArrowDown, IconArrowRight, IconArrowUp, IconBolt, IconCheck, IconCircleCheck, IconCircleCheckFilled, IconCircleMinus, IconCircleXFilled, IconCode, IconCopy, IconDots, IconDownload, IconExternalLink, IconEye,
  IconHistory, IconIndentIncrease, IconKey, IconLock, IconLockOpen, IconPlayerPlayFilled, IconPlayerStopFilled, IconPlugConnected, IconPlugX, IconRefresh, IconSearch, IconSend, IconTrash, IconX,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api/client'
import type { AssertResult, AssertSummary, AuthExecuteResponse, AuthStatus, AuthSummary, ExecutionResult, RenderedRequest, SseEvent, WsFrame } from '../api/types'
import { BrowserPicker, useBrowserLaunch } from './BrowserPicker'
import { useTapStore } from '../store'
import { CodeBlock, detectLanguage } from './CodeBlock'
import { TabCount } from './EditorShell'
import type { CodeSearchSpec } from './codeSearch'
import { ResultSearchBar } from './ResultSearchBar'
import { compileSearch, EMPTY_RESULT_SEARCH, Highlighted, matchesAny, type ResultMatcher, type ResultSearchState } from './resultSearch'
import { RequestErrorCard } from './RequestErrorCard'
import { useTabView } from './useTabView'
import { COLLECTION_FILE } from '../shell/tapFiles'

/**
 * What "search" means on each sub-tab. `find` walks matches inside one document and gets
 * step arrows; `filter` narrows a list of rows and gets a survivor count. Tabs missing from
 * the map (asserts, flow, secrets) are short, structured views with nothing to sift.
 */
const SEARCH_MODE: Record<string, 'find' | 'filter'> = {
  body: 'find',
  request: 'find',
  events: 'filter',
  frames: 'filter',
  headers: 'filter',
  cookies: 'filter',
}

interface Props {
  /** Tab the panel belongs to. Keys the sticky sub-tab selection, so flipping to Headers
   *  and back to another tab doesn't land you on Body again. Note this is the *tab*, not
   *  `requestPath` — a `.http` file's panel changes request as you send, but it is one tab. */
  tabPath: string
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
  /** Set when the pane is showing a recorded exchange rather than a live one. Carries when it
   *  was recorded, so nobody mistakes an entry from Tuesday for the response they just got. */
  replayedAt?: string | null
  /** Whether that recorded entry was redacted. Only meaningful alongside `replayedAt`; it is
   *  what tells the reader whether `***` means "masked" or "that is what was sent". */
  replayRedacted?: boolean
  /** Open a TLS diagnosis for this request. Offered from the error card when a send died on
   *  certificate validation — the one moment the chain the server sent is worth reading. */
  onDiagnoseTls?: () => void
  /** True while that diagnosis is in flight. */
  diagnosingTls?: boolean
  /** Jump the editor to its Transport settings — where certificate validation and the
   *  timeout live. Absent for editors that have no such tab (a `.http` file). */
  onOpenTransport?: () => void
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
export function ResponsePanel({ tabPath, rendered, execution, error, busy, stopped, onStop, requestPath, requestName, requestAuth, onClose, replayedAt, replayRedacted, onDiagnoseTls, diagnosingTls, onOpenTransport }: Props) {
  const sseEvents = execution?.sseEvents
  const hasSse = !!sseEvents && sseEvents.length > 0
  const wsFrames = execution?.wsFrames
  // WebSocket execution doesn't have a meaningful HTTP body, so default the active tab
  // to Frames for ws requests. SSE keeps its existing auto-switch behavior on first event.
  const isWs = execution?.protocol === 'websocket' || rendered?.protocol === 'websocket'
  const [tab, setTab] = useTabView<string | null>(tabPath, 'responseTab', isWs ? 'frames' : 'body')

  // The three auto-switches below react to something *arriving*. Each therefore starts from
  // "whatever is already here has been seen": the panel now remounts onto a response the user
  // may have been reading for a while, and re-firing would yank them off the tab they chose.
  //
  // First time SSE frames appear, snap the user to the Events tab — they almost
  // certainly want to watch them stream in.
  const lastSeenCountRef = useRef<number | null>(null)
  useEffect(() => {
    const count = sseEvents?.length ?? 0
    const prev = lastSeenCountRef.current
    lastSeenCountRef.current = count
    if (prev !== null && count > 0 && prev === 0) setTab('events')
  }, [sseEvents, setTab])

  // Same idea for ws frames — flip to the Frames tab when the first one arrives.
  const lastWsCountRef = useRef<number | null>(null)
  useEffect(() => {
    const count = wsFrames?.length ?? 0
    const prev = lastWsCountRef.current
    lastWsCountRef.current = count
    if (prev !== null && count > 0 && prev === 0) setTab('frames')
  }, [wsFrames, setTab])

  // A send that never reached the server has exactly one thing to show, and it lives on the
  // Body tab. Land there — otherwise a failure that arrives while you are reading Headers or
  // Cookies renders as an empty list, which looks like "no response" rather than "it failed".
  const failNudgedRef = useRef<ExecutionResult | null | undefined>(undefined)
  useEffect(() => {
    if (!execution) { failNudgedRef.current = null; return }
    if (failNudgedRef.current === execution) return
    const remounted = failNudgedRef.current === undefined
    failNudgedRef.current = execution
    if (remounted) return
    // A WebSocket panel has no Body tab; its failures belong with the frame timeline.
    if (execution.error) setTab(isWs ? 'frames' : 'body')
  }, [execution, isWs, setTab])

  // When the server reports the request needed auth but didn't have a usable token —
  // flip to the Flow tab so the "Run auth" affordance is front-and-center. Tracked per
  // execution so manually navigating away doesn't snap the user back on every re-render.
  const authNudgedRef = useRef<ExecutionResult | null | undefined>(undefined)
  useEffect(() => {
    if (!execution) { authNudgedRef.current = null; return }
    if (authNudgedRef.current === execution) return
    // `undefined` means this is the panel's first look — the execution was already on screen
    // before the remount, so it isn't news either.
    const remounted = authNudgedRef.current === undefined
    authNudgedRef.current = execution
    if (remounted) return
    // A send that died in transport is not an auth problem, whatever the token cache said.
    if (execution.error) return
    const src = execution.authStatus?.source
    if (src === 'missing' || src === 'expired') setTab('flow')
  }, [execution, setTab])

  const cookies = useMemo(() => parseSetCookies(execution?.responseHeaders), [execution?.responseHeaders])

  // ---- Body view: raw bytes vs rendered page ---------------------------------------------
  // HTML is the one common content type where the bytes and the thing they describe are
  // different artifacts: a 200 that renders "your session expired" is one glance as a page
  // and a scroll-hunt as markup. The choice is parked on the tab so it survives a tab switch,
  // and it defaults to raw — this is a request client, and the body is the primary evidence.
  const [bodyMode, setBodyMode] = useTabView<BodyMode>(tabPath, 'bodyMode', 'raw')
  const canPreviewHtml = !!execution && isPreviewableHtml(execution)
  const previewing = tab === 'body' && canPreviewHtml && bodyMode === 'preview'

  // ---- Body view: formatted vs raw -------------------------------------------------------
  // JSON and XML are re-indented for reading, which is right nearly always and wrong exactly
  // when the bytes themselves are the question — a whitespace-sensitive signature, a body you
  // are diffing against what curl printed, an "is this really one line?" check. This defaults
  // to formatted (what the panel has always done to JSON) and parks the choice on the tab.
  const [bodyFormat, setBodyFormat] = useTabView<BodyFormat>(tabPath, 'bodyFormat', 'formatted')
  const canFormatBody = !!execution && isFormattableBody(execution)

  // ---- Find in result ------------------------------------------------------------------
  // One query for the whole panel. It is parked on the tab (not on this mount) so it
  // survives a tab switch the same way the sub-tab selection does, and it is deliberately
  // *not* cleared on a new Send — "watch this field across runs" is the common case.
  const [search, setSearch] = useTabView<ResultSearchState>(tabPath, 'resultSearch', EMPTY_RESULT_SEARCH)
  const [activeMatch, setActiveMatch] = useState(0)
  const [findCount, setFindCount] = useState(0)
  const searchInputRef = useRef<HTMLInputElement>(null)

  // A rendered preview is an iframe, not a document we can index — there is nothing for
  // find-in-result to step through, so the affordance goes away with it.
  const searchMode = previewing ? null : tab ? SEARCH_MODE[tab] ?? null : null
  const { matcher: compiled, error: searchError } = useMemo(() => compileSearch(search), [search])
  // Closing the bar keeps the query (so re-opening restores it) but stops it acting on
  // anything — otherwise a hidden filter would silently be hiding rows.
  const matcher = search.open && searchMode ? compiled : null

  const filteredEvents = useMemo(() => {
    const all = sseEvents ?? []
    return matcher ? all.filter((e) => matchesAny(matcher, e.event, e.id, e.data)) : all
  }, [sseEvents, matcher])

  const filteredFrames = useMemo(() => {
    const all = wsFrames ?? []
    return matcher
      ? all.filter((f) => matchesAny(matcher, f.type, f.direction, f.text, f.closeDescription, f.closeStatus?.toString()))
      : all
  }, [wsFrames, matcher])

  const headerEntries = useMemo(
    () => Object.entries(execution?.responseHeaders ?? {}),
    [execution?.responseHeaders])
  const filteredHeaders = useMemo(
    () => matcher ? headerEntries.filter(([k, v]) => matchesAny(matcher, k, v)) : headerEntries,
    [headerEntries, matcher])

  const filteredCookies = useMemo(
    () => matcher ? cookies.filter((c) => matchesAny(matcher, c.name, c.value, c.domain, c.path, c.sameSite)) : cookies,
    [cookies, matcher])

  // A new query, a new response or a different tab all invalidate "which match am I on".
  useEffect(() => { setActiveMatch(0) }, [search.query, search.regex, search.caseSensitive, tab, execution])

  // Derived rather than stored, because the match count arrives from the editor a beat after
  // the query changes — clamping the state instead would fight the reset above.
  const activeIndex = findCount > 0 ? Math.min(activeMatch, findCount - 1) : -1

  const stepMatch = useCallback((delta: 1 | -1) => {
    setActiveMatch((cur) => {
      if (findCount === 0) return 0
      return (Math.min(cur, findCount - 1) + delta + findCount) % findCount
    })
  }, [findCount])

  // One-shot: true only for the render that opens the bar, so a bar restored by switching
  // back to this tab doesn't yank focus out of whatever the user was typing in.
  const wantSearchFocus = useRef(false)
  useEffect(() => { wantSearchFocus.current = false })

  const openSearch = useCallback(() => {
    // Already open: re-select, so a second Mod+F replaces the query rather than appending.
    if (search.open) { searchInputRef.current?.select(); return }
    wantSearchFocus.current = true
    setSearch({ ...search, open: true })
  }, [search, setSearch])

  // Mod+F has to be claimed on `window`, not on the panel's React tree: clicking a response
  // row doesn't move focus, so the keystroke's target is usually `<body>` and a scoped
  // handler would never see it. Only one editor is mounted at a time, so exactly one panel
  // is ever listening. Capture phase both beats the browser's own find bar and keeps the
  // event away from CodeMirror's search keymap — one find bar for the panel, not two.
  const panelRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!searchMode) return
    const onKey = (e: KeyboardEvent) => {
      if (!(e.metaKey || e.ctrlKey) || e.altKey || e.key.toLowerCase() !== 'f') return
      const root = panelRef.current
      if (!root) return
      const target = e.target as HTMLElement | null
      // Someone typing in the URL bar or a header field keeps their own Mod+F.
      if (!(target && root.contains(target)) && isEditable(target)) return
      e.preventDefault()
      e.stopPropagation()
      openSearch()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [searchMode, openSearch])

  // `find` documents get the active-match emphasis; the payloads expanded inside a filtered
  // list get plain highlighting, since stepping through them has no meaning there.
  const findSpec = useMemo<CodeSearchSpec | null>(
    () => matcher && searchMode === 'find' ? { source: matcher.source, flags: matcher.flags, active: activeIndex } : null,
    [matcher, searchMode, activeIndex])
  const listSpec = useMemo<CodeSearchSpec | null>(
    () => matcher && searchMode === 'filter' ? { source: matcher.source, flags: matcher.flags, active: -1 } : null,
    [matcher, searchMode])

  /** "12 of 40" for whichever list tab is showing. */
  const filterCounts = useMemo(() => {
    switch (tab) {
      case 'events': return { count: filteredEvents.length, total: (sseEvents ?? []).length }
      case 'frames': return { count: filteredFrames.length, total: (wsFrames ?? []).length }
      case 'headers': return { count: filteredHeaders.length, total: headerEntries.length }
      case 'cookies': return { count: filteredCookies.length, total: cookies.length }
      default: return { count: 0, total: 0 }
    }
  }, [tab, filteredEvents, filteredFrames, filteredHeaders, filteredCookies, sseEvents, wsFrames, headerEntries, cookies])

  // Which tabs actually exist for the current response. `body` is HTTP-only; `events` /
  // `frames` / `cookies` are conditional. The rest are always present.
  const assertResults = execution?.assertions ?? []
  const assertSummary = execution?.assertSummary ?? null

  const availableTabs = useMemo(() => {
    const set = new Set(['headers', 'request', 'flow', 'secrets'])
    if (!isWs) set.add('body')
    if (hasSse) set.add('events')
    if (isWs) set.add('frames')
    if (cookies.length > 0) set.add('cookies')
    if (assertResults.length > 0) set.add('asserts')
    return set
  }, [isWs, hasSse, cookies.length, assertResults.length])

  // Guard against a stale selection: switching from an SSE/WS result to a plain one (or
  // vice-versa) can leave `tab` pointing at a tab that no longer renders, blanking the
  // panel. Snap back to the natural default when that happens.
  useEffect(() => {
    if (tab && !availableTabs.has(tab)) setTab(isWs ? 'frames' : 'body')
  }, [availableTabs, tab, isWs, setTab])

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
    <Tabs
      ref={panelRef}
      value={tab}
      onChange={setTab}
      keepMounted={false}
      variant="default"
      style={{ display: 'flex', flexDirection: 'column', height: '100%' }}
    >
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
              Events <TabCount count={sseEvents!.length} active={tab === 'events'} />
            </Tabs.Tab>
          )}
          {isWs && (
            <Tabs.Tab value="frames" py={6} leftSection={<IconBolt size={12} />}>
              Frames <TabCount count={wsFrames?.length ?? 0} active={tab === 'frames'} />
            </Tabs.Tab>
          )}
          <Tabs.Tab value="headers" py={6}>
            Headers <TabCount count={headerCount} active={tab === 'headers'} />
          </Tabs.Tab>
          {cookies.length > 0 && (
            <Tabs.Tab value="cookies" py={6}>
              Cookies <TabCount count={cookies.length} active={tab === 'cookies'} />
            </Tabs.Tab>
          )}
          {assertResults.length > 0 && (
            <Tabs.Tab value="asserts" py={6} leftSection={<IconCircleCheck size={12} />}>
              Asserts
              {assertSummary && (
                <Text component="span" ml={6} c={assertSummary.failed > 0 ? 'red' : 'green'}>
                  {assertSummary.failed > 0
                    ? `${assertSummary.failed} failed`
                    : `${assertSummary.passed}/${assertSummary.passed + assertSummary.failed}`}
                </Text>
              )}
            </Tabs.Tab>
          )}
          <Tabs.Tab value="request" py={6}>Request</Tabs.Tab>
          <Tabs.Tab value="flow" py={6}>Flow</Tabs.Tab>
          <Tabs.Tab value="secrets" py={6}>
            Secrets <TabCount count={secretCount} active={tab === 'secrets'} />
          </Tabs.Tab>
        </Tabs.List>
        <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
          {tab === 'body' && canPreviewHtml && (
            <SegmentedControl
              size="xs"
              value={bodyMode}
              onChange={(v) => setBodyMode(v as BodyMode)}
              data={[
                {
                  value: 'raw',
                  label: (
                    <Tooltip label="Response body" withArrow>
                      <IconCode size={14} style={{ display: 'block' }} />
                    </Tooltip>
                  ),
                },
                {
                  value: 'preview',
                  label: (
                    <Tooltip label="Render as a page (sandboxed: no scripts, no remote assets)" withArrow>
                      <IconEye size={14} style={{ display: 'block' }} />
                    </Tooltip>
                  ),
                },
              ]}
            />
          )}
          {tab === 'body' && canFormatBody && (
            <SegmentedControl
              size="xs"
              value={bodyFormat}
              onChange={(v) => setBodyFormat(v as BodyFormat)}
              data={[
                {
                  value: 'formatted',
                  label: (
                    <Tooltip label="Formatted — indent and wrap the body" withArrow>
                      <IconIndentIncrease size={14} style={{ display: 'block' }} />
                    </Tooltip>
                  ),
                },
                {
                  value: 'raw',
                  label: (
                    <Tooltip label="Raw — exactly the bytes the server sent" withArrow>
                      <IconCode size={14} style={{ display: 'block' }} />
                    </Tooltip>
                  ),
                },
              ]}
            />
          )}
          {replayedAt && <ReplayChip at={replayedAt} redacted={replayRedacted} />}
          {execution && <StatusStrip execution={execution} busy={busy} stopped={stopped} />}
          {busy && onStop && (
            <Tooltip label="Stop request" withArrow>
              <ActionIcon variant="light" color="red" size="sm" onClick={onStop} aria-label="Stop request">
                <IconPlayerStopFilled size={14} />
              </ActionIcon>
            </Tooltip>
          )}
          {execution && searchMode && (
            <Tooltip label={search.open ? 'Hide search' : 'Search this response'} withArrow>
              <ActionIcon
                variant={search.open ? 'light' : 'subtle'}
                color={search.open ? 'tap' : 'gray'}
                size="sm"
                onClick={() => search.open ? setSearch({ ...search, open: false }) : openSearch()}
                aria-label="Search this response"
                aria-pressed={search.open}
              >
                <IconSearch size={14} />
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

      {search.open && searchMode && (
        <ResultSearchBar
          ref={searchInputRef}
          value={search}
          onChange={setSearch}
          onClose={() => setSearch({ ...search, open: false })}
          mode={searchMode}
          count={searchMode === 'find' ? findCount : filterCounts.count}
          total={searchMode === 'filter' ? filterCounts.total : undefined}
          active={activeIndex < 0 ? 0 : activeIndex}
          onStep={stepMatch}
          error={searchError}
          autoFocus={wantSearchFocus.current}
        />
      )}

      <Box style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
        {!isWs && (
          <Tabs.Panel value="body" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <BodyView
              execution={execution}
              stopped={stopped}
              requestName={requestName}
              preview={previewing}
              format={bodyFormat === 'formatted'}
              search={findSpec}
              onSearchCount={setFindCount}
              onDiagnoseTls={onDiagnoseTls}
              diagnosingTls={diagnosingTls}
              onOpenTransport={onOpenTransport}
            />
          </Tabs.Panel>
        )}
        {hasSse && (
          <Tabs.Panel value="events" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <EventsView
              events={filteredEvents}
              total={sseEvents!.length}
              busy={busy}
              matcher={matcher}
              search={listSpec}
            />
          </Tabs.Panel>
        )}
        {isWs && (
          <Tabs.Panel value="frames" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <FramesView
              frames={filteredFrames}
              total={(wsFrames ?? []).length}
              busy={busy}
              matcher={matcher}
              search={listSpec}
              error={execution?.error ?? null}
              onDiagnoseTls={onDiagnoseTls}
              diagnosingTls={diagnosingTls}
              onOpenTransport={onOpenTransport}
            />
          </Tabs.Panel>
        )}
        {cookies.length > 0 && (
          <Tabs.Panel value="cookies" h="100%">
            <CookiesView cookies={filteredCookies} total={cookies.length} matcher={matcher} />
          </Tabs.Panel>
        )}
        <Tabs.Panel value="headers" h="100%">
          <HeaderTable entries={filteredHeaders} total={headerEntries.length} matcher={matcher} />
        </Tabs.Panel>
        {assertResults.length > 0 && (
          <Tabs.Panel value="asserts" h="100%">
            <AssertResultsView results={assertResults} summary={assertSummary} />
          </Tabs.Panel>
        )}
        <Tabs.Panel value="request" h="100%" style={{ display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <RequestView rendered={rendered} execution={execution} search={findSpec} onSearchCount={setFindCount} />
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
/**
 * Marks the pane as showing something recorded rather than something that just happened.
 *
 * <p>Without it a stored exchange is indistinguishable from a live one, which is the single
 * most dangerous confusion this feature can cause — acting on a body from Tuesday as though the
 * endpoint returned it a second ago. The redaction state rides along because it changes what the
 * masks mean: <c>***</c> in a redacted entry hides a value, and in an unredacted one it is the
 * value.</p>
 */
function ReplayChip({ at, redacted }: { at: string; redacted?: boolean }) {
  const when = new Date(at)
  const label = Number.isNaN(when.getTime()) ? at : when.toLocaleString()
  return (
    <Tooltip
      label={redacted === false
        ? `Recorded ${label} — stored unredacted and encrypted at rest.`
        : `Recorded ${label} — secrets were masked before it was written.`}
      withArrow
      multiline
      w={280}
    >
      <Badge size="sm" variant="light" color="grape" leftSection={<IconHistory size={11} />}>
        From history
      </Badge>
    </Tooltip>
  )
}

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
  const downloadable = useMemo(
    () => !!execution.bodyId || isDownloadableImage(execution) || (text != null && text.length > 0),
    [execution, text])
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
          {wasTruncated(execution) ? 'Download full response' : 'Download response'}
        </Menu.Item>
        {!canCopy && !downloadable && (
          <Menu.Label>Nothing to save — this result has no response body.</Menu.Label>
        )}
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

/** True when the panel is showing a prefix rather than the whole body. Only then is the
 *  download offering something more than what is on screen — a retained body also backs a
 *  binary response that fit inline, and calling that one "full" would promise a difference
 *  there isn't. */
function wasTruncated(execution: ExecutionResult): boolean {
  return !!execution.bodyId && execution.responseBodyBytes > (execution.responseBodyInlineBytes ?? 0)
}

function isDownloadableImage(execution: ExecutionResult): boolean {
  return !!execution.contentType?.toLowerCase().startsWith('image/') && !!execution.responseBody?.startsWith('data:')
}

/** Save the response to disk. A body the server held back is streamed from it, so what
 *  lands on disk is the whole response rather than the prefix the panel rendered. Image
 *  responses (stored as `data:` URLs) download as the original binary; everything else
 *  writes its text body with an extension guessed from the content type. Filename is
 *  `<request-name>_response_<timestamp>.<ext>`. */
function downloadResult(execution: ExecutionResult, requestName?: string): void {
  const ext = extForContentType(execution.contentType)
  const filename = buildDownloadName(requestName, ext)
  if (execution.bodyId) {
    downloadRetainedBody(execution, requestName)
    return
  }
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

/** Stream the server's retained copy straight to disk. Deliberately an anchor to the API
 *  rather than a fetch-then-Blob: the body is by definition larger than what we were willing
 *  to hold in the page, and buffering it into memory to hand it back is how a download of a
 *  200 MB response takes the tab down with it. */
function downloadRetainedBody(execution: ExecutionResult, requestName?: string): void {
  if (!execution.bodyId) return
  const filename = buildDownloadName(requestName, extForContentType(execution.contentType))
  triggerDownload(api.responseBodyUrl(execution.bodyId, filename), filename)
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
  // No content type at all means a transcript we assembled ourselves (SSE / WebSocket
  // frames), which is text. A content type we didn't recognize as text is binary — and
  // since a retained body downloads verbatim, calling that file `.txt` mislabels it.
  return main ? 'bin' : 'txt'
}

// ---- Body view (CodeMirror) ----------------------------------------------------------

function BodyView({ execution, stopped, requestName, preview, format, search, onSearchCount, onDiagnoseTls, diagnosingTls, onOpenTransport }: {
  execution: ExecutionResult | null
  stopped?: boolean
  requestName?: string
  /** Render the body as a page instead of as markup. Only ever true for HTML — the header's
   *  toggle is the only thing that sets it, and it only appears for previewable responses. */
  preview?: boolean
  /** Re-indent a JSON or XML body. False shows the wire bytes — see the header's toggle. */
  format?: boolean
  search: CodeSearchSpec | null
  onSearchCount: (count: number) => void
  onDiagnoseTls?: () => void
  diagnosingTls?: boolean
  onOpenTransport?: () => void
}) {
  // A longer prefix pulled from the server's retained copy, once the user asks for it.
  // Keyed to the execution it came from so a new Send never shows the previous body.
  const [expanded, setExpanded] = useState<{ source: ExecutionResult; text: string; bytes: number } | null>(null)
  const [expanding, setExpanding] = useState(false)
  const [expandError, setExpandError] = useState<string | null>(null)

  useEffect(() => { setExpanded(null); setExpandError(null) }, [execution])

  // Most of the states below never reach a CodeBlock (cancelled, error, image, binary), so
  // nothing would report a count and the bar would keep showing the previous body's total.
  const searchable = !!execution?.responseBody
    && !execution.error
    && !execution.contentType?.toLowerCase().includes('text/event-stream')
    && !execution.responseBody.startsWith('[binary ')
    && !(execution.contentType?.toLowerCase().startsWith('image/') && execution.responseBody.startsWith('data:'))
    && !preview
  useEffect(() => { if (!searchable) onSearchCount(0) }, [searchable, onSearchCount])

  const shown = expanded?.source === execution ? expanded : null

  const expandTo = useCallback(async (bodyId: string, max: number, source: ExecutionResult) => {
    setExpanding(true); setExpandError(null)
    try {
      const more = await api.responseBodyText(bodyId, max)
      setExpanded({ source, text: more.text ?? '', bytes: more.inlineBytes })
    } catch (e) {
      setExpandError(e instanceof Error ? e.message : String(e))
    } finally {
      setExpanding(false)
    }
  }, [])

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
      <RequestErrorCard
        message={execution.error}
        onDiagnoseTls={onDiagnoseTls}
        diagnosing={diagnosingTls}
        onOpenTransport={onOpenTransport}
      />
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

  // Image preview — server hands us a `data:image/...;base64,...` URL. A truncated one
  // renders as a broken image, which is exactly when the download offer matters most, so
  // the notice rides above it; there is nothing useful to "show all" of.
  if (execution.contentType?.toLowerCase().startsWith('image/') && execution.responseBody.startsWith('data:')) {
    return (
      <Stack h="100%" gap={0} style={{ minHeight: 0 }}>
        <TruncationNotice execution={execution} requestName={requestName} expandable={false} busy={false} error={null} onShowAll={() => {}} />
        <Center h="100%" p="md" style={{ overflow: 'auto', flex: 1, minHeight: 0 }}>
          <img
            src={execution.responseBody}
            alt={execution.contentType}
            style={{ maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', boxShadow: '0 1px 4px rgba(0,0,0,0.12)' }}
          />
        </Center>
      </Stack>
    )
  }

  // Binary placeholder (server returns "[binary N bytes — …]") — render plainly. Same
  // reasoning as the image case: no preview to expand, but the bytes are still downloadable.
  if (execution.responseBody.startsWith('[binary ')) {
    return (
      <Stack h="100%" gap={0} style={{ minHeight: 0 }}>
        <TruncationNotice execution={execution} requestName={requestName} expandable={false} busy={false} error={null} onShowAll={() => {}} />
        <Center h="100%" style={{ flex: 1, minHeight: 0 }}>
          <Stack align="center" gap="xs" maw={320} ta="center">
            <Text size="sm" c="dimmed" ff="var(--mono)">{execution.responseBody}</Text>
            <Text size="xs" c="dimmed">No text preview available for this content type.</Text>
          </Stack>
        </Center>
      </Stack>
    )
  }

  const body = shown?.text ?? execution.responseBody

  if (preview) {
    return (
      <Stack h="100%" gap={0} style={{ minHeight: 0 }}>
        <TruncationNotice
          execution={execution}
          requestName={requestName}
          shownBytes={shown?.bytes}
          busy={expanding}
          error={expandError}
          onShowAll={(bodyId, max) => void expandTo(bodyId, max, execution)}
        />
        <HtmlPreview html={body} />
      </Stack>
    )
  }

  return (
    <Stack h="100%" gap={0} style={{ minHeight: 0 }}>
      <TruncationNotice
        execution={execution}
        requestName={requestName}
        shownBytes={shown?.bytes}
        busy={expanding}
        error={expandError}
        onShowAll={(bodyId, max) => void expandTo(bodyId, max, execution)}
      />
      <ScrollArea h="100%" type="auto" scrollbarSize={8} style={{ flex: 1, minHeight: 0 }}>
        <CodeBlock
          value={body}
          contentType={execution.contentType}
          readOnly
          format={format}
          search={search}
          onSearchCount={onSearchCount}
        />
      </ScrollArea>
    </Stack>
  )
}

// ---- HTML preview --------------------------------------------------------------------

type BodyMode = 'raw' | 'preview'
type BodyFormat = 'formatted' | 'raw'

/** Whether the Body tab should offer the formatted/raw toggle at all. Only the two content
 *  types we can re-indent, and only when a CodeBlock is what ends up rendering them — an SVG
 *  is `+xml` but reaches the viewer as an image, and there is nothing to indent in an error,
 *  a binary placeholder, or an empty body. */
function isFormattableBody(execution: ExecutionResult): boolean {
  if (execution.error || !execution.responseBody) return false
  if (execution.responseBody.startsWith('[binary ')) return false
  const ct = execution.contentType?.toLowerCase() ?? ''
  if (ct.includes('text/event-stream')) return false
  if (ct.startsWith('image/') && execution.responseBody.startsWith('data:')) return false
  const lang = detectLanguage(execution.contentType)
  return lang === 'json' || lang === 'xml'
}

/** Whether the Body tab should offer the raw/rendered toggle at all. Errors, binary
 *  placeholders and empty bodies have nothing to render, whatever the header claimed. */
function isPreviewableHtml(execution: ExecutionResult): boolean {
  if (execution.error || !execution.responseBody) return false
  if (execution.responseBody.startsWith('[binary ')) return false
  const main = execution.contentType?.split(';')[0].trim().toLowerCase()
  return main === 'text/html' || main === 'application/xhtml+xml'
}

/**
 * Renders a response body as the page it describes.
 *
 * <p>The body is untrusted — it came from whatever host the request named — so a debugging
 * tool that ran its scripts inside the Studio's origin would be handing that host the
 * workspace. Two things keep that from happening, and both are worth knowing about because
 * together they decide what the preview can show.</p>
 *
 * <p>The frame is fully sandboxed: no `allow-scripts`, no `allow-same-origin`. And a
 * `srcdoc` document inherits its parent's CSP, so the Studio's own policy
 * (`src/ui-studio/index.html`) governs the page inside — which means remote stylesheets,
 * images and fonts do not load either, only inline styles and `data:` assets. What you get
 * is the document the server sent, not the page it would become in a browser. That is the
 * right trade for an error page, a login redirect, or HTML that arrived where JSON was
 * expected, which is what HTML in an API client almost always is — but it does mean a
 * styled page can render bare, so the strip below says so rather than leaving it a mystery.</p>
 */
function HtmlPreview({ html }: { html: string }) {
  return (
    <>
      <Box style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
        <iframe
          title="Rendered response"
          srcDoc={html}
          sandbox=""
          referrerPolicy="no-referrer"
          style={{
            display: 'block',
            width: '100%',
            height: '100%',
            border: 'none',
            // The page brings its own colours. Without pinning the scheme, a dark Studio has
            // an unstyled document painting light text onto the frame's white canvas.
            colorScheme: 'light',
            background: '#fff',
          }}
        />
      </Box>
      <Group
        gap={6}
        px="xs"
        py={4}
        wrap="nowrap"
        style={{ flexShrink: 0, borderTop: '1px solid var(--mantine-color-default-border)' }}
      >
        <IconLock size={12} style={{ flexShrink: 0, color: 'var(--mantine-color-dimmed)' }} />
        <Text size="xs" c="dimmed">
          Scripts and remote assets are blocked — inline styles still apply.
        </Text>
      </Group>
    </>
  )
}

/**
 * The strip above a body that didn't fit. It exists because "…truncated" on its own is a
 * dead end: the bytes were gone and the only way to see them was to send the request again,
 * which for anything that charges money or changes state is not an option.
 *
 * Now the server keeps a longer copy (up to the workspace's `response.maxRetainedBytes`),
 * so there are two honest offers to make — render more of it here, or hand the whole thing
 * to disk. When even the retained copy is a prefix, say so rather than implying the download
 * is complete.
 */
function TruncationNotice({ execution, requestName, shownBytes, expandable = true, busy, error, onShowAll }: {
  execution: ExecutionResult
  requestName?: string
  shownBytes?: number
  /** False for previews with nothing to expand — an image, a binary placeholder. The
   *  download still applies: those are exactly the bodies you want whole. */
  expandable?: boolean
  busy: boolean
  error: string | null
  onShowAll: (bodyId: string, max: number) => void
}) {
  const inline = execution.responseBodyInlineBytes ?? 0
  const total = execution.responseBodyBytes
  const retained = Math.max(execution.retainedBytes ?? inline, inline)
  const shown = shownBytes ?? inline

  // `inline === 0` covers WebSocket results and errors, where the byte count describes
  // something other than a body we cut short.
  if (inline === 0 || total <= shown) return null

  const bodyId = execution.bodyId
  const canShowMore = expandable && !!bodyId && retained > shown
  const droppedBeyondRetained = total > retained

  return (
    <Alert
      color="yellow"
      variant="light"
      radius={0}
      p="xs"
      icon={<IconAlertCircle size={16} />}
      styles={{ body: { minWidth: 0 } }}
      style={{ flexShrink: 0, borderBottom: '1px solid var(--mantine-color-default-border)' }}
    >
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Text size="xs" style={{ minWidth: 0 }}>
          Showing the first {formatBytes(shown)} of {formatBytes(total)}.
          {droppedBeyondRetained
            ? ` ${formatBytes(retained)} was kept — raise response.maxRetainedBytes in the workspace to keep more.`
            : ''}
          {error ? ` ${error}` : ''}
        </Text>
        <Group gap="xs" wrap="nowrap" style={{ flexShrink: 0 }}>
          {canShowMore && (
            <Button
              size="compact-xs"
              variant="light"
              loading={busy}
              onClick={() => onShowAll(bodyId!, retained)}
            >
              Show all ({formatBytes(retained)})
            </Button>
          )}
          {bodyId && (
            <Button
              size="compact-xs"
              variant="subtle"
              leftSection={<IconDownload size={12} />}
              onClick={() => downloadRetainedBody(execution, requestName)}
            >
              Download {droppedBeyondRetained ? formatBytes(retained) : 'full response'}
            </Button>
          )}
        </Group>
      </Group>
    </Alert>
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
function EventsView({ events, total, busy, matcher, search }: {
  /** Already filtered by the panel's query — `total` is the count before filtering. */
  events: SseEvent[]
  total: number
  busy: boolean
  matcher: ResultMatcher | null
  search: CodeSearchSpec | null
}) {
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
          <Text size="sm" c="dimmed">
            {total > 0 ? `No events match — ${total} hidden.` : 'Waiting for the first event…'}
          </Text>
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
                  <Highlighted text={preview} matcher={matcher} />
                </Text>
              </Group>
              {open && (
                <Box px="md" pb="sm" onClick={(e) => e.stopPropagation()}>
                  <CodeBlock
                    value={ev.data}
                    language={guessEventLanguage(ev.data)}
                    readOnly
                    search={search}
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
function FramesView({ frames, total, busy, matcher, search, error, onDiagnoseTls, diagnosingTls, onOpenTransport }: {
  /** Already filtered by the panel's query — `total` is the count before filtering. */
  frames: WsFrame[]
  total: number
  busy: boolean
  matcher: ResultMatcher | null
  search: CodeSearchSpec | null
  /** Set when the connection never opened. Takes the pane, since "waiting for the
   *  handshake…" is the one thing that is definitely not happening. */
  error?: string | null
  onDiagnoseTls?: () => void
  diagnosingTls?: boolean
  onOpenTransport?: () => void
}) {
  const viewportRef = useRef<HTMLDivElement>(null)
  const [openSeq, setOpenSeq] = useState<number | null>(null)

  useEffect(() => {
    if (!busy) return
    const el = viewportRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [frames.length, busy])

  if (frames.length === 0 && total === 0 && error) {
    return (
      <RequestErrorCard
        message={error}
        onDiagnoseTls={onDiagnoseTls}
        diagnosing={diagnosingTls}
        onOpenTransport={onOpenTransport}
      />
    )
  }
  if (frames.length === 0) {
    return (
      <Center h="100%">
        <Stack align="center" gap="xs" maw={320} ta="center">
          <IconPlugConnected size={20} color="var(--mantine-color-dimmed)" />
          <Text size="sm" c="dimmed">
            {total > 0 ? `No frames match — ${total} hidden.` : 'Waiting for the WebSocket handshake…'}
          </Text>
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
                  <Highlighted text={preview} matcher={matcher} />
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
                    search={search}
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

// ---- Assertion results ---------------------------------------------------------------

/**
 * The verdict list for a run. Passing rows stay quiet — a name and a tick — because the
 * only thing worth reading on a green run is that it was green. Failing rows spell out
 * what was expected, what actually arrived, and the reason when there is one beyond a
 * plain mismatch (a body that wasn't JSON, an expression that matched three nodes).
 */
function AssertResultsView({ results, summary }: { results: AssertResult[]; summary: AssertSummary | null }) {
  if (results.length === 0) {
    return <Center h="100%"><Text size="sm" c="dimmed">This request declares no assertions.</Text></Center>
  }

  return (
    <ScrollArea h="100%">
      <Box p="md">
        {summary && (
          <Group gap="xs" mb="sm" align="center">
            <Badge
              variant="light"
              color={summary.failed > 0 ? 'red' : summary.passed > 0 ? 'green' : 'gray'}
            >
              {summary.failed > 0 ? `${summary.failed} failed` : 'all passed'}
            </Badge>
            <Text size="xs" c="dimmed">
              {summary.passed} passed
              {summary.failed > 0 && ` · ${summary.failed} failed`}
              {summary.skipped > 0 && ` · ${summary.skipped} skipped`}
            </Text>
          </Group>
        )}

        <Stack gap={6}>
          {results.map((result) => (
            <Paper key={result.index} withBorder p="xs" radius="sm">
              <Group gap="xs" align="flex-start" wrap="nowrap">
                <Box mt={2} style={{ display: 'flex', flexShrink: 0 }}>
                  {result.skipped
                    ? <Text c="dimmed" component="span" style={{ display: 'flex' }}><IconCircleMinus size={16} /></Text>
                    : result.ok
                      ? <Text c="green" component="span" style={{ display: 'flex' }}><IconCircleCheckFilled size={16} /></Text>
                      : <Text c="red" component="span" style={{ display: 'flex' }}><IconCircleXFilled size={16} /></Text>}
                </Box>

                <Box style={{ minWidth: 0, flex: 1 }}>
                  <Text size="sm" ff="var(--mono)" style={{ wordBreak: 'break-word' }}>
                    {result.name}
                  </Text>

                  {!result.ok && !result.skipped && (
                    <Stack gap={2} mt={4}>
                      {result.expected !== null && (
                        <Text size="xs" c="dimmed">
                          expected <Code>{result.expected}</Code>
                        </Text>
                      )}
                      <Text size="xs" c="dimmed">
                        got {result.actual === null
                          ? <Text component="span" fs="italic">nothing</Text>
                          : <Code>{result.actual}</Code>}
                      </Text>
                      {result.message && <Text size="xs" c="red">{result.message}</Text>}
                    </Stack>
                  )}

                  {result.skipped && result.message && (
                    <Text size="xs" c="dimmed" mt={2}>{result.message}</Text>
                  )}
                </Box>
              </Group>
            </Paper>
          ))}
        </Stack>
      </Box>
    </ScrollArea>
  )
}

// ---- Headers table -------------------------------------------------------------------

function HeaderTable({ entries, total, matcher }: {
  /** Already filtered by the panel's query — `total` is the count before filtering. */
  entries: [string, string][]
  total: number
  matcher: ResultMatcher | null
}) {
  if (entries.length === 0) {
    return (
      <Center h="100%">
        <Text size="sm" c="dimmed">{total > 0 ? `No headers match — ${total} hidden.` : 'No headers.'}</Text>
      </Center>
    )
  }
  return (
    <ScrollArea h="100%">
      <Table verticalSpacing={4} horizontalSpacing="md" striped withRowBorders={false} fz="xs">
        <Table.Tbody>
          {entries.map(([k, v]) => (
            <Table.Tr key={k}>
              <Table.Td style={{ width: '32%' }} ff="var(--mono)" fw={500} c="dimmed">
                <Highlighted text={k} matcher={matcher} />
              </Table.Td>
              <Table.Td ff="var(--mono)" style={{ wordBreak: 'break-word' }}>
                <Highlighted text={v} matcher={matcher} />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  )
}

// ---- Request view (what was sent) ----------------------------------------------------

function RequestView({ rendered, execution, search, onSearchCount }: {
  rendered: RenderedRequest | null
  execution: ExecutionResult | null
  search: CodeSearchSpec | null
  onSearchCount: (count: number) => void
}) {
  // Prefer execution data (most recent Send); fall back to rendered (Preview).
  const method = execution?.method ?? rendered?.method
  const url = execution?.url ?? rendered?.url
  const reqHeaders = execution?.requestHeaders ?? rendered?.headers
  const reqBody = execution?.requestBody ?? rendered?.body

  const empty = !method || !url
  useEffect(() => { if (empty) onSearchCount(0) }, [empty, onSearchCount])

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
      <CodeBlock value={wire} language="http" readOnly search={search} onSearchCount={onSearchCount} />
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

function CookiesView({ cookies, total, matcher }: {
  /** Already filtered by the panel's query — `total` is the count before filtering. */
  cookies: ParsedCookie[]
  total: number
  matcher: ResultMatcher | null
}) {
  if (cookies.length === 0) {
    return (
      <Center h="100%">
        <Text size="sm" c="dimmed">{total > 0 ? `No cookies match — ${total} hidden.` : 'No Set-Cookie headers.'}</Text>
      </Center>
    )
  }
  const check = (on: boolean) => on ? <Text size="xs" c="green" fw={600}>✓</Text> : <Text size="xs" c="dimmed">—</Text>
  const cell = (v: string | undefined) => v
    ? <Text size="xs" ff="var(--mono)"><Highlighted text={v} matcher={matcher} /></Text>
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
              <Table.Td ff="var(--mono)" fw={500}><Highlighted text={c.name} matcher={matcher} /></Table.Td>
              <Table.Td ff="var(--mono)" style={{ wordBreak: 'break-all', maxWidth: 280 }} c="dimmed">
                <Highlighted text={c.value} matcher={matcher} />
              </Table.Td>
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
            ? <AuthFlowCardFromStatus
                status={serverStatus}
                summary={summaryForStatus}
                env={execution?.env ?? rendered?.env ?? undefined}
              />
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
  // Collection defaultAuth is relative to the collection file (e.g. `_collection.tap`).
  const collectionFile = `${requestPath.split('/').slice(0, 2).join('/')}/${COLLECTION_FILE}`
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
 *  Distinguishes cached / expired / missing / static / none + flags interactive flows.
 *  `env` is the environment this render actually resolved under — handed to the runner so
 *  the flow mints its token under the same key the send will read it from. */
function AuthFlowCardFromStatus({ status, summary, env }: {
  status: AuthStatus
  summary: AuthSummary | null
  env?: string
}) {
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
          <AuthRunPanel
            key={`${status.path ?? 'no-path'}@${env ?? ''}`}
            status={status}
            env={env}
          />
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
function AuthRunPanel({ status, env }: {
  status: AuthStatus
  env?: string
}) {
  const [result, setResult] = useState<AuthExecuteResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [launchMode, setLaunchMode] = useState<'open' | 'copy' | null>(null)
  const [cleared, setCleared] = useState(false)
  const pollRef = useRef<number | null>(null)
  const clipboard = useClipboard({ timeout: 2000 })
  const { browsers, pref, setPref, openLogin } = useBrowserLaunch()

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
      // The token is cached per env, so running the flow under a different one than the send
      // resolved to would mint an entry the send never reads.
      const r = await api.executeAuth(status.path, force, { env })
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
    void openLogin(loginUrl).catch((e) => setError(e instanceof Error ? e.message : String(e)))
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
            <BrowserPicker browsers={browsers} pref={pref} onChange={setPref} />
            <Group gap="xs" mt={4}>
              <Button size="xs" leftSection={<IconExternalLink size={12} />} onClick={() => openAuthWindow(result.loginUrl!)}>
                {pref.browser ? 'Open in browser' : 'Open auth window'}
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

/** Would this element swallow a keystroke as text input? */
function isEditable(el: HTMLElement | null): boolean {
  if (!el) return false
  const tag = el.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable
}

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
