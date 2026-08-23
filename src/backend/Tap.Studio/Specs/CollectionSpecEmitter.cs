using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits the canonical YAML for a <c>_collection.tap</c>. Field order: kind / id / name /
/// baseUrl / defaultAuth / defaultHeaders / vars / tags / agent.
/// The file always lives at <c>collections/&lt;slug&gt;/_collection.tap</c>.
/// </summary>
public static class CollectionSpecEmitter
{
    public static string ToFileSource(CollectionSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "collection");
        fm.Set("id", SpecIds.Ensure(spec.Id));
        fm.Set("name", spec.Name);
        fm.SetIfNotEmpty("baseUrl", spec.BaseUrl);
        fm.SetIfNotEmpty("defaultAuth", spec.DefaultAuth);
        fm.SetStringMap("defaultHeaders", spec.DefaultHeaders);
        fm.SetTransport(spec.Transport);
        fm.SetHistory(spec.History);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        fm.SetStringList("tags", spec.Tags);

        // Enabled is the default, so only the opt-out is written — the canonical file says
        // nothing unless the author actually fenced the collection off from agents.
        if (spec.AgentEnabled == false) fm.Set("agent", "false");

        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }
}
