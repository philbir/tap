using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;

namespace Tap.Execution.Auth;

/// <summary>
/// Expands <c>{{var}}</c> + <c>{{provider:name}}</c> references inside auth-profile fields.
/// An auth profile isn't bound to a request, so the cascade here is the renderer's minus the
/// request layer: workspace &lt; collection &lt; stage &lt; env. The collection/stage layers
/// are only populated for a profile that lives under <c>collections/&lt;slug&gt;/</c> — that's
/// the whole point of putting one there, so its endpoints and client ids can come from the
/// collection's (or the active stage's) variables. Workspace-scoped profiles under
/// <c>auth/</c> see workspace &lt; env exactly as before. The provider registry is consulted
/// for anything not in the cascade.
///
/// Without this pass, a tokenUrl like <c>http://{{DEMO_API_URL}}/connect/token</c> would
/// be sent verbatim and the runner would 500 with an invalid-URI.
///
/// <para>Every layer — including the env — comes off the <see cref="AuthContext"/>. It used to
/// take the env as a separate argument, which let a caller build the cascade from one env and
/// the registry from another; the registry's provider bindings and the cascade have to agree
/// or <c>{{kv:secret}}</c> resolves against a vault the selected env never named.</para>
/// </summary>
public sealed class AuthFieldResolver
{
    private readonly VariableProviderRegistry _registry;
    private readonly IReadOnlyDictionary<string, string> _cascade;

    public AuthFieldResolver(
        LoadedWorkspace workspace,
        VariableProviderRegistry registry,
        AuthContext context)
    {
        _registry = registry;

        // workspace < collection < stage < env. Skipped nulls so an unset value doesn't
        // write the literal "null".
        var cascade = new Dictionary<string, string>(StringComparer.Ordinal);
        if (workspace.Manifest is not null) Merge(cascade, workspace.Manifest.Vars);
        if (context.Collection is not null) Merge(cascade, context.Collection.Vars);
        if (context.Stage is not null) Merge(cascade, context.Stage.Vars);
        if (context.Env is not null) Merge(cascade, context.Env.Vars);
        _cascade = cascade;
    }

    private static void Merge(Dictionary<string, string> dest, IReadOnlyDictionary<string, VarSpec> src)
    {
        foreach (var (k, v) in src)
            if (v.Default is { } d) dest[k] = d;
    }

    /// <summary>Expand <c>{{var}}</c> + <c>{{provider:name}}</c> in one call. Async because
    /// providers can be remote (azkv).</summary>
    public async ValueTask<string?> AllAsync(string? input, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return await Interpolation.ExpandAsync(input!, _cascade, _registry, ct).ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="AllAsync(string?, CancellationToken)"/> but expanded for
    /// every entry in a list — used for <c>scopes</c>.</summary>
    public async ValueTask<IReadOnlyList<string>> AllAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        if (input.Count == 0) return input;
        var result = new string[input.Count];
        for (int i = 0; i < input.Count; i++)
            result[i] = await AllAsync(input[i], ct).ConfigureAwait(false) ?? string.Empty;
        return result;
    }
}
