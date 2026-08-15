import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(() => {
  const studioApiUrl = process.env['VITE_STUDIO_API_URL'] ?? 'http://localhost:5298'
  const port = Number(process.env['PORT'] ?? 5297)

  // CodeMirror's Facet/Extension identity is instanceof-based: a second copy of
  // @codemirror/state (or /view) makes every extension unrecognizable and throws
  // "Unrecognized extension value in extension set" during render, blanking every
  // editor surface. Two independent things can produce that second copy, so both
  // are pinned here rather than relying on how the tree happens to hoist:
  //
  //   - `dedupe` collapses the resolution, so a transitive dep asking for a
  //     different range can't fork it in node_modules.
  //   - `optimizeDeps.include` pre-bundles the whole family in one pass. If some
  //     packages are optimized and others are reached from source, the optimizer
  //     mints its own second instance even when node_modules holds exactly one.
  //
  // yarn `resolutions` covers the on-disk half too; this covers the dev server.
  const codemirror = [
    '@codemirror/state', '@codemirror/view', '@codemirror/language',
    '@codemirror/autocomplete', '@codemirror/commands', '@codemirror/lint',
    '@codemirror/search',
    '@codemirror/lang-css', '@codemirror/lang-html', '@codemirror/lang-javascript',
    '@codemirror/lang-json', '@codemirror/lang-markdown', '@codemirror/lang-xml',
    '@codemirror/lang-yaml',
    '@lezer/highlight', '@lezer/lr',
    '@uiw/react-codemirror', '@uiw/codemirror-theme-vscode',
    'cm6-graphql',
  ]

  return {
    plugins: [react()],
    resolve: {
      dedupe: ['@codemirror/state', '@codemirror/view'],
    },
    optimizeDeps: {
      include: codemirror,
    },
    server: {
      port,
      strictPort: true,
      proxy: {
        '/api': {
          target: studioApiUrl,
          changeOrigin: true,
          ws: false,
        },
      },
    },
  }
})
