namespace Tap.Execution.Contracts;

// The discovery surface an agent (CLI --json, MCP tool, Studio endpoint) reads before it
// runs anything: what exists in the workspace, what a request will do, and what it needs.
// Same rules as the rest of this folder — these are public wire shapes, so changes here are
// breaking changes to every front end. One deliberate constraint throughout: nothing in
// these DTOs ever carries a resolved value. Templates ({{token}} text) are file content and
// safe to show; auth profiles surface as name + type only, never fields.

public sealed record WorkspaceInventoryDto(
    string? Name,
    /// <summary>Workspace-relative path of the manifest's <c>defaultEnv</c>, or null.</summary>
    string? DefaultEnv,
    IReadOnlyList<CollectionSummaryDto> Collections,
    IReadOnlyList<RequestSummaryDto> Requests,
    IReadOnlyList<EnvSummaryDto> Envs,
    /// <summary>Test sets and flows — everything <c>test</c> can run.</summary>
    IReadOnlyList<TestTargetSummaryDto> Tests,
    IReadOnlyList<AuthSummaryDto> Auths);

public sealed record CollectionSummaryDto(
    string Name,
    string Path,
    /// <summary>The baseUrl template as written — may carry <c>{{tokens}}</c>.</summary>
    string? BaseUrl,
    IReadOnlyList<string> Stages,
    string? DefaultStage,
    IReadOnlyList<string> Tags,
    int RequestCount);

public sealed record RequestSummaryDto(
    string Name,
    string Path,
    /// <summary>Path of the owning collection's <c>_collection.md</c>, or null for a request
    /// outside any collection.</summary>
    string? Collection,
    string Method,
    /// <summary>The URL as written in the http block — unexpanded, possibly relative.</summary>
    string UrlTemplate,
    string Protocol,
    /// <summary>Display name of the effective auth profile (request &lt; stage &lt; collection
    /// default), or null when the request goes out anonymous.</summary>
    string? Auth,
    string? AuthType,
    IReadOnlyList<string> Tags,
    int AssertionCount);

public sealed record EnvSummaryDto(string Name, string Path, bool IsDefault);

public sealed record TestTargetSummaryDto(
    string Name,
    string Path,
    /// <summary><c>test</c> or <c>flow</c> — same vocabulary as <see cref="TestRunResultDto.Kind"/>.</summary>
    string Kind,
    IReadOnlyList<string> Tags,
    /// <summary>Tests in a set, steps in a flow.</summary>
    int EntryCount);

/// <summary>Name + type only, by design: knowing a profile exists is what an agent needs to
/// pick it; what's inside it is exactly what an agent must never see.</summary>
public sealed record AuthSummaryDto(string Name, string Path, string Type, IReadOnlyList<string> Tags);

/// <summary>Everything an agent needs to call one request well — or to decide it wants the
/// dynamic path instead. All template text, nothing resolved.</summary>
public sealed record RequestDescriptionDto(
    string Name,
    string Path,
    string? Collection,
    string Method,
    string UrlTemplate,
    string Protocol,
    /// <summary>Headers as written in the http block (collection defaults not merged in —
    /// those are listed on the collection).</summary>
    IReadOnlyList<HeaderTemplateDto> Headers,
    string? BodyTemplate,
    string? Auth,
    string? AuthType,
    /// <summary>Stage names available on the owning collection.</summary>
    IReadOnlyList<string> Stages,
    /// <summary>Unprefixed <c>{{name}}</c> tokens the http block and the collection baseUrl
    /// reference — the variables a caller may want to override per run.</summary>
    IReadOnlyList<string> VariablesReferenced,
    /// <summary>Variable names the request file declares defaults for.</summary>
    IReadOnlyList<string> DeclaredVars,
    /// <summary>One human-readable line per declared assertion.</summary>
    IReadOnlyList<string> Assertions,
    IReadOnlyList<string> Tags);

public sealed record HeaderTemplateDto(string Name, string Value);
