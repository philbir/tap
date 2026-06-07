import {
  Alert, Button, Code, Group, Modal, Select, SimpleGrid, Stack, Text, TextInput, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertCircle, IconFolders, IconLock, IconPlus, IconSend, IconWorld,
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
  }

  async function create() {
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

        {targetPath && kind !== 'auth' && (
          <Text size="xs" c="dimmed">Created at <Code fz="xs">.tap/{targetPath}</Code></Text>
        )}
        {kind === 'auth' && slug && (
          <Text size="xs" c="dimmed">Next step picks a provider (GitHub, OAuth, API key, …).</Text>
        )}

        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{error}</Alert>}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
          <Button onClick={create} loading={busy} disabled={!slug || (kind === 'request' && !collectionSlug)}>
            {kind === 'auth' ? 'Continue' : 'Create'}
          </Button>
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
