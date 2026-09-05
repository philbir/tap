import {
  Alert, Badge, ActionIcon, Box, Button, Code, Group, Loader, NumberInput, SegmentedControl, Select, Stack, Tabs, TagsInput, Text, TextInput, Tooltip,
} from '@mantine/core'
import { Dropzone } from '@mantine/dropzone'
import {
  IconAlertTriangle, IconBolt, IconBraces, IconCode, IconCircleCheck, IconExternalLink, IconFile, IconFileCode, IconFileText, IconFlag, IconHistory, IconList, IconLock, IconParentheses, IconPlayerPlayFilled, IconRotateClockwise, IconShieldCheck, IconSparkles, IconUpload, IconVariable, IconX,
} from '@tabler/icons-react'
import { useDisclosure } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, type AssertResponseSnapshot } from '../api/client'
import type {
  AssertResult, AssertSummary, ExecutionResult, HistoryEntry, HttpHeaderSpec, RequestDetail,
  RequestSpec, TlsDiagnosis, VariableContext,
} from '../api/types'
import { useEffectiveEnv, useEnvsFor, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import {
  BODY_MODE_LABELS, contentTypeForBodyMode, contentTypeOrigin, detectBodyMode, detectRawSubType, looksLikeGraphql,
  looksLikeSoap, parseFormBody, parseGraphQLBody, parseMultipartBody, parseSoapBody,
  serializeFormBody, serializeGraphQLBody, serializeMultipartBody, serializeSoapBody, tryPrettyJson,
  RAW_SUB_LABELS, type BodyMode, type RawSubType,
} from './body-mode'
import { AdaptiveTabsList } from './AdaptiveTabsList'
import { CollectionLinkChip, effectiveBaseUrl } from './CollectionLinkChip'
import { methodTextColor } from './methodColor'
import { authSelectGroups, relativizeFrom } from './authOptions'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount, TabDot } from './EditorShell'
import { HistoryPanel } from './HistoryPanel'
import { HistorySettings } from './HistorySettings'
import { GraphQLEditor } from './GraphQLEditor'
import { SoapEditor } from './SoapEditor'
import { AssertsPanel } from './AssertsPanel'
import { KvTable, type KvRow } from './KvTable'
import { MultipartTable } from './MultipartTable'
import { RawBodyEditor } from './RawBodyEditor'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { ResponsePanel } from './ResponsePanel'
import { SourceTab } from './SourceTab'
import { TlsDiagnosisModal } from './TlsDiagnosisModal'
import { restoreDraft, usePublishDraft } from './useDraft'
import { useTabView } from './useTabView'
import { moveExecution, useExecution } from './useExecution'
import { joinUrl, splitUrl } from './url-utils'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'
import { AssistantPane } from '../features/assistant/AssistantPane'
import { fileNameFor, isHttpBackedRequest, splitHttpFragment, stripTapSuffix } from '../shell/tapFiles'

interface Props { path: string }

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] as const
const BODY_MODES: BodyMode[] = ['none', 'form-urlencoded', 'multipart', 'raw', 'binary', 'graphql', 'soap']
const RAW_SUB_TYPES: RawSubType[] = ['json', 'text', 'xml']
/** Header-name suggestions for a request whose `Content-Type` is pinned above the list —
 *  offering it here too would only invite a duplicate. When there is no pinned row the list
 *  owns the header, and the full set applies. */
const HEADER_NAMES_WITHOUT_CONTENT_TYPE = COMMON_HEADER_NAMES.filter((n) => n !== 'Content-Type')

export function RequestEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const collections = useTapStore((s) => s.collections)
  const auths = useTapStore((s) => s.auths)
  const openTab = useTapStore((s) => s.openTab)
  const renameTab = useTapStore((s) => s.renameTab)
  const clearDraft = useTapStore((s) => s.clearDraft)
  const reload = useTapStore((s) => s.reload)
  const tagSuggestions = useTagDictionary()

  const [detail, setDetail] = useState<RequestDetail | null>(null)
  const [spec, setSpec] = useState<RequestSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<RequestSpec | null>(null)
  const [tab, setTab] = useTabView<string | null>(path, 'tab', 'params')
  const [saving, setSaving] = useState(false)
  // Set while the user is entering `Content-Type` as an ordinary row in the Headers tab. See
  // `showContentTypeRow` — the pinned row steps aside for as long as it lasts.
  const [ctInList, setCtInList] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  // Sending, and everything the response pane renders. Shared with the .http editor, which
  // sends the same way from its own request list. Keyed by tab path, so the response — and a
  // stream still arriving — outlives a trip to another tab.
  const {
    rendered, execution, error: actionError, sending, stopped,
    send: startSend, stop, clear: clearExecution, setError: setActionError,
  } = useExecution(path)
  const [diagnosis, setDiagnosis] = useState<TlsDiagnosis | null>(null)
  const [diagnosing, setDiagnosing] = useState(false)
  const [varsOpened, varsCtl] = useDisclosure(false)
  const [assistantOpened, assistantCtl] = useDisclosure(false)
  // Verdicts from re-checking the current assertions against the response already on
  // screen. Lets someone shape an assertion and watch it flip without re-sending.
  const [liveAsserts, setLiveAsserts] = useState<{ results: AssertResult[]; summary: AssertSummary } | null>(null)
  // The assertions the last Send actually evaluated — re-checking those would just
  // recompute what the server already told us.
  const sentAssertionsRef = useRef<string>('')
  // A recorded exchange the user opened from the History tab. Kept apart from the live
  // execution so picking one doesn't overwrite the response you just got — and so closing it
  // puts the live one straight back.
  const [replay, setReplay] = useState<HistoryEntry | null>(null)
  const [historyCount, setHistoryCount] = useState(0)
  // Which entry is open, as tab state rather than local state. Two things need that: the
  // sidebar timeline, which selects an entry in a tab whose editor isn't mounted yet, and a
  // tab switch, which unmounts this editor and would otherwise drop the selection.
  const [openEntryId, setOpenEntryId] = useTabView<string | null>(path, 'historyEntry', null)
  // The id we last asked the server for. Guards the fetch effect against re-running for an
  // entry that is already open — or that failed to open, which is not worth retrying on
  // every `generation` bump.
  const requestedEntryRef = useRef<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.request(path).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d, path)
      // `restoreDraft` keeps unsaved edits across a tab switch and across the re-fetch a
      // `generation` bump forces; `savedSpec` is always what is actually on disk.
      setSpec(restoreDraft(path, initial)); setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [path, generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])
  usePublishDraft(path, spec, dirty)

  /**
   * This request lives inside a `.http` file, which has no spec form on disk: the raw text is
   * the document and Tap never rewrites it. So this editor can *show* the request — reading it
   * is exactly what the parser is for — but it cannot write one back, and a draft spec has
   * nowhere to be emitted to. Saving and draft-sending are therefore turned off here and the
   * header points at the `.http` editor, which does both on the raw text.
   */
  const httpBacked = useMemo(() => isHttpBackedRequest(path), [path])
  const httpFilePath = useMemo(() => splitHttpFragment(path).path, [path])

  function update<K extends keyof RequestSpec>(key: K, value: RequestSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
  }

  async function save() {
    if (!spec) return
    setSaving(true); setError(null)
    let saved: { id: string }
    try {
      saved = await api.saveRequestSpec(spec)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      setSaving(false)
      return
    }

    // A request created without an id gets one from the server. Adopting it here rather than
    // waiting for the watcher-driven refetch is what lets a just-created request be sent
    // straight away and still land in history — the send carries the draft spec, and a draft
    // with no id has nothing durable to file the exchange under.
    const stored: RequestSpec = spec.id === saved.id ? spec : { ...spec, id: saved.id }
    if (stored !== spec) setSpec(stored)

    // The spec is on disk from here on, so the draft has done its job. Dropping it here
    // rather than leaving it to the publish effect matters for the rename branch below:
    // that one re-keys the tab and unmounts this editor, so the effect never gets to run
    // and a stale draft would come back as a phantom dirty marker on the renamed tab.
    clearDraft(path)

    // Renaming the file that holds it is a separate, best-effort step — a collision on the
    // target name must not leave the editor looking unsaved, so it reports through a
    // notification instead of the save error bar.
    const label = stored.name || basename(stored.path)
    try {
      // When the request was renamed, keep the on-disk filename in step with the new
      // name (the explorer shows the spec name, so a stale filename would only surface
      // in git / the filesystem view). Falls back to a no-op when the slug is unchanged
      // or empty.
      const renamedPath = await syncFilenameToName(stored, savedSpec)
      if (renamedPath && renamedPath !== stored.path) {
        const moved = { ...stored, path: renamedPath }
        // Rename before the reload: the tab (and the editor keyed off it) has to follow the
        // file to its new path in the same commit the explorer learns about the move, or the
        // editor refetches a path that no longer exists. The response moves first — dropping
        // the old tab is what prunes its entry.
        moveExecution(stored.path, renamedPath)
        renameTab(stored.path, renamedPath, label)
        setSpec(moved); setSavedSpec(moved)
        await reload()
      } else {
        // Same file, possibly a new name — the tab still carries the old label.
        renameTab(stored.path, stored.path, label)
        setSavedSpec(stored)
      }
    } catch (e) {
      renameTab(stored.path, stored.path, label)
      setSavedSpec(stored)
      notifications.show({
        color: 'yellow',
        title: 'Saved, but the file kept its name',
        message: e instanceof ApiError ? e.message : String(e),
      })
    } finally { setSaving(false) }
  }

  /** If the request's name changed this session, rename the underlying `*.req.tap` file to
   *  a slug derived from the new name (kept in the same folder). Returns the new path, or
   *  null when no rename is needed (slug unchanged / empty). */
  async function syncFilenameToName(next: RequestSpec, prev: RequestSpec | null): Promise<string | null> {
    if (!prev || prev.name === next.name) return null
    const slug = nameToSlug(next.name)
    if (!slug) return null
    const dir = next.path.includes('/') ? next.path.slice(0, next.path.lastIndexOf('/')) : ''
    const currentBase = stripTapSuffix(basename(next.path))
    if (slug === currentBase) return null
    const targetPath = dir ? `${dir}/${fileNameFor('req', slug)}` : fileNameFor('req', slug)
    await api.moveItem(next.path, targetPath)
    return targetPath
  }

  // The request's owning collection is purely positional: every request lives under
  // `collections/<slug>/...`, so its collection is whatever sits at the same slug.
  const linkedCollection = useMemo(() => {
    const parts = path.split('/')
    if (parts.length < 3 || parts[0] !== 'collections') return null
    const slug = parts[1]
    return collections.find((c) => c.slug === slug) ?? null
  }, [collections, path])

  // The environment this request resolves under: the collection's own choice when it has
  // one, else the workspace default. Unlike the stage override it replaced, this is a
  // property of the collection rather than of the tab — every request under `demo` follows
  // the same pick, and it survives a reload.
  const slug = linkedCollection?.slug ?? null
  const env = useEffectiveEnv(slug)
  const setCollectionEnv = useTapStore((s) => s.setCollectionEnv)

  // The chip only earns its place when a base URL is actually going to be prepended. Two
  // things take that away, and both leave it stating a prefix the send won't use: a URL that
  // carries its own scheme (`WorkspaceRenderer` skips the join entirely for those), and a
  // collection with no baseUrl configured and no environment supplying one. In either case
  // hide it and give the width to the URL — the header's picker still switches the
  // environment, which continues to feed variables and auth.
  const envs = useEnvsFor(slug)
  const showCollectionChip = useMemo(() => {
    if (!linkedCollection || hasScheme(spec?.url ?? '')) return false
    return effectiveBaseUrl(linkedCollection, envs.find((e) => e.path === env) ?? null).trim() !== ''
  }, [linkedCollection, spec?.url, envs, env])

  const variableContext = useMemo<VariableContext>(() => ({
    requestPath: path,
    envPath: env ?? undefined,
  }), [path, env])

  const assertions = useMemo(() => spec?.assertions ?? [], [spec?.assertions])
  const assertionsKey = useMemo(() => JSON.stringify(assertions), [assertions])

  // Live re-check. While the Asserts tab is open and a response is on screen, edited
  // assertions are re-evaluated against that response by the same server-side engine a
  // Send uses — so the pass/fail you tune against is the one a real run will produce.
  // Skipped when the assertions still match what the last Send already evaluated.
  useEffect(() => {
    if (tab !== 'asserts' || sending || !execution) return
    if (assertionsKey === sentAssertionsRef.current) { setLiveAsserts(null); return }
    if (assertions.length === 0) { setLiveAsserts(null); return }

    let cancelled = false
    const timer = setTimeout(() => {
      api.evaluateAssertions(assertions, assertSnapshot(execution), { path, env })
        .then((r) => { if (!cancelled) setLiveAsserts({ results: r.results, summary: r.summary }) })
        // A failed re-check is not worth an error bar — the next keystroke retries, and the
        // authoritative verdict still arrives on the next Send.
        .catch(() => { if (!cancelled) setLiveAsserts(null) })
    }, 300)

    return () => { cancelled = true; clearTimeout(timer) }
  }, [tab, sending, execution, assertions, assertionsKey, path, env])

  // Verdicts to paint on the editor rows: the live re-check when it applies, otherwise
  // whatever the last Send returned.
  const assertResults = liveAsserts?.results ?? execution?.assertions ?? null
  const assertSummary = liveAsserts?.summary ?? execution?.assertSummary ?? null

  /**
   * Loads whichever entry `openEntryId` names into the response panel. Fetched on demand — the
   * History list holds summaries, and the bodies stay on disk until someone actually wants one.
   *
   * <p>Driven by an effect rather than by the click handler so both routes in behave the same:
   * picking a row in the History tab, and arriving from the sidebar timeline with the entry
   * already chosen for a tab this editor was not yet mounted for.</p>
   *
   * <p>A locked entry (encrypted, no key on this machine) fails with 423 rather than 404, which
   * is a different thing to tell the user: the entry is there, it just can't be opened here.</p>
   */
  useEffect(() => {
    const requestId = detail?.id
    if (!openEntryId || !requestId) return
    if (requestedEntryRef.current === openEntryId) return
    requestedEntryRef.current = openEntryId

    let cancelled = false
    setActionError(null)
    api.historyEntry(requestId, openEntryId)
      .then((entry) => { if (!cancelled) setReplay(entry) })
      .catch((e) => {
        if (cancelled) return
        setReplay(null)
        setActionError(e instanceof ApiError && e.status === 423
          ? 'This entry is encrypted and this machine has no key that opens it.'
          : e instanceof Error ? e.message : String(e))
      })
    return () => { cancelled = true }
  }, [openEntryId, detail?.id, setActionError])

  /** Puts the live response back. Clears the selection too — leaving it set would have the
   *  effect above re-open the entry the moment this tab is revisited. */
  function closeHistoryEntry() {
    requestedEntryRef.current = null
    setReplay(null)
    setOpenEntryId(null)
  }

  /** A recorded entry rendered in the shapes the response panel already speaks. Assembled here
   *  rather than stored that way so the on-disk format stays its own thing. */
  const replayed = useMemo(() => {
    if (!replay) return null
    const result: ExecutionResult = {
      status: replay.response?.status ?? 0,
      statusText: replay.response?.statusText ?? null,
      url: replay.request.url,
      method: replay.request.method,
      requestHeaders: replay.request.headers,
      requestBody: replay.request.body,
      responseHeaders: replay.response?.headers ?? {},
      responseBody: replay.response?.body ?? null,
      contentType: replay.response?.contentType ?? null,
      responseBodyBytes: replay.response?.bodyBytes ?? 0,
      // What was stored is all there is — there is no retained copy to expand into, so the
      // inline count is the body's own length and "Show all" correctly offers nothing.
      responseBodyInlineBytes: replay.response?.body?.length ?? 0,
      durationMs: replay.durationMs,
      variablesUsed: replay.variablesUsed.map((v) => ({
        variableProvider: v.provider, name: v.name, resolved: true, isSecret: v.secret, durationMs: 0,
      })),
      env: replay.env,
      error: replay.error,
      protocol: replay.request.protocol === 'websocket' ? 'websocket' : 'http',
      assertions: replay.assertions,
      assertSummary: replay.assertSummary,
    }
    return result
  }, [replay])

  function send() {
    // Record what this Send evaluates, so the Asserts tab's live re-check knows not to
    // recompute a verdict the server just handed us.
    sentAssertionsRef.current = JSON.stringify(spec?.assertions ?? [])
    setLiveAsserts(null)
    startSend({
      path,
      env,
      // No draft for a .http-backed request: there is no spec to emit, so sending one is a
      // parse error rather than a run. Its drafts are raw text, sent from the .http editor.
      spec: !httpBacked && dirty && spec ? spec : undefined,
      protocol: spec?.protocol ?? 'http',
    })
  }

  if (!detail || !spec) {
    return (
      <EditorShell title={detail?.name ?? basename(path)} kindLabel="Request" dirty={false} saving={saving} errorMessage={errorMessage} onSave={save}>
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  // WebSocket is a protocol flag on the spec, but the picker treats it as one more entry in
  // the verb list — the handshake is a GET the user never has to think about.
  const currentMethod = spec.protocol === 'websocket' ? 'WS' : spec.method
  const split = splitUrl(spec.url)
  const queryRows: KvRow[] = split.query.map((p) => ({ key: p.key, value: p.value }))
  const headers = spec.headers ?? []
  const contentTypeHeader = headers.find((h) => h.name.toLowerCase() === 'content-type')?.value ?? null

  // Content-Type is owned by the Body tab, but it is still a header the user may need to
  // bend (vendor media types, an explicit charset). The Headers tab therefore shows it as a
  // pinned row: name locked, value editable, badged with where the current value came from…
  const bodyMode = detectBodyMode(contentTypeHeader, spec.requestBody ?? '')
  const bodyRawSub = detectRawSubType(contentTypeHeader)
  const autoContentType = contentTypeForBodyMode(bodyMode, bodyRawSub)
  const ctOrigin = contentTypeOrigin(contentTypeHeader, bodyMode, bodyRawSub)
  // …but only once there is a Content-Type to say that about: one on the request, or a body
  // mode that implies one (a Form body with no rows yet still owns its Content-Type, which is
  // why this is not a test on the body text). On a bodyless DELETE with nothing set, the row
  // was a permanently empty field advertising a header that is never sent, so it is hidden and
  // the list below owns the header instead.
  //
  // `ctInList` is the other half: once the list owns it, the pinned row stays away until the
  // row is gone or the Body tab claims the header back. Letting it appear the moment a value
  // is typed would pull the field being typed in out from under the cursor.
  const hasContentTypeHeader = headers.some((h) => h.name.toLowerCase() === 'content-type')
  const showContentTypeRow = !ctInList && (hasContentTypeHeader || bodyMode !== 'none')
  const headersOnly = showContentTypeRow
    ? headers.filter((h) => h.name.toLowerCase() !== 'content-type')
    : headers
  const bodySourceLabel = bodyMode === 'raw'
    ? `Raw · ${RAW_SUB_LABELS[bodyRawSub]}`
    : BODY_MODE_LABELS[bodyMode]

  function setHeaders(next: HttpHeaderSpec[]) {
    update('headers', next.length > 0 ? next : undefined)
  }
  function setContentType(contentType: string | null) {
    // Whoever calls this owns the header now — the Body tab, or the pinned row itself.
    setCtInList(false)
    const without = headers.filter((h) => h.name.toLowerCase() !== 'content-type')
    setHeaders(contentType ? [{ name: 'Content-Type', value: contentType }, ...without] : without)
  }
  function setRequestBody(body: string | undefined, contentType: string | null) {
    update('requestBody', body && body.length > 0 ? body : undefined)
    setContentType(contentType)
  }
  function updateTransport(patch: Partial<NonNullable<RequestSpec['transport']>>) {
    setSpec((cur) => {
      if (!cur) return cur
      const transport = { ...cur.transport, ...patch }
      return { ...cur, transport: transport.ignoreTlsErrors === undefined && transport.timeoutMs === undefined ? undefined : transport }
    })
  }
  async function diagnoseTls() {
    setDiagnosing(true)
    try { setDiagnosis(await api.diagnoseTls(path, env, spec ?? undefined)) }
    catch (e) {
      // Not `setActionError`: the panel only renders that when there is no execution, so a
      // failed diagnosis after a failed send would land nowhere while quietly replacing the
      // send's own error.
      notifications.show({
        title: 'TLS diagnosis failed',
        message: e instanceof Error ? e.message : String(e),
        color: 'red',
      })
    }
    finally { setDiagnosing(false) }
  }

  return (
    <>
    <EditorShell
      title={spec.name || basename(path)}
      kindLabel="Request"
      // A .http-backed request can be read and sent here, never written — so it never reports
      // itself as dirty, which is what keeps Save (and the title's rename) from firing at a
      // file that has no spec to write.
      dirty={dirty && !httpBacked} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={httpBacked ? undefined : () => setSpec(savedSpec)}
      onTitleChange={httpBacked ? undefined : (n) => update('name', n)}
      toolbarExtras={
        <Group gap="xs" wrap="nowrap">
          {httpBacked && (
            <Tooltip
              label={`Defined in ${basename(httpFilePath)}. Edit and send it there — this view reads it, but never rewrites the file.`}
              withArrow multiline w={280}
            >
              <Button
                variant="light" color="blue" size="xs"
                leftSection={<IconFileCode size={13} />}
                onClick={() => openTab({ path: httpFilePath, kind: 'httpfile', label: basename(httpFilePath) })}
              >
                {basename(httpFilePath)}
              </Button>
            </Tooltip>
          )}
          <Tooltip label="Open the AI assistant to craft or edit this request">
            <ActionIcon
              variant={assistantOpened ? 'light' : 'default'}
              color="tap"
              size="lg"
              onClick={assistantCtl.toggle}
              aria-label="Open the AI assistant to craft or edit this request"
            >
              <IconSparkles size={16} />
            </ActionIcon>
          </Tooltip>
        </Group>
      }
      rightPane={
        assistantOpened ? (
          <AssistantPane
            requestPath={path}
            currentSpec={spec}
            onApply={(proposal) => setSpec(proposal)}
            onClose={assistantCtl.close}
          />
        ) : undefined
      }
      bottomPane={
        // Only mount the response pane when there's actually something to show — keeps
        // the request editor full-height until the user clicks Send, and lets
        // the × close button collapse it back.
        (replayed || execution || rendered || actionError || sending) ? (
          <ResponsePanel
            tabPath={path}
            // A recorded entry takes the pane while it is open; closing it puts the live
            // response — which was never discarded — straight back.
            rendered={replayed ? null : rendered}
            execution={replayed ?? execution}
            error={actionError}
            busy={replayed ? false : sending}
            stopped={replayed ? false : stopped}
            onStop={!replayed && sending ? stop : undefined}
            requestPath={path}
            requestName={spec.name || basename(path)}
            requestAuth={spec.auth ?? null}
            replayedAt={replay?.at ?? null}
            replayRedacted={replay ? replay.redacted : undefined}
            onDiagnoseTls={() => void diagnoseTls()}
            diagnosingTls={diagnosing}
            onOpenTransport={() => setTab('transport')}
            onClose={replayed ? closeHistoryEntry : clearExecution}
          />
        ) : undefined
      }
    >
      <Group gap="xs" mt="xs" mb="md" align="center" wrap="nowrap">
        <Select
          data={[
            { group: 'HTTP', items: METHODS as unknown as string[] },
            { group: 'WebSocket', items: [{ value: 'WS', label: 'WS' }] },
          ]}
          value={currentMethod}
          onChange={(v) => {
            if (!v) return
            setSpec((cur) => {
              if (!cur) return cur
              if (v === 'WS') {
                // WS upgrade handshake is GET-only; pin the method even though it's hidden.
                return { ...cur, protocol: 'websocket', method: 'GET' }
              }
              return { ...cur, protocol: undefined, method: v }
            })
          }}
          // Sized to the widest verb rather than to the widest verb plus slack: the URL is the
          // field that wants the room, and the verb is already legible from its colour before
          // it is read as a word.
          w={92}
          styles={{
            input: {
              fontFamily: 'var(--mono)',
              fontWeight: 700,
              color: methodTextColor(currentMethod),
              paddingRight: 24,
            },
          }}
          rightSectionWidth={20}
          allowDeselect={false}
          renderOption={({ option }) => (
            <Text size="sm" ff="var(--mono)" fw={700} c={methodTextColor(option.value)}>
              {option.value === 'WS'
                ? <Group gap={4} wrap="nowrap" component="span"><IconBolt size={11} /> {option.label}</Group>
                : option.label}
            </Text>
          )}
          leftSection={spec.protocol === 'websocket' ? <IconBolt size={12} /> : undefined}
          leftSectionWidth={spec.protocol === 'websocket' ? 20 : 0}
        />
        {linkedCollection && showCollectionChip && (
          <CollectionLinkChip
            summary={linkedCollection}
            env={env}
            onEnvChange={(next) => setCollectionEnv(linkedCollection.slug, next)}
            variableContext={variableContext}
            onOpen={() => openTab({ path: `collections/${linkedCollection.slug}`, kind: 'collection', label: linkedCollection.name })}
          />
        )}
        <Box style={{ flex: 1, minWidth: 0 }}>
          <VariableInput
            value={spec.url}
            onChange={(v) => update('url', v)}
            placeholder={linkedCollection ? '/path?query={{var}}' : 'https://api.example.com/path'}
            context={variableContext}
            onOpenVariables={varsCtl.open}
          />
        </Box>
        {sending ? (
          // In-flight: a red, spinner-led button that doubles as the cancel control. The
          // spinner shows the request is running; clicking it aborts (Mantine's `loading`
          // prop would disable the button, so we render the Loader ourselves to keep it
          // clickable).
          <Button
            color="red"
            leftSection={<Loader size={14} color="white" />}
            onClick={stop}
            title="Stop the running request"
          >
            Stop
          </Button>
        ) : (
          <Button
            leftSection={<IconPlayerPlayFilled size={14} />}
            onClick={send}
            disabled={sending}
            title={
              httpBacked ? 'Send the request as defined in the .http file'
                : dirty ? 'Send the request (using unsaved changes)'
                : 'Send the request'
            }
          >
            Send
          </Button>
        )}
      </Group>

      {/* Editing a .http-backed request here changes nothing on disk and nothing on the wire.
          Saying so beats letting Send quietly run something other than what is on screen —
          the one failure this whole feature exists to rule out. */}
      {httpBacked && dirty && (
        <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={14} />} mb="md" py="xs">
          <Group gap="xs" wrap="nowrap" justify="space-between">
            <Text size="xs">
              Changes here aren't saved or sent — this request is defined in{' '}
              <Text component="span" ff="var(--mono)" fz="xs">{basename(httpFilePath)}</Text>.
            </Text>
            <Button
              size="compact-xs" variant="light" color="yellow"
              onClick={() => openTab({ path: httpFilePath, kind: 'httpfile', label: basename(httpFilePath) })}
            >
              Edit there
            </Button>
          </Group>
        </Alert>
      )}

      <Tabs value={tab} onChange={setTab}>
        {/* Ordered left-to-right by how often a section is touched: the tail (Meta, Docs,
            Source) is what AdaptiveTabsList strips down to icons first when space runs out. */}
        <AdaptiveTabsList
          mb="md"
          tabs={[
            { value: 'params', label: 'Params', icon: <IconParentheses size={14} />, adornment: <TabCount count={queryRows.length} /> },
            { value: 'headers', label: 'Headers', icon: <IconList size={14} />, adornment: <TabCount count={headers.length} /> },
            { value: 'body', label: 'Body', icon: <IconBraces size={14} /> },
            { value: 'auth', label: 'Auth', icon: <IconLock size={14} />, adornment: <TabDot active={!!spec.auth && spec.auth !== 'none'} color="orange" /> },
            {
              value: 'asserts',
              label: 'Asserts',
              icon: <IconCircleCheck size={14} />,
              adornment: (
                <>
                  <TabCount count={assertions.length} />
                  <TabDot active={!!assertSummary} color={assertSummary?.failed ? 'red' : 'green'} />
                </>
              ),
            },
            { value: 'transport', label: 'Transport', icon: <IconShieldCheck size={14} /> },
            { value: 'vars', label: 'Variables', icon: <IconVariable size={14} />, adornment: <TabCount count={Object.keys(spec.vars ?? {}).length} /> },
            { value: 'history', label: 'History', icon: <IconHistory size={14} />, adornment: <TabCount count={historyCount} /> },
            { value: 'meta', label: 'Meta', icon: <IconFlag size={14} /> },
            { value: 'docs', label: 'Docs', icon: <IconFileText size={14} />, adornment: <TabDot active={!!spec.body && spec.body.trim().length > 0} /> },
            { value: 'source', label: 'Source', icon: <IconCode size={14} /> },
          ]}
        />

        <Tabs.Panel value="params">
          <Box maw={880}>
            <KvTable
              rows={queryRows}
              onChange={(rows) => update('url', joinUrl({ ...split, query: rows.filter((r) => r.key).map((r) => ({ key: r.key, value: r.value })) }))}
              keyPlaceholder="name"
              valuePlaceholder="value or {{var}}"
              emptyHint="No parameters yet."
              variableContext={variableContext}
              onOpenVariables={varsCtl.open}
            />
          </Box>
        </Tabs.Panel>

        <Tabs.Panel value="headers">
          <Box maw={880}>
            <KvTable
              rows={headersOnly.map((h) => ({ key: h.name, value: h.value }))}
              onChange={(rows) => {
                // No pinned row means the list owns Content-Type like any other header — take
                // the rows as written, and remember it so the pinned row does not reappear
                // between two keystrokes of the value.
                if (!showContentTypeRow) {
                  setCtInList(rows.some((r) => r.key.toLowerCase() === 'content-type'))
                  setHeaders(rows.filter((r) => r.key).map((r) => ({ name: r.key, value: r.value })))
                  return
                }
                // With the pinned row on screen, a row typed as `Content-Type` belongs to it
                // rather than the list — adopt its value there rather than emitting a duplicate.
                const typedCt = rows.find((r) => r.key.toLowerCase() === 'content-type' && r.value)
                const fresh: HttpHeaderSpec[] = rows
                  .filter((r) => r.key && r.key.toLowerCase() !== 'content-type')
                  .map((r) => ({ name: r.key, value: r.value }))
                const ct = typedCt?.value ?? contentTypeHeader
                if (ct) fresh.unshift({ name: 'Content-Type', value: ct })
                setHeaders(fresh)
              }}
              keyPlaceholder="Header-Name"
              valuePlaceholder="value"
              variableContext={variableContext}
              onOpenVariables={varsCtl.open}
              keySuggestions={showContentTypeRow ? HEADER_NAMES_WITHOUT_CONTENT_TYPE : COMMON_HEADER_NAMES}
              getValueSuggestions={valuesForHeader}
              pinnedRow={!showContentTypeRow ? undefined : {
                key: 'Content-Type',
                value: contentTypeHeader ?? '',
                onChange: (v) => setContentType(v.trim() ? v : null),
                valuePlaceholder: autoContentType ?? 'not sent — pick a body mode',
                valueSuggestions: valuesForHeader('Content-Type'),
                keyAdornment: contentTypeHeader === null ? undefined : (
                  <Tooltip
                    withArrow
                    multiline
                    w={260}
                    label={ctOrigin === 'auto'
                      ? `Set from the Body tab (${bodySourceLabel}). Edit the value to override it.`
                      : `Overrides the Body tab's ${autoContentType}. Switching body mode resets it.`}
                  >
                    <Badge size="xs" variant="light" color={ctOrigin === 'auto' ? 'gray' : 'yellow'}>
                      {ctOrigin === 'auto' ? 'auto' : 'custom'}
                    </Badge>
                  </Tooltip>
                ),
                action: ctOrigin === 'override' && autoContentType ? (
                  <Tooltip label={`Reset to ${autoContentType}`} withArrow>
                    <ActionIcon
                      variant="subtle"
                      color="gray"
                      size="sm"
                      onClick={() => setContentType(autoContentType)}
                      aria-label="Reset Content-Type"
                    >
                      <IconRotateClockwise size={14} />
                    </ActionIcon>
                  </Tooltip>
                ) : undefined,
              }}
            />
          </Box>
        </Tabs.Panel>

        <Tabs.Panel value="body">
          <BodyEditor
            body={spec.requestBody ?? ''}
            contentType={contentTypeHeader}
            onChange={setRequestBody}
            variableContext={variableContext}
            onOpenVariables={varsCtl.open}
            requestPath={path}
            env={env}
            dirty={dirty}
          />
        </Tabs.Panel>

        <Tabs.Panel value="auth">
          <Stack gap="md" maw={760}>
            <Group align="flex-end" gap="xs" wrap="nowrap">
              <Select
                label="Auth profile"
                style={{ flex: 1 }}
                data={[
                  { value: '', label: '(inherit from collection)' },
                  { value: 'none', label: 'None (opt out)' },
                  ...authSelectGroups({ auths, collections, fromPath: path }),
                ]}
                value={spec.auth ?? ''}
                onChange={(v) => update('auth', v && v !== '' ? v : undefined)}
                allowDeselect={false}
              />
              {(() => {
                const selected = spec.auth && spec.auth !== 'none' && spec.auth !== ''
                  ? auths.find((a) => relativizeFrom(path, a.path) === spec.auth)
                  : null
                return (
                  <Tooltip label={selected ? `Open ${selected.name}` : 'Select an auth profile to open it'} withArrow>
                    <Button
                      variant="default"
                      leftSection={<IconExternalLink size={14} />}
                      disabled={!selected}
                      onClick={() => selected && openTab({ path: selected.path, kind: 'auth', label: selected.name })}
                    >
                      Open
                    </Button>
                  </Tooltip>
                )
              })()}
            </Group>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="asserts">
          <AssertsPanel
            assertions={assertions}
            onChange={(next) => update('assertions', next.length > 0 ? next : undefined)}
            results={assertResults}
            summary={assertSummary}
            variableContext={variableContext}
            onOpenVariables={varsCtl.open}
            hint={
              execution
                ? liveAsserts
                  ? 'Checked against the response below — edit and the verdicts follow.'
                  : 'Verdicts from the last Send. Edit an assertion to re-check it against that response.'
                : 'Send the request once, then edit these against the real response.'
            }
          />
        </Tabs.Panel>

        <Tabs.Panel value="transport">
          <Stack gap="md" maw={760}>
            <Select
              label="TLS certificate validation"
              description="Choose whether this request inherits the collection policy, validates certificates normally, or accepts certificate errors."
              data={[
                { value: '', label: '(inherit from collection)' },
                { value: 'validate', label: 'Validate certificates' },
                { value: 'ignore', label: 'Ignore certificate errors' },
              ]}
              value={spec.transport?.ignoreTlsErrors === undefined ? '' : spec.transport.ignoreTlsErrors ? 'ignore' : 'validate'}
              onChange={(value) => updateTransport({ ignoreTlsErrors: value === 'ignore' ? true : value === 'validate' ? false : undefined })}
              allowDeselect={false}
            />
            <NumberInput
              label="Timeout (ms)"
              description="Total time allowed for connection and response. Leave blank to inherit from the collection or use the default; zero disables the timeout."
              value={spec.transport?.timeoutMs ?? ''}
              min={0}
              step={1000}
              onChange={(value) => updateTransport({ timeoutMs: typeof value === 'number' ? value : undefined })}
            />
            <Group>
              <Button variant="default" leftSection={<IconShieldCheck size={14} />} loading={diagnosing} onClick={diagnoseTls}>
                Diagnose TLS
              </Button>
              {linkedCollection && !spec.transport && <Text size="xs" c="dimmed">Unset values inherit collection defaults.</Text>}
            </Group>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="vars">
          <Box maw={880}>
            <KvTable
              rows={(() => {
                const ss = new Set(spec.secrets ?? [])
                return Object.entries(spec.vars ?? {}).map(([k, v]) => ({ key: k, value: v, secret: ss.has(k) }))
              })()}
              onChange={(rows) => {
                const obj: Record<string, string> = {}
                const sec: string[] = []
                for (const r of rows) {
                  if (!r.key) continue
                  obj[r.key] = r.value
                  if (r.secret) sec.push(r.key)
                }
                setSpec((cur) => cur ? {
                  ...cur,
                  vars: Object.keys(obj).length > 0 ? obj : undefined,
                  secrets: sec.length > 0 ? sec : undefined,
                } : cur)
              }}
              keyPlaceholder="var.name"
              valuePlaceholder="default value"
              emptyHint="No request-scoped variables yet."
              allowSecretToggle
              variableContext={variableContext}
              onOpenVariables={varsCtl.open}
            />
          </Box>
        </Tabs.Panel>

        <Tabs.Panel value="history">
          <Box maw={880}>
            <HistoryPanel
              requestId={detail.id}
              enabled={detail.effectiveHistory?.enabled ?? false}
              selectedId={openEntryId}
              onSelect={(row) => setOpenEntryId(row.id)}
              onCountChange={setHistoryCount}
              onOpenSettings={() => setTab('meta')}
            />
          </Box>
        </Tabs.Panel>

        <Tabs.Panel value="meta">
          <Stack gap="md" maw={760}>
            <TextInput label="Name" value={spec.name} onChange={(e) => update('name', e.currentTarget.value)} />
            <TagsInput
              label="Tags"
              placeholder={(spec.tags?.length ?? 0) === 0 ? 'Add tag…' : ''}
              data={tagSuggestions}
              value={spec.tags ?? []}
              onChange={(v) => update('tags', v.length > 0 ? v : undefined)}
              acceptValueOnBlur
              clearable
            />
            {linkedCollection && (
              <Text size="xs" c="dimmed">
                Inherits from collection <Code fz="xs">{linkedCollection.name}</Code> — base URL,
                default auth, and default headers come from there.
              </Text>
            )}

            <Box>
              <Text fw={600} size="sm" mb={2}>History</Text>
              <Text size="xs" c="dimmed" mb="sm">
                Overrides this request's collection and the workspace, one key at a time.
              </Text>
              <HistorySettings
                value={spec.history}
                onChange={(v) => update('history', v)}
                inherited={detail.effectiveHistory}
                inheritedFrom={linkedCollection?.name}
              />
            </Box>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="docs">
          <DocsEditor
            value={spec.body ?? ''}
            onChange={(v) => update('body', v.trim().length > 0 ? v : undefined)}
            emptyHint="No docs yet. Describe what this request does, its parameters, and expected responses."
          />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          {/* For a .http-backed request the source is the whole file, in its own format —
              so it edits as http against the file path. Passing the fragment path here would
              claim the text is canonical YAML and hand Save a path with a '#' in it, which
              the server rejects as an unrecognized suffix. */}
          <SourceTab
            path={httpBacked ? httpFilePath : path}
            source={detail.source}
            language={httpBacked ? 'http' : 'yaml'}
            deletable={httpBacked
              // The source here is the whole .http file, so Delete removes the file and every
              // request in it — name the file, not this one request.
              ? { kind: 'httpfile', path: httpFilePath, name: basename(httpFilePath) }
              : { kind: 'request', path, name: spec.name || basename(path) }}
          />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    <TlsDiagnosisModal diagnosis={diagnosis} onClose={() => setDiagnosis(null)} />
    </>
  )
}

function BodyEditor({ body, contentType, onChange, variableContext, onOpenVariables, requestPath, env, dirty }: {
  body: string
  contentType: string | null
  onChange: (body: string | undefined, ct: string | null) => void
  variableContext?: import('../api/types').VariableContext | null
  onOpenVariables?: () => void
  requestPath: string
  env: string | null
  dirty: boolean
}) {
  const [mode, setMode] = useState<BodyMode>(() => detectBodyMode(contentType, body))
  // Sub-type within `raw` — only matters while mode === 'raw'. Detected from CT on load,
  // then user-controlled via the segmented control inside the raw editor.
  const [rawSub, setRawSub] = useState<RawSubType>(() => detectRawSubType(contentType))
  useEffect(() => {
    setMode(detectBodyMode(contentType, body))
    setRawSub(detectRawSubType(contentType))
  }, [body, contentType])

  function pickMode(next: BodyMode) {
    setMode(next)
    if (next === 'none') {
      // Drop both the body and the Content-Type — switching to None means "no body".
      onChange(undefined, null)
      return
    }
    if (next === 'raw') {
      onChange(body || undefined, contentTypeForBodyMode('raw', rawSub))
      return
    }
    if (next === 'graphql') {
      // detectBodyMode only classifies a body as graphql when it's a `{ query }` JSON
      // envelope. A raw/empty body would be re-detected as 'raw' on the next render and
      // snap the segmented control back, so seed a minimal envelope to make it stick.
      const seeded = looksLikeGraphql(body) ? body : serializeGraphQLBody(parseGraphQLBody(body))
      onChange(seeded, contentTypeForBodyMode('graphql'))
      return
    }
    if (next === 'soap') {
      // Same reason as graphql above: detectBodyMode only says 'soap' for a real envelope,
      // so seed one. parseSoapBody carries an existing raw XML body in as the payload.
      const seeded = looksLikeSoap(body) ? body : serializeSoapBody(parseSoapBody(body))
      onChange(seeded, contentTypeForBodyMode('soap'))
      return
    }
    onChange(body || undefined, contentTypeForBodyMode(next))
  }

  function pickRawSub(next: RawSubType) {
    setRawSub(next)
    onChange(body || undefined, contentTypeForBodyMode('raw', next))
  }

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="center" wrap="nowrap">
        <SegmentedControl
          size="xs"
          value={mode}
          onChange={(v) => pickMode(v as BodyMode)}
          data={BODY_MODES.map((m) => ({ value: m, label: BODY_MODE_LABELS[m] }))}
        />
        <Group gap="xs">
          {mode === 'raw' && (
            <SegmentedControl
              size="xs"
              value={rawSub}
              onChange={(v) => pickRawSub(v as RawSubType)}
              data={RAW_SUB_TYPES.map((s) => ({ value: s, label: RAW_SUB_LABELS[s] }))}
            />
          )}
          {mode === 'raw' && rawSub === 'json' && body && (
            <Button
              size="xs"
              variant="default"
              leftSection={<IconSparkles size={12} />}
              onClick={() => onChange(tryPrettyJson(body), contentType)}
            >
              Format
            </Button>
          )}
        </Group>
      </Group>

      {mode === 'form-urlencoded' && (
        <KvTable
          rows={parseFormBody(body)}
          onChange={(rows) => onChange(serializeFormBody(rows) || undefined, contentType ?? contentTypeForBodyMode('form-urlencoded'))}
          keyPlaceholder="name"
          valuePlaceholder="value or {{var}}"
          variableContext={variableContext}
          onOpenVariables={onOpenVariables}
        />
      )}

      {mode === 'multipart' && (
        <MultipartTable
          parts={parseMultipartBody(body)}
          // Unlike the other modes, multipart re-asserts the canonical Content-Type on every
          // edit rather than preserving a user override: the header's `boundary` parameter
          // has to match the delimiter serializeMultipartBody just wrote into the body.
          onChange={(parts) => onChange(serializeMultipartBody(parts) || undefined, contentTypeForBodyMode('multipart'))}
          variableContext={variableContext}
          onOpenVariables={onOpenVariables}
        />
      )}

      {mode === 'graphql' && (
        <GraphQLEditor
          requestPath={requestPath}
          env={env}
          body={body}
          dirty={dirty}
          onChange={(b) => onChange(b, contentType ?? contentTypeForBodyMode('graphql'))}
        />
      )}

      {mode === 'soap' && (
        <SoapEditor
          body={body}
          onChange={(b) => onChange(b, contentType ?? contentTypeForBodyMode('soap'))}
          variableContext={variableContext}
          onOpenVariables={onOpenVariables}
        />
      )}

      {mode === 'raw' && (
        <RawBodyEditor
          value={body}
          onChange={(v) => onChange(v || undefined, contentType)}
          rawSub={rawSub}
        />
      )}

      {mode === 'binary' && (
        <BinaryBody
          body={body}
          contentType={contentType}
          requestPath={requestPath}
          onChange={(b, ct) => onChange(b || undefined, ct)}
        />
      )}
    </Stack>
  )
}

/** Binary body: file picker uploads to the workspace's sideband store and writes a
 *  ref marker (<c>&lt; ./.files/foo.png</c>) into the body. The executor swaps the
 *  ref for actual bytes at send time, so non-text files are byte-perfect end-to-end
 *  and round-trip-safe through the on-disk spec. Manual editing of the ref string in
 *  the textarea is allowed for power users who already have a file checked in. */
function BinaryBody({ body, contentType, requestPath, onChange }: {
  body: string
  contentType: string | null
  requestPath: string
  onChange: (body: string, contentType: string | null) => void
}) {
  const [uploading, setUploading] = useState(false)
  const [meta, setMeta] = useState<{ name: string; size: number; ref: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Reset the cached metadata when the body changes from outside (e.g., user edited
  // the ref manually, or switched tabs). The ref text in the body is the source of
  // truth; meta only drives the prettier display row.
  const parsedRef = useMemo(() => parseBinaryRef(body), [body])
  useEffect(() => {
    if (!parsedRef) setMeta(null)
    else if (!meta || meta.ref !== parsedRef.ref) setMeta((m) => m && m.ref === parsedRef.ref ? m : { name: parsedRef.name, size: 0, ref: parsedRef.ref })
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [parsedRef])

  async function handleDrop(files: File[]) {
    const f = files[0]
    if (!f) return
    setError(null); setUploading(true)
    try {
      const resp = await api.uploadRequestFile(requestPath, f)
      setMeta({ name: resp.name, size: resp.size, ref: resp.ref })
      onChange(resp.ref, resp.contentType || f.type || contentType || 'application/octet-stream')
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setUploading(false)
    }
  }

  function clear() {
    setMeta(null)
    setError(null)
    onChange('', null)
  }

  return (
    <Stack gap="xs">
      <Group gap="md" wrap="nowrap" align="flex-start">
        <Box style={{ flex: 1 }}>
          <Text size="xs" c="dimmed" mb={4}>File</Text>
          <Dropzone
            onDrop={handleDrop}
            onReject={() => setError('File rejected')}
            multiple={false}
            loading={uploading}
            maxSize={50 * 1024 * 1024}
            p="sm"
          >
            <Group justify="center" gap="md" mih={56} style={{ pointerEvents: 'none' }}>
              <Dropzone.Accept><IconUpload size={22} /></Dropzone.Accept>
              <Dropzone.Reject><IconX size={22} /></Dropzone.Reject>
              <Dropzone.Idle><IconFile size={22} /></Dropzone.Idle>
              <div>
                <Text size="sm" inline>{parsedRef ? 'Replace file' : 'Drop a file or click to pick'}</Text>
                <Text size="xs" c="dimmed" inline mt={2}>
                  Stored under <Code fz="xs">.files/</Code> next to the request.
                </Text>
              </div>
            </Group>
          </Dropzone>
        </Box>
        <Box w={260}>
          <TextInput
            size="xs"
            label="Content-Type"
            value={contentType ?? ''}
            onChange={(e) => onChange(body, e.currentTarget.value || null)}
            placeholder="application/octet-stream"
            styles={{ input: { fontFamily: 'var(--mono)' } }}
          />
        </Box>
      </Group>
      {parsedRef && (
        <Group gap="xs">
          <Badge variant="light" leftSection={<IconFile size={12} />}>
            {parsedRef.name}{meta?.size ? ` · ${formatBytes(meta.size)}` : ''}
          </Badge>
          <Code fz="xs" c="dimmed">{parsedRef.ref}</Code>
          <Button size="compact-xs" variant="subtle" onClick={clear}>Clear</Button>
        </Group>
      )}
      {error && <Text size="xs" c="red">{error}</Text>}
      {!parsedRef && body && (
        <Text size="xs" c="dimmed">
          Body is inline ({formatBytes(body.length)}). Drop a file to switch to a checked-in
          ref under <Code fz="xs">.files/</Code>.
        </Text>
      )}
    </Stack>
  )
}

/** Parse a one-line `< ./relative/path` body into its components for display. Returns
 *  null when the body is empty, multi-line, or otherwise not a ref. */
function parseBinaryRef(body: string): { ref: string; relPath: string; name: string } | null {
  if (!body) return null
  const trimmed = body.trim()
  if (!trimmed.startsWith('< ')) return null
  if (/[\r\n]/.test(trimmed)) return null
  let rest = trimmed.slice(2).trim()
  const refForDisplay = `< ./${rest.replace(/^\.\//, '')}`
  if (rest.startsWith('./')) rest = rest.slice(2)
  if (!rest) return null
  const name = rest.split('/').pop() ?? rest
  return { ref: refForDisplay, relPath: rest, name }
}

/** Repackage an on-screen result as the snapshot the re-check endpoint evaluates against.
 *  Truncation is whatever the server reported for this response — the cap is the workspace's
 *  `response.maxBytes`, not a constant the client can assume. Past it the body on screen is a
 *  prefix, and body assertions refuse to run rather than match one. */
function assertSnapshot(execution: ExecutionResult): AssertResponseSnapshot {
  const inline = execution.responseBodyInlineBytes ?? 0
  return {
    status: execution.status,
    headers: Object.entries(execution.responseHeaders ?? {}).map(([name, value]) => ({ name, value })),
    body: execution.responseBody,
    bodyTruncated: inline > 0 && execution.responseBodyBytes > inline,
    durationMs: execution.durationMs,
  }
}

/** Mirrors `WorkspaceRenderer.HasAnyScheme`: these four prefixes are exactly what makes the
 *  renderer send the URL as written instead of joining it onto the collection's baseUrl. */
function hasScheme(url: string): boolean {
  return /^(https?|wss?):\/\//i.test(url.trimStart())
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}

function specFromDetail(d: RequestDetail, path: string): RequestSpec {
  // Split the VarSpec map into a plain values dict + the list of names flagged secret —
  // the wire format the emitter expects.
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const [k, v] of Object.entries(d.vars ?? {})) {
    if (v?.default != null) vars[k] = v.default
    if (v?.secret) secrets.push(k)
  }
  return {
    path, id: d.id, name: d.name,
    auth: d.auth ?? undefined,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
    method: d.method,
    url: d.url,
    headers: d.headers && d.headers.length > 0 ? d.headers : undefined,
    requestBody: d.requestBody ?? undefined,
    // Default is `http` — omit from spec so dirty-tracking + emitter stay quiet.
    protocol: d.protocol === 'websocket' ? 'websocket' : undefined,
    transport: d.transport ?? undefined,
    assertions: d.assertions && d.assertions.length > 0 ? d.assertions : undefined,
  }
}

function basename(p: string): string { return p.split('/').pop() ?? p }

/** Fold a display name into a safe, lowercase filename slug (no extension). Mirrors the
 *  slug rules used by the create / duplicate dialogs so renamed files match newly-created
 *  ones. Returns '' when nothing usable survives (caller then skips the rename). */
function nameToSlug(name: string): string {
  return name.trim().toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/-+/g, '-')
    .slice(0, 60)
}

