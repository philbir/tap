using System.Text.Json.Serialization;
using Tap.Workspace.Model;

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
    IReadOnlyList<WorkspaceErrorDto> Errors);

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

public sealed record WorkspaceErrorDto(string Code, string Message, string? Path, int? Line);

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
    string Protocol);

public sealed record AuthDetailDto(
    string Path,
    string Name,
    string? Id,
    string Type,
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
    string Source);

/// <summary>Listing-row representation of a collection. <see cref="Exists"/> is false when
/// the directory <c>collections/&lt;slug&gt;/</c> is present on disk but has no
/// <c>_collection.md</c> yet — the editor uses that to render a create-on-save form.</summary>
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
/// <c>collections/&lt;slug&gt;/_collection.md</c>. Collections own the baseUrl, optional
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
    IReadOnlyDictionary<string, VarSpec> Vars,
    IReadOnlyList<string> Tags,
    string Body,
    string Source,
    IReadOnlyList<CollectionStageDto> Stages,
    string? DefaultStage);

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
    public IReadOnlyDictionary<string, string>? Vars { get; init; }
    /// <summary>Variable names marked secret. Same encoding as <see cref="EnvSpecDto.Secrets"/>.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<CollectionStageSpecDto>? Stages { get; init; }
    public string? DefaultStage { get; init; }
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

/// <summary>Body for <c>POST /api/auth/execute</c>.</summary>
public sealed record AuthExecuteRequestDto(string Path, bool ForceReauthenticate);

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

public sealed record AuthSummaryDto(string Path, string Name, string? Id, string Type);

public sealed record EnvSummaryDto(string Path, string Name, string? Id);

public sealed record SaveFileDto(string Content);

/// <summary>
/// Raw-source save. The Source tab in every editor lets the user hand-edit the file's
/// canonical YAML directly; the server validates the content with FileParser before
/// writing so a broken file never lands on disk.
/// </summary>
public sealed record SaveSourceDto(string Path, string Content);

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

    // jwt
    public string? JwtAlgorithm { get; init; }
    public string? JwtKey { get; init; }
    public string? JwtKeyId { get; init; }
    public string? JwtIssuer { get; init; }
    public string? JwtAudience { get; init; }
    public string? JwtSubject { get; init; }
    public int? JwtExpiresIn { get; init; }
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
}

public sealed record HttpHeaderSpecDto(string Name, string Value);

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
    string? DefaultProvider);

public sealed record VariableSetDto(
    string Scope,
    string SourcePath,
    string Label,
    int Count,
    string? ProviderName,
    IReadOnlyList<VariableDto> Variables);

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
    string? VariableProvider);

public sealed record ProviderSummaryDto(
    string Name,
    string Type,
    string Mode,
    string Origin,
    IReadOnlyDictionary<string, string?> Settings,
    int? VariableCount,
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
    IReadOnlyList<SystemVariableDto> Variables,
    IReadOnlyList<string> AvailableProviderTypes);

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

public sealed record ExecuteRequestDto(
    string Path,
    string? Env,
    IReadOnlyDictionary<string, string>? Overrides,
    string? Stage);

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
    string Protocol);

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
    string? Error);

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
[JsonSerializable(typeof(TaggedItemDto))]
[JsonSerializable(typeof(IReadOnlyList<TaggedItemDto>))]
[JsonSerializable(typeof(WorkspaceSpecDto))]
[JsonSerializable(typeof(RequestSpecDto))]
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
[JsonSerializable(typeof(GraphQLSchemaRequestDto))]
[JsonSerializable(typeof(GraphQLSchemaResponseDto))]
[JsonSerializable(typeof(FileUploadResponseDto))]
[JsonSerializable(typeof(Tap.Studio.Endpoints.GraphQLSchemaEndpoint.IntrospectionBody))]
[JsonSerializable(typeof(WorkspaceErrorDto))]
[JsonSerializable(typeof(SaveSourceDto))]
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
[JsonSerializable(typeof(SystemSettingsDto))]
[JsonSerializable(typeof(SaveSystemSettingsDto))]
[JsonSerializable(typeof(SystemProviderDto))]
[JsonSerializable(typeof(SystemVariableDto))]
[JsonSerializable(typeof(IReadOnlyList<SystemProviderDto>))]
[JsonSerializable(typeof(IReadOnlyList<SystemVariableDto>))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class StudioJson : JsonSerializerContext
{
}
