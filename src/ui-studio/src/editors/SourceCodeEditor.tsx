import { Box } from '@mantine/core'
import * as monaco from 'monaco-editor'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { WorkspaceErrorDto } from '../api/types'
import { MonacoEditor } from './MonacoEditor'
import { useMonacoTheme } from './monacoSetup'

interface Props {
  value: string
  onChange: (next: string) => void
  /** Monaco language id. `.http` files are raw-first — they have no canonical YAML form —
   *  so they reuse this editor with their own highlighting. */
  language: 'yaml' | 'http'
  /** A server-reported parse failure to pin on its line as a squiggle + gutter glyph.
   *  Cleared whenever it goes null. */
  error?: WorkspaceErrorDto | null
  /** Cmd/Ctrl+S inside the editor. */
  onSave?: () => void
  /** Fixed height. Omit to fill down to the bottom of the surrounding scroll area, which is
   *  what every current caller wants. */
  height?: string | number
  /** Scroll to a line and put the cursor there. Carries a `nonce` so asking for the same
   *  line twice still scrolls — the caller bumps it per click. */
  reveal?: { line: number; nonce: number } | null
}

/** Smallest usable editor, for when the pane is dragged very short. */
const MIN_HEIGHT = 160
/** Breathing room below the editor so it doesn't butt against the pane's edge. */
const BOTTOM_GAP = 16

/**
 * The source editing surface shared by every raw-text view: theme wiring, error markers,
 * Cmd+S, and a height that follows the pane. Deliberately owns no save/dirty state — the two
 * callers frame that differently (the Source tab has its own Save button; the `.http` editor
 * saves through the editor shell's), and only the editing surface itself is genuinely common.
 */
export function SourceCodeEditor({ value, onChange, language, error, onSave, height, reveal }: Props) {
  const monacoTheme = useMonacoTheme()
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null)

  // Latest save fn — read by the Cmd+S command so it doesn't need re-binding on every render.
  const saveRef = useRef<(() => void) | undefined>(onSave)
  saveRef.current = onSave

  const boxRef = useRef<HTMLDivElement>(null)
  const measured = useFillHeight(boxRef, height === undefined)
  const resolvedHeight = height ?? (measured === null ? MIN_HEIGHT : measured)

  // Runs again after a hidden tab is shown, because that re-creates the editor. Anything
  // attached to the old instance died with it, so bind here rather than once on mount.
  const onReady = useCallback((next: monaco.editor.IStandaloneCodeEditor) => {
    next.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveRef.current?.())
    setEditor(next)
  }, [])

  // Push server-side parse errors into Monaco's marker channel so they render as a red
  // squiggle + gutter glyph on the offending line.
  useEffect(() => {
    if (!editor) return
    const model = editor.getModel()
    if (!model) return
    if (!error || error.line == null) {
      monaco.editor.setModelMarkers(model, 'tap-source', [])
      return
    }
    const line = Math.min(Math.max(1, error.line), model.getLineCount())
    monaco.editor.setModelMarkers(model, 'tap-source', [{
      severity: monaco.MarkerSeverity.Error,
      message: `${error.code}: ${error.message}`,
      startLineNumber: line,
      endLineNumber: line,
      startColumn: 1,
      endColumn: model.getLineMaxColumn(line),
    }])
    editor.revealLineInCenterIfOutsideViewport(line)
  }, [error, editor])

  useEffect(() => {
    if (!editor || !reveal) return
    const model = editor.getModel()
    if (!model) return
    const line = Math.min(Math.max(1, reveal.line), model.getLineCount())
    editor.revealLineInCenter(line)
    editor.setPosition({ lineNumber: line, column: 1 })
    editor.focus()
  }, [reveal, editor])

  return (
    <Box
      ref={boxRef}
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        overflow: 'hidden',
        height: typeof resolvedHeight === 'number' ? `${resolvedHeight}px` : resolvedHeight,
      }}
    >
      <MonacoEditor
        value={value}
        onChange={onChange}
        language={language}
        theme={monacoTheme}
        onReady={onReady}
      />
    </Box>
  )
}

/**
 * Height that fills from the element's top edge to the bottom of the scroll area it lives in,
 * so the editor takes the space available rather than a guessed slice of the viewport, and long
 * files scroll *inside* Monaco instead of growing the pane around it.
 *
 * Measured rather than expressed in CSS because the chain from the editor shell's ScrollArea
 * down to here is not height-definite, and making it so would mean turning every editor's tab
 * content into a flex column — a much wider change than this needs.
 */
function useFillHeight(ref: React.RefObject<HTMLElement | null>, enabled: boolean): number | null {
  const [height, setHeight] = useState<number | null>(null)

  useLayoutEffect(() => {
    if (!enabled) return
    const el = ref.current
    if (!el) return

    let frame = 0
    const measure = () => {
      // Re-resolved every time: opening the response pane rebuilds the editor shell's layout
      // around a PanelGroup, so the scroll container we fill to is not necessarily the node it
      // was when this effect first ran.
      const scroller = findScrollParent(el)
      const top = el.getBoundingClientRect().top
      const bottom = scroller ? scroller.getBoundingClientRect().bottom : window.innerHeight
      setHeight(Math.max(MIN_HEIGHT, Math.round(bottom - top - BOTTOM_GAP)))
    }

    // react-resizable-panels sizes its panes a frame after they mount, so a measurement taken
    // during layout can read the geometry that is about to be replaced. Measure now for the
    // common case, then again once the frame has settled.
    const remeasure = () => {
      measure()
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    }
    remeasure()

    // Watching the scroll container covers what actually moves our bottom edge: the window
    // resizing, the response pane opening, and the split handle being dragged. Watching the
    // parent covers content appearing above us — an alert, another request row — which pushes
    // our top edge down. Watching the element itself would feed its own height back in.
    const observer = new ResizeObserver(remeasure)
    const scroller = findScrollParent(el)
    if (scroller) observer.observe(scroller)
    if (el.parentElement) observer.observe(el.parentElement)
    window.addEventListener('resize', remeasure)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
      window.removeEventListener('resize', remeasure)
    }
  }, [ref, enabled])

  return enabled ? height : null
}

function findScrollParent(el: HTMLElement): HTMLElement | null {
  let node = el.parentElement
  while (node) {
    const { overflowY } = getComputedStyle(node)
    if (overflowY === 'auto' || overflowY === 'scroll') return node
    node = node.parentElement
  }
  return null
}
