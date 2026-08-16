import type {
  AssertResult,
  AssertSpec,
  AssertSummary,
  AuthDetail,
  AuthExecuteResponse,
  AuthSpec,
  AuthSummary,
  BrowseResponse,
  BrowserOption,
  CollectionDetail,
  CollectionSpec,
  CollectionSummary,
  CompileResult,
  EnvDetail,
  EnvSpec,
  EnvSummary,
  ExecutionResult,
  FileUploadResponse,
  FlowDetail,
  FlowSpec,
  FlowSummary,
  GitInfo,
  GitBranch,
  GitCommandResult,
  GitCommitResult,
  GitFileChange,
  GitStatus,
  GraphQLSchemaMode,
  GraphQLSchemaResponse,
  HttpFileParseResult,
  KnownWorkspace,
  OidcDiscovery,
  PostmanImportResponse,
  RenderedRequest,
  RequestDetail,
  RequestSpec,
  RequestSummary,
  TaggedItem,
  TestEntryResult,
  TestRunResult,
  TestRunStart,
  TestRunStepEvent,
  TestSetDetail,
  TestSetSpec,
  TestSetSummary,
  TlsDiagnosis,
  VariableTrace,
  AzureKeyVault,
  AzureSubscription,
  OnePasswordVault,
  OnePasswordDetect,
  ProviderSummary,
  ProviderTypeDescriptor,
  ProviderVariable,
  ProviderVariableValue,
  TestProviderResult,
  SaveSystemSettings,
  SetVariablePayload,
  SseEvent,
  SystemSettings,
  TreeNode,
  WsFrame,
  VariableContext,
  VariableView,
  WorkspaceDetail,
  WorkspaceErrorDto,
  WorkspaceInfo,
  WorkspaceSpec,
  AiStatus,
  AiConfig,
  SaveAiConfig,
  AiCliDetect,
  AiTestResult,
  AiModels,
  AiAssistRequest,
  AiAssistResponse,
} from './types'

async function get<T>(path: string): Promise<T> {
  // `cache: 'no-store'` keeps the browser from heuristically caching API responses.
  // Without it, Vite-proxied 200s with no Cache-Control can get pinned in memory and
  // mask backend changes during local dev (the SPA fallback HTML from a misconfigured
  // proxy is particularly sticky).
  const r = await fetch(path, { cache: 'no-store' })
  if (!r.ok) {
    if (r.status === 404) throw new ApiError(404, `Not found: ${path}`)
    throw new ApiError(r.status, `${r.status} ${r.statusText} on GET ${path}`)
  }
  return (await r.json()) as T
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const r = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const text = await r.text()
  const json = text ? JSON.parse(text) : null
  if (!r.ok) throw new ApiError(r.status, json?.message ?? r.statusText, json)
  return json as T
}

async function put(path: string, body: unknown): Promise<void> {
  const r = await fetch(path, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (r.ok) return
  const text = await r.text()
  const json = text ? JSON.parse(text) : null
  throw new ApiError(r.status, json?.message ?? r.statusText, json)
}

/** PUT that returns a JSON body (used by endpoints that echo the saved resource). */
async function putJson<T>(path: string, body: unknown): Promise<T> {
  const r = await fetch(path, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const text = await r.text()
  const json = text ? JSON.parse(text) : null
  if (!r.ok) throw new ApiError(r.status, json?.message ?? r.statusText, json)
  return json as T
}

async function del(path: string): Promise<void> {
  const r = await fetch(path, { method: 'DELETE' })
  if (r.ok) return
  const text = await r.text()
  const json = text ? JSON.parse(text) : null
  throw new ApiError(r.status, json?.message ?? r.statusText, json)
}

export class ApiError extends Error {
  status: number
  payload?: WorkspaceErrorDto | unknown
  constructor(status: number, message: string, payload?: WorkspaceErrorDto | unknown) {
    super(message)
    this.status = status
    this.payload = payload
  }
}

const encodePath = (p: string) => p.split('/').map(encodeURIComponent).join('/')

export const api = {
  // Workspace
  workspace: () => get<WorkspaceInfo>('/api/workspace'),
  workspaceManifest: () => get<WorkspaceDetail>('/api/workspace/manifest'),
  saveWorkspaceSpec: (spec: WorkspaceSpec) => put('/api/workspace/manifest/spec', spec),

  /** Save a workspace file by its raw text content. The server validates by parsing
   *  through `FileParser` before writing — invalid YAML/kind/etc. rejects with 400 +
   *  WorkspaceErrorDto, and nothing is written. Used by every editor's Source tab. */
  /** Raw text of one workspace file. Used by the .http editor, which has no structured spec. */
  source: (path: string) =>
    get<{ path: string; content: string }>(`/api/workspace/source?path=${encodeURIComponent(path)}`)
      .then((r) => r.content),

  saveSource: (path: string, content: string) =>
    put('/api/workspace/source', { path, content }),

  /** Parse unsaved `.http` text server-side, listing the requests it currently holds.
   *  The `.http` editor drives its request list off this rather than off the workspace tree:
   *  a request's name is derived from its own content, so an unsaved edit can add, rename or
   *  remove requests. Same parser the loader and the executor use, so the row the user clicks
   *  and the request the server then runs cannot disagree. */
  parseHttpFile: (path: string, content: string) =>
    post<HttpFileParseResult>('/api/http/parse', { path, content }),
  tree: () => get<TreeNode[]>('/api/workspace/tree'),

  /** Create an empty folder under `.tap/`. Folders are pure grouping — no spec file is written. */
  createFolder: (path: string) =>
    post<null>('/api/workspace/folders', { path }),

  /** Recursively delete a workspace folder. */
  deleteFolder: (path: string) =>
    del(`/api/workspace/folders?path=${encodeURIComponent(path)}`),

  /** Delete a single workspace file (request / auth / env / `_collection.tap`). */
  deleteFile: (path: string) =>
    del(`/api/workspace/files?path=${encodeURIComponent(path)}`),

  /** Move a file or folder to a new workspace-relative path. */
  moveItem: (from: string, to: string) => post<null>('/api/workspace/move', { from, to }),

  // Workspace switcher
  knownWorkspaces: () => get<KnownWorkspace[]>('/api/workspaces'),
  addWorkspace: (path: string) =>
    post<KnownWorkspace>('/api/workspaces', { path }),
  activateWorkspace: (path: string) => post<null>('/api/workspaces/activate', { path }),
  removeWorkspace: (path: string) => del(`/api/workspaces?path=${encodeURIComponent(path)}`),

  // Filesystem picker. `path` undefined → server uses the user's home directory.
  browse: (path?: string) =>
    get<BrowseResponse>(`/api/fs/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),
  /** Creates a new directory at `{parent}/{name}` and returns its canonical absolute path. */
  createDirectory: (parent: string, name: string) =>
    post<{ path: string }>('/api/fs/folders', { parent, name }),

  // Requests
  requests: () => get<RequestSummary[]>('/api/requests'),
  request: (path: string) => get<RequestDetail>(`/api/requests/${encodePath(path)}`),
  saveRequestSpec: (spec: RequestSpec) => put('/api/requests/spec', spec),

  /** Re-check assertions against a response the client already holds, without sending the
   *  request again. Backs the Asserts tab's live pass/fail while you edit: the verdicts are
   *  computed by the same server-side evaluator a real Send uses, so tuning against them is
   *  tuning against the truth. `context` supplies the scope that `{{var}}` expected values
   *  resolve through. Individual malformed assertions come back as failed rows rather than
   *  failing the whole call. */
  evaluateAssertions: (
    assertions: AssertSpec[],
    response: AssertResponseSnapshot,
    context: { path?: string; env?: string | null; stage?: string | null },
  ) => post<EvaluateAssertsResponse>('/api/assertions/evaluate', {
    assertions,
    response,
    path: context.path,
    env: context.env ?? undefined,
    stage: context.stage ?? undefined,
  }),

  /** Upload a binary payload to the workspace's sideband store. The server writes it
   *  under <c>.files/</c> next to the owning request and returns the ref string
   *  (e.g. <c>&lt; ./.files/foo.png</c>) that the editor embeds in the request body.
   *  The executor swaps the ref for actual bytes at send time. */
  uploadRequestFile: async (requestPath: string, file: File): Promise<FileUploadResponse> => {
    const form = new FormData()
    form.append('requestPath', requestPath)
    form.append('file', file, file.name)
    const r = await fetch('/api/files/upload', { method: 'POST', body: form })
    const text = await r.text()
    const json = text ? JSON.parse(text) : null
    if (!r.ok) throw new ApiError(r.status, json?.message ?? r.statusText ?? text, json)
    return json as FileUploadResponse
  },

  // Auths
  auths: () => get<AuthSummary[]>('/api/auths'),
  authDetail: (path: string) => get<AuthDetail>(`/api/auths/${encodePath(path)}`),
  saveAuthSpec: (spec: AuthSpec) => put('/api/auths/spec', spec),

  // Environments
  environments: () => get<EnvSummary[]>('/api/environments'),
  envDetail: (path: string) => get<EnvDetail>(`/api/environments/${encodePath(path)}`),
  saveEnvSpec: (spec: EnvSpec) => put('/api/environments/spec', spec),

  // Collections
  collections: () => get<CollectionSummary[]>('/api/collections'),
  collectionDetail: (slug: string) => get<CollectionDetail>(`/api/collections/${encodeURIComponent(slug)}`),
  saveCollectionSpec: (spec: CollectionSpec) => put('/api/collections/spec', spec),
  deleteCollection: (slug: string) => del(`/api/collections/${encodeURIComponent(slug)}`),

  /** Import a Postman v2.1 collection JSON. <c>collection</c> is the parsed JSON body of
   *  a Postman export. <c>slug</c> overrides the slug derived from <c>info.name</c>;
   *  <c>overwrite</c> wipes an existing collection directory before re-importing. */
  importPostmanCollection: (collection: unknown, slug: string | null, overwrite: boolean) =>
    post<PostmanImportResponse>('/api/collections/import/postman', { collection, slug, overwrite }),

  // Tags
  tags: () => get<TaggedItem[]>('/api/tags'),
  /** Workspace tag dictionary: union of curated tags from `workspace.tap` and tags currently
   *  in use on any entity. Source for every TagsInput's autocomplete + the Tags-view
   *  filter picker. */
  tagDictionary: () => get<string[]>('/api/tags/dictionary'),

  // Render — request path goes in body since ASP.NET routing can't suffix /render after a catch-all
  render: (path: string, env: string | null, stage: string | null, overrides?: Record<string, string>) =>
    post<RenderedRequest>('/api/render', { path, env, stage, overrides }),

  /** Probe a GraphQL endpoint for its schema. Uses the request's URL + headers + auth from
   *  the usual render pipeline but swaps the body for a standard introspection POST (or
   *  appends `?sdl` for SDL mode). Returns the raw upstream payload; the client builds a
   *  `GraphQLSchema` from it. */
  graphqlSchema: (path: string, env: string | null, stage: string | null, mode: GraphQLSchemaMode) =>
    post<GraphQLSchemaResponse>('/api/graphql/schema', { path, env, stage, mode }),

  // Actually fire the request against the upstream and return status/headers/body/timing.
  execute: (path: string, env: string | null, stage: string | null, overrides?: Record<string, string>) =>
    post<ExecutionResult>('/api/execute', { path, env, stage, overrides }),

  diagnoseTls: (path: string, env: string | null, stage: string | null, spec?: RequestSpec) =>
    post<TlsDiagnosis>('/api/execute/tls-diagnose', { path, env, stage, spec }),

  /**
   * Streaming variant of `execute()`. Drives a single fetch + ReadableStream against
   * `/api/execute/stream` and dispatches each SSE event (`meta` / `body` / `sse` /
   * `done` / `error`) through the callback. The caller decides how to assemble the
   * progressive payload into an `ExecutionResult`.
   *
   * Returns an `AbortController` so the caller can cancel an in-flight stream — useful
   * when the user clicks Send again, navigates away, or hits the × close button on a
   * long-running SSE producer.
   *
   * Pass `spec` to send an unsaved (dirty) draft: the server builds the request from the
   * spec in-memory — same emit pipeline as Save — without writing it to disk, then renders
   * and executes that transient request instead of the on-disk file.
   *
   * `source` is the same idea for a `.http` file, which has no spec — it carries the raw
   * editor text, and `path` names which request inside it to run.
   */
  executeStream(
    path: string,
    env: string | null,
    stage: string | null,
    handler: (event: StreamEvent) => void,
    overrides?: Record<string, string>,
    spec?: RequestSpec,
    source?: string,
  ): AbortController {
    const ctrl = new AbortController()
    void runExecuteStream(path, env, stage, overrides, handler, ctrl.signal, spec, source)
    return ctrl
  },

  // --- Testing ------------------------------------------------------------------------

  flows: () => get<FlowSummary[]>('/api/flows'),
  flow: (path: string) => get<FlowDetail>(`/api/flows/${encodePath(path)}`),
  saveFlowSpec: (spec: FlowSpec) => put('/api/flows/spec', spec),

  testSets: () => get<TestSetSummary[]>('/api/test-sets'),
  testSet: (path: string) => get<TestSetDetail>(`/api/test-sets/${encodePath(path)}`),
  saveTestSetSpec: (spec: TestSetSpec) => put('/api/test-sets/spec', spec),

  /**
   * Run a test set or a flow — `path` points at either kind and the server decides from the
   * file. Streams a `start` (the plan), a `step` per request, an `entry` per test, then
   * `done`; a ten-entry set against a real API takes real time and the caller wants to paint
   * each row as it lands rather than after the last one.
   *
   * `only` narrows the run to a single entry by index, for re-running one failing test.
   * Returns an `AbortController` so the caller can stop a run in flight.
   */
  runTests(
    path: string,
    env: string | null,
    stage: string | null,
    handler: (event: TestRunEvent) => void,
    options?: { only?: number | null; overrides?: Record<string, string> },
  ): AbortController {
    const ctrl = new AbortController()
    void runTestStream(path, env, stage, options, handler, ctrl.signal)
    return ctrl
  },

  // --- Auth flow ----------------------------------------------------------------------

  /** Fetch the OpenID Connect discovery document for a given authority URL. `authPath` is
   *  the profile being edited — passing it lets a collection-scoped profile resolve an
   *  authority that references its collection's (or stage's) variables. `env` is the active
   *  environment, without which the authority resolves against the workspace default. */
  oidcDiscovery: (authority: string, authPath?: string, stage?: string, env?: string) => {
    const q = new URLSearchParams({ authority })
    if (authPath) q.set('authPath', authPath)
    if (stage) q.set('stage', stage)
    if (env) q.set('env', env)
    return get<OidcDiscovery>(`/api/auth/discovery?${q}`)
  },

  /** Run an auth profile. For OAuth2 client_credentials returns tokens synchronously;
   *  for authorization_code+PKCE returns a loginUrl + flowId for the UI to drive.
   *  `context` carries the caller's request + stage so a collection-scoped profile expands
   *  against the right stage (and caches its token there); ignored for workspace-scoped ones.
   *  `env` applies to every profile — it must be the environment the editor previewed the
   *  profile's fields against, or the runner resolves `{{…}}` refs against a different one. */
  executeAuth: (path: string, forceReauthenticate = false,
                context?: { requestPath?: string; stage?: string; env?: string }) =>
    post<AuthExecuteResponse>('/api/auth/execute', {
      path, forceReauthenticate,
      requestPath: context?.requestPath, stage: context?.stage, env: context?.env,
    }),

  /** Poll a pending flow until it transitions to completed or failed. */
  authFlow: (id: string) => get<AuthExecuteResponse>(`/api/auth/flows/${encodeURIComponent(id)}`),

  /** Remove the cached runtime token for an auth profile in the active workspace. The
   *  next request Send will fire without an Authorization header until the flow runs again. */
  clearAuthToken: (path: string) => post<void>('/api/auth/clear', { path }),

  /** Browsers + profiles installed on the host (for the OAuth "open sign-in" picker). */
  browsers: () => get<BrowserOption[]>('/api/browsers'),

  /** Open a URL in a specific browser + profile on the host (null browser = system default). */
  openInBrowser: (url: string, browser: string | null, profile: string | null) =>
    post<null>('/api/browsers/open', { url, browser, profile }),

  /** Studio-owned OAuth redirect URI. The server derives it from its own base URL so
   *  the value follows whichever port Aspire allocates. The UI displays it read-only and
   *  reminds the user to whitelist it in their identity provider. */
  authCallbackUri: () => get<{ redirectUri: string }>('/api/auth/callback-uri'),

  // --- Variables ---------------------------------------------------------------------

  /** Resolve the layered + merged variable view for an editor context. */
  variablesView: (ctx: VariableContext) => post<VariableView>('/api/variables/views', ctx),

  /** Compile a template against the current context (Variables panel "Test it"). */
  compileTemplate: (template: string, ctx: VariableContext) =>
    post<CompileResult>('/api/variables/compile', { template, context: ctx }),

  /** Write one variable via the chosen provider (or the default writable provider). */
  setVariable: (payload: SetVariablePayload) =>
    post<void>('/api/variables/set', payload),

  // --- Providers ---------------------------------------------------------------------

  /** Lists the active variable providers (system + workspace). Sensitive setting values
   *  are already masked server-side. Pass the active env path so its provider binding
   *  (default + aliases) is reflected. */
  listVariableProviders: (env?: string | null) =>
    get<ProviderSummary[]>(`/api/variable-providers${env ? `?env=${encodeURIComponent(env)}` : ''}`),

  /** Provider type descriptors: display name, icon key, and the typed settings schema.
   *  Source of the provider picker and the generated settings forms. */
  providerTypes: () => get<ProviderTypeDescriptor[]>('/api/variable-providers/types'),

  /** Test a draft provider config without saving it. Masked (`***`) settings are
   *  restored server-side from the stored provider with the same name. */
  testVariableProvider: (body: { name: string | null; type: string; settings: Record<string, string | null> }) =>
    post<TestProviderResult>('/api/variable-providers/test', body),

  /** Browse a provider's variables (values masked for secrets). `refresh` busts any
   *  server-side listing cache (azkv). */
  providerVariables: (name: string, refresh = false, env?: string | null) => {
    const q = new URLSearchParams({ refresh: String(refresh) })
    if (env) q.set('env', env)
    return get<ProviderVariable[]>(`/api/variable-providers/${encodeURIComponent(name)}/variables?${q}`)
  },

  /** Reveal one provider variable's clear-text value (explicit per-row action). */
  providerVariableValue: (name: string, key: string, env?: string | null) => {
    const q = env ? `?env=${encodeURIComponent(env)}` : ''
    return get<ProviderVariableValue>(
      `/api/variable-providers/${encodeURIComponent(name)}/variables/${encodeURIComponent(key)}${q}`)
  },

  // --- Azure discovery (Key Vault picker) ----------------------------------------------

  /** Subscriptions visible to the Azure CLI credential (`az login`). */
  azureSubscriptions: () => get<AzureSubscription[]>('/api/azure/subscriptions'),

  /** Key Vaults inside one subscription (name + resource group + location). */
  azureKeyVaults: (subscriptionId: string) =>
    get<AzureKeyVault[]>(`/api/azure/subscriptions/${encodeURIComponent(subscriptionId)}/keyvaults`),

  // --- 1Password discovery (vault picker + op CLI detect) ------------------------------

  /** Vaults visible to the current `op` sign-in. POST so the draft service-account token
   *  never rides in a URL; `***` values are restored server-side from the stored config. */
  onePasswordVaults: (name: string | null, settings: Record<string, string | null>) =>
    post<OnePasswordVault[]>('/api/onepassword/vaults', { name, settings }),

  /** Locate the `op` binary and read its version, for the cliPath field's Detect button. */
  detectOnePasswordCli: (name: string | null, settings: Record<string, string | null>) =>
    post<OnePasswordDetect>('/api/onepassword/detect', { name, settings }),

  // --- Git ---------------------------------------------------------------------------

  /** Status of the git repo enclosing the active workspace. 409 → no git repo. */
  gitStatus: async (): Promise<GitStatus | null> => {
    const r = await fetch('/api/git', { cache: 'no-store' })
    if (r.status === 409) return null
    if (!r.ok) throw new ApiError(r.status, `${r.status} ${r.statusText} on GET /api/git`)
    return (await r.json()) as GitStatus
  },
  gitChanges: () => get<GitFileChange[]>('/api/git/changes'),
  gitBranches: () => get<GitBranch[]>('/api/git/branches'),
  gitDiff: async (path: string, staged: boolean): Promise<string> => {
    const r = await fetch(`/api/git/diff?path=${encodeURIComponent(path)}&staged=${staged}`,
      { cache: 'no-store' })
    if (!r.ok) throw new ApiError(r.status, `${r.status} ${r.statusText} on GET /api/git/diff`)
    return r.text()
  },
  gitStage: (paths: string[]) => post<null>('/api/git/stage', { paths }),
  gitUnstage: (paths: string[]) => post<null>('/api/git/unstage', { paths }),
  /** Discard working-tree changes: untracked files are deleted, tracked
   *  modifications are reverted to HEAD. Staged-only paths aren't touched —
   *  call `gitUnstage` first if you want to throw them away. */
  gitDiscard: (paths: string[]) => post<null>('/api/git/discard', { paths }),
  gitCommit: (message: string) => post<GitCommitResult>('/api/git/commit', { message }),
  gitCreateBranch: (name: string, checkout = true) =>
    post<null>('/api/git/branches', { name, checkout }),
  gitCheckout: (name: string) => post<null>('/api/git/checkout', { name }),
  gitFetch: () => post<GitCommandResult>('/api/git/fetch', {}),
  gitPull: () => post<GitCommandResult>('/api/git/pull', {}),
  gitPush: (setUpstream = true) => post<GitCommandResult>('/api/git/push', { setUpstream }),
  gitInit: () => post<GitInfo>('/api/git/init', {}),
  gitSetRemote: (name: string, url: string) => post<GitInfo>('/api/git/remote', { name, url }),

  // --- System settings -------------------------------------------------------------

  /** Read the user-level system settings file. Lives under `TAP_SYSTEM_DIR` (default
   *  `~/.tap`). Sensitive provider settings are masked; secret variable values come back
   *  blanked (the UI shows a placeholder and round-trips the empty string to keep them). */
  systemSettings: () => get<SystemSettings>('/api/system/settings'),

  /** Save the providers + variables list atomically. */
  saveSystemSettings: (body: SaveSystemSettings) => put('/api/system/settings', body),

  // --- AI assistant ----------------------------------------------------------------

  /** Resolved status of the active AI provider — drives the assistant's gating + setup hint. */
  aiStatus: () => get<AiStatus>('/api/ai/status'),
  /** Read persisted AI config (provider + CLI paths + default model). */
  aiConfig: () => get<AiConfig>('/api/ai/config'),
  /** Persist AI config; returns the saved config. */
  saveAiConfig: (body: SaveAiConfig) => putJson<AiConfig>('/api/ai/config', body),
  /** Clear AI config, reverting to auto-detected defaults. */
  clearAiConfig: () => del('/api/ai/config'),
  /** Probe for the Copilot CLI (optionally at an explicit path). */
  detectCopilotCli: (path: string | null) => post<AiCliDetect>('/api/ai/copilot-cli/detect', { path }),
  /** Probe for the Claude Code CLI (optionally at an explicit path). */
  detectClaudeCli: (path: string | null) => post<AiCliDetect>('/api/ai/claude-cli/detect', { path }),
  /** Validate draft AI settings without persisting them. */
  testAiConfig: (body: SaveAiConfig) => post<AiTestResult>('/api/ai/test', body),
  /** List models exposed by a provider. Pass draft settings to preview a provider the user
   *  has selected but not saved yet; omit them to use the persisted config. */
  aiModels: (draft?: { provider?: string; copilotCliPath?: string | null; claudeCliPath?: string | null }) => {
    const q = new URLSearchParams()
    if (draft?.provider) q.set('provider', draft.provider)
    if (draft?.copilotCliPath) q.set('copilotCliPath', draft.copilotCliPath)
    if (draft?.claudeCliPath) q.set('claudeCliPath', draft.claudeCliPath)
    const qs = q.toString()
    return get<AiModels>(`/api/ai/models${qs ? `?${qs}` : ''}`)
  },
  /** Ask the assistant to craft/edit a request. Returns a reply + optional applyable spec. */
  aiAssist: (body: AiAssistRequest) => post<AiAssistResponse>('/api/ai/assist', body),
}

// SSE — fires whenever the workspace files change on disk.
export function subscribeWorkspaceChanges(onChange: () => void): () => void {
  const es = new EventSource('/api/stream')
  es.addEventListener('workspace-changed', onChange)
  return () => es.close()
}

// ---- execute/stream parser --------------------------------------------------------

/** Tagged union of events emitted by `/api/execute/stream`. The server sends each
 *  one as an SSE frame; we decode here and hand a typed object to the caller. */
export type StreamEvent =
  | { kind: 'meta'; payload: StreamMeta }
  | { kind: 'body'; payload: StreamBody }
  | { kind: 'sse'; payload: SseEvent }
  | { kind: 'ws'; payload: WsFrame }
  | { kind: 'done'; payload: StreamDone }
  | { kind: 'error'; payload: { message: string } }

export interface StreamMeta {
  method: string
  url: string
  status: number
  statusText: string | null
  requestHeaders: Record<string, string>
  requestBody: string | null
  responseHeaders: Record<string, string>
  contentType: string | null
  protocol: 'http' | 'websocket'
  authStatus: import('./types').AuthStatus
}

export interface StreamBody {
  responseBody: string | null
  responseBodyBytes: number
}

export interface StreamDone {
  durationMs: number
  responseBodyBytes: number
  variablesUsed: VariableTrace[]
  stage: string | null
  error: string | null
  /** Assertion verdicts, evaluated server-side once the body is complete. */
  assertions: AssertResult[]
  assertSummary: AssertSummary | null
}

/** The captured response an {@link api.evaluateAssertions} call is checked against. */
export interface AssertResponseSnapshot {
  status: number
  headers: { name: string; value: string }[]
  body: string | null
  bodyTruncated: boolean
  durationMs: number
}

export interface EvaluateAssertsResponse {
  results: AssertResult[]
  summary: AssertSummary
}

/** Tagged union of events emitted by `/api/tests/run`. */
export type TestRunEvent =
  | { kind: 'start'; payload: TestRunStart }
  | { kind: 'step'; payload: TestRunStepEvent }
  | { kind: 'entry'; payload: TestEntryResult }
  | { kind: 'done'; payload: TestRunResult }
  | { kind: 'error'; payload: { message: string } }

async function runExecuteStream(
  path: string,
  env: string | null,
  stage: string | null,
  overrides: Record<string, string> | undefined,
  handler: (event: StreamEvent) => void,
  signal: AbortSignal,
  spec?: RequestSpec,
  source?: string,
): Promise<void> {
  await postSse(
    '/api/execute/stream',
    { path, env, stage, overrides, spec, source },
    signal,
    (name, payload) => {
      switch (name) {
        case 'meta':  handler({ kind: 'meta',  payload: payload as StreamMeta }); break
        case 'body':  handler({ kind: 'body',  payload: payload as StreamBody }); break
        case 'sse':   handler({ kind: 'sse',   payload: payload as SseEvent }); break
        case 'ws':    handler({ kind: 'ws',    payload: payload as WsFrame }); break
        case 'done':  handler({ kind: 'done',  payload: payload as StreamDone }); break
        case 'error': handler({ kind: 'error', payload: payload as { message: string } }); break
      }
    },
    (message) => handler({ kind: 'error', payload: { message } }),
  )
}

async function runTestStream(
  path: string,
  env: string | null,
  stage: string | null,
  options: { only?: number | null; overrides?: Record<string, string> } | undefined,
  handler: (event: TestRunEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  await postSse(
    '/api/tests/run',
    { path, env, stage, only: options?.only ?? null, overrides: options?.overrides },
    signal,
    (name, payload) => {
      switch (name) {
        case 'start': handler({ kind: 'start', payload: payload as TestRunStart }); break
        case 'step':  handler({ kind: 'step',  payload: payload as TestRunStepEvent }); break
        case 'entry': handler({ kind: 'entry', payload: payload as TestEntryResult }); break
        case 'done':  handler({ kind: 'done',  payload: payload as TestRunResult }); break
        case 'error': handler({ kind: 'error', payload: payload as { message: string } }); break
      }
    },
    (message) => handler({ kind: 'error', payload: { message } }),
  )
}

/**
 * POSTs `body` and parses the `text/event-stream` response into discrete frames, invoking
 * `onFrame(eventName, decodedJson)` for each. Frames are separated by a blank line; field
 * syntax is `event: name` / `data: payload`, and `data:` lines accumulate until the blank
 * line closes the frame.
 *
 * Shared by every streaming endpoint the Studio talks to — the framing is identical, only
 * the event vocabulary differs, and two copies of a hand-rolled SSE parser is one too many.
 */
async function postSse(
  url: string,
  body: unknown,
  signal: AbortSignal,
  onFrame: (event: string, payload: unknown) => void,
  onError: (message: string) => void,
): Promise<void> {
  try {
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
      body: JSON.stringify(body),
      signal,
    })
    if (!resp.ok || !resp.body) {
      onError(`${resp.status} ${resp.statusText}`)
      return
    }

    const reader = resp.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    let currentEvent = 'message'
    const dataLines: string[] = []

    function flush() {
      if (dataLines.length === 0) {
        currentEvent = 'message'
        return
      }
      const data = dataLines.join('\n')
      dataLines.length = 0
      const ev = currentEvent
      currentEvent = 'message'
      try {
        onFrame(ev, JSON.parse(data))
      } catch (e) {
        // A malformed frame shouldn't kill the stream — log and keep going.
        console.warn(`[${url}] failed to parse frame`, ev, data, e)
      }
    }

    for (;;) {
      const { value, done } = await reader.read()
      if (done) { flush(); break }
      buffer += decoder.decode(value, { stream: true })

      // Process complete lines; keep the trailing partial line for the next chunk.
      let newlineIdx: number
      while ((newlineIdx = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, newlineIdx).replace(/\r$/, '')
        buffer = buffer.slice(newlineIdx + 1)
        if (line === '') { flush(); continue }
        if (line.startsWith(':')) continue // SSE comment
        const colon = line.indexOf(':')
        const field = colon < 0 ? line : line.slice(0, colon)
        const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '')
        if (field === 'event') currentEvent = value
        else if (field === 'data') dataLines.push(value)
      }
    }
  } catch (e) {
    if (signal.aborted) return
    onError(e instanceof Error ? e.message : String(e))
  }
}
