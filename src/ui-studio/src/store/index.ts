import { useCallback, useMemo } from 'react'
import { create } from 'zustand'
import { persist, createJSONStorage } from 'zustand/middleware'
import { api, subscribeWorkspaceChanges } from '../api/client'
import type {
  AuthSummary, CollectionSummary, EnvSummary, FlowSummary, KnownWorkspace, TestSetSummary, TreeNode,
  WorkspaceFileKind, WorkspaceInfo,
} from '../api/types'
import { envAppliesTo } from '../api/types'
import { toCanonicalPath } from '../shell/tapFiles'

/** One entry the convert-to-variable panel wrote into a file's `vars:`. See
 *  `TapStore.declaredVars`. */
export interface DeclaredVar {
  /** What the `vars:` entry holds — the literal, or the `{{provider:key}}` reference that
   *  stands in for a secret stored elsewhere. */
  value: string
  secret: boolean
}

/** Per-tab state that outlives the editor's mount. See `TapStore.tabState`. */
export interface TabState {
  /**
   * The editor's unsaved value, in whatever shape that editor works in. Present exactly
   * while the tab has unsaved changes — saving or discarding drops it — so it doubles as
   * the dirty flag the tab strip marks up.
   */
  draft?: unknown
  /**
   * View state by slot name: which sub-tab is open, which body view, and so on. Losing
   * these on a tab switch is small but constant friction — you come back to the request you
   * were reading and it has snapped from Body back to Params.
   */
  view?: Record<string, unknown>
}

/** One open file in the tab bar. */
export interface OpenTab {
  /** Workspace-relative path; '__manifest__' is reserved for the workspace.tap workspace editor. */
  path: string
  kind: WorkspaceFileKind
  /** Display label for the tab header. */
  label: string
}

export const MANIFEST_TAB_PATH = '__manifest__'
/** Reserved path for the Settings tab — not a real workspace file. */
export const SETTINGS_TAB_PATH = '__settings__'

/** Reserved tab path for one variable provider's editor. Providers aren't workspace files —
 *  a system-scope one has no path at all — so the name is carried in the token itself. */
export const PROVIDER_TAB_PREFIX = '__provider__:'
export const providerTabPath = (name: string) => `${PROVIDER_TAB_PREFIX}${name}`
export const providerNameFromTab = (path: string) =>
  path.startsWith(PROVIDER_TAB_PREFIX) ? path.slice(PROVIDER_TAB_PREFIX.length) : null

/**
 * Single global app store. Holds server-derived state (workspace info, tree, catalogs,
 * known-workspaces) plus UI state that's shared across components (tabs, active env).
 *
 * Editor-local form state (specs, dirty flags, busy markers) stays inside each editor's
 * `useState` — it doesn't belong here.
 *
 * Selectors are the access pattern: <code>useTapStore(s => s.tree)</code>. A consumer
 * only re-renders when its selected slice changes, which is the whole point of moving
 * off the hand-rolled hook (the old hook re-rendered every consumer on any field change).
 */
export interface TapStore {
  // -- Server state ----------------------------------------------------------
  info: WorkspaceInfo | null
  tree: TreeNode[]
  envs: EnvSummary[]
  collections: CollectionSummary[]
  auths: AuthSummary[]
  /** Test sets and flows, for the Testing view. Both are cheap listings — a workspace has
   *  far fewer of them than requests. */
  testSets: TestSetSummary[]
  flows: FlowSummary[]
  knownWorkspaces: KnownWorkspace[]
  /** Increments after every successful reload. Editors `useEffect` on this to refetch. */
  generation: number
  loadError: string | null

  // -- UI state (persisted) --------------------------------------------------
  tabs: OpenTab[]
  activeTab: string | null
  /** Per-workspace active env path — the workspace default, chosen in the header from the
   *  global environments. Keyed by workspace root so switching workspaces doesn't smuggle one
   *  workspace's env into another. */
  activeEnvByRoot: Record<string, string | null>
  /** Per-collection env override, keyed by `${root}::${slug}` — what the baseUrl chip sets.
   *  This is where a collection-scoped environment is selected, and it is remembered rather
   *  than held per tab: "this collection points at UAT right now" is a decision about the
   *  collection, and re-picking it in every request that belongs to it is the friction the
   *  merge of stages into environments exists to remove. */
  envByCollection: Record<string, string | null>

  // -- UI state (in-memory) --------------------------------------------------
  /**
   * Everything about a tab that has to outlive its editor, keyed by tab path.
   *
   * Only the active tab's editor is mounted, so without this a tab switch unmounted the form
   * and took its in-progress state with it; the same re-seed happens on every `generation`
   * bump (the file watcher, or saving any *other* file). Editors hand this over on the way
   * out and pick it back up on the way in.
   *
   * Deliberately NOT persisted: an auth profile's draft can hold a client secret, and the
   * persisted slice goes to localStorage. Tab state survives a tab switch, not a page
   * reload. Responses are the third member of this family and live in their own registry
   * (`editors/useExecution`) — a streaming body writes far too often to belong in a store
   * every component subscribes to.
   */
  tabState: Record<string, TabState>

  /**
   * Variables declared into a file by something other than that file's editor — the
   * convert-to-variable panel, which writes a `vars:` entry into whichever scope the user
   * picked. Keyed by tab path, then by variable name.
   *
   * <p>The write lands on disk immediately, but that is not enough on its own. An editor for
   * the same file re-seeds on the `generation` bump and then prefers its stored draft, which
   * predates the declaration — so its next save would write the file back without the entry.
   * Recording the declaration here instead of patching the draft directly makes the fix
   * order-independent: `restoreDraft` folds these in on every seed, so it does not matter
   * whether the draft was published before or after the declaration, or existed at all.</p>
   *
   * <p>Cleared when the tab stops being dirty — by then the entry is in the file the editor
   * just saved, or in the baseline it just reverted to.</p>
   */
  declaredVars: Record<string, Record<string, DeclaredVar>>

  // -- Actions ---------------------------------------------------------------
  reload: () => Promise<void>
  setActiveEnv: (path: string | null) => void
  /** Point one collection at an environment, or pass null to fall back to the workspace
   *  default. */
  setCollectionEnv: (slug: string, path: string | null) => void
  openTab: (tab: OpenTab) => void
  closeTab: (path: string) => void
  closeOtherTabs: (path: string) => void
  closeAllTabs: () => void
  selectTab: (path: string) => void
  /** Rename a tab in place — swaps `path` (id) + label, preserving its position
   *  in the tab strip and active state. Used by the git-diff editor to switch
   *  Working ↔ Staged without leaving an orphan tab behind. */
  renameTab: (oldPath: string, newPath: string, newLabel: string) => void
  /** Store (or replace) a tab's unsaved editor state. */
  setDraft: (path: string, value: unknown) => void
  /** Drop a tab's unsaved editor state — it was saved, or discarded. */
  clearDraft: (path: string) => void
  /** Record a variable declared into `path` from outside that file's editor. */
  declareVar: (path: string, name: string, value: DeclaredVar) => void
  /** Forget `path`'s pending declarations — its editor has saved or discarded. */
  clearDeclaredVars: (path: string) => void
  /** Remember one slot of a tab's view state (an open sub-tab, a body view, …). */
  setTabView: (path: string, slot: string, value: unknown) => void
  activateWorkspace: (path: string) => Promise<void>
  addAndActivateWorkspace: (path: string) => Promise<void>
}

// Subset of the store that gets persisted to localStorage. Anything server-derived is
// excluded — it'll be repopulated on the next reload().
interface PersistedSlice {
  tabs: OpenTab[]
  activeTab: string | null
  activeEnvByRoot: Record<string, string | null>
  envByCollection: Record<string, string | null>
}

/** Key for {@link TapStore.envByCollection}. Root-qualified for the same reason
 *  `activeEnvByRoot` is: two workspaces can both have a `demo` collection. */
const collectionEnvKey = (root: string, slug: string) => `${root}::${slug}`

/** A copy of `tabState` without the entry for one tab path. */
function withoutTab(tabState: Record<string, TabState>, path: string): Record<string, TabState> {
  const next = { ...tabState }
  delete next[path]
  return next
}

export const useTapStore = create<TapStore>()(
  persist(
    (set, get) => ({
      info: null,
      tree: [],
      envs: [],
      collections: [],
      auths: [],
      testSets: [],
      flows: [],
      knownWorkspaces: [],
      generation: 0,
      loadError: null,

      tabs: [],
      activeTab: null,
      activeEnvByRoot: {},
      envByCollection: {},
      tabState: {},
      declaredVars: {},

      reload: async () => {
        try {
          const [w, t, e, k, c, au, ts, fl] = await Promise.all([
            api.workspace(),
            api.tree(),
            api.environments(),
            api.knownWorkspaces(),
            api.collections(),
            api.auths(),
            api.testSets(),
            api.flows(),
          ])
          const previousRoot = get().info?.root ?? null
          const prevActiveEnv = previousRoot ? get().activeEnvByRoot[previousRoot] : null

          // Open tabs and the selected env are persisted by path, so a workspace that has just
          // been through `tap-studio migrate` would restore a screenful of dead .md paths.
          // Repoint anything whose canonical twin is now on disk.
          const livePaths = new Set<string>()
          const collectPaths = (nodes: typeof t) => {
            for (const n of nodes) {
              livePaths.add(n.path)
              collectPaths(n.children ?? [])
            }
          }
          collectPaths(t)
          // Generic over the input so a non-null path stays non-null: tab paths are `string`,
          // the active env is `string | null`, and both go through here.
          const heal = <T extends string | null>(path: T): T => {
            if (!path || livePaths.has(path)) return path
            const canonical = toCanonicalPath(path)
            return (canonical !== path && livePaths.has(canonical) ? canonical : path) as T
          }

          set((state) => {
            const rawActiveEnv = state.activeEnvByRoot[w.root] ?? prevActiveEnv ?? w.defaultEnv
            const nextActiveEnv = heal(rawActiveEnv)
            const healedTabs = state.tabs.map((tab) => {
              const path = heal(tab.path)
              return path === tab.path ? tab : { ...tab, path }
            })
            // Tab state is keyed by tab path, so it follows its tab through the same rename.
            const healedTabState = Object.fromEntries(
              Object.entries(state.tabState).map(([path, st]) => [heal(path), st]),
            )
            return {
              info: w,
              tree: t,
              envs: e,
              knownWorkspaces: k,
              collections: c,
              auths: au,
              testSets: ts,
              flows: fl,
              generation: state.generation + 1,
              loadError: null,
              tabs: healedTabs,
              activeTab: heal(state.activeTab),
              tabState: healedTabState,
              activeEnvByRoot: { ...state.activeEnvByRoot, [w.root]: nextActiveEnv },
            }
          })
        } catch (err) {
          set({ loadError: err instanceof Error ? err.message : String(err) })
        }
      },

      setActiveEnv: (path) => set((state) => {
        const root = state.info?.root
        if (!root) return {}
        return { activeEnvByRoot: { ...state.activeEnvByRoot, [root]: path } }
      }),

      setCollectionEnv: (slug, path) => set((state) => {
        const root = state.info?.root
        if (!root) return {}
        return { envByCollection: { ...state.envByCollection, [collectionEnvKey(root, slug)]: path } }
      }),

      openTab: (tab) => set((state) => ({
        tabs: state.tabs.some((t) => t.path === tab.path) ? state.tabs : [...state.tabs, tab],
        activeTab: tab.path,
      })),

      closeTab: (path) => set((state) => {
        const idx = state.tabs.findIndex((t) => t.path === path)
        if (idx < 0) return {}
        const next = state.tabs.filter((t) => t.path !== path)
        const nextActive = state.activeTab !== path
          ? state.activeTab
          : (next.length === 0 ? null : next[Math.min(idx, next.length - 1)].path)
        return { tabs: next, activeTab: nextActive, tabState: withoutTab(state.tabState, path) }
      }),

      closeOtherTabs: (path) => set((state) => {
        const keep = state.tabs.find((t) => t.path === path)
        if (!keep) return {}
        const kept = state.tabState[path]
        return {
          tabs: [keep],
          activeTab: path,
          tabState: kept === undefined ? {} : { [path]: kept },
        }
      }),

      closeAllTabs: () => set({ tabs: [], activeTab: null, tabState: {} }),

      selectTab: (path) => set({ activeTab: path }),

      renameTab: (oldPath, newPath, newLabel) => set((state) => {
        if (oldPath === newPath) {
          // Label may still have changed — patch it in place.
          return {
            tabs: state.tabs.map((t) => t.path === oldPath ? { ...t, label: newLabel } : t),
          }
        }
        const idx = state.tabs.findIndex((t) => t.path === oldPath)
        if (idx < 0) return {}
        const existing = state.tabs.find((t) => t.path === newPath)
        const tabs = existing
          // Already-open destination wins; drop the old one so we don't duplicate.
          ? state.tabs.filter((t) => t.path !== oldPath)
          : state.tabs.map((t) => t.path === oldPath
              ? { ...t, path: newPath, label: newLabel }
              : t)
        const activeTab = state.activeTab === oldPath ? newPath : state.activeTab
        // The editor remounts under the new path (App keys it on the tab path), so unsaved
        // work has to move with the tab or it would be orphaned under the old key.
        const carried = state.tabState[oldPath]
        const tabState = carried === undefined
          ? state.tabState
          : { ...withoutTab(state.tabState, oldPath), [newPath]: carried }
        return { tabs, activeTab, tabState }
      }),

      setDraft: (path, value) => set((state) => {
        const current = state.tabState[path]
        if (current?.draft === value) return {}
        return { tabState: { ...state.tabState, [path]: { ...current, draft: value } } }
      }),

      clearDraft: (path) => set((state) => {
        const current = state.tabState[path]
        if (current?.draft === undefined) return {}
        return { tabState: { ...state.tabState, [path]: { ...current, draft: undefined } } }
      }),

      declareVar: (path, name, value) => set((state) => ({
        declaredVars: {
          ...state.declaredVars,
          [path]: { ...state.declaredVars[path], [name]: value },
        },
      })),

      clearDeclaredVars: (path) => set((state) => {
        if (state.declaredVars[path] === undefined) return {}
        const next = { ...state.declaredVars }
        delete next[path]
        return { declaredVars: next }
      }),

      setTabView: (path, slot, value) => set((state) => {
        const current = state.tabState[path]
        if (current?.view?.[slot] === value) return {}
        return {
          tabState: {
            ...state.tabState,
            [path]: { ...current, view: { ...current?.view, [slot]: value } },
          },
        }
      }),

      activateWorkspace: async (path) => {
        await api.activateWorkspace(path)
        // The open tabs reference paths in the OLD workspace; clearing avoids 404 loops on
        // refetch. activeEnvByRoot is preserved so jumping back keeps your selection.
        // Pending declarations go with the drafts they were waiting for — they name paths in
        // the workspace being left, and every editor that could have consumed them is gone.
        set({ tabs: [], activeTab: null, tabState: {}, declaredVars: {} })
        await get().reload()
      },

      addAndActivateWorkspace: async (path) => {
        await api.addWorkspace(path)
        await get().activateWorkspace(path)
      },
    }),
    {
      name: 'tap-studio:ui',
      version: 1,
      storage: createJSONStorage(() => localStorage),
      // Persist only the UI slice — never the server-derived catalogs.
      partialize: (state): PersistedSlice => ({
        tabs: state.tabs,
        activeTab: state.activeTab,
        activeEnvByRoot: state.activeEnvByRoot,
        envByCollection: state.envByCollection,
      }),
    },
  ),
)

// -----------------------------------------------------------------------------------------
// Convenience selectors. These pick exactly one slice so consumers don't accidentally
// subscribe to half the store via `useTapStore(s => ({ a, b }))` (which creates a new
// object every render and breaks `===` referential equality checks).
// -----------------------------------------------------------------------------------------

/** Current active env path for the loaded workspace, or null when none / not loaded. */
export const useActiveEnv = (): string | null => useTapStore((s) => {
  const root = s.info?.root
  return root ? (s.activeEnvByRoot[root] ?? null) : null
})

/**
 * The collection a tab belongs to, or null when it belongs to none. Covers both a file under
 * `collections/<slug>/…` and the collection's own tab, whose path is the bare directory.
 */
export function collectionOfTab(path: string | null): string | null {
  if (!path) return null
  const parts = path.split('/')
  if (parts.length < 2 || parts[0].toLowerCase() !== 'collections') return null
  return parts[1] || null
}

/** The collection whose editor is in front of the user right now, or null. This is the context
 *  every environment control narrows by. */
export const useActiveCollection = (): string | null =>
  useTapStore((s) => collectionOfTab(s.activeTab))

/** The environments offerable while a file owned by `slug` is in front of the user: every
 *  global one, plus the ones assigned to that collection. Mirrors
 *  `LoadedWorkspace.EnvironmentsFor`.
 *
 *  <p>`slug === null` means "no collection in front of us", and then there is nothing to
 *  narrow by — every environment is offered, assigned ones included. Filtering to globals
 *  there would hide environments the user has no other way to reach.</p> */
export const useEnvsFor = (slug: string | null): EnvSummary[] => {
  const envs = useTapStore((s) => s.envs)
  return useMemo(() => (slug === null ? envs : envs.filter((e) => envAppliesTo(e, slug))), [envs, slug])
}

/**
 * The environment a request in `slug` actually resolves under: the collection's own override
 * when one is set and still valid, else the workspace default when it is in scope here, else
 * none.
 *
 * <p>Both fallbacks matter. An override can go stale — the env file gets deleted, or its
 * `collections:` list stops naming this collection — and silently keeping it would send the
 * request somewhere the picker no longer offers. And the workspace default is frequently a
 * global env that every collection should honour without being told.</p>
 */
export const useEffectiveEnv = (slug: string | null): string | null => {
  const envs = useTapStore((s) => s.envs)
  const root = useTapStore((s) => s.info?.root ?? null)
  const override = useTapStore((s) => (root && slug ? s.envByCollection[`${root}::${slug}`] ?? null : null))
  const workspaceDefault = useActiveEnv()

  return useMemo(() => {
    const usable = (path: string | null) => {
      if (!path) return null
      const env = envs.find((e) => e.path === path)
      return env && envAppliesTo(env, slug) ? path : null
    }
    return usable(override) ?? usable(workspaceDefault)
  }, [envs, slug, override, workspaceDefault])
}

/**
 * The single environment control, in whichever context the user is in.
 *
 * <p>With a collection in front of you it reads and writes *that collection's* environment;
 * with none, the workspace default. One meaning per context, so the header and the base-URL
 * chip are two surfaces on the same choice rather than two competing ones.</p>
 */
export function useEnvSelection(slug: string | null): {
  value: string | null
  options: EnvSummary[]
  select: (path: string | null) => void
} {
  const options = useEnvsFor(slug)
  const workspaceDefault = useActiveEnv()
  const effective = useEffectiveEnv(slug)
  const setActiveEnv = useTapStore((s) => s.setActiveEnv)
  const setCollectionEnv = useTapStore((s) => s.setCollectionEnv)

  const select = useCallback(
    (path: string | null) => {
      if (slug === null) setActiveEnv(path)
      else setCollectionEnv(slug, path)
    },
    [slug, setActiveEnv, setCollectionEnv],
  )

  return { value: slug === null ? workspaceDefault : effective, options, select }
}

/**
 * Tab paths that currently hold unsaved edits. Selected as a stable joined string rather
 * than `tabState` itself: a draft is rewritten on every keystroke, and subscribers (the tab
 * strip) only care when the *set* of dirty tabs changes.
 */
export const useDirtyTabPaths = (): ReadonlySet<string> => {
  const joined = useTapStore((s) => Object.entries(s.tabState)
    .filter(([, st]) => st.draft !== undefined)
    .map(([path]) => path)
    .sort()
    .join('\n'))
  return useMemo(() => new Set(joined ? joined.split('\n') : []), [joined])
}

/** Has a usable workspace been loaded (info + at least one known workspace marked active)? */
export const useHasActiveWorkspace = (): boolean => useTapStore(
  (s) => s.info !== null && s.knownWorkspaces.some((w) => w.isActive && w.available),
)

// -----------------------------------------------------------------------------------------
// One-time bootstrap: trigger an initial reload + subscribe to SSE. Call once at app start.
// Stays out of React's lifecycle so we don't re-subscribe on remount loops.
// -----------------------------------------------------------------------------------------

let bootstrapped = false
let unsubscribe: (() => void) | null = null

export function bootstrapStore(): () => void {
  if (bootstrapped) return () => {}
  bootstrapped = true
  void useTapStore.getState().reload()
  unsubscribe = subscribeWorkspaceChanges(() => { void useTapStore.getState().reload() })
  return () => {
    unsubscribe?.()
    unsubscribe = null
    bootstrapped = false
  }
}
