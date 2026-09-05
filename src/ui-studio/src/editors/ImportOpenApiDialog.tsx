import {
  Alert, Badge, Button, Checkbox, Code, Divider, FileButton, Group, Loader, Modal, Radio,
  ScrollArea, SegmentedControl, Select, Stack, Stepper, Text, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertTriangle, IconApi, IconCheck, IconFileCode, IconLock, IconSearch, IconSend, IconSparkles,
  IconUpload, IconWorld,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  OpenApiDiagnostic, OpenApiDocument, OpenApiImportMode, OpenApiLayout, OpenApiLink, OpenApiOperation,
  OpenApiSuggestion,
} from '../api/types'
import { useTapStore } from '../store'
import { methodColor } from './methodColor'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  onImported: (collectionPath: string, slug: string) => void
  /** Pre-fills the slug when launched from a collection's context menu. */
  initialSlug?: string | null
}

type SourceMode = 'file' | 'url'

const NO_AUTH = '__none__'

export function ImportOpenApiDialog({ open, onOpenChange, onImported, initialSlug }: Props) {
  const reload = useTapStore((s) => s.reload)

  const [step, setStep] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Step 0 — source
  const [sourceMode, setSourceMode] = useState<SourceMode>('file')
  const [url, setUrl] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)

  // Step 1 — the staged document + selection
  const [doc, setDoc] = useState<OpenApiDocument | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')

  // Step 2 — options
  const [slug, setSlug] = useState('')
  const [layout, setLayout] = useState<OpenApiLayout>('req')
  const [baseUrl, setBaseUrl] = useState('')
  const [authScheme, setAuthScheme] = useState<string>(NO_AUTH)
  const [includeOptionalQuery, setIncludeOptionalQuery] = useState(false)
  const [mode, setMode] = useState<OpenApiImportMode>('create')

  const [warnings, setWarnings] = useState<string[] | null>(null)

  // Optional AI assist. Everything below works with this untouched — which is the default,
  // since AI is unconfigured until the user sets up a CLI in Settings.
  const [suggestions, setSuggestions] = useState<OpenApiSuggestion[] | null>(null)
  const [suggesting, setSuggesting] = useState(false)
  const [suggestNote, setSuggestNote] = useState<string | null>(null)

  // What already exists at the target slug, so the options step can offer the right choice
  // instead of failing the import and making the user guess.
  const [existingSlugs, setExistingSlugs] = useState<string[]>([])
  const [link, setLink] = useState<OpenApiLink | null>(null)

  useEffect(() => {
    if (!open) return
    api.collections().then((c) => setExistingSlugs(c.map((x) => x.slug))).catch(() => setExistingSlugs([]))
  }, [open])

  const targetExists = existingSlugs.includes(slug.trim())

  // Launched from a collection's context menu: show what it is already linked to, and default to
  // adding rather than replacing.
  useEffect(() => {
    if (!open || !initialSlug) { setLink(null); return }
    api.openApiLink(initialSlug).then(setLink).catch(() => setLink(null))
  }, [open, initialSlug])

  // Adding to a collection that exists is almost always the intent when the slug already matches;
  // replacing is destructive enough to be an explicit choice.
  useEffect(() => {
    setMode((cur) => (cur === 'replace' ? cur : targetExists ? 'merge' : 'create'))
  }, [targetExists])

  useEffect(() => {
    if (!open) return
    reset()
    if (initialSlug) setSlug(initialSlug)
    // Only when the dialog transitions to open — re-running on every prop change would
    // wipe what the user has typed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  function reset() {
    setStep(0); setBusy(false); setError(null)
    setSourceMode('file'); setUrl(''); setFileName(null)
    setDoc(null); setSelected(new Set()); setFilter('')
    setSlug(''); setLayout('req'); setBaseUrl(''); setAuthScheme(NO_AUTH)
    setIncludeOptionalQuery(false); setMode('create'); setWarnings(null); setLink(null)
    setSuggestions(null); setSuggesting(false); setSuggestNote(null)
  }

  /** Applies a freshly staged document: everything after step 0 defaults from it. */
  function applyDocument(next: OpenApiDocument) {
    setDoc(next)
    setSelected(new Set(next.operations.map((o) => o.opKey)))
    setSlug((cur) => cur || next.suggestedSlug)
    setBaseUrl(next.servers[0]?.url ?? '')
    const mappable = next.securitySchemes.find((s) => s.tapAuthType)
    setAuthScheme(mappable ? mappable.key : NO_AUTH)
    setStep(1)
  }

  async function stageFile(file: File | null) {
    if (!file) return
    setError(null); setBusy(true); setFileName(file.name)
    try {
      // Sent verbatim — the server is the single source of truth for parsing, and it has to
      // be, because the browser has no YAML parser.
      applyDocument(await api.stageOpenApiDocument(await file.text(), file.name))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  /** `override` lets the "fetch it again" shortcut skip a render cycle waiting for `url` state. */
  async function stageUrl(override?: string) {
    const target = (override ?? url).trim()
    if (!target) { setError('Enter the URL of an OpenAPI document.'); return }
    setError(null); setBusy(true)
    try {
      applyDocument(await api.fetchOpenApiDocument(target))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  async function runImport() {
    if (!doc) return
    if (!slug.trim()) { setError('Give the collection a name.'); return }
    if (selected.size === 0) { setError('Select at least one operation.'); return }

    setError(null); setBusy(true)
    try {
      const result = await api.importOpenApiCollection({
        documentId: doc.documentId,
        slug: slug.trim(),
        layout,
        // Null means "everything" on the wire; send explicit keys unless nothing is filtered out.
        operationKeys: selected.size === doc.operations.length ? null : [...selected],
        baseUrl: baseUrl.trim() || null,
        securitySchemeKey: authScheme === NO_AUTH ? null : authScheme,
        linkAuthPath: null,
        includeOptionalQueryParams: includeOptionalQuery,
        variableDefaults: suggestions?.length
          ? Object.fromEntries(suggestions.map((s) => [s.opKey, s.values]))
          : null,
        mode,
      })
      await reload()
      onImported(result.collectionPath, result.slug)
      // Warnings keep the dialog open so they're actually read; a clean import just closes.
      if (result.warnings.length > 0) { setWarnings(result.warnings); setBusy(false); return }
      onOpenChange(false)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      setBusy(false)
    }
  }

  async function suggestValues() {
    if (!doc) return
    setSuggesting(true); setSuggestNote(null)
    try {
      const keys = selected.size === doc.operations.length ? null : [...selected]
      const r = await api.suggestOpenApiValues(doc.documentId, keys)
      setSuggestions(r.suggestions)
      const filled = r.suggestions.reduce((n, s) => n + Object.keys(s.values).length, 0)
      setSuggestNote(r.suggestions.length === 0
        ? `${r.provider} had nothing to suggest for these operations.`
        : `${r.provider} filled ${filled} value${filled === 1 ? '' : 's'} across `
          + `${r.suggestions.length} request${r.suggestions.length === 1 ? '' : 's'}.`)
    } catch (e) {
      // 503 means AI simply isn't set up. That's a normal state, not a failure of the import.
      setSuggestNote(e instanceof ApiError && e.status === 503
        ? e.message
        : `Couldn't get suggestions: ${e instanceof Error ? e.message : String(e)}`)
    } finally { setSuggesting(false) }
  }

  const suggestedCount = suggestions?.reduce((n, s) => n + Object.keys(s.values).length, 0) ?? 0

  const byTag = useMemo(() => groupByTag(doc?.operations ?? [], filter), [doc, filter])

  // Reaching this step means the document parsed, so every diagnostic on it is advisory by
  // definition — a spec missing `responses` still imports perfectly well. Presenting them as
  // "errors" would say the opposite of what is about to happen. Identical messages are collapsed
  // with a count: one omission repeated across 40 operations is one problem, not 40.
  const issues = useMemo(() => collapse(doc?.diagnostics ?? []), [doc])

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      size="xl"
      title={
        <Group gap={6}>
          <IconApi size={16} />
          <Text fw={600}>Import from OpenAPI</Text>
          {doc && <Badge size="sm" variant="light">{doc.title}</Badge>}
          {doc && <Badge size="sm" variant="dot" color="gray">OpenAPI {doc.specVersion}</Badge>}
        </Group>
      }
    >
      <Stepper active={step} onStepClick={(s) => doc && setStep(s)} size="xs" mb="md">
        <Stepper.Step label="Source" description="File or URL" />
        <Stepper.Step label="Operations" description="Pick what to import" />
        <Stepper.Step label="Options" description="Layout and auth" />
      </Stepper>

      {error && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} mb="md" title="Import failed">
          {error}
        </Alert>
      )}

      {warnings && warnings.length > 0 && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} mb="md" title="Imported with warnings">
          <Stack gap={4}>
            {warnings.map((w, i) => <Text key={i} size="xs">{w}</Text>)}
          </Stack>
          <Group justify="flex-end" mt="sm">
            <Button size="xs" onClick={() => { onOpenChange(false); reset() }}>Done</Button>
          </Group>
        </Alert>
      )}

      {/* ---- Step 0: source ---------------------------------------------------------- */}
      {step === 0 && (
        <Stack gap="md">
          {/* Launched from a collection that was imported before: the fastest path is almost always
              "the same document again", so offer it as one click rather than making the user find
              the URL. This is also the seam re-sync will hang off. */}
          {link && (
            <Alert color="tap" icon={<IconApi size={16} />} title={`“${link.slug}” is linked to an OpenAPI document`}>
              <Stack gap={6}>
                <Text size="xs">
                  {link.url ?? link.fileName ?? 'an uploaded file'}
                  {link.apiVersion && ` · version ${link.apiVersion}`}
                  {` · ${link.trackedOperations} tracked operation${link.trackedOperations === 1 ? '' : 's'}`}
                </Text>
                <Text size="xs" c="dimmed">
                  Imported {new Date(link.fetchedAt).toLocaleString()} as{' '}
                  {link.layout === 'http' ? 'one .http file per tag' : 'one .req.tap per operation'}.
                </Text>
                {link.url && (
                  <Group>
                    <Button
                      size="xs"
                      variant="light"
                      loading={busy}
                      leftSection={<IconWorld size={14} />}
                      onClick={() => { setSourceMode('url'); setUrl(link.url!); void stageUrl(link.url!) }}
                    >
                      Fetch it again
                    </Button>
                  </Group>
                )}
              </Stack>
            </Alert>
          )}

          <SegmentedControl
            value={sourceMode}
            onChange={(v) => { setSourceMode(v as SourceMode); setError(null) }}
            data={[
              { value: 'file', label: 'Upload a file' },
              { value: 'url', label: 'Fetch a URL' },
            ]}
          />

          {sourceMode === 'file' ? (
            <Stack gap="xs">
              <Group gap="sm">
                <FileButton onChange={stageFile} accept=".json,.yaml,.yml,application/json,text/yaml">
                  {(props) => (
                    <Button {...props} leftSection={<IconUpload size={14} />} variant="light" loading={busy}>
                      Choose a spec file
                    </Button>
                  )}
                </FileButton>
                {fileName && <Text size="sm" c="dimmed">{fileName}</Text>}
              </Group>
              <Text size="xs" c="dimmed">
                JSON or YAML, OpenAPI 3.0 / 3.1, or Swagger 2.0 (converted on read).
              </Text>
            </Stack>
          ) : (
            <Stack gap="xs">
              <TextInput
                label="Document URL"
                placeholder="https://api.example.com/openapi.json"
                value={url}
                onChange={(e) => setUrl(e.currentTarget.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') void stageUrl() }}
                rightSection={busy ? <Loader size={14} /> : null}
              />
              <Text size="xs" c="dimmed">
                A local dev API works too — for example <Code>http://localhost:5001/openapi/v1.json</Code>.
              </Text>
              <Group justify="flex-end">
                <Button onClick={() => void stageUrl()} loading={busy} leftSection={<IconWorld size={14} />}>
                  Fetch
                </Button>
              </Group>
            </Stack>
          )}
        </Stack>
      )}

      {/* ---- Step 1: operations ------------------------------------------------------ */}
      {step === 1 && doc && (
        <Stack gap="sm">
          {issues.length > 0 && (
            <Alert
              color="yellow"
              icon={<IconAlertTriangle size={16} />}
              title={`${issues.length} thing${issues.length === 1 ? '' : 's'} to know about this document`}
            >
              <Stack gap={2}>
                {issues.slice(0, 6).map((d, i) => (
                  <Text key={i} size="xs">
                    {d.message}{d.count > 1 && <Text component="span" c="dimmed"> (×{d.count})</Text>}
                  </Text>
                ))}
                {issues.length > 6 && (
                  <Text size="xs" c="dimmed">…and {issues.length - 6} more</Text>
                )}
                <Text size="xs" c="dimmed" mt={4}>These don’t block the import.</Text>
              </Stack>
            </Alert>
          )}

          <Group justify="space-between">
            <TextInput
              placeholder="Filter by path, summary or tag"
              leftSection={<IconSearch size={14} />}
              value={filter}
              onChange={(e) => setFilter(e.currentTarget.value)}
              style={{ flex: 1 }}
            />
            <Group gap="xs">
              <Button size="xs" variant="subtle"
                onClick={() => setSelected(new Set(doc.operations.map((o) => o.opKey)))}>
                All
              </Button>
              <Button size="xs" variant="subtle" onClick={() => setSelected(new Set())}>None</Button>
            </Group>
          </Group>

          <Text size="xs" c="dimmed">
            {selected.size} of {doc.operations.length} operations selected
          </Text>

          <ScrollArea h={380} type="auto" scrollbarSize={8}>
            <Stack gap="xs" pr="sm">
              {byTag.map(([tag, ops]) => {
                const allOn = ops.every((o) => selected.has(o.opKey))
                return (
                  <Stack key={tag} gap={4}>
                    <Group gap="xs">
                      <Checkbox
                        size="xs"
                        checked={allOn}
                        indeterminate={!allOn && ops.some((o) => selected.has(o.opKey))}
                        onChange={() => setSelected((cur) => toggleMany(cur, ops, !allOn))}
                        label={<Text fw={600} size="sm">{tag}</Text>}
                      />
                      <Text size="xs" c="dimmed">{ops.length}</Text>
                    </Group>
                    <Stack gap={2} pl="lg">
                      {ops.map((op) => (
                        <OperationRow
                          key={op.opKey}
                          op={op}
                          checked={selected.has(op.opKey)}
                          onToggle={() => setSelected((cur) => toggleOne(cur, op.opKey))}
                        />
                      ))}
                    </Stack>
                  </Stack>
                )
              })}
              {byTag.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="xl">No operations match that filter.</Text>
              )}
            </Stack>
          </ScrollArea>
        </Stack>
      )}

      {/* ---- Step 2: options --------------------------------------------------------- */}
      {step === 2 && doc && (
        <ScrollArea h={440} type="auto" scrollbarSize={8}>
          <Stack gap="md" pr="sm">
            <TextInput
              label="Collection name"
              description="Becomes the folder under collections/"
              value={slug}
              onChange={(e) => setSlug(e.currentTarget.value)}
              withAsterisk
            />

            <Stack gap={6}>
              <Text size="sm" fw={500}>Layout</Text>
              <Radio.Group value={layout} onChange={(v) => setLayout(v as OpenApiLayout)}>
                <Stack gap="xs">
                  <LayoutOption
                    value="req" active={layout === 'req'} icon={<IconSend size={18} />}
                    title="One .req.tap per operation"
                    detail={`${fileCount(selected.size)} — structured editing, assertions, per-request docs`}
                  />
                  <LayoutOption
                    value="http" active={layout === 'http'} icon={<IconFileCode size={18} />}
                    title="One .http file per tag"
                    detail={`${fileCount(countTags(doc, selected))} — portable, opens in Visual Studio and REST Client`}
                  />
                </Stack>
              </Radio.Group>
            </Stack>

            <Divider />

            {doc.servers.length > 0 ? (
              <Select
                label="Base URL"
                description={doc.servers.length > 1
                  ? 'The rest become environments scoped to this collection'
                  : 'From the document’s servers list'}
                data={doc.servers.map((s) => ({
                  value: s.url,
                  label: s.description ? `${s.url} — ${s.description}` : s.url,
                }))}
                value={baseUrl}
                onChange={(v) => setBaseUrl(v ?? '')}
                allowDeselect={false}
                searchable
              />
            ) : (
              <TextInput
                label="Base URL"
                description="The document declares no servers"
                placeholder="https://api.example.com"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.currentTarget.value)}
              />
            )}

            <Select
              label="Auth profile"
              description="Generated with variable placeholders — no credentials are ever taken from the spec"
              leftSection={<IconLock size={14} />}
              data={[
                { value: NO_AUTH, label: 'None' },
                ...doc.securitySchemes.map((s) => ({
                  value: s.key,
                  label: s.tapAuthType
                    ? `${s.key} — ${s.description ?? s.type}`
                    : `${s.key} — ${s.type} (not supported)`,
                  disabled: !s.tapAuthType,
                })),
              ]}
              value={authScheme}
              onChange={(v) => setAuthScheme(v ?? NO_AUTH)}
              allowDeselect={false}
            />

            {doc.securitySchemes.filter((s) => s.warning).map((s) => (
              <Text key={s.key} size="xs" c="dimmed">{s.key}: {s.warning}</Text>
            ))}

            <Divider />

            {/* Optional: propose values for the generated variables, reusing workspace variables
                where one already means the right thing. Never on the critical path. */}
            <Stack gap={6}>
              <Group gap="xs">
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconSparkles size={14} />}
                  loading={suggesting}
                  onClick={suggestValues}
                >
                  Suggest values with AI
                </Button>
                {suggestedCount > 0 && (
                  <Badge size="sm" variant="light" color="green">
                    {suggestedCount} value{suggestedCount === 1 ? '' : 's'} ready
                  </Badge>
                )}
              </Group>
              {suggestNote && <Text size="xs" c="dimmed">{suggestNote}</Text>}
              {suggestedCount === 0 && !suggestNote && (
                <Text size="xs" c="dimmed">
                  Fills path and query variables — reusing a workspace variable when one matches,
                  otherwise sample data. Optional; the import works without it.
                </Text>
              )}
            </Stack>

            <Divider />

            <Checkbox
              label="Include optional query parameters"
              description="Adds them to the URL and declares them as variables"
              checked={includeOptionalQuery}
              onChange={(e) => setIncludeOptionalQuery(e.currentTarget.checked)}
            />
            {targetExists && (
              <Stack gap={6}>
                <Text size="sm" fw={500}>A collection named “{slug.trim()}” already exists</Text>
                <Radio.Group value={mode} onChange={(v) => setMode(v as OpenApiImportMode)}>
                  <Stack gap="xs">
                    <Radio
                      value="merge"
                      label="Add to it"
                      description="Writes these operations into the existing collection and leaves everything else alone"
                    />
                    <Radio
                      value="replace"
                      label="Replace it"
                      description="Deletes the folder first — hand-written assertions and any requests not in this document are lost"
                    />
                  </Stack>
                </Radio.Group>
              </Stack>
            )}
          </Stack>
        </ScrollArea>
      )}

      {/* ---- Footer ------------------------------------------------------------------ */}
      <Group justify="space-between" mt="lg">
        <Button variant="subtle" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
        <Group gap="xs">
          {step > 0 && <Button variant="default" onClick={() => setStep(step - 1)} disabled={busy}>Back</Button>}
          {step === 1 && (
            <Button onClick={() => setStep(2)} disabled={selected.size === 0}>
              Next
            </Button>
          )}
          {step === 2 && (
            <Button onClick={runImport} loading={busy} leftSection={<IconCheck size={14} />}>
              Import {selected.size} {selected.size === 1 ? 'operation' : 'operations'}
            </Button>
          )}
        </Group>
      </Group>
    </Modal>
  )
}

function OperationRow({ op, checked, onToggle }: { op: OpenApiOperation; checked: boolean; onToggle: () => void }) {
  return (
    <UnstyledButton onClick={onToggle} px={4} py={2} style={{ borderRadius: 4 }}>
      <Group gap="xs" wrap="nowrap">
        <Checkbox size="xs" checked={checked} onChange={onToggle} tabIndex={-1} />
        <Badge size="xs" variant="light" color={methodColor(op.method)} w={58}>
          {op.method}
        </Badge>
        <Text size="xs" ff="var(--mono)" style={{ flexShrink: 0 }}>{op.path}</Text>
        {op.summary && (
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{op.summary}</Text>
        )}
        {op.deprecated && <Badge size="xs" variant="light" color="red">deprecated</Badge>}
        {op.hasRequestBody && (
          <Tooltip label="Has a request body — an example is generated from its schema">
            <Badge size="xs" variant="dot" color="blue">body</Badge>
          </Tooltip>
        )}
      </Group>
    </UnstyledButton>
  )
}

function LayoutOption(
  { value, active, icon, title, detail }:
  { value: string; active: boolean; icon: React.ReactNode; title: string; detail: string },
) {
  return (
    <UnstyledButton
      component="label"
      p="sm"
      style={{
        border: `1px solid ${active ? 'var(--mantine-color-tap-filled)' : 'var(--mantine-color-default-border)'}`,
        borderRadius: 8,
        background: active ? 'var(--mantine-color-tap-light)' : 'transparent',
      }}
    >
      <Group gap="sm" wrap="nowrap">
        <Radio value={value} />
        {icon}
        <Stack gap={2}>
          <Text size="sm" fw={600}>{title}</Text>
          <Text size="xs" c="dimmed">{detail}</Text>
        </Stack>
      </Group>
    </UnstyledButton>
  )
}

/** Groups by first tag, matching how the API's own docs are organized, and how the importer
 *  lays the files out. Untagged operations collect under "api". */
function groupByTag(operations: OpenApiOperation[], filter: string): [string, OpenApiOperation[]][] {
  const needle = filter.trim().toLowerCase()
  const matches = needle
    ? operations.filter((o) =>
        o.path.toLowerCase().includes(needle)
        || (o.summary ?? '').toLowerCase().includes(needle)
        || o.method.toLowerCase().includes(needle)
        || o.tags.some((t) => t.toLowerCase().includes(needle)))
    : operations

  const groups = new Map<string, OpenApiOperation[]>()
  for (const op of matches) {
    const tag = op.tags[0] ?? 'api'
    const bucket = groups.get(tag)
    if (bucket) bucket.push(op)
    else groups.set(tag, [op])
  }
  return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))
}

const fileCount = (n: number) => `${n} ${n === 1 ? 'file' : 'files'}`

/** Collapses repeated diagnostics into one row per distinct message, keeping document order. */
function collapse(diagnostics: OpenApiDiagnostic[]): { message: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const d of diagnostics) counts.set(d.message, (counts.get(d.message) ?? 0) + 1)
  return [...counts.entries()].map(([message, count]) => ({ message, count }))
}

function countTags(doc: OpenApiDocument, selected: Set<string>): number {
  const tags = new Set(
    doc.operations.filter((o) => selected.has(o.opKey)).map((o) => o.tags[0] ?? 'api'))
  return tags.size
}

function toggleOne(current: Set<string>, key: string): Set<string> {
  const next = new Set(current)
  if (!next.delete(key)) next.add(key)
  return next
}

function toggleMany(current: Set<string>, ops: OpenApiOperation[], on: boolean): Set<string> {
  const next = new Set(current)
  for (const op of ops) {
    if (on) next.add(op.opKey)
    else next.delete(op.opKey)
  }
  return next
}
