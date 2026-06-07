using YamlDotNet.RepresentationModel;
using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Small helpers for reading the YAML mapping returned by <see cref="FrontmatterReader"/>.
/// Frontmatter shapes are simple enough that a hand-rolled reader beats reflection-based
/// deserialization — and gives precise error attribution.
/// </summary>
internal static class YamlExt
{
    public static string? String(this YamlMappingNode map, string key)
        => map.TryGet(key, out var n) && n is YamlScalarNode s ? s.Value : null;

    public static bool Bool(this YamlMappingNode map, string key, bool fallback = false)
        => map.TryGet(key, out var n) && n is YamlScalarNode s
            && bool.TryParse(s.Value, out var b) ? b : fallback;

    public static IReadOnlyList<string> StringList(this YamlMappingNode map, string key)
    {
        if (!map.TryGet(key, out var n) || n is not YamlSequenceNode seq) return [];
        var list = new List<string>(seq.Children.Count);
        foreach (var c in seq.Children)
            if (c is YamlScalarNode s && s.Value is not null)
                list.Add(s.Value);
        return list;
    }

    public static IReadOnlyDictionary<string, string> StringMap(this YamlMappingNode map, string key)
    {
        if (!map.TryGet(key, out var n) || n is not YamlMappingNode inner) return new Dictionary<string, string>();
        var dict = new Dictionary<string, string>(inner.Children.Count);
        foreach (var (k, v) in inner.Children)
        {
            if (k is YamlScalarNode ks && ks.Value is not null && v is YamlScalarNode vs && vs.Value is not null)
                dict[ks.Value] = vs.Value;
        }
        return dict;
    }

    public static IReadOnlyDictionary<string, VarSpec> VarSpecMap(this YamlMappingNode map, string key)
    {
        if (!map.TryGet(key, out var n) || n is not YamlMappingNode inner)
            return new Dictionary<string, VarSpec>();

        var dict = new Dictionary<string, VarSpec>(inner.Children.Count);
        foreach (var (k, v) in inner.Children)
        {
            if (k is not YamlScalarNode ks || ks.Value is null) continue;

            if (v is YamlScalarNode scalar)
            {
                dict[ks.Value] = VarSpec.FromLiteral(scalar.Value ?? string.Empty);
            }
            else if (v is YamlMappingNode obj)
            {
                dict[ks.Value] = new VarSpec
                {
                    Default = obj.String("default"),
                    Description = obj.String("description"),
                    Required = obj.Bool("required"),
                    Example = obj.String("example"),
                    Secret = obj.Bool("secret"),
                };
            }
        }
        return dict;
    }

    public static WorkspaceRef? Ref(this YamlMappingNode map, string key)
    {
        var raw = map.String(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            return WorkspaceRef.FromId(raw[3..]);
        return WorkspaceRef.FromPath(raw);
    }

    private static bool TryGet(this YamlMappingNode map, string key, out YamlNode? value)
    {
        var scalar = new YamlScalarNode(key);
        if (map.Children.TryGetValue(scalar, out var n))
        {
            value = n;
            return true;
        }
        value = null;
        return false;
    }
}
