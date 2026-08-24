import { Alert, Button, Code, Group, Stack, Text } from '@mantine/core'
import { IconAlertCircle, IconDeviceFloppy, IconRotateClockwise } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { WorkspaceErrorDto } from '../api/types'
import { SourceCodeEditor } from './SourceCodeEditor'

interface Props {
  /** Workspace-relative file path (e.g. `apis/foo.api.md`, `workspace.tap`). */
  path: string
  /** Canonical YAML emitted by the server — the snapshot the editor starts from. */
  source: string
  /** Filename shown above the editor (defaults to the basename of `path`). */
  label?: string
  /** Monaco language id. `.http` files are raw-first — they have no canonical YAML form —
   *  so they reuse this whole editor with their own highlighting. */
  language?: 'yaml' | 'http'
}

/**
 * Editable canonical YAML view shared by every editor's Source tab.
 *
 * The server is still the sole authority on YAML format — but power users can hand-edit
 * the file directly here. We POST the raw content to `/api/workspace/source`, which
 * round-trips it through `FileParser` before writing. Invalid YAML / mismatched kind /
 * unknown fields surface inline as a `WorkspaceErrorDto` and nothing lands on disk.
 *
 * On success we don't manually refresh — the workspace file watcher fires
 * `workspace-changed` and the store bumps its `generation`, which makes the parent
 * editor refetch and pass us a new `source` snapshot.
 *
 * Editor is Monaco (same engine as VS Code): YAML syntax highlighting, line numbers,
 * folding, find/replace, Cmd+S triggers save. When the server reports a parse error
 * with a line number we pin a Monaco marker on that line so the squiggle + gutter
 * indicator point straight at the problem.
 */
export function SourceTab({ path, source, label, language = 'yaml' }: Props) {
  const [draft, setDraft] = useState(source)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<WorkspaceErrorDto | null>(null)
  const [genericError, setGenericError] = useState<string | null>(null)

  // Sync the local draft whenever the parent refetches (after a successful save,
  // or when the user switches to a different file in the same editor instance).
  useEffect(() => {
    setDraft(source)
    setError(null)
    setGenericError(null)
  }, [source, path])

  const dirty = useMemo(() => draft !== source, [draft, source])
  const fileName = label ?? path.split('/').pop() ?? path

  async function save() {
    setSaving(true); setError(null); setGenericError(null)
    try {
      await api.saveSource(path, draft)
      // generation bump on the file-watcher SSE will reset `source` -> `draft` via the effect.
    } catch (e) {
      if (e instanceof ApiError && e.payload && typeof e.payload === 'object' && 'code' in e.payload) {
        setError(e.payload as WorkspaceErrorDto)
      } else {
        setGenericError(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack gap="xs">
      <Group justify="space-between" align="center">
        <Code>{fileName}</Code>
        <Group gap="xs">
          {dirty && (
            <Button
              variant="default" size="xs"
              leftSection={<IconRotateClockwise size={12} />}
              onClick={() => { setDraft(source); setError(null); setGenericError(null) }}
              disabled={saving}
            >
              Revert
            </Button>
          )}
          <Button
            size="xs"
            leftSection={<IconDeviceFloppy size={12} />}
            onClick={save}
            disabled={!dirty || saving}
            loading={saving}
          >
            Save source
          </Button>
        </Group>
      </Group>
      <Text size="xs" c="dimmed">
        {language === 'http'
          // A .http file has no canonical form — saying otherwise would promise a reformat
          // that will never happen, and the promise NOT to reformat is the whole contract
          // for a file shared with other tools.
          ? 'This file is the source of truth and is never reformatted by Tap. Edits are parsed server-side before being written; invalid content stays in the editor and the file on disk is untouched.'
          : 'Canonical YAML for this file. Edits are validated server-side (parsing + schema) before being written. Invalid content stays in the editor and the file on disk is untouched.'}
      </Text>

      {error && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />} title={error.code}>
          <Stack gap={2}>
            <Text size="sm">{error.message}</Text>
            {(error.path || error.line != null) && (
              <Text size="xs" c="dimmed" ff="var(--mono)">
                {error.path}{error.line != null ? `:${error.line}` : ''}
              </Text>
            )}
          </Stack>
        </Alert>
      )}
      {genericError && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
          <Text size="sm">{genericError}</Text>
        </Alert>
      )}

      <SourceCodeEditor
        value={draft}
        onChange={setDraft}
        language={language}
        error={error}
        onSave={save}
      />
    </Stack>
  )
}
