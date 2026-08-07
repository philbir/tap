import {
  ActionIcon, Badge, Box, Button, Checkbox, Code, Group, ScrollArea, Select, Stack, Tabs, TagsInput, Text, TextInput,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconCode, IconFileText, IconLayoutDashboard, IconList, IconPlus, IconRocket, IconTrash, IconVariable,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  AuthSummary, CollectionDetail, CollectionSpec, CollectionStageSpec, CollectionSummary, VariableContext,
} from '../api/types'
import { useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { authSelectGroups } from './authOptions'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabCount, TabDot } from './EditorShell'
import { KvTable, type KvRow } from './KvTable'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { SourceTab } from './SourceTab'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'

interface Props {
  /** Workspace-relative path of the collection directory (e.g. `collections/demo`). */
  path: string
}

/** Collection editor. The on-disk metadata file lives at `<path>/_collection.md`; the
 *  collection owns the base URL, optional stages, default auth + headers, plus vars/tags.
 *  Requests living under the collection inherit all of it. */
export function CollectionEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const reload = useTapStore((s) => s.reload)
  const auths = useTapStore((s) => s.auths)
  const collections = useTapStore((s) => s.collections)
  const tagSuggestions = useTagDictionary()
  const slug = useMemo(() => path.split('/').pop() ?? path, [path])
  // Auth refs in the collection file are relative to `collections/<slug>/_collection.md`.
  const collectionFilePath = `${path}/_collection.md`

  const [detail, setDetail] = useState<CollectionDetail | null>(null)
  const [spec, setSpec] = useState<CollectionSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<CollectionSpec | null>(null)
  const [tab, setTab] = useState<string | null>('general')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)
  const [varsOpened, varsCtl] = useDisclosure(false)
  const variableContext = useMemo<VariableContext>(() => ({ collectionPath: collectionFilePath }), [collectionFilePath])

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.collectionDetail(slug).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d)
      setSpec(initial)
      setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [slug, generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

  function update<K extends keyof CollectionSpec>(key: K, value: CollectionSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
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
  const stages = spec.stages ?? []

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
        <Tabs.List mb="md">
          <Tabs.Tab value="general" leftSection={<IconLayoutDashboard size={14} />}>General</Tabs.Tab>
          <Tabs.Tab value="headers" leftSection={<IconList size={14} />}>
            Headers <TabCount count={headerRows.length} />
          </Tabs.Tab>
          <Tabs.Tab value="variables" leftSection={<IconVariable size={14} />}>
            Variables <TabCount count={varRows.length} />
          </Tabs.Tab>
          <Tabs.Tab value="stages" leftSection={<IconRocket size={14} />}>
            Stages <TabCount count={stages.length} />
          </Tabs.Tab>
          <Tabs.Tab value="docs" leftSection={<IconFileText size={14} />}>
            Docs <TabDot active={!!spec.body && spec.body.trim().length > 0} />
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

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
            {!detail.exists && (
              <Text size="xs" c="dimmed">
                No <Code fz="xs">_collection.md</Code> on disk yet — saving will create it.
              </Text>
            )}
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

        <Tabs.Panel value="variables">
          <Stack gap="md" maw={880}>
            <Text size="xs" c="dimmed">
              Collection-scoped variables. Cascade tier between workspace and stage
              (workspace &lt; <b>collection</b> &lt; stage &lt; env &lt; request).
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

        <Tabs.Panel value="stages">
          <StagesEditor
            stages={stages}
            defaultStage={spec.defaultStage ?? null}
            auths={auths}
            collections={collections}
            collectionFilePath={collectionFilePath}
            onChangeStages={(next) => update('stages', next.length > 0 ? next : undefined)}
            onChangeDefault={(name) => update('defaultStage', name ?? undefined)}
            onOpenVariables={varsCtl.open}
          />
        </Tabs.Panel>

        <Tabs.Panel value="docs">
          <DocsEditor
            value={spec.body ?? ''}
            onChange={(v) => update('body', v.trim().length > 0 ? v : undefined)}
            emptyHint="No docs yet. Describe this collection's API and how its requests are organized."
          />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          {detail.exists
            ? <SourceTab path={collectionFilePath} source={detail.source} />
            : <Text size="sm" c="dimmed">Save the collection first to view the source file.</Text>}
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={varsCtl.close} context={variableContext} />
    </>
  )
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
    vars: split.vars,
    secrets: split.secrets,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    stages: d.stages.length > 0
      ? d.stages.map((s) => {
          const stageSplit = splitVarSpec(s.vars)
          return {
            name: s.name,
            baseUrl: s.baseUrl ?? undefined,
            defaultAuth: s.defaultAuth ?? undefined,
            vars: stageSplit.vars,
            secrets: stageSplit.secrets,
          }
        })
      : undefined,
    defaultStage: d.defaultStage ?? undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
  }
}

// ---- Stages master/detail ------------------------------------------------------------

interface StagesEditorProps {
  stages: CollectionStageSpec[]
  defaultStage: string | null
  auths: AuthSummary[]
  collections: CollectionSummary[]
  collectionFilePath: string
  onChangeStages: (next: CollectionStageSpec[]) => void
  onChangeDefault: (name: string | null) => void
  onOpenVariables: () => void
}

function StagesEditor({ stages, defaultStage, auths, collections, collectionFilePath, onChangeStages, onChangeDefault, onOpenVariables }: StagesEditorProps) {
  const [selected, setSelected] = useState<number>(stages.length > 0 ? 0 : -1)
  const safe = selected >= 0 && selected < stages.length ? selected : stages.length > 0 ? 0 : -1
  const stage = safe >= 0 ? stages[safe] : null

  function addStage() {
    const existing = new Set(stages.map((s) => s.name.toLowerCase()))
    let i = stages.length + 1
    let name = `stage${i}`
    while (existing.has(name.toLowerCase())) { i++; name = `stage${i}` }
    const next = [...stages, { name }]
    onChangeStages(next); setSelected(next.length - 1)
  }

  function removeStage(idx: number) {
    const removed = stages[idx]?.name ?? ''
    const next = stages.filter((_, i) => i !== idx)
    onChangeStages(next)
    if (defaultStage && removed.toLowerCase() === defaultStage.toLowerCase()) onChangeDefault(null)
    setSelected(next.length === 0 ? -1 : Math.min(idx, next.length - 1))
  }

  function patchStage(idx: number, p: Partial<CollectionStageSpec>) {
    const next = stages.map((s, i) => (i === idx ? trim({ ...s, ...p }) : s))
    onChangeStages(next)
  }

  function trim(s: CollectionStageSpec): CollectionStageSpec {
    return {
      name: s.name,
      baseUrl: s.baseUrl?.trim() ? s.baseUrl : undefined,
      defaultAuth: s.defaultAuth?.trim() ? s.defaultAuth : undefined,
      vars: s.vars && Object.keys(s.vars).length > 0 ? s.vars : undefined,
      secrets: s.secrets && s.secrets.length > 0 ? s.secrets : undefined,
    }
  }

  if (stages.length === 0) {
    return (
      <Stack gap="md" maw={500}>
        <Text size="sm" c="dimmed">
          Stages are named environments (dev, staging, prod) within this collection. Each can
          override the baseUrl, default auth, and variables. Vary headers per stage by
          referencing stage-scoped vars in the collection's default headers.
        </Text>
        <Box>
          <Button leftSection={<IconPlus size={14} />} onClick={addStage}>Add first stage</Button>
        </Box>
      </Stack>
    )
  }

  return (
    <Box style={{ display: 'grid', gridTemplateColumns: '220px 1fr', gap: 16, minHeight: 360 }}>
      <Box style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 6, overflow: 'hidden' }}>
        <Group justify="space-between" px="sm" py="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
          <Text size="xs" tt="uppercase" c="dimmed" lts={0.5}>Stages</Text>
          <ActionIcon variant="subtle" size="sm" onClick={addStage} aria-label="Add stage" title="Add stage">
            <IconPlus size={12} />
          </ActionIcon>
        </Group>
        <ScrollArea h={320} type="hover" scrollbarSize={6}>
          {stages.map((s, i) => {
            const isDefault = defaultStage && s.name.toLowerCase() === defaultStage.toLowerCase()
            const active = i === safe
            return (
              <Group
                key={i}
                gap="xs"
                px="sm"
                py={6}
                style={{
                  cursor: 'pointer',
                  background: active ? 'var(--mantine-color-default-hover)' : undefined,
                  borderLeft: `3px solid ${active ? 'var(--mantine-color-tap-filled)' : 'transparent'}`,
                }}
                onClick={() => setSelected(i)}
              >
                <Text size="sm" flex={1} truncate c={active ? 'tap' : undefined}>
                  {s.name || `(stage ${i + 1})`}
                </Text>
                {isDefault && <Badge size="xs" variant="light" color="tap">default</Badge>}
                <ActionIcon
                  variant="subtle" color="red" size="sm"
                  onClick={(e) => { e.stopPropagation(); removeStage(i) }}
                  aria-label="Remove stage"
                >
                  <IconTrash size={12} />
                </ActionIcon>
              </Group>
            )
          })}
        </ScrollArea>
      </Box>

      <Box>
        {stage && (
          <Stack gap="md" maw={620}>
            <TextInput
              label="Name"
              placeholder="dev, staging, prod…"
              value={stage.name}
              onChange={(e) => patchStage(safe, { name: e.currentTarget.value })}
            />
            <Box>
              <Text size="sm" fw={500} mb={4}>Base URL (override)</Text>
              <VariableInput
                value={stage.baseUrl ?? ''}
                onChange={(v) => patchStage(safe, { baseUrl: v })}
                placeholder="leave empty to inherit"
                context={{ collectionPath: collectionFilePath, stage: stage.name }}
                onOpenVariables={onOpenVariables}
              />
              <Text size="xs" c="dimmed" mt={4}>Inherits the collection's baseUrl when empty.</Text>
            </Box>
            <Select
              label="Default Auth (override)"
              data={[
                { value: '', label: '(inherit from collection)' },
                ...authSelectGroups({ auths, collections, fromPath: collectionFilePath }),
              ]}
              value={stage.defaultAuth ?? ''}
              onChange={(v) => patchStage(safe, { defaultAuth: v && v !== '' ? v : undefined })}
              allowDeselect={false}
            />
            <Box>
              <Text size="sm" fw={500} mb={4}>Variables</Text>
              <Text size="xs" c="dimmed" mb="xs">
                Override collection/workspace defaults for this stage. Reference these in the
                collection's default headers (e.g. <Code>{`X-Env: {{env}}`}</Code>) to vary
                headers per stage.
              </Text>
              <KvTable
                rows={(() => {
                  const ss = new Set(stage.secrets ?? [])
                  return Object.entries(stage.vars ?? {}).map(([k, v]) => ({ key: k, value: v, secret: ss.has(k) }))
                })()}
                onChange={(rows) => {
                  const obj: Record<string, string> = {}
                  const sec: string[] = []
                  for (const r of rows) {
                    if (!r.key) continue
                    obj[r.key] = r.value
                    if (r.secret) sec.push(r.key)
                  }
                  patchStage(safe, { vars: obj, secrets: sec })
                }}
                keyPlaceholder="var.name"
                valuePlaceholder="value"
                allowSecretToggle
                variableContext={{ collectionPath: collectionFilePath, stage: stage.name }}
                onOpenVariables={onOpenVariables}
              />
            </Box>
            <Checkbox
              label="Use this stage as the default"
              description="Preselected in the request editor's stage switcher."
              checked={defaultStage?.toLowerCase() === stage.name.toLowerCase()}
              onChange={(e) => onChangeDefault(e.currentTarget.checked ? stage.name : null)}
            />
          </Stack>
        )}
      </Box>
    </Box>
  )
}
