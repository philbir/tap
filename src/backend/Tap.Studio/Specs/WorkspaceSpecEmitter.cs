using Tap.Studio.Contracts;
using Tap.Workspace.Parsing;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits the canonical YAML for <c>workspace.tap</c>. The variable-providers shape is structured:
/// each entry carries <c>name</c>, <c>type</c>, and a <c>settings</c> block. <c>mode</c> is
/// not on disk — it's a static property of the provider type. The manifest also carries an
/// optional <c>defaultVariableProvider:</c> string at the root.
///
/// <para>Sensitive settings: the manifest PUT endpoint restores masked (<c>"***"</c>) values
/// from the on-disk config before calling this emitter (see <c>ProviderSettingsMask</c>).
/// Any mask that still reaches us — a masked secret with no stored counterpart — is dropped
/// rather than written to disk as the literal placeholder.</para>
/// </summary>
public static class WorkspaceSpecEmitter
{
    private const string MaskPlaceholder = "***";

    public static string ToFileSource(WorkspaceSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "workspace");
        fm.Set("id", SpecIds.Ensure(spec.Id));
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
                    if (v == MaskPlaceholder) continue;
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

        SetResponseLimits(fm, spec.Response);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        fm.SetHistory(spec.History);
        fm.SetStringList("tags", spec.Tags);
        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }

    /// <summary>Writes the <c>response:</c> block, in the same human sizes the field accepts
    /// (<c>8mb</c> rather than <c>8388608</c>). A cap left at its default is left out of the
    /// file entirely — a manifest shouldn't accumulate values nobody chose.</summary>
    private static void SetResponseLimits(YamlMappingNode fm, ResponseLimitsDto? limits)
    {
        if (limits is null || (limits.MaxBytes is null && limits.MaxRetainedBytes is null)) return;

        var node = new YamlMappingNode();
        if (limits.MaxBytes is { } max) node.Set("maxBytes", ByteSize.Format(max));
        if (limits.MaxRetainedBytes is { } retained) node.Set("maxRetainedBytes", ByteSize.Format(retained));
        if (node.Children.Count > 0) fm.Add("response", node);
    }
}
