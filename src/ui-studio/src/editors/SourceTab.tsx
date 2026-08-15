import { Alert, Box, Button, Code, Group, Stack, Text } from '@mantine/core'
import { IconAlertCircle, IconDeviceFloppy, IconRotateClockwise } from '@tabler/icons-react'
import Editor, { type BeforeMount, type OnMount } from '@monaco-editor/react'
import * as monaco from 'monaco-editor'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { WorkspaceErrorDto } from '../api/types'
import { ensureThemes, useMonacoTheme } from './monacoSetup'

interface Props {
  /** Workspace-relative file path (e.g. `apis/foo.api.md`, `tap.md`). */
  path: string
  /** Canonical YAML emitted by the server — the snapshot the editor starts from. */
  source: string
  /** Filename shown above the editor (defaults to the basename of `path`). */
  label?: string
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
export function SourceTab({ path, source, label }: Props) {
  // We drive Monaco's theme explicitly off Mantine's color scheme (see effect below)
  // because @monaco-editor/react's prop-driven theme switch is unreliable when the same
  // editor instance is kept mounted across toggles.
  const monacoTheme = useMonacoTheme()
  const [draft, setDraft] = useState(source)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<WorkspaceErrorDto | null>(null)
  const [genericError, setGenericError] = useState<string | null>(null)
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)
  // Latest save fn — captured by Cmd+S Monaco action so we don't re-bind on every render.
  const saveRef = useRef<() => void>(() => {})

  // Sync the local draft whenever the parent refetches (after a successful save,
  // or when the user switches to a different file in the same editor instance).
  useEffect(() => {
    setDraft(source)
    setError(null)
    setGenericError(null)
  }, [source, path])

  // Push server-side parse errors into Monaco's marker channel so they render as a
  // red squiggle + gutter glyph on the offending line. Cleared whenever the error
  // clears (successful save, revert, or fresh edit).
  useEffect(() => {
    const editor = editorRef.current
    if (!editor) return
    const model = editor.getModel()
    if (!model) return
    if (!error || error.line == null) {
      monaco.editor.setModelMarkers(model, 'tap-source', [])
      return
    }
    const line = Math.max(1, error.line)
    monaco.editor.setModelMarkers(model, 'tap-source', [{
      severity: monaco.MarkerSeverity.Error,
      message: `${error.code}: ${error.message}`,
      startLineNumber: line,
      endLineNumber: line,
      startColumn: 1,
      endColumn: model.getLineMaxColumn(line),
    }])
    editor.revealLineInCenterIfOutsideViewport(line)
  }, [error])

  // Drive Monaco's theme directly off the Mantine color scheme. The Editor's `theme`
  // prop is set on mount, but explicit setTheme keeps follow-up toggles in lockstep
  // even when @monaco-editor/react's internal effect skips a re-application.
  useEffect(() => {
    if (!editorRef.current) return
    monaco.editor.setTheme(monacoTheme)
  }, [monacoTheme])

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
  saveRef.current = save

  const beforeMount: BeforeMount = (m) => ensureThemes(m)
  const onMount: OnMount = (editor) => {
    editorRef.current = editor
    // Cmd/Ctrl+S inside the editor triggers a save just like the toolbar button.
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current())
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
        Canonical YAML for this file. Edits are validated server-side (parsing + schema)
        before being written. Invalid content stays in the editor and the file on disk
        is untouched.
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

      <Box
        style={{
          border: '1px solid var(--mantine-color-default-border)',
          borderRadius: 'var(--mantine-radius-sm)',
          overflow: 'hidden',
        }}
      >
        <Editor
          height="60vh"
          language="yaml"
          value={draft}
          onChange={(v) => setDraft(v ?? '')}
          theme={monacoTheme}
          beforeMount={beforeMount}
          onMount={onMount}
          options={{
            minimap: { enabled: false },
            fontSize: 12,
            fontFamily: 'var(--mono)',
            tabSize: 2,
            insertSpaces: true,
            renderWhitespace: 'selection',
            scrollBeyondLastLine: false,
            scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
            automaticLayout: true,
            wordWrap: 'on',
            lineNumbersMinChars: 3,
            padding: { top: 8, bottom: 8 },
          }}
        />
      </Box>
    </Stack>
  )
}
