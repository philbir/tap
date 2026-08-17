import {
  Alert, Badge, Button, Group, Loader, Modal, ScrollArea, Select, Stack, Text, TextInput, Tooltip,
} from '@mantine/core'
import {
  IconAlertTriangle, IconApi, IconCheck, IconPencil, IconRefresh, IconWorld,
} from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  OpenApiChange, OpenApiLink, OpenApiResyncAction, OpenApiResyncPreview, OpenApiResyncResult,
} from '../api/types'
import { useTapStore } from '../store'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  slug: string
}

const METHOD_COLOR: Record<string, string> = {
  GET: 'teal', POST: 'blue', PUT: 'orange', PATCH: 'grape', DELETE: 'red', HEAD: 'gray', OPTIONS: 'gray',
}

/** Colour and label per verdict. Conflicts are the only thing that needs attention. */
const KIND: Record<string, { color: string; label: string }> = {
  conflict: { color: 'red', label: 'conflict' },
  added: { color: 'green', label: 'new' },
  changed: { color: 'blue', label: 'changed' },
  removed: { color: 'orange', label: 'gone upstream' },
  orphaned: { color: 'yellow', label: 'file missing' },
  unchanged: { color: 'gray', label: 'unchanged' },
}

/** Which actions make sense for each verdict, most appropriate first. */
const ACTIONS: Record<string, { value: OpenApiResyncAction; label: string }[]> = {
  added: [
    { value: 'add', label: 'Create it' },
    { value: 'skip', label: 'Ignore' },
  ],
  changed: [
    { value: 'update', label: 'Update' },
    { value: 'skip', label: 'Leave alone' },
  ],
  conflict: [
    { value: 'skip', label: 'Keep mine' },
    { value: 'update', label: 'Take upstream (keeps assertions)' },
  ],
  removed: [
    { value: 'deprecate', label: 'Tag deprecated' },
    { value: 'untrack', label: 'Stop tracking' },
    { value: 'skip', label: 'Leave alone' },
  ],
  orphaned: [
    { value: 'skip', label: 'Leave alone' },
    { value: 'add', label: 'Re-create it' },
    { value: 'untrack', label: 'Stop tracking' },
  ],
  unchanged: [{ value: 'skip', label: '—' }],
}

export function ResyncOpenApiDialog({ open, onOpenChange, slug }: Props) {
  const reload = useTapStore((s) => s.reload)

  const [link, setLink] = useState<OpenApiLink | null>(null)
  const [documentId, setDocumentId] = useState<string | null>(null)
  const [preview, setPreview] = useState<OpenApiResyncPreview | null>(null)
  const [actions, setActions] = useState<Record<string, OpenApiResyncAction>>({})
  const [result, setResult] = useState<OpenApiResyncResult | null>(null)

  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setLink(null); setDocumentId(null); setPreview(null); setActions({})
    setResult(null); setError(null); setUrl('')

    api.openApiLink(slug)
      .then((l) => {
        setLink(l)
        // The source is recorded, so the common case needs no input at all — go straight to the
        // diff rather than asking the user to paste a URL they already gave us once.
        if (l?.url) void run(l.url)
      })
      .catch((e) => setError(e instanceof ApiError ? e.message : String(e)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, slug])

  async function run(target: string) {
    setError(null); setBusy(true)
    try {
      const staged = await api.fetchOpenApiDocument(target)
      setDocumentId(staged.documentId)
      const p = await api.previewOpenApiResync(slug, staged.documentId)
      setPreview(p)
      setActions(Object.fromEntries(p.changes.map((c) => [c.opKey, c.defaultAction])))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  async function apply() {
    if (!documentId || !preview) return
    setError(null); setBusy(true)
    try {
      const decisions = preview.changes
        .filter((c) => (actions[c.opKey] ?? 'skip') !== 'skip')
        .map((c) => ({ opKey: c.opKey, action: actions[c.opKey] }))

      const r = await api.applyOpenApiResync(slug, documentId, decisions)
      await reload()
      setResult(r)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  const actionable = preview?.changes.filter((c) => c.kind !== 'unchanged') ?? []
  const pending = actionable.filter((c) => (actions[c.opKey] ?? 'skip') !== 'skip').length

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      size="xl"
      title={
        <Group gap={6}>
          <IconRefresh size={16} />
          <Text fw={600}>Re-sync from OpenAPI</Text>
          <Badge size="sm" variant="light">{slug}</Badge>
        </Group>
      }
    >
      {error && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} mb="md" title="Re-sync failed">
          {error}
        </Alert>
      )}

      {result ? (
        <Stack gap="sm">
          <Alert color="green" icon={<IconCheck size={16} />} title="Re-synced">
            <Text size="sm">
              {result.added} added · {result.updated} updated · {result.deprecated} deprecated ·{' '}
              {result.untracked} untracked · {result.skipped} left alone
            </Text>
          </Alert>
          {result.warnings.length > 0 && (
            <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title="Warnings">
              <Stack gap={2}>{result.warnings.map((w, i) => <Text key={i} size="xs">{w}</Text>)}</Stack>
            </Alert>
          )}
          <Group justify="flex-end">
            <Button onClick={() => onOpenChange(false)}>Done</Button>
          </Group>
        </Stack>
      ) : !preview ? (
        <Stack gap="md">
          {busy && <Group gap="xs"><Loader size="sm" /><Text size="sm">Fetching and comparing…</Text></Group>}
          {!busy && !link && (
            <Text size="sm" c="dimmed">
              This collection isn’t linked to an OpenAPI document. Import one into it first.
            </Text>
          )}
          {!busy && link && !link.url && (
            // Imported from an uploaded file: we have no URL to re-fetch, so ask for one.
            <Stack gap="xs">
              <Text size="sm">
                “{slug}” was imported from the file <b>{link.fileName}</b>, so there’s no URL to
                re-fetch. Point at the document to compare against:
              </Text>
              <TextInput
                placeholder="https://api.example.com/openapi.json"
                value={url}
                onChange={(e) => setUrl(e.currentTarget.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && url.trim()) void run(url.trim()) }}
              />
              <Group justify="flex-end">
                <Button
                  leftSection={<IconWorld size={14} />}
                  disabled={!url.trim()}
                  onClick={() => void run(url.trim())}
                >
                  Compare
                </Button>
              </Group>
            </Stack>
          )}
        </Stack>
      ) : (
        <Stack gap="sm">
          <Group gap="xs">
            <IconApi size={14} />
            <Text size="xs" c="dimmed">
              {preview.sourceUrl}
              {preview.previousApiVersion && preview.newApiVersion
                && preview.previousApiVersion !== preview.newApiVersion
                && ` · version ${preview.previousApiVersion} → ${preview.newApiVersion}`}
            </Text>
          </Group>

          {preview.documentUnchanged ? (
            <Alert color="gray" title="The document hasn’t changed">
              <Text size="sm">Byte-for-byte identical to the one this collection was built from.</Text>
            </Alert>
          ) : actionable.length === 0 ? (
            <Alert color="gray" title="Nothing to do">
              <Text size="sm">The document changed, but nothing that affects these requests.</Text>
            </Alert>
          ) : (
            <>
              <Group gap="xs">
                {preview.conflicts > 0 && <Badge color="red" variant="light">{preview.conflicts} conflict</Badge>}
                {preview.added > 0 && <Badge color="green" variant="light">{preview.added} new</Badge>}
                {preview.changed > 0 && <Badge color="blue" variant="light">{preview.changed} changed</Badge>}
                {preview.removed > 0 && <Badge color="orange" variant="light">{preview.removed} gone</Badge>}
              </Group>

              {preview.conflicts > 0 && (
                <Alert color="red" icon={<IconAlertTriangle size={16} />} title="Some of these you edited">
                  <Text size="xs">
                    Taking upstream rewrites the URL, headers and example body — your assertions,
                    variables, auth and tags are kept either way.
                  </Text>
                </Alert>
              )}

              <ScrollArea h={380} type="auto" scrollbarSize={8}>
                <Stack gap={4} pr="sm">
                  {actionable.map((c) => (
                    <ChangeRow
                      key={c.opKey}
                      change={c}
                      action={actions[c.opKey] ?? 'skip'}
                      onAction={(a) => setActions((cur) => ({ ...cur, [c.opKey]: a }))}
                    />
                  ))}
                </Stack>
              </ScrollArea>
            </>
          )}

          <Group justify="space-between" mt="sm">
            <Button variant="subtle" onClick={() => onOpenChange(false)} disabled={busy}>Close</Button>
            <Button onClick={apply} loading={busy} disabled={pending === 0} leftSection={<IconCheck size={14} />}>
              Apply {pending > 0 ? `${pending} change${pending === 1 ? '' : 's'}` : ''}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  )
}

function ChangeRow(
  { change, action, onAction }:
  { change: OpenApiChange; action: OpenApiResyncAction; onAction: (a: OpenApiResyncAction) => void },
) {
  const kind = KIND[change.kind] ?? KIND.unchanged
  const options = ACTIONS[change.kind] ?? ACTIONS.unchanged

  return (
    <Group gap="xs" wrap="nowrap" align="center">
      <Badge size="xs" variant="light" color={kind.color} w={92}>{kind.label}</Badge>
      <Badge size="xs" variant="light" color={METHOD_COLOR[change.method] ?? 'gray'} w={58}>
        {change.method}
      </Badge>
      <Text size="xs" ff="var(--mono)" style={{ flexShrink: 0 }}>{change.path}</Text>
      {change.locallyEdited && (
        <Tooltip label="You've edited this file since it was generated">
          <IconPencil size={12} />
        </Tooltip>
      )}
      {change.summary && <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{change.summary}</Text>}
      <Select
        size="xs"
        w={220}
        data={options}
        value={action}
        onChange={(v) => v && onAction(v as OpenApiResyncAction)}
        allowDeselect={false}
        comboboxProps={{ withinPortal: true }}
      />
    </Group>
  )
}
