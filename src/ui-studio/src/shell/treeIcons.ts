import type { FileTreeIcons } from '@pierre/trees'
import { COLLECTION_FILE, LEGACY_COLLECTION_FILE, MANIFEST_FILE } from './tapFiles'

/**
 * Sprite of Tabler outline icons remapped onto the pierre file tree, so request/auth/env
 * files render with the same iconography (paper plane / lock / globe / folders) the rest
 * of the Studio uses. Paths are copy-pasted from `@tabler/icons` outline SVGs.
 *
 * The pierre tree injects this sprite into its shadow DOM. To color each one to match
 * its `<IconSend color="tap.6">`-style sibling elsewhere in the UI we ship a paired
 * `TAP_TREE_UNSAFE_CSS` block (selectors target the `data-icon-name` the tree stamps
 * on every `<svg>` it renders from the sprite).
 */
const TAP_ICON_SPRITE = `
  <svg xmlns="http://www.w3.org/2000/svg" aria-hidden="true" style="display:none">
    <symbol id="tap-icon-send" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M10 14l11 -11" />
      <path d="M21 3l-6.5 18a.55 .55 0 0 1 -1 0l-3.5 -7l-7 -3.5a.55 .55 0 0 1 0 -1l18 -6.5" />
    </symbol>
    <symbol id="tap-icon-lock" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M5 13a2 2 0 0 1 2 -2h10a2 2 0 0 1 2 2v6a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-6" />
      <path d="M11 16a1 1 0 1 0 2 0a1 1 0 0 0 -2 0" />
      <path d="M8 11v-4a4 4 0 1 1 8 0v4" />
    </symbol>
    <symbol id="tap-icon-world" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M3 12a9 9 0 1 0 18 0a9 9 0 0 0 -18 0" />
      <path d="M3.6 9h16.8" />
      <path d="M3.6 15h16.8" />
      <path d="M11.5 3a17 17 0 0 0 0 18" />
      <path d="M12.5 3a17 17 0 0 1 0 18" />
    </symbol>
    <symbol id="tap-icon-folders" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M9 3h3l2 2h5a2 2 0 0 1 2 2v7a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-9a2 2 0 0 1 2 -2" />
      <path d="M17 16v2a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-9a2 2 0 0 1 2 -2h2" />
    </symbol>
    <symbol id="tap-icon-folder" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M5 4h4l3 3h7a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2" />
    </symbol>
    <symbol id="tap-icon-file-code" viewBox="0 0 24 24" fill="none" stroke="currentColor"
            stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M14 3v4a1 1 0 0 0 1 1h4" />
      <path d="M17 21h-10a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2h7l5 5v11a2 2 0 0 1 -2 2z" />
      <path d="M10 13l-1 2l1 2" />
      <path d="M14 13l1 2l-1 2" />
    </symbol>
  </svg>
`

/**
 * CSS injected into the tree's shadow DOM via the `unsafeCSS` option.
 *
 * - Background + foreground follow Mantine theme tokens so the tree blends into
 *   `--mantine-color-body` (white in light mode, dark in dark mode) instead of pierre's
 *   own default which doesn't track the theme.
 * - Per-icon colors mirror the Studio's IconSend(tap)/IconLock(orange)/IconWorld(grape)
 *   /IconFolders(blue) palette — selectors target the `data-icon-name` the tree stamps
 *   on every `<svg use href="#…">` it renders from our sprite.
 */
// Pierre keeps the original `data-icon-name` (e.g. `file-tree-icon-file`) on the SVG
// and only swaps the inner `<use href>`. We have to colorise via the `use` href instead.
//
// Folder rows: pierre's built-in icon set has no folder slot — directory rows render
// with just the chevron. We inject a CSS-mask folder icon right after the chevron via
// `[data-item-section="icon"]::after` on rows that pierre marks with
// `data-item-type="folder"`. Closed and open variants are picked off `aria-expanded`.
const FOLDER_CLOSED_MASK =
  "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 4h4l3 3h7a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2'/></svg>\")"
const FOLDER_OPEN_MASK =
  "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 19l2.757 -7.351a1 1 0 0 1 .936 -.649h12.307a1 1 0 0 1 .986 1.164l-.996 5.211a2 2 0 0 1 -1.964 1.625h-14.026a2 2 0 0 1 -2 -2v-11a2 2 0 0 1 2 -2h4l3 3h7a2 2 0 0 1 2 2v2'/></svg>\")"

// A .http row expands like a folder because it holds several requests, but it is one file —
// so it gets the file-code glyph instead of a folder, matching the icon its tab and its editor
// header already use. Same mask mechanism as the folder above, keyed off the display path
// pierre stamps on every row (folder rows carry a trailing '/').
const FILE_CODE_MASK =
  "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M14 3v4a1 1 0 0 0 1 1h4'/><path d='M17 21h-10a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2h7l5 5v11a2 2 0 0 1 -2 2z'/><path d='M10 13l-1 2l1 2'/><path d='M14 13l1 2l-1 2'/></svg>\")"

const TAP_TREE_UNSAFE_CSS = `
  :host {
    --trees-bg: var(--mantine-color-body);
    --trees-fg: var(--mantine-color-text);
    --trees-fg-muted: var(--mantine-color-dimmed);
    --trees-border-color: var(--mantine-color-default-border);
    --trees-accent: var(--mantine-color-tap-filled);
  }
  svg:has(use[href="#tap-icon-send"])    { color: var(--mantine-color-tap-6); }
  svg:has(use[href="#tap-icon-lock"])    { color: var(--mantine-color-orange-6); }
  svg:has(use[href="#tap-icon-world"])   { color: var(--mantine-color-grape-6); }
  svg:has(use[href="#tap-icon-folders"]) { color: var(--mantine-color-yellow-6); }
  svg:has(use[href="#tap-icon-folder"])  { color: var(--mantine-color-yellow-6); }
  svg:has(use[href="#tap-icon-file-code"]) { color: var(--mantine-color-blue-6); }

  /* Selection styling: pierre paints a strong tap-light band on the selected row.
     Tone it down to just bold + keep the foreground colour so the selection reads
     as the active tab without dominating the sidebar.
     The overlay var matters as much as the row background: the middle-truncation
     marker (the "…" that occludes overflowing text) paints
     --truncate-marker-background-overlay-color on top of its own backdrop, and
     pierre points that at --trees-selected-bg for selected rows — which is why a
     truncated selected label showed a purple block behind the ellipsis. Both
     declarations need !important: pierre's rule nests to
     [data-type="item"][data-item-selected="true"], which outranks ours. */
  [data-item-selected="true"] {
    background-color: transparent !important;
    color: var(--trees-fg) !important;
    font-weight: 600;
    --truncate-marker-background-overlay-color: transparent !important;
  }
  [data-item-selected="true"]::before {
    outline-color: transparent !important;
  }
  [data-item-selected="true"] [data-item-section="icon"] {
    color: inherit;
  }

  /* Context-menu trigger visibility: pierre only renders the dots icon when
     buttonVisibility is set to always, so we ask for always and then hide the
     lane ourselves until the row is hovered / focused / its menu is open. */
  [data-type="item"] [data-item-section="action"] {
    opacity: 0;
    transition: opacity 120ms ease;
  }
  [data-type="item"]:hover [data-item-section="action"],
  [data-type="item"]:focus [data-item-section="action"],
  [data-type="item"]:focus-within [data-item-section="action"],
  [data-type="item"][data-item-context-menu-open="true"] [data-item-section="action"] {
    opacity: 1;
  }

  [data-item-type="folder"] [data-item-section="icon"]::after {
    content: '';
    display: block;
    flex: 0 0 14px;
    width: 14px;
    height: 14px;
    margin-left: 4px;
    background-color: var(--mantine-color-yellow-6);
    -webkit-mask: ${FOLDER_CLOSED_MASK} center / contain no-repeat;
    mask: ${FOLDER_CLOSED_MASK} center / contain no-repeat;
  }
  [data-item-type="folder"][aria-expanded="true"] [data-item-section="icon"]::after {
    -webkit-mask: ${FOLDER_OPEN_MASK} center / contain no-repeat;
    mask: ${FOLDER_OPEN_MASK} center / contain no-repeat;
  }

  /* .http rows: folder-shaped (they expand to their requests) but a single file, so the glyph
     is file-code rather than a folder, in the same blue the rest of the UI badges this kind
     with. Both the collapsed and expanded selectors are spelled out — the open-folder rule
     above has equal specificity, and matching it explicitly beats relying on source order. */
  [data-item-type="folder"][data-item-path$=".http/"] [data-item-section="icon"]::after,
  [data-item-type="folder"][data-item-path$=".http/"][aria-expanded="true"] [data-item-section="icon"]::after {
    background-color: var(--mantine-color-blue-6);
    -webkit-mask: ${FILE_CODE_MASK} center / contain no-repeat;
    mask: ${FILE_CODE_MASK} center / contain no-repeat;
  }
`

const customIcon = (name: string) => ({ name, viewBox: '0 0 24 24', width: 16, height: 16 })

export const TAP_ICONS = {
  send: customIcon('tap-icon-send'),
  lock: customIcon('tap-icon-lock'),
  world: customIcon('tap-icon-world'),
  folders: customIcon('tap-icon-folders'),
  folder: customIcon('tap-icon-folder'),
  fileCode: customIcon('tap-icon-file-code'),
} as const

/** Build a FileTree icon config that maps the given per-basename overrides onto our
 *  shared sprite + theme CSS.
 *
 *  Display paths in the requests/auth views strip the `.req.tap` / `.auth.tap` suffix,
 *  so pierre's `byFileExtension` can't see what kind a file is. Instead, the host
 *  view (Sidebar) builds an explicit `display-basename → icon` map at the same time
 *  it strips the suffix, and hands it to this builder. */
export function makeTapTreeIcons(byBasename?: Record<string, ReturnType<typeof customIcon>>): FileTreeIcons {
  return {
    set: 'standard',
    colored: true,
    spriteSheet: TAP_ICON_SPRITE,
    remap: {
      folder: TAP_ICONS.folder,
    },
    // Compound-extension fallbacks for paths that didn't go through display-stripping
    // (e.g. the Filesystem view). Both extension families appear while workspaces are
    // mid-migration, and a file with no icon reads as 'unrecognized', so list both.
    byFileExtension: {
      'req.tap': TAP_ICONS.send,
      'auth.tap': TAP_ICONS.lock,
      'env.tap': TAP_ICONS.world,
      'flow.tap': TAP_ICONS.send,
      'test.tap': TAP_ICONS.send,
      'req.md': TAP_ICONS.send,
      'auth.md': TAP_ICONS.lock,
      'env.md': TAP_ICONS.world,
      'flow.md': TAP_ICONS.send,
      'test.md': TAP_ICONS.send,
      // The Filesystem view renders a .http file as the plain file row it is, so it picks its
      // icon up here rather than from the folder-shaped override in the CSS above.
      http: TAP_ICONS.fileCode,
    },
    byFileName: {
      [COLLECTION_FILE]: TAP_ICONS.folders,
      [LEGACY_COLLECTION_FILE]: TAP_ICONS.folders,
      [MANIFEST_FILE]: TAP_ICONS.folders,
      ...(byBasename ?? {}),
    },
  }
}

/** Default config (no per-basename overrides). Use for views like the Filesystem
 *  tree where the on-disk extension is preserved. */
export const TAP_TREE_ICONS: FileTreeIcons = makeTapTreeIcons()

export { TAP_TREE_UNSAFE_CSS }
