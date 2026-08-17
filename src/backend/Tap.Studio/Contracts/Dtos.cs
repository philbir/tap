using System.Text.Json.Serialization;
using Tap.Execution.Contracts;
using Tap.Workspace.Model;
using Tap.Execution.Auth;

namespace Tap.Studio.Contracts;

/// <summary>
/// Wire DTOs for the REST API. Kept separate from <see cref="Tap.Workspace.Model"/> so the on-disk
/// model can evolve without breaking the UI contract. Source-generated JSON via
/// <see cref="StudioJson"/> per the conventions in <c>CLAUDE.md</c>.
/// </summary>
public sealed record WorkspaceInfoDto(
    string Name,
    string Root,
    string? DefaultEnv,
    IReadOnlyList<string> Providers,
    IReadOnlyList<WorkspaceErrorDto> Errors,
    /// <summary>"normal" or "aspire". Aspire means the workspace is pinned by the host —
    /// the UI locks the switcher and badges the header instead of offering an action that 409s.</summary>
    string Mode = "normal");

/// <summary>Entry in the workspace switcher dropdown. <c>Available</c> is false if the
/// folder no longer contains a <c>.tap/</c> directory — the UI greys it out. <c>Git</c> is
/// filled when an enclosing git repository was discovered at add-time (and is still on
/// disk); the UI shows branch + origin chips.</summary>
public sealed record KnownWorkspaceDto(string Path, string Label, bool IsActive, bool Available, GitInfoDto? Git);

public sealed record AddWorkspaceDto(string Path);
public sealed record ActivateWorkspaceDto(string Path);

/// <summary>Snapshot of the git repository enclosing a workspace. <c>Root</c> is the
/// working-directory root (the folder containing <c>.git/</c>), which may sit several
/// levels above the workspace path itself.</summary>
public sealed record GitInfoDto(
    string Root,
    string Branch,
    bool IsDetached,
    string? OriginUrl,
    IReadOnlyList<GitRemoteDto> Remotes);

public sealed record GitRemoteDto(string Name, string Url);

/// <summary>Snapshot of the active workspace's git repo. <see cref="HasRemote"/> drives the
/// pull/push UI affordances; <see cref="Ahead"/> / <see cref="Behind"/> are null when the
/// current branch has no upstream.</summary>
public sealed record GitStatusDto(
    string Root,
    string Branch,
    bool IsDetached,
    bool HasRemote,
    string? Upstream,
    int? Ahead,
    int? Behind,
    int StagedCount,
    int UnstagedCount);

/// <summary>One changed path. <see cref="IndexStatus"/> is null when the file has no
/// staged change; <see cref="WorkingStatus"/> is null when the working tree matches the
/// index. Values: <c>added | modified | deleted | renamed | typechange | untracked |
/// conflicted</c>.</summary>
public sealed record GitFileChangeDto(string Path, string? IndexStatus, string? WorkingStatus);

public sealed record GitBranchDto(string Name, bool IsCurrent, bool IsRemote, string? Upstream, string? Tip);

public sealed record GitStagePathsDto(IReadOnlyList<string> Paths);
public sealed record GitCommitRequestDto(string Message);
public sealed record GitCommitResultDto(string Sha, string ShortSha, string Message);
public sealed record GitCreateBranchDto(string Name, bool Checkout);
public sealed record GitCheckoutDto(string Name);
public sealed record GitPushRequestDto(bool SetUpstream);
public sealed record GitSetRemoteDto(string Name, string Url);
public sealed record GitCommandResultDto(int ExitCode, string Stdout, string Stderr);

/// <summary>One subdirectory in the picker. <c>HasTap</c> is set when the entry already
/// contains a <c>.tap/</c> directory, so the UI can draw a workspace badge.</summary>
public sealed record DirectoryEntryDto(string Name, string Path, bool HasTap);

/// <summary>Body for <c>POST /api/fs/folders</c>. Creates <c>{Parent}/{Name}</c>.
/// <see cref="Name"/> must be a single leaf — path separators and <c>..</c> are rejected.</summary>
public sealed record CreateDirectoryDto(string Parent, string Name);

/// <summary>Response from <c>POST /api/fs/folders</c>. The picker uses <see cref="Path"/>
/// to descend straight into the new folder.</summary>
public sealed record CreateDirectoryResponseDto(string Path);

/// <summary>Response from <c>GET /api/fs/browse</c>. <c>Parent</c> is null at the filesystem
/// root. <c>IsWorkspace</c> says the listed directory itself contains a <c>.tap/</c> —
/// the picker enables the confirm button on that signal.</summary>
public sealed record BrowseResponseDto(
    string Path,
    string? Parent,
    string Home,
    bool IsWorkspace,
    string? GitRoot,
    IReadOnlyList<DirectoryEntryDto> Entries);

/// <summary>Body for <c>POST /api/workspace/folders</c> — creates an empty directory under
/// <c>.tap/</c>. Folders are pure grouping for the explorer tree; no spec file is written.</summary>
public sealed record CreateFolderDto(string Path);

/// <summary>Body for <c>POST /api/workspace/move</c> — moves a file or directory. Paths are
/// workspace-relative (under <c>.tap/</c>); the server creates any missing parent directories
/// of the destination.</summary>
public sealed record MoveItemDto(string From, string To);

/// <summary>A workspace problem on the wire. <c>Severity</c> is "error" or "warning";
/// warnings (deprecations) are reported but do not make a workspace invalid.</summary>
/// <summary>A workspace file's raw text, for kinds Studio edits as source rather than
/// through a structured spec.</summary>
public sealed record RawSourceDto(string Path, string Content);

public sealed record WorkspaceErrorDto(string Code, string Message, string? Path, int? Line, string Severity = "error");

public sealed record TreeNodeDto(
    string Path,
    string Kind,
    string Name,
    string? Id,
    IReadOnlyList<TreeNodeDto> Children);

public sealed record RequestSummaryDto(
    string Path,
    string Name,
    string? Id,
    string? Auth,
    IReadOnlyList<string> Tags);

public sealed record RequestDetailDto(
    string Path,
    string Name,
    string? Id,
    string? Auth,
    IReadOnlyList<string> Tags,
    /// <summary>HTTP method parsed from the <c>http</c> block. Always present even for
    /// WebSocket requests (the upgrade handshake uses a method, conventionally <c>GET</c>).</summary>
    string Method,
    string Url,
    IReadOnlyList<HttpHeaderSpecDto> Headers,
    string? RequestBody,
    /// <summary>Markdown body around (or after) the HTTP block.</summary>
    string Body,
    IReadOnlyDictionary<string, VarSpec> Vars,
    string Source,
    /// <summary>Wire protocol — <c>"http"</c> (default) or <c>"websocket"</c>. Drives the
    /// renderer's baseUrl scheme normalization and the executor's transport selection.</summary>
    string Protocol,
    RequestTransportSettingsDto? Transport,
    /// <summary>Declared expectations about the response, in file order.</summary>
    IReadOnlyList<AssertSpecDto> Assertions);

public sealed record AuthDetailDto(
    string Path,
    string Name,
    string? Id,
    string Type,
    /// <summary>Slug of the collection that owns this profile (it lives under
    /// <c>collections/&lt;slug&gt;/</c>), or null for a workspace-scoped profile under
    /// <c>auth/</c>. A collection-scoped profile resolves its fields against that
    /// collection's variables and stages.</summary>
    string? Collection,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Tags,
    string Body,
    string Source);

public sealed record EnvDetailDto(
    string Path,
    string Name,
    string? Id,
    /// <summary>Env-scoped variables (the same VarSpec shape used by all other scopes —
    /// each carries an optional <c>Default</c> + the <c>Secret</c> flag).</summary>
    IReadOnlyDictionary<string, VarSpec> Vars,
    IReadOnlyList<string> Tags,
    string Body,
    string Source,
    /// <summary>Provider bare tokens hit first while this env is active (may be an alias).
    /// Null = inherit the workspace/system default.</summary>
    string? DefaultVariableProvider,
    /// <summary>Alias → provider-name bindings active with this env.</summary>
    IReadOnlyDictionary<string, string> ProviderAliases,
    /// <summary>True when bare tokens must not fall through past the default provider.</summary>
    bool StrictVariables);

/// <summary>Listing-row representation of a collection. <see cref="Exists"/> is false when
/// the directory <c>collections/&lt;slug&gt;/</c> is present on disk but has no
/// <c>_collection.tap</c> yet — the editor uses that to render a create-on-save form.</summary>
public sealed record CollectionSummaryDto(
    string Slug,
    string Name,
    string? Id,
    bool Exists,
    string BaseUrl,
    /// <summary>Stage names defined on this collection — full stage detail lives on
    /// CollectionDetailDto.Stages. Empty when the collection has none.</summary>
    IReadOnlyList<string> StageNames,
    string? DefaultStage);

/// <summary>One named stage inside a collection. Stage fields override the parent
/// collection's defaults; variables here override workspace + collection scopes but are
/// still overridden by env / request scopes.</summary>
public sealed record CollectionStageDto(
    string Name,
    string? BaseUrl,
    string? DefaultAuth,
    IReadOnlyDictionary<string, VarSpec> Vars);

/// <summary>Detail view of a collection. <see cref="Slug"/> is the directory name under
/// <c>collections/</c>; the metadata file lives at
/// <c>collections/&lt;slug&gt;/_collection.tap</c>. Collections own the baseUrl, optional
/// stages, default auth/headers, plus their own vars / tags / markdown body — what used
/// to live on a separate <c>api</c> file.</summary>
public sealed record CollectionDetailDto(
    string Slug,
    string Name,
    string? Id,
    bool Exists,
    string BaseUrl,
    string? DefaultAuth,
    IReadOnlyDictionary<string, string> DefaultHeaders,
    RequestTransportSettingsDto? Transport,
    IReadOnlyDictionary<string, VarSpec> Vars,
    IReadOnlyList<string> Tags,
    string Body,
    string Source,
    IReadOnlyList<CollectionStageDto> Stages,
    string? DefaultStage,
    /// <summary>The collection's <c>agent:</c> option — whether agent surfaces may use it.</summary>
    bool AgentEnabled);

/// <summary>Structured PUT spec for a collection. The server resolves the on-disk path
/// from <see cref="Slug"/> and writes canonical YAML via <c>CollectionSpecEmitter</c>.</summary>
public sealed record CollectionSpecDto
{
    public required string Slug { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    public string? BaseUrl { get; init; }
    /// <summary>Relative path to an auth file, or <c>id:…</c>. Falls through to requests.</summary>
    public string? DefaultAuth { get; init; }
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }
    public RequestTransportSettingsDto? Transport { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    /// <summary>Variable names marked secret. Same encoding as <see cref="EnvSpecDto.Secrets"/>.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<CollectionStageSpecDto>? Stages { get; init; }
    public string? DefaultStage { get; init; }
    /// <summary>The <c>agent:</c> option. Null or true emits nothing (enabled is the
    /// default); false emits <c>agent: false</c>.</summary>
    public bool? AgentEnabled { get; init; }
    public string? Body { get; init; }
}

public sealed record CollectionStageSpecDto
{
    public required string Name { get; init; }
    public string? BaseUrl { get; init; }
    public string? DefaultAuth { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    /// <summary>Names of stage-scoped variables marked secret.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
}

// ---------------------------------------------------------------------------------------------
// OpenAPI import.
//
// Two phases. `POST /api/openapi/documents[/fetch]` parses once and stages the result, returning a
// DocumentId; every later call references that id. This guarantees the operation list the user
// picked from and the document that gets imported are the same one — a URL re-fetched a minute
// later need not return the same bytes — and keeps a document that may reach 16 MB out of the
// model binder on every call.
//
// The raw spec arrives as a `string`, not a JsonElement like Postman's: OpenAPI documents are
// commonly YAML, which JsonElement cannot carry at all.
// ---------------------------------------------------------------------------------------------

/// <summary>Body for <c>POST /api/openapi/documents</c> — the document as text, JSON or YAML.</summary>
public sealed record OpenApiUploadRequestDto
{
    public required string Text { get; init; }
    public string? FileName { get; init; }
}

/// <summary>Body for <c>POST /api/openapi/documents/fetch</c>.</summary>
public sealed record OpenApiFetchRequestDto
{
    public required string Url { get; init; }
}

/// <summary>A staged document: everything the wizard needs to render its picker, having written
/// nothing to disk.</summary>
public sealed record OpenApiDocumentDto(
    string DocumentId,
    string Title,
    string? ApiVersion,
    string SpecVersion,
    string? Description,
    string SuggestedSlug,
    IReadOnlyList<OpenApiServerDto> Servers,
    IReadOnlyList<OpenApiSecuritySchemeDto> SecuritySchemes,
    IReadOnlyList<OpenApiOperationDto> Operations,
    IReadOnlyList<OpenApiDiagnosticDto> Diagnostics);

public sealed record OpenApiServerDto(string Url, string? Description);

/// <summary><see cref="TapAuthType"/> is null when Tap has no equivalent — the wizard shows the
/// scheme greyed out with <see cref="Warning"/> as the reason rather than hiding it.</summary>
public sealed record OpenApiSecuritySchemeDto(
    string Key,
    string Type,
    string? TapAuthType,
    string? Description,
    IReadOnlyList<string> Scopes,
    string? Warning);

public sealed record OpenApiOperationDto(
    string OpKey,
    string? OperationId,
    string Method,
    string Path,
    string? Summary,
    IReadOnlyList<string> Tags,
    bool Deprecated,
    bool HasRequestBody,
    int PathParamCount,
    int QueryParamCount);

public sealed record OpenApiDiagnosticDto(string Severity, string Message, string? Pointer);

/// <summary>Body for <c>POST /api/collections/import/openapi</c>.</summary>
public sealed record OpenApiImportRequestDto
{
    public required string DocumentId { get; init; }
    public string? Slug { get; init; }

    /// <summary>
    /// <c>req</c> (one <c>.req.tap</c> per operation) or <c>http</c> (one <c>.http</c> file per
    /// tag). Null means <c>req</c>.
    ///
    /// <para>Deliberately nullable with no initializer: a property initializer does not survive
    /// source-generated deserialization when the client omits the field, so the default has to
    /// live in the code that reads it, not in the DTO.</para>
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>Null or empty imports every operation.</summary>
    public IReadOnlyList<string>? OperationKeys { get; init; }

    public string? BaseUrl { get; init; }
    public string? SecuritySchemeKey { get; init; }
    public string? LinkAuthPath { get; init; }
    public bool IncludeOptionalQueryParams { get; init; }

    /// <summary>
    /// Values to seed generated variables with, keyed by opKey then variable name. This is where
    /// an accepted AI suggestion lands; the import is identical without it.
    /// </summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>>? VariableDefaults { get; init; }

    /// <summary>
    /// What to do when the target collection already exists: <c>create</c> (the default — fail if
    /// it does), <c>merge</c> (write these files, leave everything else alone), or <c>replace</c>
    /// (delete the directory first).
    ///
    /// <para>Null means <c>create</c>. As with <c>Layout</c>, the default lives here and not in a
    /// property initializer, which source-generated deserialization does not honour.</para>
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>Legacy alias for <c>mode: "replace"</c>.</summary>
    public bool Overwrite { get; init; }
}

public sealed record OpenApiImportResponseDto(
    string Slug,
    string CollectionPath,
    string? AuthPath,
    int RequestCount,
    int FileCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>GET /api/collections/{slug}/openapi</c> — the recorded link between a collection and the
/// document it came from. 404 when the collection was not imported (or the lock was deleted).
/// </summary>
public sealed record OpenApiLinkDto(
    string Slug,
    string SourceKind,
    string? Url,
    string? FileName,
    DateTimeOffset FetchedAt,
    string SpecVersion,
    string? ApiVersion,
    string DocumentHash,
    string Layout,
    int TrackedOperations);

/// <summary>Body for <c>POST /api/ai/openapi/suggest</c>.</summary>
public sealed record OpenApiSuggestRequestDto
{
    public required string DocumentId { get; init; }
    /// <summary>Null or empty asks about every operation in the document.</summary>
    public IReadOnlyList<string>? OperationKeys { get; init; }
    public string? Model { get; init; }
}

/// <summary>Proposed values for one operation's variables, keyed by variable name.</summary>
public sealed record OpenApiSuggestionDto(
    string OpKey,
    IReadOnlyDictionary<string, string> Values,
    string? Note);

public sealed record OpenApiSuggestResponseDto(
    IReadOnlyList<OpenApiSuggestionDto> Suggestions,
    string Provider,
    string? Model,
    /// <summary>How many operations were considered. Batching means a big spec may be partial.</summary>
    int Considered,
    IReadOnlyList<string> Warnings);

/// <summary>Body for the two re-sync calls — the document to diff against.</summary>
public sealed record OpenApiResyncRequestDto
{
    public required string DocumentId { get; init; }
}

/// <summary>
/// One operation's verdict. <c>kind</c> is <c>added</c> | <c>changed</c> | <c>conflict</c> |
/// <c>unchanged</c> | <c>orphaned</c> | <c>removed</c>, and <c>defaultAction</c> is what the UI
/// pre-selects — never destructive.
/// </summary>
public sealed record OpenApiChangeDto(
    string Kind,
    string OpKey,
    string Method,
    string Path,
    string? Summary,
    string? LocalPath,
    string? Fragment,
    bool LocallyEdited,
    string DefaultAction);

public sealed record OpenApiResyncPreviewDto(
    string Slug,
    string Layout,
    string? SourceUrl,
    DateTimeOffset PreviouslyFetchedAt,
    string? PreviousApiVersion,
    string? NewApiVersion,
    bool DocumentUnchanged,
    int Added,
    int Changed,
    int Conflicts,
    int Removed,
    IReadOnlyList<OpenApiChangeDto> Changes);

/// <summary>One user decision. <c>action</c> is <c>skip</c> | <c>add</c> | <c>update</c> |
/// <c>deprecate</c> | <c>untrack</c>. Operations not listed are skipped.</summary>
public sealed record OpenApiDecisionDto
{
    public required string OpKey { get; init; }
    public required string Action { get; init; }
}

public sealed record OpenApiResyncApplyRequestDto
{
    public required string DocumentId { get; init; }
    public required IReadOnlyList<OpenApiDecisionDto> Decisions { get; init; }
}

public sealed record OpenApiResyncResultDto(
    int Added,
    int Updated,
    int Deprecated,
    int Untracked,
    int Skipped,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> Warnings);

/// <summary>Body for <c>POST /api/collections/import/postman</c>. <see cref="Collection"/>
/// is the raw Postman v2.1 collection JSON (parsed as a free-form object so we don't have
/// to register the whole Postman schema in the source-generated context).
/// <see cref="Slug"/> overrides the slug derived from <c>info.name</c>; pass null to take
/// the default. <see cref="Overwrite"/> allows the importer to clobber an existing
/// <c>collections/&lt;slug&gt;/</c> directory.</summary>
public sealed record PostmanImportRequestDto
{
    public required System.Text.Json.JsonElement Collection { get; init; }
    public string? Slug { get; init; }
    public bool Overwrite { get; init; }
}

/// <summary>Response for <c>POST /api/collections/import/postman</c>. The Studio
/// reloads the workspace, opens the collection editor, and surfaces any importer
/// warnings (e.g. unsupported auth, formdata bodies) in a notification.</summary>
public sealed record PostmanImportResponseDto(
    string Slug,
    string CollectionPath,
    string? AuthPath,
    int RequestCount,
    int FolderCount,
    IReadOnlyList<string> Warnings);

/// <summary>One entry in <c>/api/tags</c>: a tag applied to a single entity (collection,
/// request, or auth). The UI groups these client-side for the Tags top-level view.</summary>
public sealed record TaggedItemDto(string Tag, string Kind, string Path, string Name);

public sealed record WorkspaceDetailDto(
    string Name,
    string? Id,
    string? DefaultEnv,
    IReadOnlyList<ProviderConfigDto> VariableProviders,
    string? DefaultVariableProvider,
    IReadOnlyDictionary<string, VarSpec> Vars,
    /// <summary>Workspace tag dictionary — the curated list of tags this workspace knows
    /// about. Editors use it as autocomplete source on every TagsInput; the Tags view
    /// uses it for its filter picker. Tags currently in use on entities are merged in
    /// when surfaced via <c>/api/tags/dictionary</c>, so this list is purely additive.</summary>
    IReadOnlyList<string> Tags,
    string Body,
    string Source);

/// <summary>
/// Variable provider configuration as exposed to the UI. <see cref="Settings"/> may include
/// sensitive values (e.g. file provider's <c>encryptionKey</c>); the server replaces those
/// with <c>"***"</c> in any response that targets the client. <c>Mode</c> isn't carried —
/// it's a static property of the provider type and the UI looks it up from the type.
/// </summary>
public sealed record ProviderConfigDto(
    string Name,
    string Type,
    IReadOnlyDictionary<string, string?> Settings,
    /// <summary><c>"system"</c> or <c>"workspace"</c>.</summary>
    string Origin);

// --- Auth flow / discovery DTOs ----------------------------------------------------------

public sealed record OidcDiscoveryDto(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    string? EndSessionEndpoint,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> GrantTypesSupported,
    IReadOnlyList<string> CodeChallengeMethodsSupported);

/// <summary>Body for <c>POST /api/auth/execute</c>. <see cref="RequestPath"/> +
/// <see cref="Stage"/> carry the caller's editing context so a collection-scoped profile
/// expands against the right stage's variables (and caches its token there); both are
/// ignored for a workspace-scoped profile. <see cref="Env"/> is the workspace-relative path
/// of the selected environment — it applies to every profile, and omitting it falls back to
/// the workspace's <c>defaultEnv</c>.</summary>
public sealed record AuthExecuteRequestDto(string Path, bool ForceReauthenticate)
{
    public string? RequestPath { get; init; }
    public string? Stage { get; init; }
    public string? Env { get; init; }
}

/// <summary>Body for <c>POST /api/auth/clear</c> — removes any cached runtime token for the
/// given auth profile (scoped to the currently active workspace).</summary>
public sealed record AuthClearRequestDto(string Path);

/// <summary>Response for <c>POST /api/auth/execute</c> and <c>GET /api/auth/flows/{id}</c>.</summary>
public sealed record AuthExecuteResponseDto(
    string Status,
    string? FlowId,
    string? LoginUrl,
    string? AccessToken,
    string? IdToken,
    string? RefreshToken,
    string? TokenType,
    DateTimeOffset? ExpiresAt,
    bool FromCache,
    IReadOnlyDictionary<string, string>? Headers,
    string? Error)
{
    public string? UserCode { get; init; }
    public string? VerificationUri { get; init; }
    public string? VerificationUriComplete { get; init; }
    public int? DeviceCodeExpiresIn { get; init; }
}

/// <summary>Listing-row representation of an auth profile. <see cref="Collection"/> is the
/// slug of the owning collection for a profile stored under <c>collections/&lt;slug&gt;/</c>,
/// or null for a workspace-scoped one under <c>auth/</c>.</summary>
public sealed record AuthSummaryDto(string Path, string Name, string? Id, string Type, string? Collection);

public sealed record EnvSummaryDto(string Path, string Name, string? Id);

public sealed record SaveFileDto(string Content);

/// <summary>
/// Raw-source save. The Source tab in every editor lets the user hand-edit the file's
/// canonical YAML directly; the server validates the content with FileParser before
/// writing so a broken file never lands on disk.
/// </summary>
public sealed record SaveSourceDto(string Path, string Content);

/// <summary>Body for <c>POST /api/http/parse</c> — the <c>.http</c> file being edited and the
/// unsaved text currently in the editor.</summary>
public sealed record ParseHttpFileDto(string Path, string Content);

/// <summary>One request found in a <c>.http</c> file.</summary>
/// <param name="Path">Its fragment path (<c>orders.http#get-order</c>) — the identity that
/// addresses it everywhere else, and what an execute call sends back as its <c>Path</c>.</param>
/// <param name="Line">1-based line of the request line, for scrolling the editor to it.</param>
public sealed record HttpRequestSummaryDto(
    string Path,
    string Name,
    string Method,
    string Url,
    int Line);

/// <summary>What <c>POST /api/http/parse</c> found. Errors and warnings are reported together —
/// a file that fails to parse still lists whatever requests survived, because per-request error
/// isolation is the whole point of <see cref="Tap.Workspace.Parsing.HttpFileParser"/>.</summary>
public sealed record ParseHttpFileResultDto(
    IReadOnlyList<HttpRequestSummaryDto> Requests,
    IReadOnlyList<WorkspaceErrorDto> Errors);

// -----------------------------------------------------------------------------------------
// Typed PUT specs — clients send structured props, the server emits canonical YAML.
// -----------------------------------------------------------------------------------------

public sealed record AuthSpecDto
{
    public required string Path { get; init; }

    public string? Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Body { get; init; }

    // basic
    public string? Username { get; init; }
    public string? Password { get; init; }

    // bearer
    public string? Token { get; init; }

    // apiKey
    public string? In { get; init; }
    public string? ApiKeyName { get; init; }
    public string? ApiKeyValue { get; init; }

    // oauth2
    public string? Flow { get; init; }
    public bool? UseDiscovery { get; init; }
    public string? Authority { get; init; }
    public string? AuthorizeUrl { get; init; }
    public string? TokenUrl { get; init; }
    public string? DeviceAuthorizationUrl { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
    public string? Audience { get; init; }
    public string? RedirectUri { get; init; }

    public string? OauthUsername { get; init; }
    public string? OauthPassword { get; init; }

    // aws-sigv4
    public string? Region { get; init; }
    public string? Service { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
    public string? SessionToken { get; init; }

    // azure-cli
    public string? AzureFlow { get; init; }
    public string? TenantId { get; init; }
    public string? Subscription { get; init; }
    public string? Resource { get; init; }
    public string? Scope { get; init; }
    public string? UserResource { get; init; }
    public string? UserScope { get; init; }

    // jwt. iss / aud / sub are ordinary payload claims — no dedicated fields. The runner
    // still honours the legacy `issuer:` / `audience:` / `subject:` front-matter keys for
    // hand-authored files; Studio folds them into the payload when the profile is opened.
    public string? JwtAlgorithm { get; init; }
    public string? JwtKey { get; init; }
    public string? JwtKeyId { get; init; }
    public int? JwtExpiresIn { get; init; }
    /// <summary>JSON object of claims. Merged over the auto-filled <c>exp/iat/jti</c>.</summary>
    public string? JwtPayload { get; init; }

    // github
    public string? GithubMode { get; init; }
    public string? GithubAppId { get; init; }
    public string? GithubInstallationId { get; init; }
    public string? GithubPrivateKey { get; init; }

    // custom
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Query { get; init; }
}

public sealed record EnvSpecDto
{
    public required string Path { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    public IReadOnlyList<string>? Secrets { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Body { get; init; }

    /// <summary>Provider bare tokens hit first while this env is active. May name a
    /// provider directly or via <see cref="ProviderAliases"/>. Null/empty = inherit.</summary>
    public string? DefaultVariableProvider { get; init; }

    /// <summary>Alias → provider-name bindings (e.g. <c>kv → kv-prod</c>).</summary>
    public IReadOnlyDictionary<string, string>? ProviderAliases { get; init; }

    /// <summary>Forbid bare-token fall-through past the default provider.</summary>
    public bool? StrictVariables { get; init; }
}

public sealed record WorkspaceSpecDto
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public string? DefaultEnv { get; init; }
    public IReadOnlyList<ProviderConfigDto>? VariableProviders { get; init; }
    public string? DefaultVariableProvider { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    public IReadOnlyList<string>? Secrets { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Body { get; init; }
}

public sealed record RequestSpecDto
{
    public required string Path { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Relative path to the auth profile, or <c>id:…</c>, or <c>none</c> to opt out.</summary>
    public string? Auth { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    public IReadOnlyList<string>? Secrets { get; init; }
    public string? Body { get; init; }

    public required string Method { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<HttpHeaderSpecDto>? Headers { get; init; }
    public string? RequestBody { get; init; }
    public string? Protocol { get; init; }
    public RequestTransportSettingsDto? Transport { get; init; }

    /// <summary>Declared expectations about the response. Omitted (or empty) leaves the
    /// <c>assertions:</c> key out of the emitted file entirely.</summary>
    public IReadOnlyList<AssertSpecDto>? Assertions { get; init; }
}

public sealed record RequestTransportSettingsDto(bool? IgnoreTlsErrors, int? TimeoutMs);

public sealed record HttpHeaderSpecDto(string Name, string Value);

/// <summary>
/// Wire form of one assertion. Always the normalized (extractor, matcher) pair — the YAML
/// sugar (<c>- status: 200</c>, <c>- regex: …</c>) is a file-format concern that the server
/// applies on the way out, so the editor only ever deals with this one shape.
/// </summary>
public sealed record AssertSpecDto
{
    /// <summary>Display label. Null means "derive it from the assertion" — the server does
    /// that so the UI never has to reimplement the phrasing.</summary>
    public string? Name { get; init; }

    /// <summary><c>status</c> | <c>duration</c> | <c>header</c> | <c>body</c> | <c>jsonpath</c> | <c>xpath</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Header name or path expression; null for the argument-less sources.</summary>
    public string? Selector { get; init; }

    /// <summary><c>equals</c> | <c>contains</c> | <c>matches</c> | <c>lt</c> | <c>between</c> | <c>exists</c> | … </summary>
    public required string Op { get; init; }

    public string? Expected { get; init; }

    /// <summary>Expected values for <c>in</c> and <c>between</c>.</summary>
    public IReadOnlyList<string>? ExpectedList { get; init; }

    public bool IgnoreCase { get; init; }
    public bool Skip { get; init; }
}

/// <summary><c>POST /api/assertions/evaluate</c> — re-check assertions against a response the
/// client already has, without sending the request again. <see cref="Context"/> supplies the
/// scope needed to expand <c>{{var}}</c> tokens in the expected values.</summary>
public sealed record EvaluateAssertsRequestDto(
    IReadOnlyList<AssertSpecDto> Assertions,
    AssertResponseSnapshotDto Response,
    string? Path,
    string? Env,
    string? Stage);

/// <summary>The captured response an <see cref="EvaluateAssertsRequestDto"/> is checked against.</summary>
public sealed record AssertResponseSnapshotDto(
    int Status,
    IReadOnlyList<HttpHeaderSpecDto>? Headers,
    string? Body,
    bool BodyTruncated,
    double DurationMs);

public sealed record EvaluateAssertsResponseDto(
    IReadOnlyList<AssertResultDto> Results,
    AssertSummaryDto Summary);

// -----------------------------------------------------------------------------------------
// Testing — flows (*.flow.tap, §10) and test sets (*.test.tap, §11), plus the shapes a run
// streams back. Editors ship the *SpecDto; the server emits the canonical YAML.
// -----------------------------------------------------------------------------------------

/// <summary>Wire form of one extraction on a flow step — a variable name plus exactly one
/// source. <see cref="Selector"/> carries the header name / path expression / regex pattern;
/// the argument-less sources leave it null.</summary>
public sealed record ExtractSpecDto
{
    /// <summary>The variable name this binds for the steps that follow.</summary>
    public required string Var { get; init; }

    /// <summary><c>status</c> | <c>duration</c> | <c>header</c> | <c>body</c> | <c>jsonpath</c> | <c>xpath</c> | <c>regex</c>.</summary>
    public required string Source { get; init; }

    public string? Selector { get; init; }

    /// <summary>Capture group for a <c>regex</c> source. Null means group 1, or the whole
    /// match when the pattern declares no groups.</summary>
    public int? Group { get; init; }

    /// <summary>Bound when the source matches nothing, instead of failing the step.</summary>
    public string? Default { get; init; }

    /// <summary>False lets a step carry on when the source matches nothing.</summary>
    public bool Required { get; init; } = true;
}

public sealed record FlowStepSpecDto
{
    /// <summary>Ref to the request this step sends — a path relative to the flow file, or
    /// <c>id:&lt;uuid&gt;</c>.</summary>
    public required string Request { get; init; }

    public string? Name { get; init; }

    /// <summary>Per-step overrides. Values are templates expanded against the run bag first,
    /// which is how a step reads an earlier step's output.</summary>
    public IReadOnlyDictionary<string, string>? Vars { get; init; }

    public IReadOnlyList<ExtractSpecDto>? Extract { get; init; }

    /// <summary>Assertions on top of the ones the referenced request declares.</summary>
    public IReadOnlyList<AssertSpecDto>? Assertions { get; init; }

    public bool ContinueOnFailure { get; init; }
    public bool Skip { get; init; }
}

public sealed record FlowSpecDto
{
    public required string Path { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    /// <summary>Names of flow-scoped variables marked secret.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Body { get; init; }
    public required IReadOnlyList<FlowStepSpecDto> Steps { get; init; }
}

public sealed record FlowSummaryDto(string Path, string Name, string? Id, int StepCount, IReadOnlyList<string> Tags);

public sealed record FlowDetailDto(
    string Path,
    string Name,
    string? Id,
    IReadOnlyDictionary<string, VarSpec> Vars,
    IReadOnlyList<FlowStepSpecDto> Steps,
    IReadOnlyList<string> Tags,
    string Body,
    string Source);

public sealed record TestEntrySpecDto
{
    public string? Name { get; init; }

    /// <summary>Ref to the request this test sends. Exactly one of this and <see cref="Flow"/>.</summary>
    public string? Request { get; init; }

    /// <summary>Ref to the flow this test runs. Exactly one of this and <see cref="Request"/>.</summary>
    public string? Flow { get; init; }

    public IReadOnlyDictionary<string, string>? Vars { get; init; }

    /// <summary>Assertions on top of the target's own. For a flow entry they check the last
    /// step's response.</summary>
    public IReadOnlyList<AssertSpecDto>? Assertions { get; init; }

    public bool Skip { get; init; }
}

public sealed record TestSetSpecDto
{
    public required string Path { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    /// <summary>Names of set-scoped variables marked secret.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
    /// <summary><c>continue</c> (default) or <c>stop</c>.</summary>
    public string? OnFailure { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Body { get; init; }
    public required IReadOnlyList<TestEntrySpecDto> Tests { get; init; }
}

public sealed record TestSetSummaryDto(string Path, string Name, string? Id, int TestCount, IReadOnlyList<string> Tags);

public sealed record TestSetDetailDto(
    string Path,
    string Name,
    string? Id,
    IReadOnlyDictionary<string, VarSpec> Vars,
    string OnFailure,
    IReadOnlyList<TestEntrySpecDto> Tests,
    IReadOnlyList<string> Tags,
    string Body,
    string Source);

// -----------------------------------------------------------------------------------------
// AI assistant — CLI detection/config + the request-crafting assist call.
// -----------------------------------------------------------------------------------------

/// <summary>Current resolved AI provider status for the status pill / gating.</summary>
public sealed record AiStatusDto(string Provider, bool Configured, string Model, string SetupHint);

/// <summary>Persisted AI config as shown in Settings. <c>Persisted</c> is false when nothing
/// has been saved yet (the provider is running on auto-detected defaults).</summary>
public sealed record AiConfigDto(string? Provider, string? Model, string? CopilotCliPath, string? ClaudeCliPath, bool Persisted);

/// <summary>Body for saving AI config.</summary>
public sealed record SaveAiConfigDto(string Provider, string? Model, string? CopilotCliPath, string? ClaudeCliPath);

/// <summary>Body for the CLI-detect probes — an optional path override to test.</summary>
public sealed record AiCliDetectRequestDto(string? Path);

/// <summary>Result of probing a CLI binary.</summary>
public sealed record AiCliDetectResponseDto(bool Ok, string? Path, string Source, string? Version, string? Error);

/// <summary>Result of validating draft AI settings without persisting them.</summary>
public sealed record AiTestResponseDto(
    bool Ok, string Provider, int ModelCount, IReadOnlyList<string> Sample,
    IReadOnlyDictionary<string, string?>? Diagnostics, string? Error);

/// <summary>One model option for the model picker.</summary>
public sealed record AiModelDto(string Id, string? Name);

/// <summary>Models exposed by the active provider plus its default.</summary>
public sealed record AiModelsResponseDto(string Provider, string Default, IReadOnlyList<AiModelDto> Models, string? Error);

/// <summary>One prior turn in the assistant conversation.</summary>
public sealed record AiAssistMessageDto(string Role, string Content);

/// <summary>Request to the assistant. <c>CurrentSpec</c> is the live editor spec so edits are
/// incremental; <c>Conversation</c> carries prior turns for multi-message threads.</summary>
public sealed record AiAssistRequestDto(
    string Prompt,
    string? RequestPath,
    RequestSpecDto? CurrentSpec,
    IReadOnlyList<AiAssistMessageDto>? Conversation,
    string? Model);

/// <summary>Assistant reply. <c>Proposal</c> is non-null when the model emitted a
/// <c>tap-request</c> block the UI can apply into the editor.</summary>
public sealed record AiAssistResponseDto(
    string Reply, RequestSpecDto? Proposal, string Model, string Provider, IReadOnlyList<AiToolCallDto> ToolCalls);

/// <summary>A tool call the assistant ran, surfaced for display in the conversation.</summary>
public sealed record AiToolCallDto(string Name, string? Summary, bool? Success);

// -----------------------------------------------------------------------------------------
// Variables — the cascade view + template compilation for the Studio's variable picker UI.
// -----------------------------------------------------------------------------------------

/// <summary>Context for resolving variables in the editor. All fields optional:
/// <c>RequestPath</c> drives the request-editor case (workspace → collection → stage → env → request);
/// <c>CollectionPath</c> drives the collection-editor case (workspace → collection → stage); <c>EnvPath</c> and
/// <c>Stage</c> override the active env / collection default stage.</summary>
public sealed record VariableContextDto(string? RequestPath, string? CollectionPath, string? EnvPath, string? Stage);

public sealed record VariableViewDto(
    IReadOnlyList<VariableSetDto> Sets,
    IReadOnlyList<VariableDto> Result,
    /// <summary>Effective default provider (env &gt; workspace &gt; system, aliases resolved).</summary>
    string? DefaultProvider,
    /// <summary>Alias → provider-name bindings from the active env, or null.</summary>
    IReadOnlyDictionary<string, string>? Aliases);

public sealed record VariableSetDto(
    string Scope,
    string SourcePath,
    string Label,
    int Count,
    string? ProviderName,
    IReadOnlyList<VariableDto> Variables,
    /// <summary>Provider sets only: <c>"system"</c> or <c>"workspace"</c> — where the
    /// provider was declared. Null for cascade sets.</summary>
    string? Origin,
    /// <summary>Provider sets only: display name of the provider type ("Azure Key Vault").</summary>
    string? TypeDisplayName,
    /// <summary>Provider sets only: semantic icon key (azure/terminal/file/settings).</summary>
    string? Icon);

public sealed record VariableDto(
    string Name,
    string? Value,
    bool IsSensitive,
    string Scope,
    string SourcePath,
    string? ProviderName);

public sealed record CompileTemplateRequestDto(string Template, VariableContextDto Context);

public sealed record CompileTemplateResponseDto(
    string Value,
    IReadOnlyList<ReplacementDto> Replacements);

public sealed record ReplacementDto(
    string Token,
    string Name,
    int StartIndex,
    int Length,
    bool Resolved,
    bool IsSensitive,
    string? Scope,
    string? SourcePath,
    string? ProviderName);

public sealed record SetVariableDto(
    string Name,
    string Value,
    bool IsSecret,
    string? VariableProvider,
    /// <summary>Active env path — selects whose provider binding applies when
    /// <see cref="VariableProvider"/> is null. Null falls back to the default env.</summary>
    string? EnvPath = null);

public sealed record ProviderSummaryDto(
    string Name,
    string Type,
    /// <summary>Display name of the provider <b>type</b> (e.g. "Azure Key Vault"), from the
    /// type descriptor. Null when the type has no registered factory.</summary>
    string? TypeDisplayName,
    /// <summary>Semantic icon key from the type descriptor (azure/terminal/file/settings).</summary>
    string? Icon,
    string Mode,
    string Origin,
    IReadOnlyDictionary<string, string?> Settings,
    int? VariableCount,
    string? Error);

/// <summary>Static metadata for one provider type, served from the factory's
/// <c>ProviderTypeDescriptor</c>. Drives the Studio's provider picker and its generated
/// settings form.</summary>
public sealed record ProviderTypeDescriptorDto(
    string Type,
    string DisplayName,
    string Icon,
    string Description,
    /// <summary><c>"read"</c> or <c>"readwrite"</c>.</summary>
    string Mode,
    IReadOnlyList<ProviderSettingFieldDto> Fields);

public sealed record ProviderSettingFieldDto(
    string Key,
    string Label,
    string? Description,
    /// <summary><c>"text"</c>, <c>"secret"</c>, or <c>"select"</c>.</summary>
    string Kind,
    bool Required,
    string? Placeholder,
    /// <summary>Optional picker the UI can attach to this field (e.g.
    /// <c>"azure-keyvault"</c> opens the subscription → vault browser). Null = plain input.</summary>
    string? Picker,
    /// <summary>Choices for a <c>"select"</c> field; empty for every other kind.</summary>
    IReadOnlyList<ProviderFieldOptionDto> Options,
    /// <summary>Value the form shows when the settings bag has no entry for this key.</summary>
    string? DefaultValue,
    /// <summary>Render this field only while another setting holds one of these values.</summary>
    ProviderFieldVisibilityDto? VisibleWhen,
    /// <summary>Guidance rendered under the input, optionally carrying one link.</summary>
    ProviderFieldNoteDto? Note);

public sealed record ProviderFieldOptionDto(string Value, string Label, string? Description);

public sealed record ProviderFieldVisibilityDto(string Key, IReadOnlyList<string> Values);

public sealed record ProviderFieldNoteDto(string Text, string? Url, string? UrlLabel);

/// <summary>Body for <c>POST /api/variable-providers/test</c> — a <b>draft</b> provider
/// config, so the Settings UI can test before saving. When <see cref="Name"/> matches a
/// stored provider, masked (<c>***</c>) setting values are replaced server-side with the
/// stored values, so testing never requires retyping a secret.</summary>
public sealed record TestProviderRequestDto(
    string? Name,
    string Type,
    IReadOnlyDictionary<string, string?>? Settings);

public sealed record TestProviderResultDto(
    bool Ok,
    string Message,
    double DurationMs,
    int? VariableCount);

/// <summary>One row in the provider browse listing. <see cref="Value"/> is null for secret
/// entries — the UI reveals them per-row via the value endpoint.</summary>
public sealed record ProviderVariableDto(string Name, bool IsSecret, string? Value);

/// <summary>Clear-text reveal of a single provider variable. Only returned on the explicit
/// per-key endpoint, never in bulk listings.</summary>
public sealed record ProviderVariableValueDto(string Name, string Value, bool IsSecret);

/// <summary>One Azure subscription visible to the CLI credential (vault picker dialog).</summary>
public sealed record AzureSubscriptionDto(
    string SubscriptionId,
    string DisplayName,
    string? TenantId,
    string? State);

/// <summary>One Key Vault row in the picker: name + resource group (+ location).</summary>
public sealed record AzureKeyVaultDto(
    string Name,
    string ResourceGroup,
    string? Location);

/// <summary>Draft 1Password settings for the picker/detect endpoints. Carries the whole
/// settings bag (plus the provider <see cref="Name"/>, when it has one) so masked secrets can
/// be restored from the stored config exactly the way <c>/api/variable-providers/test</c>
/// does — the dialog works without making the user retype a service-account token.</summary>
public sealed record OnePasswordProbeRequestDto(
    string? Name,
    IReadOnlyDictionary<string, string?>? Settings);

/// <summary>One vault row in the 1Password picker: name, ID, and how many items it holds.</summary>
public sealed record OnePasswordVaultDto(
    string Id,
    string Name,
    int Items);

/// <summary>Result of probing for the <c>op</c> binary — same shape the AI CLI detect
/// endpoints return, so the settings form's "Detect" button is one component.</summary>
public sealed record OnePasswordDetectResponseDto(
    bool Ok,
    string? Path,
    string Source,
    string? Version,
    string? Error);

// -----------------------------------------------------------------------------------------
// System settings.
// -----------------------------------------------------------------------------------------

public sealed record SystemProviderDto(
    string Name,
    string Type,
    IReadOnlyDictionary<string, string?> Settings);

public sealed record SystemVariableDto(string Name, string Value, bool Secret);

public sealed record SystemSettingsDto(
    string SystemDir,
    string? DefaultVariableProvider,
    IReadOnlyList<SystemProviderDto> VariableProviders,
    IReadOnlyList<SystemVariableDto> Variables);

public sealed record SaveSystemSettingsDto(
    string? DefaultVariableProvider,
    IReadOnlyList<SystemProviderDto> VariableProviders,
    IReadOnlyList<SystemVariableDto> Variables);

public sealed record RenderRequestDto(
    string Path,
    string? Env,
    IReadOnlyDictionary<string, string>? Overrides,
    string? Stage);

public sealed record RenderedRequestDto(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    IReadOnlyList<VariableTraceDto> VariablesUsed,
    string? Stage,
    string Protocol);

public sealed record VariableTraceDto(string VariableProvider, string Name, bool Resolved, bool IsSecret, double DurationMs);

/// <param name="Spec">An unsaved editor draft of a structured request, built in-memory through
/// the emit pipeline instead of being read off disk. Does not apply to <c>.http</c> requests —
/// those have no canonical spec form, so their draft arrives as <paramref name="Source"/>.</param>
/// <param name="Source">The unsaved raw text of the <c>.http</c> file <paramref name="Path"/>
/// points into. The file is parsed in-memory and the fragment named by <paramref name="Path"/>
/// is executed, so the user runs what is on screen rather than what was last saved. Ignored for
/// every other kind, and mutually exclusive with <paramref name="Spec"/>.</param>
public sealed record ExecuteRequestDto(
    string Path,
    string? Env,
    IReadOnlyDictionary<string, string>? Overrides,
    string? Stage,
    RequestSpecDto? Spec = null,
    string? Source = null);

public sealed record ExecutionResultDto(
    int Status,
    string? StatusText,
    string Url,
    string Method,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody,
    string? ContentType,
    long ResponseBodyBytes,
    double DurationMs,
    IReadOnlyList<VariableTraceDto> VariablesUsed,
    string? Stage,
    string? Error,
    string Protocol,
    /// <summary>One entry per declared assertion, in file order. Empty when the request
    /// declares none.</summary>
    IReadOnlyList<AssertResultDto> Assertions,
    /// <summary>Roll-up of <see cref="Assertions"/>. Null when the request declares none, so
    /// a request without assertions shows no pass/fail chrome at all.</summary>
    AssertSummaryDto? AssertSummary);

public sealed record ExecuteStreamMetaDto(
    string Method,
    string Url,
    int Status,
    string? StatusText,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ContentType,
    string Protocol,
    AuthStatusDto AuthStatus);

/// <summary>
/// Snapshot of how the auth profile (if any) contributed to a rendered request. The Flow
/// tab uses this to tell the user whether they need to run an interactive auth step
/// (popup / device-code / `az login`) before the next Send.
///
/// <list type="bullet">
///   <item><c>none</c> — no auth attached to the request.</item>
///   <item><c>static</c> — headers were built inline at render time (basic/bearer/apiKey/custom). No flow to run.</item>
///   <item><c>cached</c> — a runtime-acquired token was found in <see cref="Auth.AuthTokenStore"/> and stamped on the request.</item>
///   <item><c>expired</c> — token exists but has lapsed; the request was sent without an Authorization header.</item>
///   <item><c>missing</c> — runtime token is needed but absent; the request was sent without an Authorization header.</item>
/// </list>
///
/// <see cref="Interactive"/> is true when running the auth flow may open a browser
/// window or device-code prompt — the UI flips the "Run auth" button label accordingly.
/// </summary>
public sealed record AuthStatusDto(
    string? Path,
    string? Type,
    string Source,
    bool Interactive,
    DateTimeOffset? ExpiresAt);

public sealed record ExecuteStreamWsDto(
    int Seq,
    string Direction,
    string Type,
    string? Text,
    string? Base64,
    int Size,
    int? CloseStatus,
    string? CloseDescription,
    double TimestampMs);

public sealed record ExecuteStreamBodyDto(
    string? ResponseBody,
    long ResponseBodyBytes);

public sealed record ExecuteStreamSseDto(
    int Seq,
    string Event,
    string Data,
    string? Id,
    double TimestampMs);

public sealed record ExecuteStreamDoneDto(
    double DurationMs,
    long ResponseBodyBytes,
    IReadOnlyList<VariableTraceDto> VariablesUsed,
    string? Stage,
    string? Error,
    /// <summary>Assertion results, evaluated server-side once the body is complete. Rides on
    /// <c>done</c> rather than its own event so the UI paints the verdict in the same frame
    /// it stops the spinner.</summary>
    IReadOnlyList<AssertResultDto> Assertions,
    AssertSummaryDto? AssertSummary);

public sealed record ExecuteStreamErrorDto(string Message);

public sealed record GraphQLSchemaRequestDto(string Path, string? Env, string? Stage, string Mode);

public sealed record GraphQLSchemaResponseDto(string? Schema, string? Error);

/// <summary>Response from <c>POST /api/files/upload</c>. <see cref="Ref"/> is the body
/// marker the client embeds in the request body (e.g. <c>&lt; ./.files/hello.png</c>);
/// <see cref="RelativePath"/> is workspace-relative for diagnostics. Both name + size
/// echo what the client just uploaded so the editor can show file metadata without
/// re-reading the file.</summary>
public sealed record FileUploadResponseDto(
    string Ref,
    string RelativePath,
    string Name,
    long Size,
    string? ContentType);

[JsonSerializable(typeof(WorkspaceInfoDto))]
[JsonSerializable(typeof(AssertSpecDto))]
[JsonSerializable(typeof(IReadOnlyList<AssertSpecDto>))]
[JsonSerializable(typeof(AssertResultDto))]
[JsonSerializable(typeof(IReadOnlyList<AssertResultDto>))]
[JsonSerializable(typeof(AssertSummaryDto))]
[JsonSerializable(typeof(AssertResponseSnapshotDto))]
[JsonSerializable(typeof(EvaluateAssertsRequestDto))]
[JsonSerializable(typeof(EvaluateAssertsResponseDto))]
[JsonSerializable(typeof(ExtractSpecDto))]
[JsonSerializable(typeof(IReadOnlyList<ExtractSpecDto>))]
[JsonSerializable(typeof(FlowStepSpecDto))]
[JsonSerializable(typeof(IReadOnlyList<FlowStepSpecDto>))]
[JsonSerializable(typeof(FlowSpecDto))]
[JsonSerializable(typeof(FlowSummaryDto))]
[JsonSerializable(typeof(IReadOnlyList<FlowSummaryDto>))]
[JsonSerializable(typeof(FlowDetailDto))]
[JsonSerializable(typeof(TestEntrySpecDto))]
[JsonSerializable(typeof(IReadOnlyList<TestEntrySpecDto>))]
[JsonSerializable(typeof(TestSetSpecDto))]
[JsonSerializable(typeof(TestSetSummaryDto))]
[JsonSerializable(typeof(IReadOnlyList<TestSetSummaryDto>))]
[JsonSerializable(typeof(TestSetDetailDto))]
[JsonSerializable(typeof(RunTestRequestDto))]
[JsonSerializable(typeof(ExtractedValueDto))]
[JsonSerializable(typeof(IReadOnlyList<ExtractedValueDto>))]
[JsonSerializable(typeof(TestStepResultDto))]
[JsonSerializable(typeof(IReadOnlyList<TestStepResultDto>))]
[JsonSerializable(typeof(TestEntryResultDto))]
[JsonSerializable(typeof(IReadOnlyList<TestEntryResultDto>))]
[JsonSerializable(typeof(TestRunPlanEntryDto))]
[JsonSerializable(typeof(IReadOnlyList<TestRunPlanEntryDto>))]
[JsonSerializable(typeof(TestRunStartDto))]
[JsonSerializable(typeof(TestRunStepEventDto))]
[JsonSerializable(typeof(TestRunResultDto))]
[JsonSerializable(typeof(CollectionStageDto))]
[JsonSerializable(typeof(IReadOnlyList<CollectionStageDto>))]
[JsonSerializable(typeof(CollectionStageSpecDto))]
[JsonSerializable(typeof(IReadOnlyList<CollectionStageSpecDto>))]
[JsonSerializable(typeof(KnownWorkspaceDto))]
[JsonSerializable(typeof(GitInfoDto))]
[JsonSerializable(typeof(GitRemoteDto))]
[JsonSerializable(typeof(IReadOnlyList<GitRemoteDto>))]
[JsonSerializable(typeof(GitStatusDto))]
[JsonSerializable(typeof(GitFileChangeDto))]
[JsonSerializable(typeof(IReadOnlyList<GitFileChangeDto>))]
[JsonSerializable(typeof(GitBranchDto))]
[JsonSerializable(typeof(IReadOnlyList<GitBranchDto>))]
[JsonSerializable(typeof(GitStagePathsDto))]
[JsonSerializable(typeof(GitCommitRequestDto))]
[JsonSerializable(typeof(GitCommitResultDto))]
[JsonSerializable(typeof(GitCreateBranchDto))]
[JsonSerializable(typeof(GitCheckoutDto))]
[JsonSerializable(typeof(GitPushRequestDto))]
[JsonSerializable(typeof(GitSetRemoteDto))]
[JsonSerializable(typeof(GitCommandResultDto))]
[JsonSerializable(typeof(DirectoryEntryDto))]
[JsonSerializable(typeof(IReadOnlyList<DirectoryEntryDto>))]
[JsonSerializable(typeof(BrowseResponseDto))]
[JsonSerializable(typeof(CreateDirectoryDto))]
[JsonSerializable(typeof(CreateDirectoryResponseDto))]
[JsonSerializable(typeof(AddWorkspaceDto))]
[JsonSerializable(typeof(ActivateWorkspaceDto))]
[JsonSerializable(typeof(CreateFolderDto))]
[JsonSerializable(typeof(MoveItemDto))]
[JsonSerializable(typeof(IReadOnlyList<KnownWorkspaceDto>))]
[JsonSerializable(typeof(WorkspaceDetailDto))]
[JsonSerializable(typeof(TreeNodeDto))]
[JsonSerializable(typeof(RequestSummaryDto))]
[JsonSerializable(typeof(RequestDetailDto))]
[JsonSerializable(typeof(AuthSummaryDto))]
[JsonSerializable(typeof(AuthDetailDto))]
[JsonSerializable(typeof(EnvSummaryDto))]
[JsonSerializable(typeof(EnvDetailDto))]
[JsonSerializable(typeof(SaveFileDto))]
[JsonSerializable(typeof(AuthSpecDto))]
[JsonSerializable(typeof(EnvSpecDto))]
[JsonSerializable(typeof(CollectionSummaryDto))]
[JsonSerializable(typeof(IReadOnlyList<CollectionSummaryDto>))]
[JsonSerializable(typeof(CollectionDetailDto))]
[JsonSerializable(typeof(CollectionSpecDto))]
[JsonSerializable(typeof(PostmanImportRequestDto))]
[JsonSerializable(typeof(PostmanImportResponseDto))]
[JsonSerializable(typeof(OpenApiUploadRequestDto))]
[JsonSerializable(typeof(OpenApiFetchRequestDto))]
[JsonSerializable(typeof(OpenApiDocumentDto))]
[JsonSerializable(typeof(OpenApiServerDto))]
[JsonSerializable(typeof(OpenApiSecuritySchemeDto))]
[JsonSerializable(typeof(OpenApiOperationDto))]
[JsonSerializable(typeof(OpenApiDiagnosticDto))]
[JsonSerializable(typeof(OpenApiImportRequestDto))]
[JsonSerializable(typeof(OpenApiImportResponseDto))]
[JsonSerializable(typeof(OpenApiLinkDto))]
[JsonSerializable(typeof(OpenApiSuggestRequestDto))]
[JsonSerializable(typeof(OpenApiSuggestionDto))]
[JsonSerializable(typeof(OpenApiSuggestResponseDto))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiSuggestionDto>))]
[JsonSerializable(typeof(OpenApiResyncRequestDto))]
[JsonSerializable(typeof(OpenApiChangeDto))]
[JsonSerializable(typeof(OpenApiResyncPreviewDto))]
[JsonSerializable(typeof(OpenApiDecisionDto))]
[JsonSerializable(typeof(OpenApiResyncApplyRequestDto))]
[JsonSerializable(typeof(OpenApiResyncResultDto))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiChangeDto>))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiDecisionDto>))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiServerDto>))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiSecuritySchemeDto>))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiOperationDto>))]
[JsonSerializable(typeof(IReadOnlyList<OpenApiDiagnosticDto>))]
[JsonSerializable(typeof(TaggedItemDto))]
[JsonSerializable(typeof(IReadOnlyList<TaggedItemDto>))]
[JsonSerializable(typeof(WorkspaceSpecDto))]
[JsonSerializable(typeof(RequestSpecDto))]
[JsonSerializable(typeof(AiStatusDto))]
[JsonSerializable(typeof(AiConfigDto))]
[JsonSerializable(typeof(SaveAiConfigDto))]
[JsonSerializable(typeof(AiCliDetectRequestDto))]
[JsonSerializable(typeof(AiCliDetectResponseDto))]
[JsonSerializable(typeof(AiTestResponseDto))]
[JsonSerializable(typeof(AiModelsResponseDto))]
[JsonSerializable(typeof(AiAssistRequestDto))]
[JsonSerializable(typeof(AiAssistResponseDto))]
[JsonSerializable(typeof(AiToolCallDto))]
[JsonSerializable(typeof(RenderRequestDto))]
[JsonSerializable(typeof(RenderedRequestDto))]
[JsonSerializable(typeof(ExecuteRequestDto))]
[JsonSerializable(typeof(ExecutionResultDto))]
[JsonSerializable(typeof(AuthStatusDto))]
[JsonSerializable(typeof(ExecuteStreamMetaDto))]
[JsonSerializable(typeof(ExecuteStreamBodyDto))]
[JsonSerializable(typeof(ExecuteStreamSseDto))]
[JsonSerializable(typeof(ExecuteStreamWsDto))]
[JsonSerializable(typeof(ExecuteStreamDoneDto))]
[JsonSerializable(typeof(ExecuteStreamErrorDto))]
[JsonSerializable(typeof(TlsDiagnosisDto))]
[JsonSerializable(typeof(GraphQLSchemaRequestDto))]
[JsonSerializable(typeof(GraphQLSchemaResponseDto))]
[JsonSerializable(typeof(FileUploadResponseDto))]
[JsonSerializable(typeof(Tap.Studio.Endpoints.GraphQLSchemaEndpoint.IntrospectionBody))]
[JsonSerializable(typeof(RawSourceDto))]
[JsonSerializable(typeof(WorkspaceErrorDto))]
[JsonSerializable(typeof(SaveSourceDto))]
[JsonSerializable(typeof(ParseHttpFileDto))]
[JsonSerializable(typeof(ParseHttpFileResultDto))]
[JsonSerializable(typeof(IReadOnlyList<RequestSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<AuthSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<EnvSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<TreeNodeDto>))]
[JsonSerializable(typeof(OidcDiscoveryDto))]
[JsonSerializable(typeof(AuthExecuteRequestDto))]
[JsonSerializable(typeof(AuthExecuteResponseDto))]
[JsonSerializable(typeof(AuthClearRequestDto))]
[JsonSerializable(typeof(VariableContextDto))]
[JsonSerializable(typeof(VariableViewDto))]
[JsonSerializable(typeof(CompileTemplateRequestDto))]
[JsonSerializable(typeof(CompileTemplateResponseDto))]
[JsonSerializable(typeof(SetVariableDto))]
[JsonSerializable(typeof(ProviderSummaryDto))]
[JsonSerializable(typeof(IReadOnlyList<ProviderSummaryDto>))]
[JsonSerializable(typeof(ProviderTypeDescriptorDto))]
[JsonSerializable(typeof(IReadOnlyList<ProviderTypeDescriptorDto>))]
[JsonSerializable(typeof(ProviderSettingFieldDto))]
[JsonSerializable(typeof(IReadOnlyList<ProviderSettingFieldDto>))]
[JsonSerializable(typeof(ProviderFieldOptionDto))]
[JsonSerializable(typeof(IReadOnlyList<ProviderFieldOptionDto>))]
[JsonSerializable(typeof(ProviderFieldVisibilityDto))]
[JsonSerializable(typeof(ProviderFieldNoteDto))]
[JsonSerializable(typeof(TestProviderRequestDto))]
[JsonSerializable(typeof(TestProviderResultDto))]
[JsonSerializable(typeof(ProviderVariableDto))]
[JsonSerializable(typeof(IReadOnlyList<ProviderVariableDto>))]
[JsonSerializable(typeof(ProviderVariableValueDto))]
[JsonSerializable(typeof(AzureSubscriptionDto))]
[JsonSerializable(typeof(AzureSubscriptionDto[]))]
[JsonSerializable(typeof(AzureKeyVaultDto))]
[JsonSerializable(typeof(AzureKeyVaultDto[]))]
[JsonSerializable(typeof(OnePasswordProbeRequestDto))]
[JsonSerializable(typeof(OnePasswordVaultDto))]
[JsonSerializable(typeof(OnePasswordVaultDto[]))]
[JsonSerializable(typeof(OnePasswordDetectResponseDto))]
[JsonSerializable(typeof(SystemSettingsDto))]
[JsonSerializable(typeof(SaveSystemSettingsDto))]
[JsonSerializable(typeof(SystemProviderDto))]
[JsonSerializable(typeof(SystemVariableDto))]
[JsonSerializable(typeof(IReadOnlyList<SystemProviderDto>))]
[JsonSerializable(typeof(IReadOnlyList<SystemVariableDto>))]
[JsonSerializable(typeof(IReadOnlyList<BrowserOptionDto>))]
[JsonSerializable(typeof(OpenBrowserRequestDto))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class StudioJson : JsonSerializerContext
{
}

// ----- Browser discovery (OAuth "open sign-in" browser + profile picker) -----

/// <summary>A launchable profile within a browser (e.g. a Chrome "Default"/"Profile 1" dir).</summary>
public sealed record BrowserProfileDto(string Key, string Label, bool IsDefault);

/// <summary>A browser installed on the host, with its discoverable profiles.</summary>
public sealed record BrowserOptionDto(
    string Id,
    string Label,
    bool Available,
    bool SupportsProfiles,
    IReadOnlyList<BrowserProfileDto> Profiles);

/// <summary>Request to open a URL in a specific browser + profile (both null = system default).</summary>
public sealed record OpenBrowserRequestDto(string Url, string? Browser, string? Profile);
