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

Four pages behind a hash router — hash rather than history, because GitHub Pages
serves static files and cannot rewrite unknown paths back to `index.html`.

| Route | Page | Source |
|---|---|---|
| `#/` | Tap Platform: the free-forever promise and the Studio + Tunnels product family | `src/pages/home.tsx` |
| `#/tunnels` | Tap Tunnels, with inspection as a built-in capability | `src/pages/tunnels.tsx` |
| `#/studio` | Tap Studio, with its own section nav | `src/pages/studio.tsx` |
| `#/download` | Every published artifact, with direct links into the latest release | `src/pages/download.tsx` |

The header carries two quick links beside the product switcher — **Download**
(`#/download`) and **Pricing** (`#/home/pricing`, a section of the overview rather
than a page of its own). Below 720px the header keeps only the download arrow and
the rail drawer carries the rest; `Shell.tsx` holds both halves of that.

A section is a second segment: `#/tunnels/cli`, `#/studio/studio-testing`. Legacy
`#/inspector/...` URLs remain aliases for the matching Tunnels page.

- `src/site.ts` — the page registry. Each page declares its rail nav; add a section
  here *and* give the rendered block a matching `id`, or the link goes nowhere.
  Also holds `legacyAnchors`, which maps every bare `#section-id` from the previous
  one-page site onto its new route so old links keep working.
- `src/router.ts` — hash parsing, scroll-on-navigate, and the scrollspy that
  highlights the section being read.
- `src/components/Shell.tsx` — the header (brand, product switcher with each
  product's own icon, the Pricing and Download quick links, GitHub) and the left
  rail holding the section nav, which collapses into a Contents drawer below
  1080px. The drawer also repeats the header actions, because the header sheds
  them as it narrows.
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
  copy of what those actually publish. `ShipGroupCard` in `ui.tsx` renders one of
  its groups; the home page lists all four, the download page pulls out NuGet and
  Docker, so the two never describe the same artifact differently.
- `src/data/release.ts` + `src/data/downloads.ts` — the download page's live half.
  Release asset names carry the version (`Tap.Studio_0.7.6_aarch64.dmg`), so
  GitHub's `/releases/latest/download/<name>` shortcut cannot address them and a
  baked-in URL would serve whatever version the docs were built from. `release.ts`
  therefore reads `/releases/latest` from the GitHub API once per session and
  `downloads.ts` matches assets by pattern — the same rule the release workflow
  follows for its own download table: derive names, never invent them. Every row
  falls back to the release page, so an unreachable API costs the direct link and
  nothing else. Change an asset name in `desktop.yml` or `release-binaries.yml`
  and the matching pattern has to move with it.

Styling is one stylesheet (`src/styles.css`) built on the ArbIQ design tokens. No
CSS modules, no utility framework.
