import { Alert, Button, Code, Group, Modal, Stack, Text, TextInput } from '@mantine/core'
import { IconAlertCircle, IconFolderPlus } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import { useTapStore } from '../store'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  /** Workspace-relative directory the new folder lands in (`""` = workspace root).
   *  Set by the row the user right-clicked — not exposed in the dialog. */
  parent: string
}

/** Create a directory under `.tap/`. Folders carry no metadata — they're pure grouping
 *  for the explorer tree. The parent is fixed by the calling context (the row that
 *  opened the dialog); the dialog only asks for a name. */
export function NewFolderDialog({ open, onOpenChange, parent }: Props) {
  const reload = useTapStore((s) => s.reload)
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (open) { setName(''); setError(null) }
  }, [open])

  const slug = nameToSlug(name)
  const target = !slug ? '' : parent ? `${parent}/${slug}` : slug

  async function submit() {
    if (!slug) { setError('Pick a folder name.'); return }
    setBusy(true); setError(null)
    try {
      await api.createFolder(target)
      await reload()
      onOpenChange(false)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  const parentLabel = parent || 'workspace root'

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      title={
        <Group gap={6}>
          <IconFolderPlus size={16} />
          <Text fw={600}>New folder</Text>
        </Group>
      }
      size="md"
    >
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          In <Code fz="xs">{parentLabel}</Code>
        </Text>
        <TextInput
          label="Name"
          placeholder="e.g. customers"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void submit() }}
          description={target && <>Created at <Code fz="xs">.tap/{target}</Code></>}
          autoFocus
        />
        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
          <Button onClick={submit} loading={busy} disabled={!slug}>Create</Button>
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
