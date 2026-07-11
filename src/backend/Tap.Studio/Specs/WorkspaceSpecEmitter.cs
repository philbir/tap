using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits the canonical YAML for <c>tap.md</c>. The variable-providers shape is structured:
/// each entry carries <c>name</c>, <c>type</c>, and a <c>settings</c> block. <c>mode</c> is
/// not on disk — it's a static property of the provider type. The manifest also carries an
/// optional <c>defaultVariableProvider:</c> string at the root.
///
/// <para>Sensitive settings: when an incoming <see cref="ProviderConfigDto.Settings"/> entry
/// contains a placeholder value (the <c>"***"</c> mask produced by the providers endpoint), we
/// preserve whatever's already on disk by skipping the masked field. That keeps round-trips
/// non-destructive when the UI re-submits a provider it pulled without ever seeing the secret.</para>
/// </summary>
public static class WorkspaceSpecEmitter
{
    private const string MaskPlaceholder = "***";
    private static readonly HashSet<string> SensitiveSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encryptionKey",
    };

    public static string ToFileSource(WorkspaceSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "workspace");
        fm.SetIfNotEmpty("id", spec.Id);
        fm.Set("name", spec.Name);
        fm.SetIfNotEmpty("defaultEnv", spec.DefaultEnv);
        fm.SetIfNotEmpty("defaultVariableProvider", spec.DefaultVariableProvider);

        if (spec.VariableProviders is { Count: > 0 })
        {
            var nodes = new List<YamlMappingNode>(spec.VariableProviders.Count);
            foreach (var p in spec.VariableProviders)
            {
                var node = new YamlMappingNode();
                node.Set("name", p.Name);
                node.Set("type", p.Type);

                var settings = new YamlMappingNode();
                foreach (var (k, v) in p.Settings)
                {
                    if (string.IsNullOrEmpty(v)) continue;
                    if (SensitiveSettingKeys.Contains(k) && v == MaskPlaceholder) continue;
                    settings.Add(k, new YamlScalarNode(v));
                }
                if (settings.Children.Count > 0)
                {
                    node.Add("settings", settings);
                }
                nodes.Add(node);
            }
            fm.SetMappingList("variableProviders", nodes);
        }

        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        fm.SetStringList("tags", spec.Tags);
        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }
}
