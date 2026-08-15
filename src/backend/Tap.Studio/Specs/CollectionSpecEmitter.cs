using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits the canonical YAML for a <c>_collection.md</c>. Field order: kind / id / name /
/// baseUrl / defaultAuth / defaultHeaders / vars / tags / stages / defaultStage / agent.
/// The file always lives at <c>collections/&lt;slug&gt;/_collection.md</c>.
/// </summary>
public static class CollectionSpecEmitter
{
    public static string ToFileSource(CollectionSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "collection");
        fm.SetIfNotEmpty("id", spec.Id);
        fm.Set("name", spec.Name);
        fm.SetIfNotEmpty("baseUrl", spec.BaseUrl);
        fm.SetIfNotEmpty("defaultAuth", spec.DefaultAuth);
        fm.SetStringMap("defaultHeaders", spec.DefaultHeaders);
        fm.SetTransport(spec.Transport);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        fm.SetStringList("tags", spec.Tags);

        if (spec.Stages is { Count: > 0 })
        {
            var stageNodes = new List<YamlMappingNode>(spec.Stages.Count);
            foreach (var s in spec.Stages)
            {
                var node = new YamlMappingNode();
                node.Set("name", s.Name);
                node.SetIfNotEmpty("baseUrl", s.BaseUrl);
                node.SetIfNotEmpty("defaultAuth", s.DefaultAuth);
                node.SetVarMap("vars", s.Vars, s.Secrets);
                stageNodes.Add(node);
            }
            fm.SetMappingList("stages", stageNodes);
        }

        fm.SetIfNotEmpty("defaultStage", spec.DefaultStage);

        // Enabled is the default, so only the opt-out is written — the canonical file says
        // nothing unless the author actually fenced the collection off from agents.
        if (spec.AgentEnabled == false) fm.Set("agent", "false");

        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }
}
