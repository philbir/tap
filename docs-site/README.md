# Tap Docs Site

Landing page and product documentation for Tap.

```bash
yarn
yarn dev
yarn build
yarn preview
```

The Vite config uses `base: "./"` so the generated `dist/` works under GitHub Pages project paths such as `https://philbir.github.io/tap/`.

## Structure

Three pages behind a hash router — hash rather than history, because GitHub Pages
serves static files and cannot rewrite unknown paths back to `index.html`.

| Route | Page | Source |
|---|---|---|
| `#/` | The Tap story: the free-forever promise, both products, what it costs | `src/pages/home.tsx` |
| `#/inspector` | Tunnel + Inspector, with its own section nav | `src/pages/inspector.tsx` |
| `#/studio` | Tap Studio, with its own section nav | `src/pages/studio.tsx` |

A section is a second segment: `#/inspector/cli`, `#/studio/studio-testing`.

- `src/site.ts` — the page registry. Each page declares its rail nav; add a section
  here *and* give the rendered block a matching `id`, or the link goes nowhere.
  Also holds `legacyAnchors`, which maps every bare `#section-id` from the previous
  one-page site onto its new route so old links keep working.
- `src/router.ts` — hash parsing, scroll-on-navigate, and the scrollspy that
  highlights the section being read.
- `src/components/Shell.tsx` — the header (brand, product switcher with each
  product's own icon, GitHub) and the left rail holding the section nav, which
  collapses into a Contents drawer below 1080px.
- `src/components/ui.tsx` — the shared blocks (`CodeBlock`, `ConfigTable`,
  `ModeGrid`, `Callout`, `ProviderDetail`, …). Doc sections are data: a
  `DocSection` carries its own `content()`, and `DocList` renders the array.
- `src/data/` — code samples (`commands.ts`) and the per-product feature lists.

Styling is one stylesheet (`src/styles.css`) built on the ArbIQ design tokens. No
CSS modules, no utility framework.
