import {
  Alert, Badge, ActionIcon, Box, Button, Code, Group, Loader, Modal, NumberInput, SegmentedControl, Select, Stack, Tabs, TagsInput, Text, TextInput, Tooltip,
} from '@mantine/core'
import { Dropzone } from '@mantine/dropzone'
import {
  IconAlertTriangle, IconBolt, IconBraces, IconCode, IconCircleCheck, IconExternalLink, IconFile, IconFileCode, IconFileText, IconFlag, IconList, IconLock, IconParentheses, IconPlayerPlayFilled, IconShieldCheck, IconSparkles, IconUpload, IconVariable, IconX,
} from '@tabler/icons-react'
import { useDisclosure } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, type AssertResponseSnapshot } from '../api/client'
import type {
  AssertResult, AssertSummary, ExecutionResult, HttpHeaderSpec, RequestDetail, RequestSpec, TlsDiagnosis, VariableContext,
} from '../api/types'
import { useActiveEnv, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import {
  BODY_MODE_LABELS, contentTypeForBodyMode, detectBodyMode, detectRawSubType, looksLikeGraphql,
  parseFormBody, parseGraphQLBody, parseMultipartBody, serializeFormBody, serializeGraphQLBody,
  serializeMultipartBody, tryPrettyJson,
  RAW_SUB_LABELS, type BodyMode, type RawSubType,
} from './body-mode'
import { AdaptiveTabsList } from './AdaptiveTabsList'
import { CollectionLinkChip } from './CollectionLinkChip'
import { authSelectGroups, relativizeFrom } from './authOptions'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount, TabDot } from './EditorShell'
import { GraphQLEditor } from './GraphQLEditor'
import { AssertsPanel } from './AssertsPanel'
import { KvTable, type KvRow } from './KvTable'
import { MultipartTable } from './MultipartTable'
import { RawBodyEditor } from './RawBodyEditor'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { ResponsePanel } from './ResponsePanel'
import { SourceTab } from './SourceTab'
import { useExecution } from './useExecution'
import { joinUrl, splitUrl } from './url-utils'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'
import { AssistantPane } from '../features/assistant/AssistantPane'
import { fileNameFor, isHttpBackedRequest, splitHttpFragment, stripTapSuffix } from '../shell/tapFiles'

interface Props { path: string }

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] as const
const BODY_MODES: BodyMode[] = ['none', 'form-urlencoded', 'multipart', 'raw', 'binary', 'graphql']
const RAW_SUB_TYPES: RawSubType[] = ['json', 'text', 'xml']

export function RequestEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const collections = useTapStore((s) => s.collections)
  const auths = useTapStore((s) => s.auths)
  const openTab = useTapStore((s) => s.openTab)
  const renameTab = useTapStore((s) => s.renameTab)
  const reload = useTapStore((s) => s.reload)
  const activeEnv = useActiveEnv()
  const tagSuggestions = useTagDictionary()

  const [detail, setDetail] = useState<RequestDetail | null>(null)
  const [spec, setSpec] = useState<RequestSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<RequestSpec | null>(null)
  const [tab, setTab] = useState<string | null>('params')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  // Sending, and everything the response pane renders. Shared with the .http editor, which
  // sends the same way from its own request list.
  const {
    rendered, execution, error: actionError, sending, stopped,
    send: startSend, stop, clear: clearExecution, abort: abortStream, setError: setActionError,
  } = useExecution()
  const [stage, setStage] = useState<string | null>(null)
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

  useEffect(() => {
    let cancelled = false
    setError(null); clearExecution()
    api.request(path).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d, path)
      setSpec(initial); setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [path, generation, clearExecution])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

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
    try {
      await api.saveRequestSpec(spec)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      setSaving(false)
      return
    }

    // The spec is on disk from here on. Renaming the file that holds it is a separate,
    // best-effort step — a collision on the target name must not leave the editor looking
    // unsaved, so it reports through a notification instead of the save error bar.
    const label = spec.name || basename(spec.path)
    try {
      // When the request was renamed, keep the on-disk filename in step with the new
      // name (the explorer shows the spec name, so a stale filename would only surface
      // in git / the filesystem view). Falls back to a no-op when the slug is unchanged
      // or empty.
      const renamedPath = await syncFilenameToName(spec, savedSpec)
      if (renamedPath && renamedPath !== spec.path) {
        const moved = { ...spec, path: renamedPath }
        // Rename before the reload: the tab (and the editor keyed off it) has to follow the
        // file to its new path in the same commit the explorer learns about the move, or the
        // editor refetches a path that no longer exists.
        renameTab(spec.path, renamedPath, label)
        setSpec(moved); setSavedSpec(moved)
        await reload()
      } else {
        // Same file, possibly a new name — the tab still carries the old label.
        renameTab(spec.path, spec.path, label)
        setSavedSpec(spec)
      }
    } catch (e) {
      renameTab(spec.path, spec.path, label)
      setSavedSpec(spec)
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

  useEffect(() => { setStage(null) }, [linkedCollection?.slug])
  const effectiveStage = stage ?? linkedCollection?.defaultStage ?? null

  const variableContext = useMemo<VariableContext>(() => ({
    requestPath: path,
    envPath: activeEnv ?? undefined,
    stage: effectiveStage ?? undefined,
  }), [path, activeEnv, effectiveStage])

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
      api.evaluateAssertions(assertions, assertSnapshot(execution), {
        path, env: activeEnv, stage: effectiveStage,
      })
        .then((r) => { if (!cancelled) setLiveAsserts({ results: r.results, summary: r.summary }) })
        // A failed re-check is not worth an error bar — the next keystroke retries, and the
        // authoritative verdict still arrives on the next Send.
        .catch(() => { if (!cancelled) setLiveAsserts(null) })
    }, 300)

    return () => { cancelled = true; clearTimeout(timer) }
  }, [tab, sending, execution, assertions, assertionsKey, path, activeEnv, effectiveStage])

  // Verdicts to paint on the editor rows: the live re-check when it applies, otherwise
  // whatever the last Send returned.
  const assertResults = liveAsserts?.results ?? execution?.assertions ?? null
  const assertSummary = liveAsserts?.summary ?? execution?.assertSummary ?? null

  // Cancel any in-flight stream on unmount or when the request file changes.
  useEffect(() => () => abortStream(), [path, abortStream])

  function send() {
    // Record what this Send evaluates, so the Asserts tab's live re-check knows not to
    // recompute a verdict the server just handed us.
    sentAssertionsRef.current = JSON.stringify(spec?.assertions ?? [])
    setLiveAsserts(null)
    startSend({
      path,
      env: activeEnv,
      stage: effectiveStage,
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

  const split = splitUrl(spec.url)
  const queryRows: KvRow[] = split.query.map((p) => ({ key: p.key, value: p.value }))
  const headers = spec.headers ?? []
  const contentTypeHeader = headers.find((h) => h.name.toLowerCase() === 'content-type')?.value ?? null
  const headersOnly = headers.filter((h) => h.name.toLowerCase() !== 'content-type')

  function setHeaders(next: HttpHeaderSpec[]) {
    update('headers', next.length > 0 ? next : undefined)
  }
  function setRequestBody(body: string | undefined, contentType: string | null) {
    update('requestBody', body && body.length > 0 ? body : undefined)
    const without = headers.filter((h) => h.name.toLowerCase() !== 'content-type')
    setHeaders(contentType ? [{ name: 'Content-Type', value: contentType }, ...without] : without)
  }
  function updateTransport(patch: Partial<NonNullable<RequestSpec['transport']>>) {
    setSpec((cur) => {
      if (!cur) return cur
      const transport = { ...cur.transport, ...patch }
      return { ...cur, transport: transport.ignoreTlsErrors === undefined && transport.timeoutMs === undefined ? undefined : transport }
    })
  }
  async function diagnoseTls() {
    setDiagnosing(true); setActionError(null)
    try { setDiagnosis(await api.diagnoseTls(path, activeEnv, effectiveStage, spec ?? undefined)) }
    catch (e) { setActionError(e instanceof Error ? e.message : String(e)) }
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
        (execution || rendered || actionError || sending) ? (
          <ResponsePanel
            rendered={rendered}
            execution={execution}
            error={actionError}
            busy={sending}
            stopped={stopped}
            onStop={sending ? stop : undefined}
            requestPath={path}
            requestName={spec.name || basename(path)}
            requestAuth={spec.auth ?? null}
            onClose={clearExecution}
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
          value={spec.protocol === 'websocket' ? 'WS' : spec.method}
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
          w={108}
          styles={{ input: { fontFamily: 'var(--mono)', fontWeight: 600 } }}
          allowDeselect={false}
          renderOption={({ option }) => (
            option.value === 'WS'
              ? <Group gap={4} wrap="nowrap"><IconBolt size={11} /> {option.label}</Group>
              : <Text size="sm" ff="var(--mono)" fw={600}>{option.label}</Text>
          )}
          leftSection={spec.protocol === 'websocket' ? <IconBolt size={12} /> : undefined}
          leftSectionWidth={spec.protocol === 'websocket' ? 22 : 0}
        />
        {linkedCollection && (
          <CollectionLinkChip
            summary={linkedCollection}
            stage={effectiveStage}
            onStageChange={setStage}
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
            { value: 'headers', label: 'Headers', icon: <IconList size={14} />, adornment: <TabCount count={headersOnly.length} /> },
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
                const fresh: HttpHeaderSpec[] = rows.filter((r) => r.key).map((r) => ({ name: r.key, value: r.value }))
                if (contentTypeHeader) fresh.unshift({ name: 'Content-Type', value: contentTypeHeader })
                setHeaders(fresh)
              }}
              keyPlaceholder="Header-Name"
              valuePlaceholder="value"
              variableContext={variableContext}
              onOpenVariables={varsCtl.open}
              keySuggestions={COMMON_HEADER_NAMES}
              getValueSuggestions={valuesForHeader}
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
            env={activeEnv}
            stage={effectiveStage}
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
                stages, default auth, and default headers come from there.
              </Text>
            )}
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
          />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    <TlsDiagnosisModal diagnosis={diagnosis} onClose={() => setDiagnosis(null)} />
    </>
  )
}

function BodyEditor({ body, contentType, onChange, variableContext, onOpenVariables, requestPath, env, stage, dirty }: {
  body: string
  contentType: string | null
  onChange: (body: string | undefined, ct: string | null) => void
  variableContext?: import('../api/types').VariableContext | null
  onOpenVariables?: () => void
  requestPath: string
  env: string | null
  stage: string | null
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
          onChange={(rows) => onChange(serializeFormBody(rows) || undefined, contentTypeForBodyMode('form-urlencoded'))}
          keyPlaceholder="name"
          valuePlaceholder="value or {{var}}"
          variableContext={variableContext}
          onOpenVariables={onOpenVariables}
        />
      )}

      {mode === 'multipart' && (
        <MultipartTable
          parts={parseMultipartBody(body)}
          onChange={(parts) => onChange(serializeMultipartBody(parts) || undefined, contentTypeForBodyMode('multipart'))}
          variableContext={variableContext}
          onOpenVariables={onOpenVariables}
        />
      )}

      {mode === 'graphql' && (
        <GraphQLEditor
          requestPath={requestPath}
          env={env}
          stage={stage}
          body={body}
          dirty={dirty}
          onChange={(b) => onChange(b, contentTypeForBodyMode('graphql'))}
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

/** The 2 MiB capture cap the server applies. Past it the body on screen is a prefix, and
 *  body assertions refuse to run rather than match one. */
const BODY_CAP_BYTES = 2 * 1024 * 1024

/** Repackage an on-screen result as the snapshot the re-check endpoint evaluates against. */
function assertSnapshot(execution: ExecutionResult): AssertResponseSnapshot {
  return {
    status: execution.status,
    headers: Object.entries(execution.responseHeaders ?? {}).map(([name, value]) => ({ name, value })),
    body: execution.responseBody,
    bodyTruncated: execution.responseBodyBytes > BODY_CAP_BYTES,
    durationMs: execution.durationMs,
  }
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

function TlsDiagnosisModal({ diagnosis, onClose }: { diagnosis: TlsDiagnosis | null; onClose: () => void }) {
  return (
    <Modal opened={diagnosis !== null} onClose={onClose} title="TLS diagnosis" size="lg">
      {diagnosis && <Stack gap="md">
        <Text size="sm" c={diagnosis.valid ? 'green' : 'red'}>{diagnosis.valid ? 'Certificate validation passed.' : diagnosis.error ?? 'Certificate validation failed.'}</Text>
        {diagnosis.errors.map((error) => <Text key={error} size="sm" c="red">{error}</Text>)}
        {diagnosis.certificates.map((certificate) => (
          <Box key={certificate.thumbprint} p="sm" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 'var(--mantine-radius-sm)' }}>
            <Text size="sm" fw={600}>{certificate.subject}</Text>
            <Text size="xs" c="dimmed">Issuer: {certificate.issuer}</Text>
            <Text size="xs" c="dimmed">Valid: {new Date(certificate.notBefore).toLocaleString()} - {new Date(certificate.notAfter).toLocaleString()}</Text>
            <Text size="xs" ff="var(--mono)" mt={4}>Thumbprint: {certificate.thumbprint}</Text>
          </Box>
        ))}
      </Stack>}
    </Modal>
  )
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

