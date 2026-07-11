using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;

namespace Tap.Studio.Auth;

/// <summary>
/// Expands <c>{{var}}</c> + <c>{{provider:name}}</c> references inside auth-profile fields.
/// Auth profiles aren't bound to a request, so the cascade here is just workspace + default env
/// — the same prefix the renderer would build for a request, minus the api/stage/request
/// layers. The provider registry is consulted for anything not in the cascade.
///
/// Without this pass, a tokenUrl like <c>http://{{DEMO_API_URL}}/connect/token</c> would
/// be sent verbatim and the runner would 500 with an invalid-URI.
/// </summary>
public sealed class AuthFieldResolver
{
    private readonly LoadedWorkspace _workspace;
    private readonly VariableProviderRegistry _registry;
    private readonly IReadOnlyDictionary<string, string> _cascade;

    public AuthFieldResolver(LoadedWorkspace workspace, VariableProviderRegistry registry, EnvFile? env)
    {
        _workspace = workspace;
        _registry = registry;

        // workspace < env. Skipped nulls so an unset value doesn't write the literal "null".
        var cascade = new Dictionary<string, string>(StringComparer.Ordinal);
        if (workspace.Manifest is not null)
        {
            foreach (var (k, v) in workspace.Manifest.Vars)
                if (v.Default is { } d) cascade[k] = d;
        }
        if (env is not null)
        {
            foreach (var (k, v) in env.Vars)
                if (v.Default is { } d) cascade[k] = d;
        }
        _cascade = cascade;
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
