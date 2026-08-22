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
        fm.SetIfNotEmpty("defaultVariableProvider", spec.DefaultVariableProvider);
        fm.SetStringMap("providerAliases", spec.ProviderAliases);
        fm.SetIfTrue("strictVariables", spec.StrictVariables);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }
}
