import {
  Badge, Box, Button, Code, Group, Loader, Menu, SegmentedControl, Select, Stack, Tabs, TagsInput, Text, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import { Dropzone } from '@mantine/dropzone'
import {
  IconBolt, IconBraces, IconCheck, IconChevronDown, IconCode, IconExternalLink, IconEye, IconFile, IconFlag, IconFolders, IconList, IconLock, IconParentheses, IconPlayerPlayFilled, IconSparkles, IconUpload, IconVariable, IconX,
} from '@tabler/icons-react'
import { useDisclosure } from '@mantine/hooks'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  CollectionDetail, CollectionSummary, ExecutionResult, HttpHeaderSpec, RenderedRequest, RequestDetail, RequestProtocol, RequestSpec, SseEvent, VariableContext, WsFrame,
} from '../api/types'
import { useActiveEnv, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { useVariableView, variableMap } from '../workspace/useVariables'
import {
  BODY_MODE_LABELS, contentTypeForBodyMode, detectBodyMode, detectRawSubType, looksLikeGraphql,
  parseFormBody, parseGraphQLBody, parseMultipartBody, serializeFormBody, serializeGraphQLBody,
  serializeMultipartBody, tryPrettyJson,
  RAW_SUB_LABELS, type BodyMode, type RawSubType,
} from './body-mode'
import { EditorShell } from './EditorShell'
import { GraphQLEditor } from './GraphQLEditor'
import { KvTable, type KvRow } from './KvTable'
import { MultipartTable } from './MultipartTable'
import { RawBodyEditor } from './RawBodyEditor'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { ResponsePanel } from './ResponsePanel'
import { SourceTab } from './SourceTab'
import { joinUrl, splitUrl } from './url-utils'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'

interface Props { path: string }

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] as const
const BODY_MODES: BodyMode[] = ['none', 'form-urlencoded', 'multipart', 'raw', 'binary', 'graphql']
const RAW_SUB_TYPES: RawSubType[] = ['json', 'text', 'xml']

export function RequestEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const collections = useTapStore((s) => s.collections)
  const auths = useTapStore((s) => s.auths)
  const openTab = useTapStore((s) => s.openTab)
  const activeEnv = useActiveEnv()
  const tagSuggestions = useTagDictionary()

  const [detail, setDetail] = useState<RequestDetail | null>(null)
  const [spec, setSpec] = useState<RequestSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<RequestSpec | null>(null)
  const [tab, setTab] = useState<string | null>('params')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  const [rendered, setRendered] = useState<RenderedRequest | null>(null)
  const [execution, setExecution] = useState<ExecutionResult | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState<'render' | 'send' | null>(null)
  // Set when the user aborts an in-flight Send — keeps the partial result on screen but
  // tags it so the panel can show a "cancelled" marker instead of pretending it finished.
  const [stopped, setStopped] = useState(false)
  const [stage, setStage] = useState<string | null>(null)
  const [varsOpened, varsCtl] = useDisclosure(false)
  // Holds the in-flight stream controller so we can abort on Send-again / close.
  const streamCtrlRef = useRef<AbortController | null>(null)
  // Wall-clock start of the current Send — lets stop() fill in an approximate duration
  // since the server's `done` event (which carries the real timing) never arrives on abort.
  const sendStartRef = useRef(0)

  useEffect(() => {
    let cancelled = false
    setError(null); setRendered(null); setExecution(null); setActionError(null); setStopped(false)
    api.request(path).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d, path)
      setSpec(initial); setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [path, generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

  function update<K extends keyof RequestSpec>(key: K, value: RequestSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
  }

  async function save() {
    if (!spec) return
    setSaving(true); setError(null)
    try { await api.saveRequestSpec(spec); setSavedSpec(spec) }
    catch (e) { setError(e instanceof ApiError ? e.message : String(e)) }
    finally { setSaving(false) }
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

  async function render() {
    setBusy('render'); setActionError(null); setRendered(null)
    try { setRendered(await api.render(path, activeEnv, effectiveStage)) }
    catch (e) { setActionError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(null) }
  }

  function abortStream() {
    streamCtrlRef.current?.abort()
    streamCtrlRef.current = null
  }

  // User-initiated cancel of an in-flight Send. Unlike Close, this keeps the partial
  // response visible — we just abort the stream, stamp an approximate duration, and flip
  // the panel out of its "streaming…" state into a "cancelled" one.
  function stop() {
    if (busy !== 'send') return
    abortStream()
    const elapsed = sendStartRef.current ? Math.max(0, Date.now() - sendStartRef.current) : 0
    setExecution((cur) => (cur ? { ...cur, durationMs: cur.durationMs || elapsed } : cur))
    setStopped(true)
    setBusy(null)
  }

  // Cancel any in-flight stream on unmount or when the request file changes.
  useEffect(() => () => abortStream(), [path])

  async function send() {
    abortStream()
    sendStartRef.current = Date.now()
    setBusy('send'); setActionError(null); setExecution(null); setStopped(false)

    // Build up the ExecutionResult progressively as `meta`, `body`/`sse`/`ws`, and `done`
    // events flow in. We hand the UI the latest snapshot on every event so SSE/WS frames
    // appear in the panel as the upstream emits them.
    const snapshot: ExecutionResult = {
      status: 0, statusText: null, url: '', method: '',
      requestHeaders: {}, requestBody: null,
      responseHeaders: {}, responseBody: null,
      contentType: null, responseBodyBytes: 0, durationMs: 0,
      variablesUsed: [], stage: null, error: null,
      protocol: (spec?.protocol ?? 'http') as RequestProtocol,
    }
    const sseAccum: SseEvent[] = []
    const wsAccum: WsFrame[] = []

    streamCtrlRef.current = api.executeStream(path, activeEnv, effectiveStage, (ev) => {
      switch (ev.kind) {
        case 'meta':
          Object.assign(snapshot, {
            method: ev.payload.method,
            url: ev.payload.url,
            status: ev.payload.status,
            statusText: ev.payload.statusText,
            requestHeaders: ev.payload.requestHeaders,
            requestBody: ev.payload.requestBody,
            responseHeaders: ev.payload.responseHeaders,
            contentType: ev.payload.contentType,
            protocol: ev.payload.protocol,
            authStatus: ev.payload.authStatus,
          })
          setRendered({
            method: ev.payload.method,
            url: ev.payload.url,
            headers: ev.payload.requestHeaders,
            body: ev.payload.requestBody,
            variablesUsed: [],
            stage: null,
            protocol: ev.payload.protocol,
          })
          setExecution({ ...snapshot })
          break
        case 'body':
          snapshot.responseBody = ev.payload.responseBody
          snapshot.responseBodyBytes = ev.payload.responseBodyBytes
          setExecution({ ...snapshot })
          break
        case 'sse':
          sseAccum.push(ev.payload)
          // New array reference so React notices the change.
          setExecution({ ...snapshot, sseEvents: [...sseAccum] })
          break
        case 'ws':
          wsAccum.push(ev.payload)
          setExecution({ ...snapshot, wsFrames: [...wsAccum] })
          break
        case 'done':
          snapshot.durationMs = ev.payload.durationMs
          snapshot.responseBodyBytes = ev.payload.responseBodyBytes || snapshot.responseBodyBytes
          snapshot.variablesUsed = ev.payload.variablesUsed
          snapshot.stage = ev.payload.stage
          if (ev.payload.error) snapshot.error = ev.payload.error
          setExecution({
            ...snapshot,
            sseEvents: sseAccum.length > 0 ? [...sseAccum] : undefined,
            wsFrames: wsAccum.length > 0 ? [...wsAccum] : undefined,
          })
          setBusy(null)
          streamCtrlRef.current = null
          break
        case 'error':
          snapshot.error = ev.payload.message
          setActionError(ev.payload.message)
          setExecution({ ...snapshot })
          setBusy(null)
          streamCtrlRef.current = null
          break
      }
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

  return (
    <>
    <EditorShell
      title={spec.name || basename(path)}
      kindLabel="Request"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={() => setSpec(savedSpec)}
      onTitleChange={(n) => update('name', n)}
      toolbarExtras={
        <Button variant="default" size="xs" leftSection={<IconEye size={14} />} onClick={render} disabled={busy !== null || dirty}>
          Preview
        </Button>
      }
      bottomPane={
        // Only mount the response pane when there's actually something to show — keeps
        // the request editor full-height until the user clicks Send / Preview, and lets
        // the × close button collapse it back.
        (execution || rendered || actionError || busy !== null) ? (
          <ResponsePanel
            rendered={rendered}
            execution={execution}
            error={actionError}
            busy={busy !== null}
            stopped={stopped}
            onStop={busy === 'send' ? stop : undefined}
            requestPath={path}
            requestName={spec.name || basename(path)}
            requestAuth={spec.auth ?? null}
            onClose={() => { abortStream(); setExecution(null); setRendered(null); setActionError(null); setBusy(null); setStopped(false) }}
          />
        ) : undefined
      }
    >
      <Group gap="xs" mb="md" align="center" wrap="nowrap">
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
        {busy === 'send' ? (
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
            disabled={busy !== null || dirty}
            title={dirty ? 'Save first to send the latest version' : 'Send the request'}
          >
            Send
          </Button>
        )}
      </Group>

      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="md">
          <Tabs.Tab value="params" leftSection={<IconParentheses size={14} />}>
            Params {queryRows.length > 0 && <Text component="span" c="dimmed" ml={6}>{queryRows.length}</Text>}
          </Tabs.Tab>
          <Tabs.Tab value="headers" leftSection={<IconList size={14} />}>
            Headers {headersOnly.length > 0 && <Text component="span" c="dimmed" ml={6}>{headersOnly.length}</Text>}
          </Tabs.Tab>
          <Tabs.Tab value="body" leftSection={<IconBraces size={14} />}>Body</Tabs.Tab>
          <Tabs.Tab value="auth" leftSection={<IconLock size={14} />}>Auth</Tabs.Tab>
          <Tabs.Tab value="vars" leftSection={<IconVariable size={14} />}>
            Variables {Object.keys(spec.vars ?? {}).length > 0 && <Text component="span" c="dimmed" ml={6}>{Object.keys(spec.vars!).length}</Text>}
          </Tabs.Tab>
          <Tabs.Tab value="meta" leftSection={<IconFlag size={14} />}>Meta</Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

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
                  ...auths.map((a) => ({ value: relativizeFrom(path, a.path), label: a.name })),
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

        <Tabs.Panel value="source">
          <SourceTab path={path} source={detail.source} />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    </>
  )
}

function CollectionLinkChip({ summary, stage, onStageChange, variableContext, onOpen }: {
  summary: CollectionSummary
  stage: string | null
  onStageChange: (next: string | null) => void
  variableContext: VariableContext
  onOpen: () => void
}) {
  const generation = useTapStore((s) => s.generation)
  const view = useVariableView(variableContext)
  const vars = useMemo(() => variableMap(view), [view])
  // Stage can override the collection-level baseUrl entirely. Fetch CollectionDetail to
  // pick up per-stage overrides so changing the stage actually moves the URL.
  const [detail, setDetail] = useState<CollectionDetail | null>(null)
  useEffect(() => {
    let cancelled = false
    api.collectionDetail(summary.slug).then((d) => { if (!cancelled) setDetail(d) }).catch(() => {})
    return () => { cancelled = true }
  }, [summary.slug, generation])
  const baseTemplate = useMemo(() => {
    if (!stage || !detail) return summary.baseUrl
    const s = detail.stages.find((x) => x.name === stage)
    return s?.baseUrl ?? summary.baseUrl
  }, [detail, stage, summary.baseUrl])
  const display = useMemo(() => resolveTokens(baseTemplate, vars), [baseTemplate, vars])
  const hasUnresolved = /\{\{[^}]+\}\}/.test(display)
  const tooltip = baseTemplate === display
    ? summary.name
    : `${summary.name} — template: ${baseTemplate}`

  return (
    <Group
      gap={0}
      wrap="nowrap"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        overflow: 'hidden',
      }}
    >
      <Tooltip label={`Open collection · ${tooltip}`} withArrow openDelay={400}>
        <UnstyledButton
          onClick={onOpen}
          px="sm"
          style={{
            display: 'flex', alignItems: 'center', gap: 6,
            height: 34, color: 'var(--mantine-color-text)',
          }}
        >
          <IconFolders size={13} opacity={0.6} />
          <Text component="span" size="sm" ff="var(--mono)" c={hasUnresolved ? 'dimmed' : undefined}>
            {display || '(no baseUrl)'}
          </Text>
        </UnstyledButton>
      </Tooltip>
      {summary.stageNames.length > 0 && (
        <Menu shadow="md" position="bottom-end" withinPortal>
          <Menu.Target>
            <UnstyledButton
              aria-label="Select stage"
              title={stage ? `Stage: ${stage}` : 'Select stage'}
              style={{
                display: 'flex', alignItems: 'center', gap: 4,
                height: 34, padding: '0 8px',
                borderLeft: '1px solid var(--mantine-color-default-border)',
                color: 'var(--mantine-color-dimmed)',
              }}
            >
              {stage && <Text size="xs" c="dimmed" ff="var(--mono)">{stage}</Text>}
              <IconChevronDown size={12} />
            </UnstyledButton>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Stage</Menu.Label>
            <Menu.Item
              onClick={() => onStageChange(null)}
              rightSection={stage === null ? <IconCheck size={12} /> : null}
            >
              (no stage)
            </Menu.Item>
            {summary.stageNames.map((s) => (
              <Menu.Item
                key={s}
                onClick={() => onStageChange(s)}
                rightSection={stage === s ? <IconCheck size={12} /> : null}
              >
                {s}
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      )}
    </Group>
  )
}

function resolveTokens(text: string, vars: Map<string, { value: string | null; isSensitive: boolean }>): string {
  return text.replace(/(\$?)\{\{\s*([^}]+?)\s*\}\}/g, (_, dollar: string, name: string) => {
    if (dollar === '$') return '***'
    const v = vars.get(name)
    if (!v) return `{{${name}}}`
    return v.isSensitive ? '***' : (v.value ?? '')
  })
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
  }
}

function basename(p: string): string { return p.split('/').pop() ?? p }

function relativizeFrom(from: string, to: string): string {
  const fromParts = from.split('/').slice(0, -1)
  const toParts = to.split('/')
  let i = 0
  while (i < fromParts.length && i < toParts.length - 1 && fromParts[i] === toParts[i]) i++
  return '../'.repeat(fromParts.length - i) + toParts.slice(i).join('/')
}
