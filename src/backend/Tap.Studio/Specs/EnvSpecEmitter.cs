using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

public static class EnvSpecEmitter
{
    public static string ToFileSource(EnvSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "env");
        fm.Set("id", SpecIds.Ensure(spec.Id));
        fm.Set("name", spec.Name);
        fm.SetStringList("tags", spec.Tags);
        SetCollections(fm, spec.Collections);
        fm.SetIfNotEmpty("defaultVariableProvider", spec.DefaultVariableProvider);
        fm.SetStringMap("providerAliases", spec.ProviderAliases);
        fm.SetIfTrue("strictVariables", spec.StrictVariables);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }

    /// <summary>
    /// Emits the <c>collections:</c> assignments. An assignment with no overrides is written as
    /// a bare slug rather than a one-key mapping — the common case is "this env is offered here",
    /// and spelling that as <c>- collection: billing</c> is noise in every diff.
    /// </summary>
    private static void SetCollections(YamlMappingNode fm, IReadOnlyList<EnvCollectionDto>? bindings)
    {
        if (bindings is not { Count: > 0 }) return;

        var seq = new YamlSequenceNode();
        foreach (var b in bindings)
        {
            if (string.IsNullOrWhiteSpace(b.Collection)) continue;

            if (string.IsNullOrWhiteSpace(b.BaseUrl) && string.IsNullOrWhiteSpace(b.DefaultAuth))
            {
                seq.Add(new YamlScalarNode(b.Collection));
                continue;
            }

            var node = new YamlMappingNode();
            node.Set("collection", b.Collection);
            node.SetIfNotEmpty("baseUrl", b.BaseUrl);
            node.SetIfNotEmpty("defaultAuth", b.DefaultAuth);
            seq.Add(node);
        }

        if (seq.Children.Count > 0) fm.Add(new YamlScalarNode("collections"), seq);
    }
}
