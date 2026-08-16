// Mirrors src/backend/Tap.Studio/Contracts/Dtos.cs. Keep in lockstep with the C# DTOs.

export type WorkspaceFileKind =
  | 'workspace' | 'request' | 'auth' | 'env' | 'collection' | 'flow' | 'test'
  | 'folder' | 'settings' | 'git-diff'
  /** A portable .http file. Holds several requests, which arrive as its tree children. */
  | 'httpfile'

export interface WorkspaceInfo {
  name: string
  root: string
  /** `aspire` means an AppHost pinned the workspace: the switcher is locked and the header
   *  says so, because the folder is part of the solution rather than a user preference. */
  mode: 'normal' | 'aspire'
  defaultEnv: string | null
  providers: string[]
  errors: WorkspaceErrorDto[]
}

export interface KnownWorkspace {
  path: string
  label: string
  isActive: boolean
  available: boolean
  git: GitInfo | null
}

export interface GitInfo {
  root: string
  branch: string
  isDetached: boolean
  originUrl: string | null
  remotes: GitRemote[]
}

export interface GitRemote {
  name: string
  url: string
}

export interface DirectoryEntry {
  name: string
  path: string
  hasTap: boolean
}

export interface BrowseResponse {
  path: string
  parent: string | null
  home: string
  isWorkspace: boolean
  gitRoot: string | null
  entries: DirectoryEntry[]
}

export interface WorkspaceErrorDto {
  code: string
  message: string
  path: string | null
  line: number | null
  /** `error` blocks; `warning` is advisory (e.g. a deprecated file extension) and must
   *  not be presented as a broken workspace. */
  severity: 'error' | 'warning'
}

export interface TreeNode {
  path: string
  kind: 'directory' | WorkspaceFileKind
  name: string
  id: string | null
  children: TreeNode[]
}

/** One request inside a `.http` file, as the server's parser sees it. */
export interface HttpRequestSummary {
  /** Fragment path (`orders.http#get-order`) — the identity that addresses this request
   *  everywhere else, and what an execute call sends back as its `path`. */
  path: string
  name: string
  method: string
  url: string
  /** 1-based line of the request line, for scrolling the editor to it. */
  line: number
}

/** Result of parsing unsaved `.http` text. A file that fails to parse still lists whatever
 *  requests survived — errors isolate per request, so one bad block doesn't hide the rest. */
export interface HttpFileParseResult {
  requests: HttpRequestSummary[]
  errors: WorkspaceErrorDto[]
}

export interface VarSpec {
  default: string | null
  description: string | null
  required: boolean
  example: string | null
  /** When true, the Studio redacts the value in catalogs and autocomplete. The on-disk
   *  flag (`secret: true` in the variable's YAML mapping) is the single source of truth —
   *  no template-syntax magic like `${{...}}`. */
  secret: boolean
}

export interface RequestSummary {
  path: string
  name: string
  id: string | null
  auth: string | null
  tags: string[]
}

export interface HttpHeaderSpec {
  name: string
  value: string
}

export interface RequestTransportSettings {
  ignoreTlsErrors?: boolean
  timeoutMs?: number
}

export interface TlsCertificate {
  subject: string
  issuer: string
  thumbprint: string
  notBefore: string
  notAfter: string
  serialNumber: string
}

export interface TlsDiagnosis {
  url: string
  valid: boolean
  error: string | null
  certificates: TlsCertificate[]
  errors: string[]
}

/** Wire protocol carried by a request — `http` (default) or `websocket`. Drives baseUrl
 *  scheme normalization and the executor's transport selection. */
export type RequestProtocol = 'http' | 'websocket'

/** What part of the response an assertion reads. */
export type AssertSource = 'status' | 'duration' | 'header' | 'body' | 'jsonpath' | 'xpath'

/** How the extracted value is compared. Not every matcher applies to every source —
 *  {@link MATCHERS_FOR_SOURCE} carries the server's rules so the editor can't build a
 *  combination the parser would reject. */
export type AssertOp =
  | 'equals' | 'notEquals'
  | 'contains' | 'notContains'
  | 'startsWith' | 'endsWith'
  | 'matches' | 'notMatches'
  | 'lt' | 'lte' | 'gt' | 'gte'
  | 'between' | 'in'
  | 'exists' | 'count' | 'length' | 'type'

/** One declared expectation about a response. Always the normalized (extractor, matcher)
 *  pair — the file format's shorthands are applied server-side on save. */
export interface AssertSpec {
  /** Display label. Null means "let the server describe it". */
  name?: string | null
  source: AssertSource
  /** Header name or path expression. Unused by status/duration/body. */
  selector?: string | null
  op: AssertOp
  expected?: string | null
  /** Values for `in` / `between`. */
  expectedList?: string[] | null
  ignoreCase?: boolean
  skip?: boolean
}

/** Outcome of one assertion. Produced only by the server — the UI never re-implements
 *  matcher semantics, so what you see while authoring is what a later run will decide. */
export interface AssertResult {
  /** Position in the request's assertion list; pairs a result back to its editor row. */
  index: number
  name: string
  ok: boolean
  skipped: boolean
  actual: string | null
  expected: string | null
  message: string | null
}

export interface AssertSummary {
  ok: boolean
  passed: number
  failed: number
  skipped: number
}

/** Which matchers the server accepts for each extractor. Mirrors
 *  `AssertSpec.ValidateCombination` in `Tap.Workspace` — keeping the editor's dropdown in
 *  step with it means an unsavable assertion can't be built by pointing and clicking. */
export const MATCHERS_FOR_SOURCE: Record<AssertSource, readonly AssertOp[]> = {
  status: ['equals', 'notEquals', 'in', 'between', 'lt', 'lte', 'gt', 'gte', 'matches', 'notMatches'],
  duration: ['lt', 'lte', 'gt', 'gte', 'between', 'equals', 'notEquals', 'in'],
  header: ['exists', 'equals', 'notEquals', 'contains', 'notContains', 'startsWith', 'endsWith',
    'matches', 'notMatches', 'length', 'in', 'lt', 'lte', 'gt', 'gte', 'between'],
  body: ['contains', 'notContains', 'matches', 'notMatches', 'equals', 'notEquals',
    'startsWith', 'endsWith', 'length'],
  jsonpath: ['equals', 'notEquals', 'exists', 'contains', 'notContains', 'matches', 'notMatches',
    'startsWith', 'endsWith', 'lt', 'lte', 'gt', 'gte', 'between', 'in', 'count', 'length', 'type'],
  xpath: ['equals', 'notEquals', 'exists', 'contains', 'notContains', 'matches', 'notMatches',
    'startsWith', 'endsWith', 'lt', 'lte', 'gt', 'gte', 'between', 'in', 'count', 'length'],
}

/** Sources whose YAML key carries a selector rather than the expected value. */
export const SOURCE_TAKES_SELECTOR: Record<AssertSource, boolean> = {
  status: false, duration: false, body: false, header: true, jsonpath: true, xpath: true,
}

/** Matchers whose expected value is a list. */
export const OP_TAKES_LIST: ReadonlySet<AssertOp> = new Set<AssertOp>(['in', 'between'])

/** Matchers that carry no expected value in the UI (`exists` is a two-state toggle). */
export const OP_TAKES_NO_VALUE: ReadonlySet<AssertOp> = new Set<AssertOp>(['exists'])

/** JSON types accepted by the `type` matcher. */
export const ASSERT_JSON_TYPES = ['string', 'number', 'boolean', 'object', 'array', 'null'] as const

// --- Testing: flows (*.flow.tap) and test sets (*.test.tap) ---------------------------

/** What part of a response an extraction reads. Same vocabulary as {@link AssertSource},
 *  plus `regex` — which assertions only expose as shorthand but extraction needs as a
 *  source of its own so it can bind a capture group. */
export type ExtractSource = 'status' | 'duration' | 'header' | 'body' | 'jsonpath' | 'xpath' | 'regex'

/** Sources whose YAML key carries an argument (`header: etag`). The rest are bare markers. */
export const EXTRACT_TAKES_SELECTOR: Record<ExtractSource, boolean> = {
  status: false, duration: false, body: false, header: true, jsonpath: true, xpath: true, regex: true,
}

/** One binding of a response value to a variable the later steps read. */
export interface ExtractSpec {
  /** The variable name this binds. */
  var: string
  source: ExtractSource
  /** Header name, path expression, or regex pattern. Unused by status/duration/body. */
  selector?: string | null
  /** Capture group for a `regex` source. Null means group 1, or the whole match when the
   *  pattern declares none. */
  group?: number | null
  /** Bound when the source matches nothing, instead of failing the step. */
  default?: string | null
  /** False lets a step carry on when the source matches nothing. */
  required?: boolean
}

export interface FlowStepSpec {
  /** Ref to the request this step sends — path relative to the flow file, or `id:<uuid>`. */
  request: string
  name?: string | null
  /** Per-step overrides. Values are templates expanded against the run bag first, which is
   *  how a step reads what an earlier one bound. */
  vars?: Record<string, string> | null
  extract?: ExtractSpec[] | null
  /** Assertions layered on top of the ones the referenced request declares. */
  assertions?: AssertSpec[] | null
  continueOnFailure?: boolean
  skip?: boolean
}

export interface FlowSummary {
  path: string
  name: string
  id: string | null
  stepCount: number
  tags: string[]
}

export interface FlowDetail {
  path: string
  name: string
  id: string | null
  vars: Record<string, VarSpec>
  steps: FlowStepSpec[]
  tags: string[]
  body: string
  source: string
}

export interface FlowSpec {
  path: string
  id: string | null
  name: string
  vars?: Record<string, string>
  /** Names of flow-scoped variables marked secret. */
  secrets?: string[]
  tags?: string[]
  body?: string
  steps: FlowStepSpec[]
}

/** What a run does when one of its entries fails. */
export type TestFailureMode = 'continue' | 'stop'

/** One check in a test set. Exactly one of `request` and `flow` is set. */
export interface TestEntrySpec {
  name?: string | null
  request?: string | null
  flow?: string | null
  vars?: Record<string, string> | null
  /** For a request entry these check its response; for a flow entry, the last step's. */
  assertions?: AssertSpec[] | null
  skip?: boolean
}

export interface TestSetSummary {
  path: string
  name: string
  id: string | null
  testCount: number
  tags: string[]
}

export interface TestSetDetail {
  path: string
  name: string
  id: string | null
  vars: Record<string, VarSpec>
  onFailure: TestFailureMode
  tests: TestEntrySpec[]
  tags: string[]
  body: string
  source: string
}

export interface TestSetSpec {
  path: string
  id: string | null
  name: string
  vars?: Record<string, string>
  /** Names of set-scoped variables marked secret. */
  secrets?: string[]
  onFailure?: TestFailureMode
  tags?: string[]
  body?: string
  tests: TestEntrySpec[]
}

// --- Testing: run results ------------------------------------------------------------

/** One value a step bound into the run bag. `error` is set when the source matched nothing
 *  and the extraction wasn't optional — which fails the step. */
export interface ExtractedValue {
  var: string
  value: string | null
  error: string | null
}

/** Outcome of one request inside a run. A request-backed test has exactly one; a
 *  flow-backed one has a step per flow step. */
export interface TestStepResult {
  index: number
  name: string
  requestPath: string | null
  method: string
  url: string
  status: number
  statusText: string | null
  contentType: string | null
  /** Truncated server-side — enough to diagnose a failure, not the whole 2 MiB. */
  responseBody: string | null
  responseBodyBytes: number
  durationMs: number
  assertions: AssertResult[]
  assertSummary: AssertSummary | null
  extracted: ExtractedValue[]
  ok: boolean
  skipped: boolean
  /** Why it failed, when the failure wasn't an assertion. On a skipped step this carries
   *  the reason it didn't run instead. */
  error: string | null
}

export interface TestEntryResult {
  index: number
  name: string
  targetKind: 'request' | 'flow'
  targetPath: string | null
  steps: TestStepResult[]
  ok: boolean
  skipped: boolean
  durationMs: number
  error: string | null
}

/** The skeleton of a run, sent before anything executes so every row can render as
 *  pending rather than appearing one at a time. */
export interface TestRunPlanEntry {
  index: number
  name: string
  targetKind: 'request' | 'flow'
  targetPath: string | null
  skip: boolean
}

export interface TestRunStart {
  path: string
  kind: 'test' | 'flow'
  name: string
  env: string | null
  stage: string | null
  entries: TestRunPlanEntry[]
}

export interface TestRunStepEvent {
  entryIndex: number
  step: TestStepResult
}

export interface TestRunResult {
  path: string
  kind: 'test' | 'flow'
  name: string
  entries: TestEntryResult[]
  ok: boolean
  passed: number
  failed: number
  skipped: number
  durationMs: number
  error: string | null
}

export interface RequestDetail extends RequestSummary {
  method: string
  url: string
  headers: HttpHeaderSpec[]
  requestBody: string | null
  body: string
  vars: Record<string, VarSpec>
  source: string
  protocol: RequestProtocol
  transport: RequestTransportSettings | null
  assertions: AssertSpec[]
}

export interface RequestSpec {
  path: string
  id: string | null
  name: string
  auth?: string
  tags?: string[]
  vars?: Record<string, string>
  /** Names of request-scoped variables marked secret. */
  secrets?: string[]
  body?: string
  method: string
  url: string
  headers?: HttpHeaderSpec[]
  requestBody?: string
  /** Omitted when `http` (default). `websocket` triggers ws scheme normalization + ws transport. */
  protocol?: RequestProtocol
  transport?: RequestTransportSettings
  /** Omitted when empty so dirty-tracking stays quiet on requests that declare none. */
  assertions?: AssertSpec[]
}

/** One named stage inside a collection. Stage fields override the parent collection's
 *  defaults; variables here override workspace + collection scopes but are still
 *  overridden by env / request scopes. */
export interface CollectionStage {
  name: string
  baseUrl: string | null
  defaultAuth: string | null
  vars: Record<string, VarSpec>
}

export interface CollectionStageSpec {
  name: string
  baseUrl?: string
  defaultAuth?: string
  vars?: Record<string, string>
  /** Names of stage-scoped variables marked secret. */
  secrets?: string[]
}

export interface EnvSpec {
  path: string
  id: string | null
  name: string
  vars?: Record<string, string>
  /** Names of variables that are marked secret. */
  secrets?: string[]
  tags?: string[]
  body?: string
  /** Provider bare `{{name}}` tokens hit first while this env is active (may be an alias).
   *  Omitted/empty = inherit the workspace/system default. */
  defaultVariableProvider?: string | null
  /** Alias → provider-name bindings (e.g. `kv → kv-prod`) active with this env. */
  providerAliases?: Record<string, string>
  /** Forbid bare-token fall-through past the default provider. */
  strictVariables?: boolean
}

export interface WorkspaceSpec {
  id: string | null
  name: string
  defaultEnv?: string
  variableProviders?: ProviderConfig[]
  /** Variable-provider name to use when writing variables without an explicit target. */
  defaultVariableProvider?: string | null
  vars?: Record<string, string>
  /** Names of workspace-scoped variables marked secret. */
  secrets?: string[]
  /** Workspace-level tag dictionary. */
  tags?: string[]
  body?: string
}

export interface AuthSummary {
  path: string
  name: string
  id: string | null
  type: string
  /** Slug of the collection that owns this profile (it lives under `collections/<slug>/`),
   *  or null for a workspace-scoped profile under `auth/`. A collection-scoped profile
   *  resolves `{{var}}` refs against that collection's variables and stages. */
  collection: string | null
}

export interface AuthDetail extends AuthSummary {
  fields: Record<string, string | null>
  headers: Record<string, string>
  query: Record<string, string>
  scopes: string[]
  tags: string[]
  body: string
  source: string
}

/** Typed spec sent to PUT /api/auths/spec. */
export interface AuthSpec {
  path: string
  id: string | null
  name: string
  type:
    | 'none' | 'basic' | 'bearer' | 'apiKey'
    | 'oauth2' | 'aws-sigv4' | 'custom'
    | 'azure-cli' | 'jwt' | 'github'
  tags?: string[]
  body?: string

  username?: string
  password?: string

  token?: string

  in?: 'header' | 'query' | 'cookie'
  apiKeyName?: string
  apiKeyValue?: string

  flow?:
    | 'authorization_code_pkce'
    | 'authorization_code'
    | 'client_credentials'
    | 'password'
    | 'device_code'
  useDiscovery?: boolean
  authority?: string
  authorizeUrl?: string
  tokenUrl?: string
  deviceAuthorizationUrl?: string
  clientId?: string
  clientSecret?: string
  scopes?: string[]
  audience?: string
  redirectUri?: string
  oauthUsername?: string
  oauthPassword?: string

  region?: string
  service?: string
  accessKeyId?: string
  secretAccessKey?: string
  sessionToken?: string

  azureFlow?: 'direct' | 'on_behalf_of'
  tenantId?: string
  subscription?: string
  resource?: string
  scope?: string
  userResource?: string
  userScope?: string

  jwtAlgorithm?: string
  jwtKey?: string
  jwtKeyId?: string
  jwtExpiresIn?: number
  /** JSON object of claims, including iss / aud / sub. */
  jwtPayload?: string

  githubMode?: 'pat' | 'gh-cli' | 'app' | 'oauth'
  githubAppId?: string
  githubInstallationId?: string
  githubPrivateKey?: string

  headers?: Record<string, string>
  query?: Record<string, string>
}

export interface EnvSummary {
  path: string
  name: string
  id: string | null
}

export interface EnvDetail extends EnvSummary {
  vars: Record<string, VarSpec>
  tags: string[]
  body: string
  source: string
  defaultVariableProvider: string | null
  providerAliases: Record<string, string>
  strictVariables: boolean
}

export interface ProviderConfig {
  name: string
  type: string
  settings: Record<string, string | null>
  origin: string
}

export function modeForProviderType(type: string): 'read' | 'readwrite' {
  switch (type) {
    case 'env': return 'read'
    case 'file': return 'readwrite'
    case 'azkv': return 'readwrite'
    case '1password': return 'read'
    default: return 'read'
  }
}

export interface SetVariablePayload {
  name: string
  value: string
  isSecret: boolean
  variableProvider: string | null
  /** Active env path — its provider binding decides where an untargeted write lands. */
  envPath?: string | null
}

export interface ProviderSummary {
  name: string
  type: string
  /** Display name of the provider *type* (e.g. "Azure Key Vault"); null for unknown types. */
  typeDisplayName: string | null
  /** Semantic icon key (azure/terminal/file/settings); null for unknown types. */
  icon: string | null
  mode: string
  origin: string
  settings: Record<string, string | null>
  variableCount: number | null
  error: string | null
}

/** Static metadata for one provider type — drives the picker + generated settings form. */
export interface ProviderTypeDescriptor {
  type: string
  displayName: string
  icon: string
  description: string
  mode: 'read' | 'readwrite'
  fields: ProviderSettingField[]
}

export interface ProviderSettingField {
  key: string
  label: string
  description: string | null
  kind: 'text' | 'secret' | 'select'
  required: boolean
  placeholder: string | null
  /** Optional picker attached to this field ('azure-keyvault' opens the vault browser). */
  picker: string | null
  /** Choices for a 'select' field; empty for every other kind. */
  options: ProviderFieldOption[]
  /** Shown when the settings bag has no entry for this key. Never written on its own. */
  defaultValue: string | null
  /** Render this field only while another setting holds one of these values. */
  visibleWhen: ProviderFieldVisibility | null
  /** Guidance rendered under the input, optionally carrying one link. */
  note: ProviderFieldNote | null
}

export interface ProviderFieldOption {
  value: string
  label: string
  description: string | null
}

export interface ProviderFieldVisibility {
  key: string
  values: string[]
}

export interface ProviderFieldNote {
  text: string
  url: string | null
  urlLabel: string | null
}

/** One Azure subscription visible to the CLI credential (vault picker). */
export interface AzureSubscription {
  subscriptionId: string
  displayName: string
  tenantId: string | null
  state: string | null
}

/** One Key Vault row in the picker. */
export interface AzureKeyVault {
  name: string
  resourceGroup: string
  location: string | null
}

/** One vault row in the 1Password picker. `items` is a count, not contents. */
export interface OnePasswordVault {
  id: string
  name: string
  items: number
}

/** Result of POST /api/onepassword/detect — same shape as the AI CLI detect endpoints. */
export interface OnePasswordDetect {
  ok: boolean
  path: string | null
  source: string
  version: string | null
  error: string | null
}

/** Result of POST /api/variable-providers/test. */
export interface TestProviderResult {
  ok: boolean
  message: string
  durationMs: number
  variableCount: number | null
}

/** One row of the provider browse listing; `value` is null for secrets. */
export interface ProviderVariable {
  name: string
  isSecret: boolean
  value: string | null
}

/** Clear-text reveal of one provider variable (explicit per-key request only). */
export interface ProviderVariableValue {
  name: string
  value: string
  isSecret: boolean
}

// --- System settings -----------------------------------------------------------------

export interface SystemProvider {
  name: string
  type: string
  settings: Record<string, string | null>
}

export interface SystemVariable {
  name: string
  value: string
  secret: boolean
}

export interface SystemSettings {
  systemDir: string
  defaultVariableProvider: string | null
  variableProviders: SystemProvider[]
  variables: SystemVariable[]
}

export interface SaveSystemSettings {
  defaultVariableProvider: string | null
  variableProviders: SystemProvider[]
  variables: SystemVariable[]
}

// --- AI assistant types --------------------------------------------------------------

export type AiProviderName = 'copilot' | 'claude-code'

export interface AiStatus {
  provider: AiProviderName
  configured: boolean
  model: string
  setupHint: string
}

export interface AiConfig {
  provider: AiProviderName | null
  model: string | null
  copilotCliPath: string | null
  claudeCliPath: string | null
  persisted: boolean
}

export interface SaveAiConfig {
  provider: AiProviderName
  model: string | null
  copilotCliPath: string | null
  claudeCliPath: string | null
}

export interface AiCliDetect {
  ok: boolean
  path: string | null
  source: string
  version: string | null
  error: string | null
}

export interface AiTestResult {
  ok: boolean
  provider: AiProviderName
  modelCount: number
  sample: string[]
  diagnostics: Record<string, string | null> | null
  error: string | null
}

export interface AiModelOption {
  id: string
  name: string | null
}

export interface AiModels {
  provider: AiProviderName
  default: string
  models: AiModelOption[]
  error: string | null
}

export interface AiAssistMessage {
  role: 'user' | 'assistant'
  content: string
}

export interface AiAssistRequest {
  prompt: string
  requestPath?: string
  currentSpec?: RequestSpec
  conversation?: AiAssistMessage[]
  model?: string
}

export interface AiToolCall {
  name: string
  summary: string | null
  success: boolean | null
}

export interface AiAssistResponse {
  reply: string
  proposal: RequestSpec | null
  model: string
  provider: AiProviderName
  toolCalls: AiToolCall[]
}

// --- Auth flow types -----------------------------------------------------------------

export interface OidcDiscovery {
  issuer: string
  authorizationEndpoint: string
  tokenEndpoint: string
  userInfoEndpoint: string | null
  jwksUri: string | null
  endSessionEndpoint: string | null
  scopesSupported: string[]
  grantTypesSupported: string[]
  codeChallengeMethodsSupported: string[]
}

export type AuthExecuteStatus = 'completed' | 'pending' | 'failed'

// --- Variables view ----------------------------------------------------------------

/** `portable` is a `.http` file's own `@name = value` lines — the weakest scope, since it is
 *  what the file resolves to when opened outside Tap. */
export type VariableScope = 'provider' | 'portable' | 'workspace' | 'collection' | 'stage' | 'env' | 'request'

/** Listing row for a collection. `exists:false` means the on-disk directory is present
 *  but has no `_collection.tap` yet — saving a CollectionSpec creates the file. */
export interface CollectionSummary {
  slug: string
  name: string
  id: string | null
  exists: boolean
  baseUrl: string
  /** Names of stages defined on this collection — just the names, full stage definitions
   *  live on CollectionDetail.stages. Empty when the collection has none. */
  stageNames: string[]
  defaultStage: string | null
}

export interface CollectionDetail {
  slug: string
  name: string
  id: string | null
  exists: boolean
  baseUrl: string
  defaultAuth: string | null
  defaultHeaders: Record<string, string>
  transport: RequestTransportSettings | null
  vars: Record<string, VarSpec>
  tags: string[]
  body: string
  source: string
  stages: CollectionStage[]
  defaultStage: string | null
  /** The collection's `agent:` option — whether agent surfaces (MCP tools, `call`) may use it. */
  agentEnabled: boolean
}

export interface CollectionSpec {
  slug: string
  id: string | null
  name: string
  baseUrl?: string
  defaultAuth?: string
  defaultHeaders?: Record<string, string>
  transport?: RequestTransportSettings
  vars?: Record<string, string>
  /** Names of collection-scoped variables marked secret. */
  secrets?: string[]
  tags?: string[]
  stages?: CollectionStageSpec[]
  defaultStage?: string
  /** Only the opt-out travels: `false` emits `agent: false`, undefined leaves the file silent (enabled). */
  agentEnabled?: boolean
  body?: string
}

/** Response from `POST /api/collections/import/postman` — the importer's report. */
export interface PostmanImportResponse {
  slug: string
  collectionPath: string
  authPath: string | null
  requestCount: number
  folderCount: number
  warnings: string[]
}

/** One (tag, entity) row from `GET /api/tags`. */
export interface TaggedItem {
  tag: string
  /** `collection` | `request` | `auth` */
  kind: string
  path: string
  name: string
}

export interface Variable {
  name: string
  value: string | null
  isSensitive: boolean
  scope: VariableScope
  /** Workspace-relative path of the file that declared it. For stage vars: `{collectionPath}#{stageName}`,
   *  for provider vars: `provider:{name}`. */
  sourcePath: string
  providerName: string | null
}

export interface VariableSet {
  scope: VariableScope
  sourcePath: string
  label: string
  count: number
  providerName: string | null
  variables: Variable[]
  /** Provider sets only: 'system' | 'workspace' — where the provider was declared. */
  origin: string | null
  /** Provider sets only: display name of the provider type ("Azure Key Vault"). */
  typeDisplayName: string | null
  /** Provider sets only: semantic icon key (azure/terminal/file/settings). */
  icon: string | null
}

export interface VariableView {
  sets: VariableSet[]
  result: Variable[]
  /** Effective default provider (env > workspace > system, aliases resolved). */
  defaultProvider: string | null
  /** Alias → provider-name bindings from the active env, or null. */
  aliases: Record<string, string> | null
}

export interface VariableContext {
  requestPath?: string
  collectionPath?: string
  envPath?: string
  stage?: string
}

export interface CompileResult {
  value: string
  replacements: Replacement[]
}

export interface Replacement {
  token: string
  name: string
  startIndex: number
  length: number
  resolved: boolean
  isSensitive: boolean
  scope: VariableScope | null
  sourcePath: string | null
  providerName: string | null
}

export interface AuthExecuteResponse {
  status: AuthExecuteStatus
  flowId: string | null
  loginUrl: string | null
  accessToken: string | null
  idToken: string | null
  refreshToken: string | null
  tokenType: string | null
  expiresAt: string | null
  fromCache: boolean
  headers: Record<string, string> | null
  error: string | null
  userCode?: string | null
  verificationUri?: string | null
  verificationUriComplete?: string | null
  deviceCodeExpiresIn?: number | null
}

export interface WorkspaceDetail {
  name: string
  id: string | null
  defaultEnv: string | null
  variableProviders: ProviderConfig[]
  defaultVariableProvider: string | null
  vars: Record<string, VarSpec>
  tags: string[]
  body: string
  source: string
}

export interface RenderedRequest {
  method: string
  url: string
  headers: Record<string, string>
  body: string | null
  variablesUsed: VariableTrace[]
  /** Name of the stage that resolved the request, or null if the collection has no
   *  stages or the caller didn't pick one. */
  stage: string | null
  protocol: RequestProtocol
}

export interface VariableTrace {
  variableProvider: string
  name: string
  resolved: boolean
  isSecret: boolean
  durationMs: number
}

export interface ExecutionResult {
  status: number
  statusText: string | null
  url: string
  method: string
  requestHeaders: Record<string, string>
  requestBody: string | null
  responseHeaders: Record<string, string>
  responseBody: string | null
  contentType: string | null
  responseBodyBytes: number
  durationMs: number
  variablesUsed: VariableTrace[]
  stage: string | null
  error: string | null
  protocol: RequestProtocol
  /** Snapshot of how the auth profile contributed — surfaced by the stream's `meta` event.
   *  Drives the Flow tab's "Run auth" affordance. */
  authStatus?: AuthStatus
  sseEvents?: SseEvent[]
  wsFrames?: WsFrame[]
  /** One entry per declared assertion, in file order. Arrives on the stream's `done` event. */
  assertions?: AssertResult[]
  /** Null when the request declares no assertions — the UI shows no pass/fail chrome at all. */
  assertSummary?: AssertSummary | null
}

export type AuthStatusSource = 'cached' | 'expired' | 'static' | 'missing' | 'none'

/** A launchable profile within a browser (Chrome/Edge dir, Firefox profile name). */
export interface BrowserProfile {
  key: string
  label: string
  isDefault: boolean
}

/** A browser installed on the host, with its discoverable profiles. Mirrors BrowserOptionDto. */
export interface BrowserOption {
  id: string
  label: string
  available: boolean
  supportsProfiles: boolean
  profiles: BrowserProfile[]
}

export interface AuthStatus {
  /** Resolved auth profile path, or null when no auth is attached. */
  path: string | null
  /** Auth profile type — `oauth2`, `github`, etc. Null when path is null. */
  type: string | null
  source: AuthStatusSource
  /** True when running the auth flow may open a browser or device-code prompt. */
  interactive: boolean
  /** When the cached token expires (cached + expired sources). */
  expiresAt: string | null
}

export interface WsFrame {
  seq: number
  direction: 'client' | 'server' | 'system'
  type: 'text' | 'binary' | 'close' | 'open' | 'error'
  text: string | null
  base64: string | null
  size: number
  closeStatus: number | null
  closeDescription: string | null
  timestampMs: number
}

export interface SseEvent {
  seq: number
  event: string
  data: string
  id: string | null
  timestampMs: number
}

export interface GraphQLSchemaResponse {
  schema: string | null
  error: string | null
}

export type GraphQLSchemaMode = 'introspection' | 'sdl'

// --- Git ----------------------------------------------------------------------------

export interface GitStatus {
  root: string
  branch: string
  isDetached: boolean
  hasRemote: boolean
  upstream: string | null
  ahead: number | null
  behind: number | null
  stagedCount: number
  unstagedCount: number
}

export type GitFileStatus =
  | 'added' | 'modified' | 'deleted' | 'renamed' | 'typechange' | 'untracked' | 'conflicted'

export interface GitFileChange {
  path: string
  indexStatus: GitFileStatus | null
  workingStatus: GitFileStatus | null
}

export interface GitBranch {
  name: string
  isCurrent: boolean
  isRemote: boolean
  upstream: string | null
  tip: string | null
}

export interface GitCommitResult {
  sha: string
  shortSha: string
  message: string
}

export interface GitCommandResult {
  exitCode: number
  stdout: string
  stderr: string
}

/** Response from `POST /api/files/upload`. `ref` is the body marker (e.g.
 *  `< ./.files/foo.png`) the editor embeds in the request body; the executor
 *  resolves the ref at send time and ships actual bytes. `relativePath` is
 *  workspace-relative for diagnostics. */
export interface FileUploadResponse {
  ref: string
  relativePath: string
  name: string
  size: number
  contentType: string | null
}
