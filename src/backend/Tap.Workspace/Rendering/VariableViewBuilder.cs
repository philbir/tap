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
            collection = CollectionLocator.ForFile(workspace, requestPath);
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
