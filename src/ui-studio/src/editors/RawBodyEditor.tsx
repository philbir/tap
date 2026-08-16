import { Box } from '@mantine/core'
import { MonacoEditor } from './MonacoEditor'
import { useMonacoTheme } from './monacoSetup'
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

  return (
    <Box
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-sm)',
        overflow: 'hidden',
        height: typeof height === 'number' ? `${height}px` : height,
      }}
    >
      <MonacoEditor
        language={LANGUAGE_BY_SUB[rawSub]}
        value={value}
        onChange={onChange}
        theme={theme}
        options={{ formatOnPaste: true }}
      />
    </Box>
  )
}
