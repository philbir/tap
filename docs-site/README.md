# Tap Docs Site

Landing page and product documentation for Tap Platform, Tap Studio, and Tap Tunnels.

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
| `#/` | Tap Platform: the free-forever promise and the Studio + Tunnels product family | `src/pages/home.tsx` |
| `#/tunnels` | Tap Tunnels, with inspection as a built-in capability | `src/pages/tunnels.tsx` |
| `#/studio` | Tap Studio, with its own section nav | `src/pages/studio.tsx` |

A section is a second segment: `#/tunnels/cli`, `#/studio/studio-testing`. Legacy
`#/inspector/...` URLs remain aliases for the matching Tunnels page.

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
- `src/components/icons.tsx` — the marks for distribution channels and target
  platforms. Inlined paths rather than an icon package, so the site keeps no icon
  dependency: Docker, NuGet, Apple, and Tux from Simple Icons (CC0), Windows from
  Tabler (MIT), GitHub's own octocat, and .NET Aspire's raster mark from
  `public/aspire-icon.png`. Only the desktop display and the CLI prompt are drawn
  here — nothing brands those two.
- `src/data/` — code samples (`commands.ts`), the per-product feature lists,
  the job-to-product map behind the Use cases section (`usecases.ts`), and the
  published artifacts behind What ships (`shipped.ts`). Keep `shipped.ts` in step
  with the release workflows in `.github/workflows/` — it is the reader-facing
  copy of what those actually publish.

Styling is one stylesheet (`src/styles.css`) built on the ArbIQ design tokens. No
CSS modules, no utility framework.
