using Tap.Workspace.Variables;

namespace Tap.Workspace.Model;

/// <summary>
/// Base type for every parsed workspace artifact. Each concrete subtype corresponds
/// to one <see cref="WorkspaceKind"/>. All fields except <see cref="Kind"/>,
/// <see cref="RelativePath"/>, and <see cref="Body"/> mirror exactly the frontmatter
/// fields documented in <c>docs/workspace-format.md</c>.
/// </summary>
public abstract record WorkspaceFile
{
    public required WorkspaceKind Kind { get; init; }

    /// <summary>Workspace-relative path with forward slashes (e.g. <c>collections/customer/create.req.md</c>).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Stable id (UUIDv7). Auto-assigned by the writer on first save if absent.</summary>
    public string? Id { get; init; }

    /// <summary>Display name. Defaults to the filename stem when absent.</summary>
    public string? Name { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Markdown body, unparsed. Documentation for humans; for <see cref="RequestFile"/>
    /// the embedded fenced <c>http</c> block is the executable part — see <see cref="RequestFile.HttpBlock"/>.</summary>
    public string Body { get; init; } = string.Empty;
}

public sealed record WorkspaceManifestFile : WorkspaceFile
{
    public WorkspaceRef? DefaultEnv { get; init; }

    /// <summary>Workspace-scoped variable providers. Composed with system-scoped providers
    /// (from app config) by <see cref="VariableProviderRegistry"/>. Each entry has a unique
    /// name, a <c>type</c> resolvable to a registered <see cref="IVariableProviderFactory"/>,
    /// and a free-form settings bag. Mode is a static property of the provider type, not
    /// configured here. Same-named workspace and system providers cause the workspace entry
    /// to shadow the system one for this workspace.</summary>
    public IReadOnlyList<VariableProviderConfig> VariableProviders { get; init; } = [];

    /// <summary>Variable-provider name to use for writes when an API call doesn't target a
    /// specific provider. <c>null</c> means "use the first ReadWrite provider in registration
    /// order" — the registry resolves this lazily.</summary>
    public string? DefaultVariableProvider { get; init; }

    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();
}

public sealed record AuthFile : WorkspaceFile
{
    public required string Type { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Query { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string> Scopes { get; init; } = [];
}

public sealed record EnvFile : WorkspaceFile
{
    /// <summary>Environment-scoped variables. Each entry can be either a literal value or
    /// a richer <see cref="VarSpec"/> with the <see cref="VarSpec.Secret"/> flag set —
    /// the UI displays secret values as <c>***</c> in catalogs and autocomplete.</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>Provider that bare <c>{{name}}</c> lookups consult first (and that receives
    /// un-targeted writes) while this env is active. Overrides the workspace manifest's and
    /// the system-level default. May name a provider directly or through
    /// <see cref="ProviderAliases"/>. This is what lets each environment point at its own
    /// Key Vault while requests stay unchanged.</summary>
    public string? DefaultVariableProvider { get; init; }

    /// <summary>Alias → provider-name map applied while this env is active. Lets requests
    /// use a stable prefix (<c>{{kv:secret}}</c>) whose target vault is chosen per env
    /// (<c>kv: kv-dev</c> in dev.env.md, <c>kv: kv-prod</c> in prod.env.md). Providers are
    /// still declared once at workspace/system scope; the env only points at them.</summary>
    public IReadOnlyDictionary<string, string> ProviderAliases { get; init; } =
        new Dictionary<string, string>();

    /// <summary>When true (and <see cref="DefaultVariableProvider"/> is set), bare
    /// <c>{{name}}</c> lookups that miss the default provider fail instead of falling
    /// through to the remaining providers. Guards per-env vault setups against silently
    /// reading a same-named secret from another environment's vault.</summary>
    public bool StrictVariables { get; init; }
}

/// <summary>
/// A top-level grouping under <c>collections/</c>. A collection owns the base URL and the
/// optional <see cref="Stages"/>, default auth, default headers, collection-scoped variables,
/// display name, and tags — what used to live on a separate <c>ApiFile</c> in <c>apis/</c>.
/// Its file lives at <c>collections/&lt;slug&gt;/_collection.md</c>; nested directories
/// below it are pure grouping (no metadata). Sits between workspace and env in the variable
/// cascade.
/// </summary>
public sealed record CollectionFile : WorkspaceFile
{
    /// <summary>Base URL used when a request in this collection writes a relative path.
    /// May be empty when the collection's requests use absolute URLs.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Fallback auth profile applied to requests that don't pin one themselves.</summary>
    public WorkspaceRef? DefaultAuth { get; init; }

    /// <summary>Headers merged into every request from this collection. Overridden by the
    /// request's own block headers; overridden again by auth-derived headers.</summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Transport defaults inherited by requests in this collection unless the request overrides them.</summary>
    public RequestTransportSettings Transport { get; init; } = new();

    /// <summary>Collection-scoped variables. Cascade tier between workspace and env
    /// (workspace &lt; <b>collection</b> &lt; stage &lt; env &lt; request).</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>
    /// Named environments inside this collection (e.g. <c>dev</c>, <c>staging</c>, <c>prod</c>).
    /// Each stage can override <see cref="BaseUrl"/>, <see cref="DefaultAuth"/>, and
    /// <see cref="Vars"/>; unset fields fall through to the collection defaults. Stages
    /// don't carry their own header overrides — vary headers across stages by referencing
    /// stage-scoped vars in <see cref="DefaultHeaders"/>. Renderer treats the stage as a
    /// tier between Collection and Env in the variable cascade
    /// (workspace &lt; collection &lt; <b>stage</b> &lt; env &lt; request).
    /// </summary>
    public IReadOnlyList<CollectionStage> Stages { get; init; } = [];

    /// <summary>Name of the stage to preselect in the UI when no stage is explicitly chosen.</summary>
    public string? DefaultStage { get; init; }

    /// <summary>Whether agents may use this collection — see <see cref="CollectionAgentOptions"/>.</summary>
    public CollectionAgentOptions Agent { get; init; } = new();

    public CollectionStage? FindStage(string? name)
        => string.IsNullOrEmpty(name) ? null : Stages.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Agent-surface policy for a collection — the <c>agent:</c> frontmatter key on
/// <c>_collection.md</c>. Written as a bare bool (<c>agent: false</c>) or a mapping
/// (<c>agent: { enabled: false }</c>); the mapping form is the extension point for more
/// granular control later (allowed methods, dynamic-request opt-out, …). Enabled by
/// default: the option exists to fence specific collections off from agents, not to make
/// every workspace opt in.
///
/// <para>This is policy, not a sandbox — it governs what the sanctioned agent surfaces
/// (MCP tools, <c>call</c>, agent discovery) will do, and humans using the Studio or the
/// CLI's developer commands are deliberately not affected by it.</para>
/// </summary>
public sealed record CollectionAgentOptions
{
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// A named environment inside a <see cref="CollectionFile"/>. Provides per-stage overrides
/// for the baseUrl, auth, and variables. Variables here override workspace + collection
/// variables but are themselves overridden by env/request scopes.
/// </summary>
public sealed record CollectionStage
{
    /// <summary>Stage identifier (e.g. <c>dev</c>, <c>staging</c>, <c>prod</c>). Required, unique within the collection.</summary>
    public required string Name { get; init; }

    /// <summary>Optional override for the collection's baseUrl. <c>null</c> means "inherit".</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Optional override for the collection's defaultAuth. <c>null</c> means "inherit".</summary>
    public WorkspaceRef? DefaultAuth { get; init; }

    /// <summary>Stage-scoped variables. Override workspace+collection defaults; can carry <c>${{secret}}</c> refs.</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();
}

public sealed record RequestFile : WorkspaceFile
{
    public WorkspaceRef? Auth { get; init; }

    /// <summary>Wire protocol — <c>http</c> (default) or <c>websocket</c>. Set from the
    /// <c>protocol:</c> frontmatter field. Drives baseUrl scheme normalization in the
    /// renderer and transport selection in the executor.</summary>
    public RequestProtocol Protocol { get; init; } = RequestProtocol.Http;

    /// <summary>Request-specific transport overrides. Unset fields inherit from the collection.</summary>
    public RequestTransportSettings Transport { get; init; } = new();

    /// <summary>The single fenced <c>http</c> block carried by the body, verbatim (interpolations un-expanded).</summary>
    public string HttpBlock { get; init; } = string.Empty;

    /// <summary>Line number (1-based) where the <c>http</c> block opens in the source file. Used in error messages.</summary>
    public int HttpBlockStartLine { get; init; }

    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>Declared expectations about the response, evaluated after every execution.
    /// Assertions never change what is sent and never fail the exchange — they annotate the
    /// result. See §5.5 of <c>docs/workspace-format.md</c>.</summary>
    public IReadOnlyList<AssertSpec> Assertions { get; init; } = [];
}

/// <summary>Optional HTTP transport controls. A null value means inherit/use the executor default.</summary>
public sealed record RequestTransportSettings
{
    /// <summary>When true, accept otherwise-invalid TLS certificates. Keep false except for trusted development endpoints.</summary>
    public bool? IgnoreTlsErrors { get; init; }

    /// <summary>Overall request timeout in milliseconds. Null keeps the executor default; zero disables the timeout.</summary>
    public int? TimeoutMs { get; init; }
}
