import { Alert, Button, Code, Group, Modal, Stack, Text, TextInput } from '@mantine/core'
import { IconAlertCircle, IconCopy } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { RequestSpec, WorkspaceFileKind } from '../api/types'
import { useTapStore } from '../store'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  /** The request being duplicated: its workspace-relative path + display name.
   *  Set by the row the user right-clicked; null when the dialog is closed. */
  source: { realPath: string; name: string } | null
  /** Called with the new request's path once it's saved, so the host can open it. */
  onCreated: (path: string, kind: WorkspaceFileKind) => void
}

/** Copy an existing request to a sibling file under the same collection/folder with a
 *  new name. The server emits the canonical file content from the spec we ship — we
 *  read the source request, swap name + path (and drop the id so a fresh one is minted),
 *  then PUT it as a brand-new request. */
export function DuplicateRequestDialog({ open, onOpenChange, source, onCreated }: Props) {
  const reload = useTapStore((s) => s.reload)
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (open && source) { setName(`${source.name} copy`); setError(null) }
  }, [open, source])

  const slug = nameToSlug(name)
  const dir = source ? source.realPath.slice(0, source.realPath.lastIndexOf('/')) : ''
  const target = slug ? `${dir}/${slug}.req.md` : ''

  async function submit() {
    if (!source) return
    if (!slug) { setError('Pick a name.'); return }
    setBusy(true); setError(null)
    try {
      const detail = await api.request(source.realPath)
      const vars: Record<string, string> = {}
      const secrets: string[] = []
      for (const [k, v] of Object.entries(detail.vars ?? {})) {
        if (v?.default != null) vars[k] = v.default
        if (v?.secret) secrets.push(k)
      }
      const spec: RequestSpec = {
        path: target,
        id: null,
        name,
        auth: detail.auth ?? undefined,
        tags: detail.tags && detail.tags.length > 0 ? detail.tags : undefined,
        vars: Object.keys(vars).length > 0 ? vars : undefined,
        secrets: secrets.length > 0 ? secrets : undefined,
        body: detail.body && detail.body.trim().length > 0 ? detail.body : undefined,
        method: detail.method,
        url: detail.url,
        headers: detail.headers && detail.headers.length > 0 ? detail.headers : undefined,
        requestBody: detail.requestBody ?? undefined,
        protocol: detail.protocol === 'websocket' ? 'websocket' : undefined,
      }
      await api.saveRequestSpec(spec)
      await reload()
      onCreated(target, 'request')
      onOpenChange(false)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      title={
        <Group gap={6}>
          <IconCopy size={16} />
          <Text fw={600}>Duplicate request</Text>
        </Group>
      }
      size="md"
    >
      <Stack gap="sm">
        {source && (
          <Text size="sm" c="dimmed">
            Copy of <Code fz="xs">{source.name}</Code>
          </Text>
        )}
        <TextInput
          label="Name"
          placeholder="e.g. Create customer copy"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void submit() }}
          description={target && <>Created at <Code fz="xs">.tap/{target}</Code></>}
          autoFocus
        />
        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
          <Button onClick={submit} loading={busy} disabled={!slug}>Duplicate</Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function nameToSlug(name: string): string {
  return name.trim().toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/-+/g, '-')
    .slice(0, 60)
}
