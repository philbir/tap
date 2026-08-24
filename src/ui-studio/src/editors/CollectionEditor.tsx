import {
  ActionIcon, Alert, Badge, Box, Button, Checkbox, Code, Group, NumberInput, Paper, Select, Stack,
  Switch, Tabs, TagsInput, Text, TextInput, Tooltip,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconApi, IconCode, IconFileCode, IconFileText, IconLayoutDashboard, IconList, IconPlus, IconRefresh, IconRocket,
  IconShieldCheck, IconVariable, IconWorld, IconX,
} from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import { envBindingFor } from '../api/types'
import type {
  AuthSummary, CollectionDetail, CollectionSpec, CollectionSummary, EnvCollection, EnvSummary,
  OpenApiLink, VariableContext,
} from '../api/types'
import { useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { AdaptiveTabsList } from './AdaptiveTabsList'
import { authSelectGroups } from './authOptions'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount, TabDot } from './EditorShell'
import { saveEnvAssignment } from './envSpec'
import { HistorySettings } from './HistorySettings'
import { ImportOpenApiDialog } from './ImportOpenApiDialog'
import { KvTable, type KvRow } from './KvTable'
import { ResyncOpenApiDialog } from './ResyncOpenApiDialog'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { SourceTab } from './SourceTab'
import { restoreDraft, usePublishDraft } from './useDraft'
import { useTabView } from './useTabView'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'
import { COLLECTION_FILE, fileNameFor } from '../shell/tapFiles'

interface Props {
  /** Workspace-relative path of the collection directory (e.g. `collections/demo`). */
  path: string
}

/** Collection editor. The on-disk metadata file lives at `<path>/_collection.tap`; the
 *  collection owns the base URL, default auth + headers, plus vars/tags. Requests living
 *  under the collection inherit all of it.
 *
 *  Per-target overrides — what `stages:` used to hold — are environments scoped to this
 *  collection. They are separate files, so the Environments tab lists them rather than
 *  editing them inline. */
export function CollectionEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const reload = useTapStore((s) => s.reload)
  const auths = useTapStore((s) => s.auths)
  const collections = useTapStore((s) => s.collections)
  const envs = useTapStore((s) => s.envs)
  const openTab = useTapStore((s) => s.openTab)
  const tagSuggestions = useTagDictionary()
  const slug = useMemo(() => path.split('/').pop() ?? path, [path])
  // Auth refs in the collection file are relative to `collections/<slug>/_collection.tap`.
  const collectionFilePath = `${path}/${COLLECTION_FILE}`
  // Split by whether the env holds an assignment to this collection. Assigned ones are what
  // this tab edits; the rest — globals and other collections' — are what it can assign.
  const assignedEnvs = useMemo(
    () => envs.filter((e) => envBindingFor(e, slug) !== null),
    [envs, slug],
  )
  const unassignedEnvs = useMemo(
    () => envs.filter((e) => envBindingFor(e, slug) === null),
    [envs, slug],
  )

  const [detail, setDetail] = useState<CollectionDetail | null>(null)
  const [spec, setSpec] = useState<CollectionSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<CollectionSpec | null>(null)
  const [tab, setTab] = useTabView<string | null>(path, 'tab', 'general')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  const [varsOpened, varsCtl] = useDisclosure(false)
  const variableContext = useMemo<VariableContext>(() => ({ collectionPath: collectionFilePath }), [collectionFilePath])

  // The OpenAPI document this collection was imported from, if any. Null both while loading
  // and when the collection was hand-written — the tab reads the same either way.
  const [link, setLink] = useState<OpenApiLink | null>(null)
  const [importOpen, importCtl] = useDisclosure(false)
  const [resyncOpen, resyncCtl] = useDisclosure(false)

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.collectionDetail(slug).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d)
      // Keeps unsaved edits across a tab switch and across the re-fetch a `generation`
      // bump forces; `savedSpec` stays whatever is actually on disk.
      setSpec(restoreDraft(path, initial))
      setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    // A missing link isn't an error — most collections have none.
    api.openApiLink(slug).then((l) => !cancelled && setLink(l)).catch(() => {})
    return () => { cancelled = true }
  }, [slug, generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])
  usePublishDraft(path, spec, dirty)

  function update<K extends keyof CollectionSpec>(key: K, value: CollectionSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
  }

  /** Creates an env already assigned to this collection, beside the collection file — so
   *  deleting the collection takes its environments with it, the way its stages went. */
  async function createScopedEnv(name: string) {
    const envPath = `${path}/${fileNameFor('env', nameToSlug(name))}`
    await api.saveEnvSpec({
      path: envPath, id: null, name,
      collections: [{ collection: slug, baseUrl: null, defaultAuth: null }],
    })
    await reload()
    openTab({ path: envPath, kind: 'env', label: name })
  }

  async function save() {
    if (!spec) return
    setSaving(true); setError(null)
    try {
      await api.saveCollectionSpec(spec)
      setSavedSpec(spec)
      await reload()
    } catch (e) { setError(e instanceof ApiError ? e.message : String(e)) }
    finally { setSaving(false) }
  }

  if (!detail || !spec) {
    return (
      <EditorShell
        title={slug}
        kindLabel="Collection"
        dirty={false} saving={saving} errorMessage={errorMessage}
        onSave={save}
      >
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  const headerRows: KvRow[] = Object.entries(spec.defaultHeaders ?? {}).map(([k, v]) => ({ key: k, value: v }))
  const secretSet = new Set(spec.secrets ?? [])
  const varRows: KvRow[] = Object.entries(spec.vars ?? {}).map(([k, v]) => ({
    key: k, value: v, secret: secretSet.has(k),
  }))

  return (
    <>
    <EditorShell
      title={spec.name || slug}
      kindLabel="Collection"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={() => setSpec(savedSpec)}
      onTitleChange={(n) => update('name', n)}
    >
      <Tabs value={tab} onChange={setTab}>
        {/* Ordered left-to-right by how often a section is touched: the tail (Docs, OpenAPI,
            Source) is what AdaptiveTabsList strips down to icons first when space runs out. */}
        <AdaptiveTabsList
          mb="md"
          tabs={[
            { value: 'general', label: 'General', icon: <IconLayoutDashboard size={14} /> },
            { value: 'headers', label: 'Headers', icon: <IconList size={14} />, adornment: <TabCount count={headerRows.length} /> },
            { value: 'transport', label: 'Transport', icon: <IconShieldCheck size={14} /> },
            { value: 'variables', label: 'Variables', icon: <IconVariable size={14} />, adornment: <TabCount count={varRows.length} /> },
            { value: 'environments', label: 'Environments', icon: <IconRocket size={14} />, adornment: <TabCount count={assignedEnvs.length} /> },
            { value: 'docs', label: 'Docs', icon: <IconFileText size={14} />, adornment: <TabDot active={!!spec.body && spec.body.trim().length > 0} /> },
            { value: 'openapi', label: 'OpenAPI', icon: <IconApi size={14} />, adornment: <TabDot active={!!link} /> },
            { value: 'source', label: 'Source', icon: <IconCode size={14} /> },
          ]}
        />

        <Tabs.Panel value="general">
          <Stack gap="md" maw={760}>
            <TextInput
              label="Name"
              description="Display name shown in the explorer and tabs."
              value={spec.name}
              onChange={(e) => update('name', e.currentTarget.value)}
            />
            <TextInput
              label="Slug"
              description="The on-disk directory name. Read-only; rename via Git."
              value={slug}
              readOnly
            />
            <Box>
              <Text size="sm" fw={500} mb={4}>Base URL</Text>
              <VariableInput
                value={spec.baseUrl ?? ''}
                onChange={(v) => update('baseUrl', v && v.length > 0 ? v : undefined)}
                placeholder="https://api.example.com"
                context={variableContext}
                onOpenVariables={varsCtl.open}
              />
              <Text size="xs" c="dimmed" mt={4}>
                Used when a request below uses a relative URL. May contain {`{{var}}`} interpolations.
              </Text>
            </Box>
            <Select
              label="Default Auth"
              description="Inherited by requests in this collection that don't override `auth:`."
              data={[
                { value: '', label: '(none)' },
                ...authSelectGroups({ auths, collections, fromPath: collectionFilePath }),
              ]}
              value={spec.defaultAuth ?? ''}
              onChange={(v) => update('defaultAuth', v && v !== '' ? v : undefined)}
              allowDeselect={false}
            />
            <TagsInput
              label="Tags"
              placeholder={(spec.tags?.length ?? 0) === 0 ? 'Add tag…' : ''}
              data={tagSuggestions}
              value={spec.tags ?? []}
              onChange={(v) => update('tags', v.length > 0 ? v : undefined)}
              acceptValueOnBlur
              clearable
            />
            <Switch
              label="Agent access"
              description="Let AI agents (MCP tools, tap-studio call) discover and send requests through this collection. Off fences it from agents; the Studio and CLI stay unaffected."
              checked={spec.agentEnabled !== false}
              onChange={(e) => update('agentEnabled', e.currentTarget.checked ? undefined : false)}
            />
            {!detail.exists && (
              <Text size="xs" c="dimmed">
                No <Code fz="xs">_collection.tap</Code> on disk yet — saving will create it.
              </Text>
            )}

            <Box>
              <Text fw={600} size="sm" mb={2}>History</Text>
              <Text size="xs" c="dimmed" mb="sm">
                Applies to every request in this collection. A request can override any of it.
              </Text>
              <HistorySettings
                value={spec.history}
                onChange={(v) => update('history', v)}
                inherited={detail.inheritedHistory}
                inheritedFrom="workspace"
              />
            </Box>
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="headers">
          <Box maw={760}>
            <Text size="xs" c="dimmed" mb="xs">
              Default headers merged into every request in this collection.
            </Text>
            <KvTable
              rows={headerRows}
              onChange={(rows) => {
                const obj: Record<string, string> = {}
                for (const r of rows) if (r.key) obj[r.key] = r.value
                update('defaultHeaders', Object.keys(obj).length > 0 ? obj : undefined)
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

        <Tabs.Panel value="transport">
          <Stack gap="md" maw={760}>
            <Text size="xs" c="dimmed">Defaults inherited by every request in this collection unless that request overrides a value.</Text>
            <Checkbox
              label="Ignore TLS certificate errors"
              description="Accept invalid, expired, or untrusted certificates. Use only for trusted development endpoints."
              checked={spec.transport?.ignoreTlsErrors === true}
              onChange={(e) => update('transport', e.currentTarget.checked ? { ...spec.transport, ignoreTlsErrors: true } : { ...spec.transport, ignoreTlsErrors: undefined })}
            />
            <NumberInput
              label="Timeout (ms)"
              description="Total request time. Leave blank for the executor default; zero disables the timeout."
              value={spec.transport?.timeoutMs ?? ''}
              min={0}
              step={1000}
              onChange={(value) => update('transport', { ...spec.transport, timeoutMs: typeof value === 'number' ? value : undefined })}
            />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="variables">
          <Stack gap="md" maw={880}>
            <Text size="xs" c="dimmed">
              Collection-scoped variables. Cascade tier between workspace and env
              (workspace &lt; <b>collection</b> &lt; env &lt; request).
              Toggle the eye icon to mark a row as a secret.
            </Text>
            <KvTable
              rows={varRows}
              onChange={(next) => {
                const obj: Record<string, string> = {}
                const sec: string[] = []
                for (const r of next) {
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
              valuePlaceholder="value"
              allowSecretToggle
              variableContext={variableContext}
              onOpenVariables={varsCtl.open}
              emptyHint="No variables defined for this collection yet."
            />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="environments">
          <ScopedEnvironments
            slug={slug}
            collectionName={spec.name || slug}
            assigned={assignedEnvs}
            unassigned={unassignedEnvs}
            auths={auths}
            collections={collections}
            onOpen={(env) => openTab({ path: env.path, kind: 'env', label: env.name })}
            onCreate={createScopedEnv}
            onChanged={reload}
          />
        </Tabs.Panel>

        <Tabs.Panel value="docs">
          <DocsEditor
            value={spec.body ?? ''}
            onChange={(v) => update('body', v.trim().length > 0 ? v : undefined)}
            emptyHint="No docs yet. Describe this collection's API and how its requests are organized."
          />
        </Tabs.Panel>

        <Tabs.Panel value="openapi">
          <OpenApiPanel
            link={link}
            onImport={importCtl.open}
            onResync={resyncCtl.open}
          />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          {detail.exists
            ? <SourceTab
                path={collectionFilePath}
                source={detail.source}
                deletable={{ kind: 'collection', path, name: spec.name || slug, slug }}
              />
            : <Text size="sm" c="dimmed">Save the collection first to view the source file.</Text>}
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    {importOpen && (
      <ImportOpenApiDialog
        open={importOpen}
        onOpenChange={(v) => !v && importCtl.close()}
        initialSlug={slug}
        onImported={importCtl.close}
      />
    )}
    {resyncOpen && (
      <ResyncOpenApiDialog
        open={resyncOpen}
        onOpenChange={(v) => !v && resyncCtl.close()}
        slug={slug}
      />
    )}
    </>
  )
}

// ---- OpenAPI ------------------------------------------------------------------------

/** Import and re-sync live here rather than on the explorer's context menu: they're
 *  occasional, collection-scoped operations, and the tab has room to show what the
 *  collection is actually linked to before you fire one. */
function OpenApiPanel({ link, onImport, onResync }: {
  link: OpenApiLink | null
  onImport: () => void
  onResync: () => void
}) {
  if (!link) {
    return (
      <Stack gap="md" maw={620}>
        <Text size="sm" c="dimmed">
          This collection isn't linked to an OpenAPI document. Import one to generate requests
          from its operations — pick which ones you want, how they're laid out, and which
          security scheme becomes the collection's default auth.
        </Text>
        <Box>
          <Button leftSection={<IconApi size={14} />} onClick={onImport}>Import from OpenAPI…</Button>
        </Box>
      </Stack>
    )
  }

  // `aspire` links carry a URL too — what matters for the icon is whether there's an address
  // to re-fetch from or just an uploaded file.
  const fromUrl = !!link.url
  return (
    <Stack gap="md" maw={620}>
      <Paper withBorder radius="sm" p="md">
        <Stack gap="xs">
          <Group gap="xs" wrap="nowrap">
            {fromUrl ? <IconWorld size={16} opacity={0.6} /> : <IconFileCode size={16} opacity={0.6} />}
            <Text size="sm" ff="var(--mono)" truncate style={{ flex: 1 }}>
              {link.url ?? link.fileName ?? link.sourceKind}
            </Text>
          </Group>
          <Group gap={6}>
            <Badge size="sm" variant="light" color="gray">OpenAPI {link.specVersion}</Badge>
            {link.apiVersion && <Badge size="sm" variant="light" color="gray">api {link.apiVersion}</Badge>}
            {link.sourceKind === 'aspire' && <Badge size="sm" variant="light" color="gray">aspire</Badge>}
            <Badge size="sm" variant="light" color="gray">{link.layout} layout</Badge>
            <Badge size="sm" variant="light" color="tap">
              {link.trackedOperations} tracked {link.trackedOperations === 1 ? 'operation' : 'operations'}
            </Badge>
          </Group>
          <Text size="xs" c="dimmed">Last synced {formatFetchedAt(link.fetchedAt)}.</Text>
        </Stack>
      </Paper>
      <Group gap="sm">
        <Button leftSection={<IconRefresh size={14} />} onClick={onResync}>Re-sync…</Button>
        <Button variant="default" leftSection={<IconApi size={14} />} onClick={onImport}>
          Import from OpenAPI…
        </Button>
      </Group>
      <Text size="xs" c="dimmed">
        Re-sync diffs the collection against the document and lets you decide, per operation,
        what to take. Importing again adds operations from any document — including a different one.
      </Text>
    </Stack>
  )
}

/** Absolute date plus a coarse relative hint — "when did I last pull this" is the question. */
function formatFetchedAt(iso: string): string {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return iso
  const days = Math.floor((Date.now() - at.getTime()) / 86_400_000)
  const rel = days <= 0 ? 'today' : days === 1 ? 'yesterday' : `${days} days ago`
  return `${at.toLocaleDateString()} (${rel})`
}

function specFromDetail(d: CollectionDetail): CollectionSpec {
  function splitVarSpec(m?: Record<string, { default?: string | null; secret?: boolean }>):
    { vars?: Record<string, string>; secrets?: string[] } {
    if (!m) return {}
    const vars: Record<string, string> = {}
    const secrets: string[] = []
    for (const [k, v] of Object.entries(m)) {
      if (v?.default != null) vars[k] = v.default
      if (v?.secret) secrets.push(k)
    }
    return {
      vars: Object.keys(vars).length > 0 ? vars : undefined,
      secrets: secrets.length > 0 ? secrets : undefined,
    }
  }

  const split = splitVarSpec(d.vars)
  return {
    slug: d.slug,
    id: d.id,
    name: d.name,
    baseUrl: d.baseUrl && d.baseUrl.length > 0 ? d.baseUrl : undefined,
    defaultAuth: d.defaultAuth ?? undefined,
    defaultHeaders: Object.keys(d.defaultHeaders ?? {}).length > 0 ? d.defaultHeaders : undefined,
    transport: d.transport ?? undefined,
    vars: split.vars,
    secrets: split.secrets,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    // Only the opt-out is carried: undefined means enabled and keeps the emitted file
    // silent, mirroring what the server writes.
    agentEnabled: d.agentEnabled === false ? false : undefined,
    history: d.history ?? undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
  }
}

// ---- Scoped environments -----------------------------------------------------------------

/**
 * The environments assigned to this collection — the replacement for the old `stages:` block.
 *
 * <p>They are separate files, so what this tab edits is the *assignment* each of them holds to
 * this collection: whether it is offered here at all, and the base URL and default auth it
 * points this collection at. Everything else about an environment — its variables, its provider
 * bindings — stays in the environment's own editor, one click away.</p>
 *
 * <p>Each row saves on its own rather than through the collection's Save button, because each
 * row is a different file. Assigning and unassigning write immediately; editing the overrides
 * arms a per-row Save, so a half-typed URL is never written behind your back.</p>
 */
function ScopedEnvironments({ slug, collectionName, assigned, unassigned, auths, collections, onOpen, onCreate, onChanged }: {
  slug: string
  collectionName: string
  /** Environments already assigned to this collection. */
  assigned: EnvSummary[]
  /** Everything else — global environments and ones assigned elsewhere — offered for assignment. */
  unassigned: EnvSummary[]
  auths: AuthSummary[]
  collections: CollectionSummary[]
  onOpen: (env: EnvSummary) => void
  onCreate: (name: string) => Promise<void>
  onChanged: () => Promise<void>
}) {
  const [name, setName] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function run(key: string, work: () => Promise<void>) {
    setBusy(key); setError(null)
    try {
      await work()
      await onChanged()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  async function create() {
    const trimmed = name.trim()
    if (!trimmed || busy !== null) return
    setBusy('__create__'); setError(null)
    try {
      await onCreate(trimmed)
      setName('')
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  return (
    <Stack gap="md" maw={880}>
      <Text size="xs" c="dimmed">
        Environments offered in <Code fz="xs">{slug}</Code>&rsquo;s picker. Each may point this
        collection at a different base URL and default auth — those settings belong to the
        assignment, so the same environment can mean something different in another collection.
      </Text>

      {error && (
        <Alert color="red" variant="light" withCloseButton onClose={() => setError(null)}>{error}</Alert>
      )}

      {assigned.length === 0 ? (
        <Text size="sm" c="dimmed">
          None assigned. Global environments still apply here — assign one below only when it
          should override this collection&rsquo;s base URL or auth, or be offered here alone.
        </Text>
      ) : (
        <Stack gap="sm">
          {assigned.map((env) => (
            <AssignmentRow
              key={env.path}
              env={env}
              slug={slug}
              collectionName={collectionName}
              auths={auths}
              collections={collections}
              busy={busy === env.path}
              onOpen={() => onOpen(env)}
              onSave={(binding) => void run(env.path, () => saveEnvAssignment(env.path, slug, binding))}
            />
          ))}
        </Stack>
      )}

      <Select
        label="Assign an existing environment"
        description="Offers it here, where you can then override the base URL and auth."
        placeholder={unassigned.length > 0 ? 'Pick an environment…' : 'All environments are assigned'}
        data={unassigned.map((e) => ({
          value: e.path,
          label: e.collections.length === 0 ? `${e.name} (global)` : e.name,
        }))}
        value={null}
        disabled={unassigned.length === 0 || busy !== null}
        onChange={(envPath) => {
          if (envPath) {
            void run(envPath, () => saveEnvAssignment(envPath, slug, {
              collection: slug, baseUrl: null, defaultAuth: null,
            }))
          }
        }}
        searchable
      />

      <Group gap="xs" align="flex-end">
        <TextInput
          label="Or create a new one"
          placeholder="uat"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void create() }}
          w={260}
        />
        <Button
          leftSection={<IconPlus size={14} />}
          onClick={() => void create()}
          loading={busy === '__create__'}
          disabled={name.trim().length === 0}
        >
          Create
        </Button>
      </Group>
    </Stack>
  )
}

/** One assigned environment, with the two overrides it holds for this collection. Dirty state
 *  is local so typing doesn't write a file per keystroke; Save writes, Revert drops. */
function AssignmentRow({ env, slug, collectionName, auths, collections, busy, onOpen, onSave }: {
  env: EnvSummary
  slug: string
  collectionName: string
  auths: AuthSummary[]
  collections: CollectionSummary[]
  busy: boolean
  onOpen: () => void
  onSave: (binding: EnvCollection | null) => void
}) {
  const saved = useMemo(
    () => envBindingFor(env, slug) ?? { collection: slug, baseUrl: null, defaultAuth: null },
    [env, slug],
  )
  const [draft, setDraft] = useState<EnvCollection>(saved)

  // Re-baseline whenever the file changes underneath — a save here, or an edit in the
  // environment's own tab. Keyed on the saved value, not the draft, so a reload triggered by
  // some unrelated file doesn't wipe what is being typed.
  const savedKey = `${saved.baseUrl ?? ''} ${saved.defaultAuth ?? ''}`
  const lastSavedKey = useRef(savedKey)
  useEffect(() => {
    if (lastSavedKey.current !== savedKey) {
      lastSavedKey.current = savedKey
      setDraft(saved)
    }
  }, [savedKey, saved])

  const dirty = (draft.baseUrl ?? '') !== (saved.baseUrl ?? '')
    || (draft.defaultAuth ?? '') !== (saved.defaultAuth ?? '')

  return (
    <Paper withBorder p="md" radius="sm">
      <Group justify="space-between" wrap="nowrap" mb="sm">
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          <Text size="sm" fw={600} truncate>{env.name}</Text>
          {env.collections.length > 1 && (
            <Badge size="xs" variant="light" color="grape">
              +{env.collections.length - 1} other {env.collections.length === 2 ? 'collection' : 'collections'}
            </Badge>
          )}
        </Group>
        <Group gap="xs" wrap="nowrap">
          {dirty && (
            <>
              <Button size="compact-sm" variant="subtle" color="gray" onClick={() => setDraft(saved)}>
                Revert
              </Button>
              <Button size="compact-sm" loading={busy} onClick={() => onSave(draft)}>Save</Button>
            </>
          )}
          <Button size="compact-sm" variant="subtle" onClick={onOpen}>Open</Button>
          <Tooltip label="Unassign — stops being offered in this collection" withArrow>
            <ActionIcon
              variant="subtle" color="red" size="sm" disabled={busy}
              aria-label={`Unassign ${env.name}`}
              onClick={() => onSave(null)}
            >
              <IconX size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>

      <Stack gap="sm">
        <Box>
          <Text size="sm" fw={500} mb={4}>Base URL</Text>
          <VariableInput
            value={draft.baseUrl ?? ''}
            onChange={(v) => setDraft({ ...draft, baseUrl: v && v.length > 0 ? v : null })}
            placeholder={`Inherit ${collectionName}'s base URL`}
            context={{ envPath: env.path }}
          />
        </Box>
        <Select
          label="Default auth"
          // Grouped against this collection — its own profiles are the relevant ones — while
          // the ref is written relative to the environment file, which is where it lands.
          data={authSelectGroups({ auths, collections, fromPath: env.path, forCollection: slug })}
          placeholder={`Inherit ${collectionName}'s default auth`}
          value={draft.defaultAuth ?? ''}
          onChange={(v) => setDraft({ ...draft, defaultAuth: v && v.length > 0 ? v : null })}
          searchable
          clearable
        />
      </Stack>
    </Paper>
  )
}


/** Filename-safe slug for a new environment. Mirrors the header's create-env dialog. */
function nameToSlug(name: string): string {
  const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
  return slug.length > 0 ? slug : 'env'
}
