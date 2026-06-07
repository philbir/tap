import { create } from 'zustand'
import { persist, createJSONStorage } from 'zustand/middleware'
import { api, subscribeWorkspaceChanges } from '../api/client'
import type {
  AuthSummary, CollectionSummary, EnvSummary, KnownWorkspace, TreeNode, WorkspaceFileKind, WorkspaceInfo,
} from '../api/types'

/** One open file in the tab bar. */
export interface OpenTab {
  /** Workspace-relative path; '__manifest__' is reserved for the tap.md workspace editor. */
  path: string
  kind: WorkspaceFileKind
  /** Display label for the tab header. */
  label: string
}

export const MANIFEST_TAB_PATH = '__manifest__'
/** Reserved path for the Settings tab — not a real workspace file. */
export const SETTINGS_TAB_PATH = '__settings__'

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
  knownWorkspaces: KnownWorkspace[]
  /** Increments after every successful reload. Editors `useEffect` on this to refetch. */
  generation: number
  loadError: string | null

  // -- UI state (persisted) --------------------------------------------------
  tabs: OpenTab[]
  activeTab: string | null
  /** Per-workspace active env path. Keyed by workspace root so switching workspaces
   *  doesn't smuggle one workspace's env into another. */
  activeEnvByRoot: Record<string, string | null>

  // -- Actions ---------------------------------------------------------------
  reload: () => Promise<void>
  setActiveEnv: (path: string | null) => void
  openTab: (tab: OpenTab) => void
  closeTab: (path: string) => void
  closeOtherTabs: (path: string) => void
  closeAllTabs: () => void
  selectTab: (path: string) => void
  activateWorkspace: (path: string) => Promise<void>
  addAndActivateWorkspace: (path: string) => Promise<void>
}

// Subset of the store that gets persisted to localStorage. Anything server-derived is
// excluded — it'll be repopulated on the next reload().
interface PersistedSlice {
  tabs: OpenTab[]
  activeTab: string | null
  activeEnvByRoot: Record<string, string | null>
}

export const useTapStore = create<TapStore>()(
  persist(
    (set, get) => ({
      info: null,
      tree: [],
      envs: [],
      collections: [],
      auths: [],
      knownWorkspaces: [],
      generation: 0,
      loadError: null,

      tabs: [],
      activeTab: null,
      activeEnvByRoot: {},

      reload: async () => {
        try {
          const [w, t, e, k, c, au] = await Promise.all([
            api.workspace(),
            api.tree(),
            api.environments(),
            api.knownWorkspaces(),
            api.collections(),
            api.auths(),
          ])
          const previousRoot = get().info?.root ?? null
          const prevActiveEnv = previousRoot ? get().activeEnvByRoot[previousRoot] : null

          set((state) => {
            const nextActiveEnv = state.activeEnvByRoot[w.root] ?? prevActiveEnv ?? w.defaultEnv
            return {
              info: w,
              tree: t,
              envs: e,
              knownWorkspaces: k,
              collections: c,
              auths: au,
              generation: state.generation + 1,
              loadError: null,
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
        return { tabs: next, activeTab: nextActive }
      }),

      closeOtherTabs: (path) => set((state) => {
        const keep = state.tabs.find((t) => t.path === path)
        if (!keep) return {}
        return { tabs: [keep], activeTab: path }
      }),

      closeAllTabs: () => set({ tabs: [], activeTab: null }),

      selectTab: (path) => set({ activeTab: path }),

      activateWorkspace: async (path) => {
        await api.activateWorkspace(path)
        // The open tabs reference paths in the OLD workspace; clearing avoids 404 loops on
        // refetch. activeEnvByRoot is preserved so jumping back keeps your selection.
        set({ tabs: [], activeTab: null })
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
