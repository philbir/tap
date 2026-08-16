using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Builds the layered + merged variable view for a given editor context. There are two
/// flavors of layer:
/// <list type="bullet">
///   <item><b>Cascade layers</b> — workspace/collection/stage/env/request file vars.
///     These come out of the loaded workspace and override one another in scope order.</item>
///   <item><b>Provider layers</b> — one per registered <see cref="IVariableProvider"/> (env,
///     file, azkv, …). Asynchronously enumerated; the UI sees provider name and isSecret per
///     variable. Provider-scoped values can be masked individually based on the provider's
///     own classification of each entry.</item>
/// </list>
///
/// <para>The merged view applies overwrite-by-name: cascade layers win over provider layers
/// (most specific to least), and within each kind the documented ordering applies (later
/// scope wins / first provider wins). A variable's <see cref="Variable.IsSensitive"/> flag
/// is inherited from whichever source produced the winning value.</para>
/// </summary>
public static class VariableViewBuilder
{
    public static async ValueTask<VariableView> BuildAsync(
        LoadedWorkspace workspace,
        VariableProviderRegistry registry,
        CancellationToken ct,
        string? requestPath = null,
        string? collectionPath = null,
        string? envPath = null,
        string? stageName = null)
    {
        var sets = new List<VariableSet>();

        // Provider layers — listed first so the UI shows external sources distinctly. The
        // cascade layers come right after; the merge phase below applies the actual
        // precedence (cascade wins).
        foreach (var provider in registry.Providers)
        {
            IReadOnlyList<VariableValue> entries;
            try
            {
                entries = await provider.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                entries = [];
            }

            var providerVariables = entries
                .Select(v => new Variable(
                    Name: v.Name,
                    Value: v.IsSecret ? null : v.Value,
                    IsSensitive: v.IsSecret,
                    Scope: VariableScope.Provider,
                    SourcePath: "provider:" + provider.Name,
                    ProviderName: provider.Name))
                .ToList();

            sets.Add(new VariableSet(
                Scope: VariableScope.Provider,
                SourcePath: "provider:" + provider.Name,
                Label: provider.Name,
                ProviderName: provider.Name,
                Variables: providerVariables));
        }

        // Portable layer first: a .http file's own `@name = value` lines are the weakest scope,
        // so listing (and merging) them before the workspace is what makes the panel agree with
        // the renderer about which definition actually wins.
        if (requestPath is not null && workspace.FindByPath(requestPath) is RequestFile portableReq
            && portableReq.PortableVars.Count > 0)
        {
            sets.Add(BuildSet(
                VariableScope.Portable, portableReq.RelativePath,
                Path.GetFileName(HttpFragment.FilePath(portableReq.RelativePath)),
                portableReq.PortableVars));
        }

        if (workspace.Manifest is { } manifest)
        {
            sets.Add(BuildSet(VariableScope.Workspace, manifest.RelativePath, manifest.Name ?? "workspace", manifest.Vars));
        }

        CollectionFile? collection = null;
        if (collectionPath is not null)
        {
            collection = workspace.FindByPath(collectionPath) as CollectionFile;
        }
        else if (requestPath is not null)
        {
            collection = CollectionLocator.ForRequestPath(workspace, requestPath);
        }

        if (collection is not null)
        {
            var label = collection.Name ?? Path.GetFileName(Path.GetDirectoryName(collection.RelativePath) ?? string.Empty);
            sets.Add(BuildSet(VariableScope.Collection, collection.RelativePath, label, collection.Vars));

            var stage = collection.FindStage(stageName) ?? collection.FindStage(collection.DefaultStage);
            if (stage is not null)
            {
                sets.Add(BuildSet(VariableScope.Stage, collection.RelativePath + "#" + stage.Name, stage.Name, stage.Vars));
            }
        }

        EnvFile? env = null;
        if (envPath is not null)
        {
            env = workspace.FindByPath(envPath) as EnvFile;
        }
        else if (workspace.Manifest?.DefaultEnv is { } defaultRef)
        {
            env = workspace.Resolve(defaultRef) as EnvFile;
        }
        if (env is not null)
        {
            sets.Add(BuildSet(VariableScope.Env, env.RelativePath, env.Name ?? Path.GetFileNameWithoutExtension(env.RelativePath), env.Vars));
        }

        if (requestPath is not null && workspace.FindByPath(requestPath) is RequestFile req2)
        {
            sets.Add(BuildSet(VariableScope.Request, req2.RelativePath, req2.Name ?? Path.GetFileNameWithoutExtension(req2.RelativePath), req2.Vars));
        }

        // The built-in `baseUrl`, mirroring WorkspaceRenderer.BindBaseUrlAsync: bound only when no
        // *Tap* scope claimed the name, and carrying the raw template (the panel resolves tokens
        // itself, exactly as the collection chip does). Without it, a portable `{{baseUrl}}` would
        // paint as an unknown variable in the very editors this feature exists to serve.
        //
        // Portable is excluded from the check on purpose, and it is the case that matters: a file
        // declaring its own `@baseUrl` is precisely when the built-in has to win, or this panel
        // would report the standalone fallback as the winner while the renderer sends elsewhere.
        if (collection is not null && !sets.Any(s => s.Scope is not (VariableScope.Provider or VariableScope.Portable)
                                                  && s.Variables.Any(v => v.Name == WorkspaceRenderer.BaseUrlVariable)))
        {
            var activeStage = collection.FindStage(stageName) ?? collection.FindStage(collection.DefaultStage);
            var template = !string.IsNullOrWhiteSpace(activeStage?.BaseUrl) ? activeStage!.BaseUrl : collection.BaseUrl;
            if (!string.IsNullOrWhiteSpace(template))
            {
                sets.Add(new VariableSet(
                    VariableScope.Collection, collection.RelativePath, "baseUrl", ProviderName: null,
                    [new Variable(
                        WorkspaceRenderer.BaseUrlVariable, template!.TrimEnd('/'), IsSensitive: false,
                        VariableScope.Collection, collection.RelativePath, ProviderName: null)]));
            }
        }

        // Merged result mirrors what a bare {{name}} token actually resolves to. Provider
        // precedence is the registry's: default provider first, then registration order,
        // FIRST hit wins — so we merge in reverse precedence (default last) and let higher
        // precedence overwrite. With a strict env binding, bare tokens can only reach the
        // default provider, so other providers stay out of the merged result entirely
        // (their sets above remain browsable and reachable via explicit {{provider:name}}).
        var merged = new Dictionary<string, Variable>(StringComparer.Ordinal);

        var providerSets = sets.Where(s => s.Scope == VariableScope.Provider).ToList();
        var defaultSet = registry.DefaultProviderName is { } defaultName
            ? providerSets.FirstOrDefault(s => string.Equals(s.ProviderName, defaultName, StringComparison.OrdinalIgnoreCase))
            : null;

        IEnumerable<VariableSet> providerMergeOrder;
        if (registry.StrictVariables && defaultSet is not null)
        {
            providerMergeOrder = [defaultSet];
        }
        else
        {
            providerMergeOrder = providerSets
                .Where(s => !ReferenceEquals(s, defaultSet))
                .Reverse()
                .Concat(defaultSet is null ? [] : [defaultSet]);
        }
        foreach (var set in providerMergeOrder)
        {
            foreach (var v in set.Variables) merged[v.Name] = v;
        }

        // Cascade layers override providers, most specific last (list order is already
        // workspace → collection → stage → env → request).
        foreach (var set in sets.Where(s => s.Scope != VariableScope.Provider))
        {
            foreach (var v in set.Variables) merged[v.Name] = v;
        }

        return new VariableView(sets, [.. merged.Values]);
    }

    private static VariableSet BuildSet(VariableScope scope, string sourcePath, string label, IReadOnlyDictionary<string, VarSpec> vars)
    {
        var list = new List<Variable>(vars.Count);
        foreach (var (k, spec) in vars)
        {
            list.Add(new Variable(
                Name: k,
                Value: spec.Secret ? null : spec.Default,
                IsSensitive: spec.Secret,
                Scope: scope,
                SourcePath: sourcePath,
                ProviderName: null));
        }
        return new VariableSet(scope, sourcePath, label, ProviderName: null, list);
    }
}

public enum VariableScope
{
    /// <summary>Materialized from a configured <see cref="IVariableProvider"/> (env, file,
    /// azkv, …). The set's <c>ProviderName</c> identifies which one.</summary>
    Provider,

    /// <summary>A <c>.http</c> file's own <c>@name = value</c> lines — what the file resolves to
    /// outside Tap, and therefore the weakest scope inside it. See
    /// <see cref="Model.RequestFile.PortableVars"/>.</summary>
    Portable,
    Workspace,
    Collection,
    Stage,
    Env,
    Request,
}

public sealed record Variable(
    string Name,
    string? Value,
    bool IsSensitive,
    VariableScope Scope,
    string SourcePath,
    /// <summary>For <see cref="VariableScope.Provider"/> sets, the configured provider name;
    /// <c>null</c> for cascade scopes (workspace/collection/…) where the source is a file.</summary>
    string? ProviderName);

public sealed record VariableSet(
    VariableScope Scope,
    string SourcePath,
    string Label,
    string? ProviderName,
    IReadOnlyList<Variable> Variables);

public sealed record VariableView(
    IReadOnlyList<VariableSet> Sets,
    IReadOnlyList<Variable> Result);
