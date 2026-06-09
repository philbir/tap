import {
  Alert, Button, Checkbox, Code, Divider, FileButton, Group, List, Modal, SegmentedControl, Select,
  SimpleGrid, Stack, Text, TextInput, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertCircle, IconFolders, IconLock, IconPlus, IconSend, IconUpload, IconWorld,
  type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { CollectionSummary, WorkspaceFileKind } from '../api/types'
import { useTapStore } from '../store'
import { AuthWizard } from './AuthWizard'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  onCreated: (path: string, kind: WorkspaceFileKind) => void
}

type CreatableKind = 'request' | 'auth' | 'env' | 'collection'

interface KindOption {
  kind: CreatableKind
  label: string
  description: string
  icon: TablerIcon
  color: string
}

const KIND_OPTIONS: KindOption[] = [
  { kind: 'request', label: 'Request', description: 'A single HTTP call template', icon: IconSend, color: 'tap' },
  { kind: 'collection', label: 'Collection', description: 'A group of requests with a baseUrl, stages, default auth/headers, vars', icon: IconFolders, color: 'blue' },
  { kind: 'auth', label: 'Auth profile', description: 'Bearer, basic, OAuth2, AWS sigv4, …', icon: IconLock, color: 'orange' },
  { kind: 'env', label: 'Environment', description: 'Per-environment variables and secret refs', icon: IconWorld, color: 'grape' },
]

/** Collection sub-mode: from-scratch vs. import a Postman v2.1 export. */
type CollectionMode = 'blank' | 'postman'

interface PostmanPreview {
  /** Parsed JSON the importer will receive verbatim. */
  raw: unknown
  /** `info.name` from the file, used to default the slug. */
  collectionName: string
  /** Best-effort recursive counts for the preview blurb. */
  folderCount: number
  requestCount: number
  /** Schema URL (or null) — we surface it so non-v2.1 imports get a soft warning before the user clicks Import. */
  schema: string | null
  /** Raw text of the file — kept around so the user can see what was loaded if parsing reports a non-fatal hint. */
  fileName: string
  fileSize: number
}

export function CreateNewDialog({ open, onOpenChange, onCreated }: Props) {
  const reload = useTapStore((s) => s.reload)
  const [kind, setKind] = useState<CreatableKind>('request')
  const [name, setName] = useState('')
  /** For Request: which collection to drop the request into. */
  const [collectionSlug, setCollectionSlug] = useState<string | null>(null)
  /** For Request: optional sub-folder path inside the chosen collection. */
  const [subFolder, setSubFolder] = useState<string>('')
  const [collections, setCollections] = useState<CollectionSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  /** When true, the auth wizard owns the flow: the user picked Auth + a name, clicked
   *  Create, and we've handed off to a provider-pick + per-provider-fields stepper. */
  const [authWizardOpen, setAuthWizardOpen] = useState(false)
  const [authWizardName, setAuthWizardName] = useState('')

  // Collection-specific state: pick blank vs Postman import, then carry the parsed
  // Postman file + an overwrite toggle. Reset when the kind switches away.
  const [collectionMode, setCollectionMode] = useState<CollectionMode>('blank')
  const [postman, setPostman] = useState<PostmanPreview | null>(null)
  const [postmanParseError, setPostmanParseError] = useState<string | null>(null)
  const [overwriteExisting, setOverwriteExisting] = useState(false)
  const [importWarnings, setImportWarnings] = useState<string[] | null>(null)

  useEffect(() => {
    if (!open) return
    api.collections().then(setCollections).catch(() => setCollections([]))
  }, [open])

  // Auto-pick the first collection when the dialog opens or the kind switches to request.
  useEffect(() => {
    if (kind === 'request' && collectionSlug === null && collections.length > 0) {
      setCollectionSlug(collections[0].slug)
    }
  }, [kind, collectionSlug, collections])

  const slug = nameToSlug(name)

  // Resolve the on-disk target path for the chosen kind.
  const targetPath = useMemo(() => {
    if (!slug) return ''
    switch (kind) {
      case 'request': {
        if (!collectionSlug) return ''
        const sub = subFolder.replace(/\\/g, '/').replace(/^\/+|\/+$/g, '')
        const prefix = sub ? `collections/${collectionSlug}/${sub}` : `collections/${collectionSlug}`
        return `${prefix}/${slug}.req.md`
      }
      case 'auth': return `auth/${slug}.auth.md`
      case 'env': return `environments/${slug}.env.md`
      case 'collection': return `collections/${slug}/_collection.md`
    }
  }, [kind, slug, collectionSlug, subFolder])

  function reset() {
    setKind('request'); setName(''); setCollectionSlug(null); setSubFolder(''); setError(null)
    setCollectionMode('blank'); setPostman(null); setPostmanParseError(null)
    setOverwriteExisting(false); setImportWarnings(null)
  }

  /** Parse a freshly-picked Postman export. We do this client-side so the user gets
   *  instant validation + a counts preview before hitting the network. The parsed JSON
   *  is kept verbatim (we don't normalize it) so the server's importer remains the
   *  single source of truth for the on-disk mapping. */
  async function handlePostmanFile(file: File | null) {
    setPostmanParseError(null)
    setImportWarnings(null)
    if (!file) { setPostman(null); return }
    try {
      const text = await file.text()
      const json = JSON.parse(text)
      if (!json || typeof json !== 'object' || !json.info || !Array.isArray(json.item)) {
        throw new Error("This file doesn't look like a Postman v2.1 collection (missing 'info' or 'item').")
      }
      const counts = countPostmanItems(json.item)
      const preview: PostmanPreview = {
        raw: json,
        collectionName: typeof json.info.name === 'string' ? json.info.name : 'Collection',
        folderCount: counts.folders,
        requestCount: counts.requests,
        schema: typeof json.info.schema === 'string' ? json.info.schema : null,
        fileName: file.name,
        fileSize: file.size,
      }
      setPostman(preview)
      // Pre-fill the name from the Postman collection — the user can still edit it
      // before hitting Import, which becomes the slug.
      if (!name.trim()) setName(preview.collectionName)
    } catch (e) {
      setPostman(null)
      setPostmanParseError(e instanceof Error ? e.message : String(e))
    }
  }

  async function create() {
    setError(null)
    // Collection + Postman import: send the parsed JSON to the server importer.
    if (kind === 'collection' && collectionMode === 'postman') {
      if (!postman) { setError('Pick a Postman v2.1 export file.'); return }
      if (!slug) { setError('Pick a collection name (used as the slug).'); return }
      setBusy(true)
      try {
        const result = await api.importPostmanCollection(postman.raw, slug, overwriteExisting)
        await reload()
        setImportWarnings(result.warnings ?? [])
        onCreated(`collections/${result.slug}`, 'collection')
        // Keep the dialog open when there are warnings so the user can read them before
        // navigating away. Otherwise close + reset like the other create paths.
        if (!result.warnings || result.warnings.length === 0) {
          onOpenChange(false)
          reset()
        }
      } catch (e) {
        setError(e instanceof ApiError ? e.message : String(e))
      } finally { setBusy(false) }
      return
    }

    if (!slug || !name) { setError('Pick a name.'); return }
    // Auth gets a dedicated wizard — pick provider template + required fields. The base
    // dialog hands off after collecting just the name; the wizard does the saveAuthSpec.
    if (kind === 'auth') {
      setAuthWizardName(name)
      setAuthWizardOpen(true)
      onOpenChange(false)
      return
    }
    setBusy(true); setError(null)
    try {
      switch (kind) {
        case 'request':
          if (!collectionSlug) { setError('Pick a collection.'); setBusy(false); return }
          await api.saveRequestSpec({ path: targetPath, id: null, name, method: 'GET', url: '/' })
          break
        case 'env':
          await api.saveEnvSpec({ path: targetPath, id: null, name })
          break
        case 'collection':
          await api.saveCollectionSpec({ slug, id: null, name })
          break
      }
      await reload()
      if (kind === 'collection') {
        onCreated(`collections/${slug}`, 'collection')
      } else {
        onCreated(targetPath, kind as WorkspaceFileKind)
      }
      onOpenChange(false)
      reset()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  const collectionOptions = collections.map((c) => ({ value: c.slug, label: c.name }))

  return (
    <>
      <Modal
      opened={open}
      onClose={() => { if (!busy) { onOpenChange(false); reset() } }}
      size="lg"
      title={
        <Group gap={6}>
          <IconPlus size={16} />
          <Text fw={600}>Create new</Text>
        </Group>
      }
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Add a workspace artifact. Tap writes a Markdown file to <Code>.tap/</Code> in your repo.
        </Text>

        <SimpleGrid cols={2}>
          {KIND_OPTIONS.map((o) => {
            const KindIcon = o.icon
            const active = kind === o.kind
            return (
              <UnstyledButton
                key={o.kind}
                onClick={() => { setKind(o.kind); setSubFolder('') }}
                p="sm"
                style={{
                  border: `1px solid ${active ? `var(--mantine-color-${o.color}-filled)` : 'var(--mantine-color-default-border)'}`,
                  borderRadius: 8,
                  background: active ? `var(--mantine-color-${o.color}-light)` : 'transparent',
                }}
              >
                <Group gap="sm" align="flex-start">
                  <KindIcon size={20} color={`var(--mantine-color-${o.color}-6)`} />
                  <Stack gap={2} flex={1}>
                    <Text fw={600} size="sm">{o.label}</Text>
                    <Text size="xs" c="dimmed">{o.description}</Text>
                  </Stack>
                </Group>
              </UnstyledButton>
            )
          })}
        </SimpleGrid>

        <TextInput
          label="Name"
          placeholder={
            kind === 'collection' ? 'e.g. Stripe API'
              : kind === 'auth' ? 'e.g. Stripe bearer'
              : kind === 'env' ? 'e.g. Local'
              : 'e.g. Create customer'
          }
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          autoFocus
        />

        {kind === 'request' && (
          <>
            <Select
              label="Collection"
              description="Requests live inside a collection; pick one or create the collection first."
              data={collectionOptions}
              value={collectionSlug}
              onChange={setCollectionSlug}
              allowDeselect={false}
              nothingFoundMessage="No collections yet"
              placeholder={collections.length === 0 ? 'Create a collection first' : 'Pick a collection'}
            />
            <TextInput
              label="Sub-folder"
              description="Optional path inside the collection (e.g. customer/v2)."
              placeholder="(collection root)"
              value={subFolder}
              onChange={(e) => setSubFolder(e.currentTarget.value)}
            />
          </>
        )}

        {kind === 'collection' && (
          <Stack gap="sm">
            <SegmentedControl
              fullWidth
              size="sm"
              value={collectionMode}
              onChange={(v) => { setCollectionMode(v as CollectionMode); setError(null) }}
              data={[
                { value: 'blank', label: 'Blank' },
                { value: 'postman', label: 'Import from Postman' },
              ]}
            />
            {collectionMode === 'postman' && (
              <Stack gap="xs">
                <Group gap="xs" wrap="nowrap" align="center">
                  <FileButton onChange={handlePostmanFile} accept="application/json,.json">
                    {(props) => (
                      <Button {...props} size="xs" variant="default" leftSection={<IconUpload size={14} />}>
                        {postman ? 'Replace file' : 'Pick Postman v2.1 export'}
                      </Button>
                    )}
                  </FileButton>
                  {postman && (
                    <Text size="xs" c="dimmed" ff="var(--mono)" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {postman.fileName} · {formatBytes(postman.fileSize)}
                    </Text>
                  )}
                </Group>

                {postmanParseError && (
                  <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
                    {postmanParseError}
                  </Alert>
                )}

                {postman && (
                  <Stack gap={4}>
                    <Text size="xs" c="dimmed">
                      <Text component="span" fw={600}>{postman.collectionName}</Text>
                      {' · '}
                      {postman.requestCount} request{postman.requestCount === 1 ? '' : 's'}
                      {postman.folderCount > 0 && `, ${postman.folderCount} folder${postman.folderCount === 1 ? '' : 's'}`}
                    </Text>
                    {postman.schema && !postman.schema.includes('v2.1') && (
                      <Text size="xs" c="orange">
                        Schema is <Code fz="xs">{postman.schema}</Code> — only v2.1 is fully supported; mapping is best-effort.
                      </Text>
                    )}
                    <Checkbox
                      size="xs"
                      label="Replace existing collection if it exists"
                      checked={overwriteExisting}
                      onChange={(e) => setOverwriteExisting(e.currentTarget.checked)}
                    />
                  </Stack>
                )}

                {!postman && !postmanParseError && (
                  <Text size="xs" c="dimmed">
                    Export from Postman: <Code fz="xs">… → Export → Collection v2.1 → JSON</Code>.
                  </Text>
                )}
              </Stack>
            )}
            {collectionMode === 'postman' && postman && <Divider variant="dashed" />}
          </Stack>
        )}

        {targetPath && kind !== 'auth' && !(kind === 'collection' && collectionMode === 'postman') && (
          <Text size="xs" c="dimmed">Created at <Code fz="xs">.tap/{targetPath}</Code></Text>
        )}
        {kind === 'collection' && collectionMode === 'postman' && slug && (
          <Text size="xs" c="dimmed">Imported into <Code fz="xs">.tap/collections/{slug}/</Code></Text>
        )}
        {kind === 'auth' && slug && (
          <Text size="xs" c="dimmed">Next step picks a provider (GitHub, OAuth, API key, …).</Text>
        )}

        {importWarnings && importWarnings.length > 0 && (
          <Alert color="yellow" variant="light" icon={<IconAlertCircle size={14} />} title="Import completed with warnings">
            <List size="xs" spacing={2}>
              {importWarnings.map((w, i) => <List.Item key={i}>{w}</List.Item>)}
            </List>
          </Alert>
        )}

        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{error}</Alert>}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => { onOpenChange(false); reset() }} disabled={busy}>
            {importWarnings ? 'Done' : 'Cancel'}
          </Button>
          {!importWarnings && (
            <Button
              onClick={create}
              loading={busy}
              disabled={
                !slug
                || (kind === 'request' && !collectionSlug)
                || (kind === 'collection' && collectionMode === 'postman' && !postman)
              }
            >
              {kind === 'auth'
                ? 'Continue'
                : (kind === 'collection' && collectionMode === 'postman' ? 'Import' : 'Create')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
    {authWizardOpen && (
      <AuthWizard
        open={authWizardOpen}
        onOpenChange={setAuthWizardOpen}
        initialName={authWizardName}
        onCreated={(p, k) => { onCreated(p, k); reset() }}
      />
    )}
    </>
  )
}

function nameToSlug(name: string): string {
  return name.trim().toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/-+/g, '-')
    .slice(0, 60)
}

/** Walk a Postman `item[]` array and tally folders + terminal requests. Folders are
 *  any item with a nested `item[]`; everything else with a `request` field is a request.
 *  Used purely for the preview blurb — the server importer is the authoritative parser. */
function countPostmanItems(items: unknown): { folders: number; requests: number } {
  let folders = 0
  let requests = 0
  if (!Array.isArray(items)) return { folders, requests }
  for (const it of items) {
    if (it && typeof it === 'object') {
      const obj = it as Record<string, unknown>
      if (Array.isArray(obj.item)) {
        folders++
        const sub = countPostmanItems(obj.item)
        folders += sub.folders
        requests += sub.requests
      } else if (obj.request !== undefined) {
        requests++
      }
    }
  }
  return { folders, requests }
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}
