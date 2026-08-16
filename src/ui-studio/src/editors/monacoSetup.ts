import { useComputedColorScheme } from '@mantine/core'
import * as monaco from 'monaco-editor'

// There is no loader shim here any more: `@monaco-editor/react` is gone (see MonacoEditor.tsx
// for why), and with it the CDN fetch it needed pointing away from. `monaco-editor` is imported
// directly and bundled by Vite, workers included, so offline reloads keep working.

/**
 * Custom Monaco themes that follow Mantine's palette so the editor blends with the rest
 * of the Studio shell instead of looking like a transplanted VS Code window. Colors
 * come from `theme.ts` (the `tap` accent tuple) and Mantine's stock gray/dark scales.
 *
 * Registered once via `ensureThemes` — Monaco keeps themes globally, so we don't need
 * to re-register across editor instances.
 */
const TAP_LIGHT: monaco.editor.IStandaloneThemeData = {
  base: 'vs',
  inherit: true,
  rules: [
    { token: 'comment',  foreground: '6d6e75', fontStyle: 'italic' },
    { token: 'string',   foreground: '0b7261' },
    { token: 'number',   foreground: '5d2fd9' },
    { token: 'type',     foreground: '4f1cd1' },
    { token: 'keyword',  foreground: '4f1cd1', fontStyle: 'bold' },
    { token: 'tag',      foreground: '5d2fd9' },
    { token: 'attribute.name',  foreground: '4f1cd1' },
    { token: 'attribute.value', foreground: '0b7261' },
  ],
  colors: {
    'editor.background':              '#ffffff',
    'editor.foreground':              '#1e1e2e',
    'editorLineNumber.foreground':    '#adb5bd',
    'editorLineNumber.activeForeground': '#5d2fd9',
    'editorCursor.foreground':        '#5d2fd9',
    'editor.selectionBackground':     '#ddd1ff',
    'editor.inactiveSelectionBackground': '#f1ecff',
    'editor.lineHighlightBackground': '#f8f9fa',
    'editorIndentGuide.background':   '#e9ecef',
    'editorIndentGuide.activeBackground': '#ced4da',
    'editor.findMatchBackground':     '#ffd591',
    'editor.findMatchHighlightBackground': '#ffe8a8',
    'editorGutter.background':        '#ffffff',
    'editorWhitespace.foreground':    '#dee2e6',
    'editorError.foreground':         '#e03131',
  },
}

const TAP_DARK: monaco.editor.IStandaloneThemeData = {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'comment',  foreground: '7c7e85', fontStyle: 'italic' },
    { token: 'string',   foreground: '7dd3a8' },
    { token: 'number',   foreground: 'bba0ff' },
    { token: 'type',     foreground: 'a483ff' },
    { token: 'keyword',  foreground: 'a483ff', fontStyle: 'bold' },
    { token: 'tag',      foreground: 'bba0ff' },
    { token: 'attribute.name',  foreground: 'a483ff' },
    { token: 'attribute.value', foreground: '7dd3a8' },
  ],
  colors: {
    'editor.background':              '#1a1b1e',
    'editor.foreground':              '#c1c2c5',
    'editorLineNumber.foreground':    '#5c5f66',
    'editorLineNumber.activeForeground': '#a483ff',
    'editorCursor.foreground':        '#a483ff',
    'editor.selectionBackground':     '#3e15a8',
    'editor.inactiveSelectionBackground': '#2b1a6b',
    'editor.lineHighlightBackground': '#25262b',
    'editorIndentGuide.background':   '#2c2e33',
    'editorIndentGuide.activeBackground': '#3e15a8',
    'editor.findMatchBackground':     '#5d2fd9',
    'editor.findMatchHighlightBackground': '#3e15a8',
    'editorGutter.background':        '#1a1b1e',
    'editorWhitespace.foreground':    '#2c2e33',
    'editorError.foreground':         '#ff6b6b',
  },
}

let themesRegistered = false

/** Register the `tap-light` / `tap-dark` themes against the Monaco instance the editor
 *  is using. Safe to call repeatedly — only the first call actually defines. */
export function ensureThemes(m: typeof monaco): void {
  if (themesRegistered) return
  m.editor.defineTheme('tap-light', TAP_LIGHT)
  m.editor.defineTheme('tap-dark', TAP_DARK)
  themesRegistered = true
}

/** Resolves Mantine's color scheme to the matching Monaco theme name and re-renders on
 *  toggle. Use it together with `ensureThemes` in `beforeMount`. */
export function useMonacoTheme(): 'tap-light' | 'tap-dark' {
  const scheme = useComputedColorScheme('light')
  return scheme === 'dark' ? 'tap-dark' : 'tap-light'
}


/**
 * Registers a minimal `http` language with Monaco.
 *
 * Monaco ships no http grammar, and we deliberately do not depend on one being contributed by
 * an extension. This tokenizer covers what a .http file actually contains — request lines,
 * headers, `{{tokens}}`, comments, and the `@name` / `# @tap-*` directives, which get their own
 * scope so Tap's additions read as meaningful rather than as more comment noise.
 */
let httpLanguageRegistered = false

export function ensureHttpLanguage(m: typeof monaco): void {
  if (httpLanguageRegistered) return
  httpLanguageRegistered = true

  m.languages.register({ id: 'http', extensions: ['.http'] })

  m.languages.setMonarchTokensProvider('http', {
    defaultToken: '',
    tokenizer: {
      root: [
        // Request separator, with its optional title.
        [/^###.*$/, 'keyword'],
        // Directives first: they are comments, but Tap's are load-bearing.
        // NOTE: '@@' is Monarch's escape for a literal '@'. A bare '@tap-' is parsed as a
        // reference to an attribute named 'tap' on this language definition, which throws at
        // registration time and takes the whole editor down with it.
        [/^\s*(#|\/\/)\s*@@tap-[a-z-]+/, 'type.identifier'],
        [/^\s*(#|\/\/)\s*@@[a-z-]+/, 'attribute.name'],
        [/^\s*(#|\/\/).*$/, 'comment'],
        // File variables: @name = value.
        [/^\s*@@[\w.-]+(?=\s*=)/, 'variable.name'],
        // Request line.
        [/^\s*(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|TRACE|CONNECT)\b/, 'keyword'],
        // Header name.
        [/^[A-Za-z][A-Za-z0-9-]*(?=\s*:)/, 'attribute.name'],
        // Interpolation tokens, anywhere.
        [/\{\{[^}]*\}\}/, 'string.escape'],
        // Body include.
        [/^\s*<\s+\S+.*$/, 'string'],
      ],
    },
  })

  m.languages.setLanguageConfiguration('http', {
    comments: { lineComment: '#' },
    brackets: [['{', '}'], ['[', ']']],
  })
}
