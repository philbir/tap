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

    /// <summary>Workspace-relative path with forward slashes (e.g. <c>collections/customer/create.req.tap</c>).</summary>
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

    /// <summary>Caps on how much of a response body is delivered inline and how much is
    /// retained for a later "show all" / download. Workspace-wide — a response limit is a
    /// property of the machine doing the reading, not of one request.</summary>
    public ResponseLimits Response { get; init; } = new();

    /// <summary>Workspace-wide default for recording exchanges to <c>.tap-history/</c>.
    /// Weakest tier — a collection or a request overrides it per key. This is also the only
    /// scope that can set <see cref="HistoryOptions.OrphanRetentionDays"/>.</summary>
    public HistoryOptions History { get; init; } = new();
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

/// <summary>
/// One collection an <see cref="EnvFile"/> is assigned to, plus what the environment changes
/// about that collection while it is active.
///
/// <para>The overrides live here rather than on the environment because they are inherently
/// per-collection: an <c>uat</c> environment assigned to both <c>orders</c> and <c>billing</c>
/// points each at a different host. An environment-wide <c>baseUrl</c> could only ever be
/// right for one of them.</para>
/// </summary>
public sealed record EnvCollectionBinding
{
    /// <summary>Collection slug — the directory name under <c>collections/</c>.</summary>
    public required string Collection { get; init; }

    /// <summary>Replaces the collection's <c>baseUrl</c> while this env is active, or
    /// <c>null</c> to inherit it. May contain <c>{{vars}}</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Replaces the collection's <c>defaultAuth</c> while this env is active, or
    /// <c>null</c> to inherit it. Resolved relative to the <em>env file's</em> directory,
    /// since that is the file the ref is written in.</summary>
    public WorkspaceRef? DefaultAuth { get; init; }

    /// <summary>True when the assignment carries no overrides — the env contributes only its
    /// variables here. Emitted as a bare slug rather than a mapping.</summary>
    public bool IsBare => BaseUrl is null && DefaultAuth is null;
}

/// <summary>
/// A named environment — the single mechanism for "the same requests, pointed somewhere else".
///
/// <para>An environment is either <b>global</b> (<see cref="Collections"/> empty), selectable
/// anywhere in the workspace, or <b>assigned</b> to specific collections, offered only while a
/// request from one of those is in front of you. An assignment is what a collection
/// <c>stage</c> used to be: alongside the env tier of the variable cascade it may override that
/// collection's base URL and default auth — see <see cref="EnvCollectionBinding"/>.</para>
///
/// <para>A global environment therefore carries variables and provider bindings only. There is
/// deliberately no environment-wide base URL: that would move every collection at once, which
/// is never what "the dev environment" means.</para>
/// </summary>
public sealed record EnvFile : WorkspaceFile
{
    /// <summary>Environment-scoped variables. Each entry can be either a literal value or
    /// a richer <see cref="VarSpec"/> with the <see cref="VarSpec.Secret"/> flag set —
    /// the UI displays secret values as <c>***</c> in catalogs and autocomplete.</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>The collections this environment is assigned to, each with its own overrides.
    /// Empty means global — the env is selectable everywhere and overrides nothing. A non-empty
    /// list confines it to those collections, which is what makes a per-collection
    /// <c>dev</c>/<c>uat</c>/<c>prod</c> set possible without every collection's environments
    /// crowding the workspace picker.</summary>
    public IReadOnlyList<EnvCollectionBinding> Collections { get; init; } = [];

    /// <summary>True when no collection was assigned, so the env applies workspace-wide.</summary>
    public bool IsGlobal => Collections.Count == 0;

    /// <summary>Whether this env may be applied to a request owned by <paramref name="slug"/>.
    /// A global env applies to everything, including requests that belong to no collection at
    /// all; an assigned one only to the collections it names.</summary>
    public bool AppliesTo(string? slug) => IsGlobal || BindingFor(slug) is not null;

    /// <summary>This env's assignment to <paramref name="slug"/>, or <c>null</c> when it has
    /// none — which is the case for every collection when the env is global. Callers reading an
    /// override must go through here: the same env means a different base URL in each
    /// collection it is assigned to.</summary>
    public EnvCollectionBinding? BindingFor(string? slug)
        => slug is null
            ? null
            : Collections.FirstOrDefault(b => string.Equals(b.Collection, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Provider that bare <c>{{name}}</c> lookups consult first (and that receives
    /// un-targeted writes) while this env is active. Overrides the workspace manifest's and
    /// the system-level default. May name a provider directly or through
    /// <see cref="ProviderAliases"/>. This is what lets each environment point at its own
    /// Key Vault while requests stay unchanged.</summary>
    public string? DefaultVariableProvider { get; init; }

    /// <summary>Alias → provider-name map applied while this env is active. Lets requests
    /// use a stable prefix (<c>{{kv:secret}}</c>) whose target vault is chosen per env
    /// (<c>kv: kv-dev</c> in dev.env.tap, <c>kv: kv-prod</c> in prod.env.tap). Providers are
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
/// A top-level grouping under <c>collections/</c>. A collection owns the base URL, default
/// auth, default headers, collection-scoped variables, display name, and tags — what used to
/// live on a separate <c>ApiFile</c> in <c>apis/</c>. Its file lives at
/// <c>collections/&lt;slug&gt;/_collection.tap</c>; nested directories below it are pure
/// grouping (no metadata). Sits between workspace and env in the variable cascade.
///
/// <para>Per-target overrides — the old <c>stages:</c> block — are now
/// <see cref="EnvFile"/>s that name this collection in their <c>collections:</c> list.</para>
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
    /// (workspace &lt; <b>collection</b> &lt; env &lt; request).</summary>
    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>Whether agents may use this collection — see <see cref="CollectionAgentOptions"/>.</summary>
    public CollectionAgentOptions Agent { get; init; } = new();

    /// <summary>Recording policy inherited by every request in this collection. Overrides the
    /// workspace manifest per key; a request overrides it in turn.</summary>
    public HistoryOptions History { get; init; } = new();

}

/// <summary>
/// Agent-surface policy for a collection — the <c>agent:</c> frontmatter key on
/// <c>_collection.tap</c>. Written as a bare bool (<c>agent: false</c>) or a mapping
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

public sealed record RequestFile : WorkspaceFile
{
    public WorkspaceRef? Auth { get; init; }

    /// <summary>
    /// Explicit owning-collection slug, set by a <c>.http</c> file's <c># @tap-collection</c>
    /// directive. Null means the usual rule applies — the collection is whichever one the file
    /// sits under in <c>collections/</c>.
    ///
    /// <para>This exists because a portable <c>.http</c> file's whole appeal is living next to
    /// the code it exercises rather than being filed into Tap's directory layout. The directive
    /// lets such a file claim a collection's baseUrl, headers, auth, and environments from
    /// anywhere in the repo.</para>
    /// </summary>
    public string? CollectionRef { get; init; }

    /// <summary>Wire protocol — <c>http</c> (default) or <c>websocket</c>. Set from the
    /// <c>protocol:</c> frontmatter field. Drives baseUrl scheme normalization in the
    /// renderer and transport selection in the executor.</summary>
    public RequestProtocol Protocol { get; init; } = RequestProtocol.Http;

    /// <summary>Request-specific transport overrides. Unset fields inherit from the collection.</summary>
    public RequestTransportSettings Transport { get; init; } = new();

    /// <summary>Recording policy for this request alone. Strongest tier — unset fields inherit
    /// from the collection, then the workspace manifest.</summary>
    public HistoryOptions History { get; init; } = new();

    /// <summary>The single fenced <c>http</c> block carried by the body, verbatim (interpolations un-expanded).</summary>
    public string HttpBlock { get; init; } = string.Empty;

    /// <summary>Line number (1-based) where the <c>http</c> block opens in the source file. Used in error messages.</summary>
    public int HttpBlockStartLine { get; init; }

    public IReadOnlyDictionary<string, VarSpec> Vars { get; init; } =
        new Dictionary<string, VarSpec>();

    /// <summary>
    /// Variables the request brought with it from a portable source — the <c>@name = value</c>
    /// lines of a <c>.http</c> file. Always empty for a Tap-authored <c>.req.tap</c>.
    ///
    /// <para><b>These are the weakest scope in the cascade, below the workspace manifest.</b>
    /// That inversion is the whole point: a <c>.http</c> file is expected to run in Visual
    /// Studio and REST Client too, where its <c>@baseUrl</c> is the only definition there is.
    /// Inside Tap the workspace, the collection, and the environment are deliberate
    /// configuration and must win, or selecting an environment would silently do nothing to a
    /// file that carries its own fallback. See §5.7 of <c>docs/workspace-format.md</c>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, VarSpec> PortableVars { get; init; } =
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
