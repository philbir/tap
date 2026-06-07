using YamlDotNet.RepresentationModel;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Parses one workspace file into a <see cref="WorkspaceFile"/>. Pure function — no I/O.
/// Caller passes the file's raw content plus its workspace-relative path.
/// </summary>
public static class FileParser
{
    public static WorkspaceFile Parse(string relativePath, string content)
    {
        var fileName = Path.GetFileName(relativePath);
        var suffixKind = KindResolver.FromFileName(fileName)
            ?? throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISMATCH,
                $"Filename '{fileName}' does not match any known Tap workspace suffix. Expected one of: *.req.md, *.auth.md, *.env.md, _collection.md, tap.md.",
                relativePath));

        var split = FrontmatterReader.Read(content, relativePath);

        var kindStr = split.Frontmatter.String("kind")
            ?? throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISSING,
                "Required 'kind:' frontmatter field is missing.",
                relativePath));

        var fmKind = KindResolver.ParseFrontmatterValue(kindStr)
            ?? throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISMATCH,
                $"'kind: {kindStr}' is not a recognized workspace kind.",
                relativePath));

        if (fmKind != suffixKind)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_KIND_MISMATCH,
                $"Frontmatter 'kind: {kindStr}' does not match filename suffix (expected '{KindResolver.FrontmatterValue(suffixKind)}').",
                relativePath));
        }

        var common = ReadCommon(split.Frontmatter, relativePath, suffixKind);
        return suffixKind switch
        {
            WorkspaceKind.Request => ParseRequest(common, split.Frontmatter, split.Body, relativePath),
            WorkspaceKind.Auth => ParseAuth(common, split.Frontmatter, relativePath),
            WorkspaceKind.Env => ParseEnv(common, split.Frontmatter),
            WorkspaceKind.Collection => ParseCollection(common, split.Frontmatter, split.Body, relativePath),
            WorkspaceKind.Workspace => ParseWorkspace(common, split.Frontmatter),
            _ => throw new InvalidOperationException(),
        };
    }

    private readonly record struct Common(WorkspaceKind Kind, string RelativePath, string? Id, string? Name, IReadOnlyList<string> Tags, string Body);

    private static Common ReadCommon(YamlMappingNode fm, string relativePath, WorkspaceKind kind)
    {
        return new Common(
            kind,
            relativePath,
            fm.String("id"),
            fm.String("name"),
            fm.StringList("tags"),
            string.Empty); // body filled per-kind below
    }

    private static RequestFile ParseRequest(Common c, YamlMappingNode fm, string body, string relativePath)
    {
        var block = HttpBlockExtractor.Extract(body, relativePath);
        var protoRaw = fm.String("protocol");
        var protocol = protoRaw is null
            ? RequestProtocol.Http
            : RequestProtocolExtensions.TryParse(protoRaw)
                ?? throw new WorkspaceParseException(new WorkspaceError(
                    WorkspaceErrorCode.E_UNKNOWN_FIELD,
                    $"Unknown 'protocol: {protoRaw}'. Expected one of: http, websocket.",
                    relativePath));
        return new RequestFile
        {
            Kind = c.Kind,
            RelativePath = c.RelativePath,
            Id = c.Id,
            Name = c.Name,
            Tags = c.Tags,
            Body = body,
            Auth = fm.Ref("auth"),
            Protocol = protocol,
            HttpBlock = block.Content,
            HttpBlockStartLine = block.StartLine,
            Vars = fm.VarSpecMap("vars"),
        };
    }

    private static IReadOnlyList<CollectionStage> ParseStages(YamlMappingNode fm, string relativePath)
    {
        if (!fm.Children.TryGetValue(new YamlScalarNode("stages"), out var node) || node is not YamlSequenceNode seq)
            return [];

        var list = new List<CollectionStage>(seq.Children.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in seq.Children)
        {
            if (entry is not YamlMappingNode stageMap) continue;

            var stageName = stageMap.String("name") ?? throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_UNKNOWN_FIELD,
                "Each entry under 'stages:' requires a 'name'.",
                relativePath));

            if (!seen.Add(stageName))
            {
                throw new WorkspaceParseException(new WorkspaceError(
                    WorkspaceErrorCode.E_UNKNOWN_FIELD,
                    $"Duplicate stage name '{stageName}'. Stage names must be unique within a collection.",
                    relativePath));
            }

            list.Add(new CollectionStage
            {
                Name = stageName,
                BaseUrl = stageMap.String("baseUrl"),
                DefaultAuth = stageMap.Ref("defaultAuth"),
                Vars = stageMap.VarSpecMap("vars"),
            });
        }

        return list;
    }

    private static AuthFile ParseAuth(Common c, YamlMappingNode fm, string relativePath)
    {
        var type = fm.String("type") ?? throw new WorkspaceParseException(new WorkspaceError(
            WorkspaceErrorCode.E_AUTH_TYPE_INVALID,
            "Auth file requires 'type' frontmatter field.",
            relativePath));

        if (type is not ("none" or "basic" or "bearer" or "apiKey" or "oauth2" or "aws-sigv4" or "custom" or "azure-cli" or "jwt" or "github"))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_AUTH_TYPE_INVALID,
                $"Unknown auth type '{type}'.",
                relativePath));
        }

        // Type-specific fields are collected into a bag — the executor knows which to look for.
        var bag = new Dictionary<string, string?>();
        foreach (var (k, v) in fm.Children)
        {
            if (k is not YamlScalarNode ks || ks.Value is null) continue;
            if (ks.Value is "kind" or "id" or "name" or "tags" or "type" or "scopes" or "headers" or "query") continue;
            if (v is YamlScalarNode sv) bag[ks.Value] = sv.Value;
        }

        return new AuthFile
        {
            Kind = c.Kind,
            RelativePath = c.RelativePath,
            Id = c.Id,
            Name = c.Name,
            Tags = c.Tags,
            Type = type,
            Fields = bag,
            Headers = fm.StringMap("headers"),
            Query = fm.StringMap("query"),
            Scopes = fm.StringList("scopes"),
        };
    }

    private static EnvFile ParseEnv(Common c, YamlMappingNode fm)
    {
        return new EnvFile
        {
            Kind = c.Kind,
            RelativePath = c.RelativePath,
            Id = c.Id,
            Name = c.Name,
            Tags = c.Tags,
            // Env vars use the same VarSpec shape as workspace/collection/request vars
            // so the `secret: true` flag is uniformly available across every scope.
            Vars = fm.VarSpecMap("vars"),
        };
    }

    private static CollectionFile ParseCollection(Common c, YamlMappingNode fm, string body, string relativePath)
    {
        var stages = ParseStages(fm, relativePath);
        var defaultStage = fm.String("defaultStage");
        if (defaultStage is not null && !stages.Any(s => string.Equals(s.Name, defaultStage, StringComparison.OrdinalIgnoreCase)))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_UNKNOWN_FIELD,
                $"'defaultStage: {defaultStage}' does not match any defined stage.",
                relativePath));
        }

        return new CollectionFile
        {
            Kind = c.Kind,
            RelativePath = c.RelativePath,
            Id = c.Id,
            Name = c.Name,
            Tags = c.Tags,
            Body = body,
            BaseUrl = fm.String("baseUrl") ?? string.Empty,
            DefaultAuth = fm.Ref("defaultAuth"),
            DefaultHeaders = fm.StringMap("defaultHeaders"),
            Vars = fm.VarSpecMap("vars"),
            Stages = stages,
            DefaultStage = defaultStage,
        };
    }

    private static WorkspaceManifestFile ParseWorkspace(Common c, YamlMappingNode fm)
    {
        var providers = ParseProviders(fm, ProviderOrigin.Workspace);

        return new WorkspaceManifestFile
        {
            Kind = c.Kind,
            RelativePath = c.RelativePath,
            Id = c.Id,
            Name = c.Name,
            Tags = c.Tags,
            DefaultEnv = fm.Ref("defaultEnv"),
            VariableProviders = providers,
            DefaultVariableProvider = fm.String("defaultVariableProvider") ?? fm.String("defaultProvider"),
            Vars = fm.VarSpecMap("vars"),
        };
    }

    /// <summary>
    /// Reads a <c>variableProviders:</c> sequence from <paramref name="fm"/> into the
    /// <see cref="VariableProviderConfig"/> shape. Each entry must declare <c>name</c> and
    /// <c>type</c>; everything else falls into the <c>settings</c> map. Both shorthand
    /// (settings inline at the entry root) and explicit (<c>settings: {…}</c>) layouts
    /// are accepted. The legacy <c>providers:</c> key is honored too so older workspaces
    /// don't break.
    /// </summary>
    public static List<VariableProviderConfig> ParseProviders(YamlMappingNode fm, ProviderOrigin origin)
    {
        var providers = new List<VariableProviderConfig>();
        var pNode = fm.Children.TryGetValue(new YamlScalarNode("variableProviders"), out var modern)
            ? modern
            : (fm.Children.TryGetValue(new YamlScalarNode("providers"), out var legacy) ? legacy : null);
        if (pNode is not YamlSequenceNode pSeq) return providers;

        foreach (var entry in pSeq.Children.OfType<YamlMappingNode>())
        {
            var name = entry.String("name");
            var type = entry.String("type");
            if (name is null || type is null) continue;

            var settings = new Dictionary<string, string?>();
            if (entry.Children.TryGetValue(new YamlScalarNode("settings"), out var sn) && sn is YamlMappingNode sMap)
            {
                ReadSettings(sMap, settings);
            }
            else
            {
                foreach (var (k, v) in entry.Children)
                {
                    if (k is not YamlScalarNode ks || ks.Value is null) continue;
                    if (ks.Value is "name" or "type" or "mode" or "settings") continue;
                    if (v is YamlScalarNode sv) settings[ks.Value] = sv.Value;
                }
            }

            providers.Add(new VariableProviderConfig
            {
                Name = name,
                Type = type,
                Settings = settings,
                Origin = origin,
            });
        }
        return providers;
    }

    private static void ReadSettings(YamlMappingNode node, Dictionary<string, string?> bag)
    {
        foreach (var (k, v) in node.Children)
        {
            if (k is not YamlScalarNode ks || ks.Value is null) continue;
            if (v is YamlScalarNode sv) bag[ks.Value] = sv.Value;
        }
    }
}
