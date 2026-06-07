---
name: mantine
description: "Mantine v9 React component library cheat sheet for the Tap Studio UI (ui-studio/). USE WHEN: writing or editing React components under ui-studio/, asking about Mantine v9 props, color-scheme, theme tokens, AppShell, forms, modals, notifications, tabs, badges, layout. DO NOT USE FOR: the standalone Tap Inspector UI (ui/) which uses its own CSS, or for non-React code. INVOKES: file edits, build commands. The Studio UI is Mantine-only — drop in raw <div>/CSS only when there's no Mantine equivalent."
---

# Mantine v9 skill (Tap Studio)

The Tap Studio UI (`ui-studio/`) is built entirely on **Mantine 9.2.1** + **@tabler/icons-react**. There is no Tailwind, no shadcn, no base-ui — only Mantine. Custom CSS modules are reserved for the variable-input painted overlay and a couple of helper styles. Everything else uses Mantine components and theme tokens.

This skill is the working reference: idiomatic patterns we already use, v9-specific gotchas, and where to look for more.

## When to invoke

Invoke this skill any time the work touches `ui-studio/src/**` and involves form layouts, dialogs, tab bars, color-scheme behaviour, or new component scaffolding. For pure data/state work (Zustand store, API client, TypeScript types) you don't need it.

## Quick links

- **Mantine docs**: https://mantine.dev/getting-started/
- **Component index**: https://mantine.dev/core/anchor/ (replace `anchor` with the component name)
- **Tabler icons**: https://tabler.io/icons (search → import `IconXxx` from `@tabler/icons-react`)
- **v8→v9 changelog**: https://mantine.dev/changelog/9-0-0/
- **Theme object types**: `node_modules/@mantine/core/lib/core/MantineProvider/theme/...`
- **Reference app**: `/Users/p7e/code/philbir/dreamr/src/ui` — uses Mantine 8 but most patterns are stable.

## Live docs via the Context7 MCP

The repo ships a project-scoped MCP at `/.mcp.json` that registers **Context7**. Use it to pull current prop signatures + code snippets without opening a browser:

1. `mcp__context7__resolve-library-id` with `libraryName: "mantine"` → returns `/mantinedev/mantine` (and version variants).
2. `mcp__context7__get-library-docs` with that ID + a focused `topic` (e.g. `"Collapse component"`, `"TagsInput onChange"`, `"AppShell Header"`).

Context7 is the canonical source for v9 props (when in doubt about `expanded` vs `in`, ask it first). For Tabler icons, `mcp__context7__resolve-library-id` with `"@tabler/icons-react"` works the same way.

## Files that pin our setup

| File | Role |
|---|---|
| `ui-studio/src/main.tsx` | `MantineProvider` + `ColorSchemeScript` + `Notifications` + `ModalsProvider`. |
| `ui-studio/src/theme.ts` | Custom `tap` color tuple + `defaultRadius` + component defaults. |
| `ui-studio/postcss.config.cjs` | `postcss-preset-mantine` + breakpoint variables. |
| `ui-studio/src/styles/index.css` | The *only* hand-rolled global CSS (method colors + mono font stack). |
| `ui-studio/src/workspace/useTheme.ts` | Thin wrapper over `useMantineColorScheme`. |

## v9-specific gotchas you will hit

1. **`Collapse` uses `expanded`, not `in`.** v8 used `in`; v9 renamed it.
   ```tsx
   <Collapse expanded={opened}>…</Collapse>
   ```
2. **`MultiSelect` cannot create new tags** — for chip-style inputs (auth scopes, tags) use **`TagsInput`**.
3. **`Select` defaults to `allowDeselect={true}`.** For "must-pick-one" fields, set `allowDeselect={false}` so users can't click the selected option to clear it.
4. **`Select` accepts string or `{value,label}[]`** — when passing `readonly` arrays (e.g. tuples), cast: `data={METHODS as unknown as string[]}`.
5. **`Tabs` value is `string | null`** — keep state as `useState<string | null>('default-tab')`.
6. **`Modal.onClose` runs on any close (Esc, backdrop, X)** — your cleanup logic goes there, not on the "Cancel" button.
7. **`Notifications`** is a *component*, not a hook — mount it once at the root (already done in `main.tsx`). Use `notifications.show({…})` to fire.
8. **`useDisclosure`** returns `[opened, { open, close, toggle }]` — destructure the second element by name, not index.
9. **CSS variables are prefixed `--mantine-color-*`** (e.g. `--mantine-color-tap-6`). The `c` prop accepts shortcut names: `c="tap"`, `c="dimmed"`, `c="red"`.
10. **`<Code>`** is inline by default — use `<Code block>` for multi-line code blocks.
11. **The `loading` prop** on `Button` shows a spinner and disables the button — don't double-disable manually.

## Idiomatic patterns from our codebase

### Forms — typed local state, no `@mantine/form`

We don't use `@mantine/form` (yet). Editors keep `spec` and `savedSpec` via `useState` and PUT a typed DTO to the server. See `src/editors/AuthEditor.tsx`. The pattern:

```tsx
const [spec, setSpec] = useState<AuthSpec | null>(null)
const [savedSpec, setSavedSpec] = useState<AuthSpec | null>(null)
const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

function update<K extends keyof AuthSpec>(key: K, value: AuthSpec[K]) {
  setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
}
```

If a future editor needs validation, switch to `useForm()` from `@mantine/form` — but be consistent within the editor.

### Layout primitives we lean on

- **`<Stack gap="md">`** — vertical form fields, dialog bodies, panel content.
- **`<Group gap="xs">`** — inline row of controls (toolbar, header right side).
- **`<Group grow>`** — two side-by-side inputs (e.g. Name + Type).
- **`<SimpleGrid cols={2}>`** — the kind picker cards in CreateNewDialog.
- **`<AppShell>`** with `header / navbar / main` — the top-level shell in `App.tsx`.
- **`<ScrollArea>`** wherever content might overflow. Set `flex={1}` for vertical fill.
- **`<Tabs.List mb="md">` + `<Tabs.Tab leftSection={<Icon size={14} />}>`** — every editor's section navigation.

### Color usage

- Brand: `color="tap"` (primary color is set in the theme).
- Per-kind chips in editor headers (`EditorShell`):
  - Request → `tap`
  - API → `teal`
  - Auth → `orange`
  - Environment → `grape`
  - Workspace → `tap`
- Action states: `green` (success), `red` (error/destructive), `yellow` (warn/secret), `gray` (muted).
- Don't use raw hex codes in component code — pick from theme colors or use `c="dimmed"`.

### Indicating dirty / counts on tabs

```tsx
<Tabs.Tab value="headers">
  Headers {count > 0 && <Text component="span" c="dimmed" ml={6}>{count}</Text>}
</Tabs.Tab>
```

### Modals via the provider

Use the imperative API only when there's no useful body — for actual forms (Add Workspace, Create New) prefer `<Modal opened={…} onClose={…}>` directly. Confirmation dialogs:

```tsx
modals.openConfirmModal({
  title: 'Delete request?',
  children: <Text size="sm">This can't be undone.</Text>,
  labels: { confirm: 'Delete', cancel: 'Keep' },
  confirmProps: { color: 'red' },
  onConfirm: () => doDelete(),
})
```

### Notifications

```tsx
import { notifications } from '@mantine/notifications'
notifications.show({ title: 'Saved', message: 'Request updated.', color: 'green', autoClose: 2500 })
notifications.show({ title: 'Save failed', message: e.message, color: 'red' })
```

### Tabler icons

- Always import from `@tabler/icons-react`: `import { IconSend, IconLock } from '@tabler/icons-react'`.
- Default `size={14}` inside `Tabs.Tab` and toolbar buttons; `size={16}` for header icons; `size={20}` for empty-state hero icons.
- Stroke width is `2` by default. Use `stroke={1.5}` for big/decorative icons so they don't look heavy.

### Variable-token highlighting

`src/editors/VariableInput.tsx` wraps `TextInput` with a painted overlay that colorizes `{{var}}` and `${{secret}}` tokens. Use it anywhere a value can carry templates (URLs, auth fields, env values). Plain `TextInput` is fine for non-templated fields like `Name`.

## Migration anti-patterns (don't do these)

- ❌ Importing from `@base-ui-components/react` — that package has been removed.
- ❌ Inlining CSS in `style={{}}` for layout — use `<Stack>`, `<Group>`, or `mb="md"` props. Inline `style` is OK for one-off width tweaks and Mantine CSS-variable references (e.g. `color: 'var(--mantine-color-tap-6)'`).
- ❌ Creating new `.module.css` files — Mantine's `styles` prop + `style` prop cover 95% of cases; the remaining 5% goes through theme overrides in `theme.ts`.
- ❌ Using `document.documentElement.dataset.theme` — Mantine owns the color scheme via `useMantineColorScheme`.
- ❌ Wrapping every input in a manual `<label>` — Mantine's input components have a `label` prop.

## Adding a new component to a panel

1. Find the closest Mantine match at https://mantine.dev/core/.
2. Import via `import { ComponentName } from '@mantine/core'` (named exports only).
3. Default to **`size="sm"`** unless the component is a hero/empty-state.
4. If you need a layout other than `Stack`/`Group`, reach for `<Box>` with `display`/`style` rather than a new CSS module.
5. Wire icons through `@tabler/icons-react` (`leftSection`, `rightSection`, `icon` props).

## Color scheme & SSR-flash prevention

- `<ColorSchemeScript defaultColorScheme="auto" />` is the FIRST child of `<StrictMode>` in `main.tsx`. It injects a tiny pre-mount script that reads `localStorage.mantine-color-scheme-value` and sets `data-mantine-color-scheme` on `<html>` before React paints. **Don't move it.**
- The theme button calls `useMantineColorScheme().setColorScheme('light'|'dark'|'auto')`.

## Theme extension cookbook

Add component-wide defaults in `theme.ts` rather than passing the same props at every call site. Example: `Button` defaults to `size: 'sm'` there. To change Mantine's spacing scale or add a new color, see https://mantine.dev/theming/theme-object/.

## Workflow: building a new editor or panel

1. Spec the props (typed object client-side, server emits YAML — see `Specs/` on the backend).
2. Start with `<EditorShell title={…} kindLabel="…" dirty={…} saving={…} onSave={…}>` — gives you save/discard/error-bar/dirty-dot for free.
3. Put tabs inside via `<Tabs value={tab} onChange={setTab}>` + `<Tabs.List mb="md">` + `<Tabs.Tab>` per section.
4. Forms: vertical `<Stack gap="md" maw={760}>`; per-field `<TextInput label="…">` or `<Select label="…">`.
5. For tabular data: import `KvTable` from `./KvTable` (the local Mantine-styled version).
6. For OAuth-style execute panels: pass `rightPane={<AuthExecutePanel …/>}` — `EditorShell` allocates 520px for it.

## Don't forget

- After editing CSS modules or theme tokens, run `yarn build` from `ui-studio/` to catch type errors fast.
- The Studio AppHost (`samples/Studio.AppHost`) runs Vite via the JavaScript hosting integration. Hot-reload works without restarting the AppHost as long as `vite.config.ts` is unchanged.
- When in doubt about a v9-vs-v8 prop name, check `/tmp/mantine-check/node_modules/@mantine/core/lib/components/<Name>/<Name>.d.ts` — copy the package locally with `npm i --no-save @mantine/core@9.2.1` in a scratch dir if needed.
