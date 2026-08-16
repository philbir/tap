import * as monaco from 'monaco-editor'
import { useEffect, useRef } from 'react'
import { ensureHttpLanguage, ensureThemes } from './monacoSetup'

export interface MonacoEditorProps {
  value: string
  /** Omit for a read-only view. */
  onChange?: (next: string) => void
  language: string
  theme: string
  options?: monaco.editor.IStandaloneEditorConstructionOptions
  /** Called with the live editor after every create — including the re-create that follows a
   *  hidden tab being shown again. Use it to (re)attach commands, markers, or decorations;
   *  anything attached to a previous instance is gone by then. */
  onReady?: (editor: monaco.editor.IStandaloneCodeEditor) => void
  className?: string
}

const BASE_OPTIONS: monaco.editor.IStandaloneEditorConstructionOptions = {
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
}

/**
 * Monaco, mounted directly.
 *
 * **Why not `@monaco-editor/react`.** That wrapper disposes the editor and its model from an
 * effect cleanup, but records "I already built one" in refs and state. React 19's `<Activity>` —
 * which Mantine's `Tabs` uses for the panel you switched away from — destroys effects while
 * *preserving* refs and state. So the wrapper disposes on hide, then on re-show believes it
 * still has a live editor: it never re-creates, and its prop-diffing effects call
 * `updateOptions` / `getOption` / `setValue` on the disposed instance. The panel dies with
 * `InstantiationService has been disposed`, and no amount of remounting it from the outside
 * gets ahead of the effect that throws.
 *
 * Owning `create` and `dispose` in our own effect makes the cycle correct by construction: the
 * cleanup disposes and clears our ref, the body builds a fresh editor. It also costs less than
 * the wrapper did — we were already driving themes, markers and reveals through the raw monaco
 * API, so the only thing lost is a loading spinner for a module that is bundled, not fetched.
 */
export function MonacoEditor({ value, onChange, language, theme, options, onReady, className }: MonacoEditorProps) {
  const hostRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)

  // Latest callbacks and value, read by the create effect without becoming dependencies of it —
  // re-creating the editor on every keystroke would be absurd, and on every render worse.
  const valueRef = useRef(value)
  const onChangeRef = useRef(onChange)
  const onReadyRef = useRef(onReady)
  const optionsRef = useRef(options)
  valueRef.current = value
  onChangeRef.current = onChange
  onReadyRef.current = onReady
  optionsRef.current = options

  useEffect(() => {
    const host = hostRef.current
    if (!host) return

    ensureThemes(monaco)
    ensureHttpLanguage(monaco)

    const editor = monaco.editor.create(host, {
      ...BASE_OPTIONS,
      ...optionsRef.current,
      value: valueRef.current,
      language,
      theme,
    })
    editorRef.current = editor

    const subscription = editor.onDidChangeModelContent(() => {
      onChangeRef.current?.(editor.getValue())
    })
    onReadyRef.current?.(editor)

    return () => {
      subscription.dispose()
      // The model is ours (monaco created it implicitly for this editor), so it goes with it.
      editor.getModel()?.dispose()
      editor.dispose()
      editorRef.current = null
    }
    // `theme` and the initial value are seeded here but maintained by the effects below;
    // only a language change genuinely needs a different editor.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [language])

  // External value changes (a reload from disk, a Revert). Guarded on inequality so this never
  // fights the user mid-keystroke — our own onChange is what produced most of these renders.
  useEffect(() => {
    const editor = editorRef.current
    if (!editor || editor.getValue() === value) return
    editor.setValue(value)
  }, [value])

  useEffect(() => {
    if (editorRef.current) monaco.editor.setTheme(theme)
  }, [theme])

  return <div ref={hostRef} className={className} style={{ width: '100%', height: '100%' }} />
}
