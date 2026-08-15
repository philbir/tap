import { Box } from '@mantine/core'
import Editor, { type BeforeMount, type OnMount } from '@monaco-editor/react'
import * as monaco from 'monaco-editor'
import { useEffect, useRef } from 'react'
import { ensureThemes, useMonacoTheme } from './monacoSetup'
import type { RawSubType } from './body-mode'

/**
 * Monaco-backed editor for the request's `raw` body mode. Language tracks the
 * sub-type selector (json / xml / plaintext) so the user gets matching
 * highlighting, bracket matching, and (for JSON) parse diagnostics.
 *
 * We share the `tap-light` / `tap-dark` themes with the Source tab so the body
 * editor visually matches the on-disk view. Cmd+S is intentionally NOT bound
 * here — Save is owned by the EditorShell toolbar and double-binding it inside
 * Monaco produced subtle "saves an old draft" races when the Monaco model
 * lagged the React `body` prop by a tick.
 */
export interface RawBodyEditorProps {
  value: string
  onChange: (next: string) => void
  rawSub: RawSubType
  /** Editor body height. Defaults to a generous fixed value because Monaco can't
   *  autosize without a parent height — we want the editor to fill the body tab. */
  height?: number | string
}

const LANGUAGE_BY_SUB: Record<RawSubType, string> = {
  json: 'json',
  xml: 'xml',
  text: 'plaintext',
}

export function RawBodyEditor({ value, onChange, rawSub, height = 460 }: RawBodyEditorProps) {
  const theme = useMonacoTheme()
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)

  // Theme follow-through, same pattern as SourceTab.
  useEffect(() => {
    if (!editorRef.current) return
    monaco.editor.setTheme(theme)
  }, [theme])

  const beforeMount: BeforeMount = (m) => ensureThemes(m)
  const onMount: OnMount = (editor) => { editorRef.current = editor }

  return (
    <Box
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        overflow: 'hidden',
      }}
    >
      <Editor
        height={height}
        language={LANGUAGE_BY_SUB[rawSub]}
        value={value}
        onChange={(v) => onChange(v ?? '')}
        theme={theme}
        beforeMount={beforeMount}
        onMount={onMount}
        options={{
          minimap: { enabled: false },
          fontSize: 12,
          fontFamily: 'var(--mono)',
          tabSize: 2,
          insertSpaces: true,
          scrollBeyondLastLine: false,
          scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
          automaticLayout: true,
          wordWrap: 'on',
          lineNumbersMinChars: 3,
          padding: { top: 8, bottom: 8 },
          // Schema validation for JSON without a schema is just visual noise; the
          // user is composing a request body, not authoring a JSON file.
          formatOnPaste: true,
        }}
      />
    </Box>
  )
}
