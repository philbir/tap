using Tap.Execution.Contracts;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Execution.Agent;

/// <summary>
/// Read-model over a <see cref="LoadedWorkspace"/> for callers that discover before they run —
/// an agent listing what exists, or describing one request before sending it. Pure projection:
/// no rendering, no provider lookups, so building it can never block on a vault or leak a
/// resolved value.
/// </summary>
public static class WorkspaceInventory
{
    public static WorkspaceInventoryDto Build(LoadedWorkspace workspace)
    {
        var collections = workspace.Collections
            .OrderBy(c => c.RelativePath, StringComparer.Ordinal)
            .Select(c => new CollectionSummaryDto(
                Name: DisplayName(c),
                Path: c.RelativePath,
                BaseUrl: string.IsNullOrWhiteSpace(c.BaseUrl) ? null : c.BaseUrl,
                Environments: ScopedEnvs(workspace, c).Select(e => e.RelativePath).ToArray(),
                Tags: c.Tags,
                RequestCount: workspace.Requests.Count(r => IsInCollection(workspace, r, c)),
                AgentEnabled: c.Agent.Enabled))
            .ToArray();

        // Requests in agent-disabled collections are left out entirely: discovery lists
        // what an agent may use, and the collection row's AgentEnabled=false explains the
        // gap. The count on the collection still tells the truth about what exists.
        var requests = workspace.Requests
            .Where(r => AgentAccess.IsEnabled(workspace, r))
            .OrderBy(r => r.RelativePath, StringComparer.Ordinal)
            .Select(r => Summarize(workspace, r))
            .ToArray();

        var defaultEnvPath = workspace.Manifest?.DefaultEnv is { } defaultRef
            ? workspace.Resolve(defaultRef)?.RelativePath
            : null;
        var envs = workspace.Environments
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
            .Select(e => new EnvSummaryDto(
                DisplayName(e), e.RelativePath, IsDefault: e.RelativePath == defaultEnvPath,
                Collections: e.Collections
                    .Select(b => new EnvCollectionDto(b.Collection, b.BaseUrl))
                    .ToArray()))
            .ToArray();

        var tests = workspace.TestSets
            .Select(t => new TestTargetSummaryDto(DisplayName(t), t.RelativePath, "test", t.Tags, t.Tests.Count))
            .Concat(workspace.Flows
                .Select(f => new TestTargetSummaryDto(DisplayName(f), f.RelativePath, "flow", f.Tags, f.Steps.Count)))
            .OrderBy(t => t.Path, StringComparer.Ordinal)
            .ToArray();

        var auths = workspace.Auths
            .OrderBy(a => a.RelativePath, StringComparer.Ordinal)
            .Select(a => new AuthSummaryDto(DisplayName(a), a.RelativePath, a.Type, a.Tags))
            .ToArray();

        return new WorkspaceInventoryDto(
            Name: workspace.Manifest?.Name,
            DefaultEnv: defaultEnvPath,
            Collections: collections,
            Requests: requests,
            Envs: envs,
            Tests: tests,
            Auths: auths);
    }

    public static RequestDescriptionDto Describe(LoadedWorkspace workspace, RequestFile request)
    {
        var collection = CollectionLocator.ForRequest(workspace, request);
        var block = ScanBlock(request.HttpBlock);
        var auth = RequestPipeline.ResolveAuth(workspace, request, envPath: null);

        // The block plus the baseUrl it will be joined onto is the full template surface a
        // caller can influence; both contribute the tokens worth overriding per run.
        var referenced = Interpolation.ReferencedNames(request.HttpBlock)
            .Concat(collection?.BaseUrl is { Length: > 0 } baseUrl ? Interpolation.ReferencedNames(baseUrl) : [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return new RequestDescriptionDto(
            Name: DisplayName(request),
            Path: request.RelativePath,
            Collection: collection?.RelativePath,
            Method: block.Method,
            UrlTemplate: block.Url,
            Protocol: request.Protocol.ToWire(),
            Headers: block.Headers,
            BodyTemplate: block.Body,
            Auth: auth is null ? null : DisplayName(auth),
            AuthType: auth?.Type,
            Environments: workspace
                .EnvironmentsFor(collection is null ? null : CollectionLocator.SlugForFile(collection.RelativePath))
                .Select(e => e.RelativePath)
                .ToArray(),
            VariablesReferenced: referenced,
            DeclaredVars: request.Vars.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            Assertions: request.Assertions.Select(a => a.Describe()).ToArray(),
            Tags: request.Tags);
    }

    private static RequestSummaryDto Summarize(LoadedWorkspace workspace, RequestFile request)
    {
        var collection = CollectionLocator.ForRequest(workspace, request);
        var block = ScanBlock(request.HttpBlock);
        var auth = RequestPipeline.ResolveAuth(workspace, request, envPath: null);

        return new RequestSummaryDto(
            Name: DisplayName(request),
            Path: request.RelativePath,
            Collection: collection?.RelativePath,
            Method: block.Method,
            UrlTemplate: block.Url,
            Protocol: request.Protocol.ToWire(),
            Auth: auth is null ? null : DisplayName(auth),
            AuthType: auth?.Type,
            Tags: request.Tags,
            AssertionCount: request.Assertions.Count);
    }

    private static bool IsInCollection(LoadedWorkspace workspace, RequestFile request, CollectionFile collection)
        => CollectionLocator.ForRequest(workspace, request)?.RelativePath == collection.RelativePath;

    /// <summary>Falls back to the filename stem (everything before the first dot, so
    /// <c>01-get.req.tap</c> → <c>01-get</c>); a collection falls back to its folder name,
    /// since every collection file has the same fixed name.</summary>
    /// <summary>Environments confined to <paramref name="collection"/>. Global envs are left
    /// out on purpose: they apply everywhere, so repeating them on every collection row would
    /// make the inventory grow with the product of the two lists and say nothing extra.</summary>
    private static IEnumerable<EnvFile> ScopedEnvs(LoadedWorkspace workspace, CollectionFile collection)
    {
        var slug = CollectionLocator.SlugForFile(collection.RelativePath);
        return workspace.Environments.Where(e => !e.IsGlobal && e.AppliesTo(slug));
    }

    private static string DisplayName(WorkspaceFile file)
    {
        if (file.Name is { Length: > 0 }) return file.Name;
        if (file is CollectionFile)
        {
            var dir = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            if (Path.GetFileName(dir) is { Length: > 0 } slug) return slug;
        }
        var fileName = Path.GetFileName(file.RelativePath);
        var dot = fileName.IndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private readonly record struct RawBlock(
        string Method, string Url, IReadOnlyList<HeaderTemplateDto> Headers, string? Body);

    /// <summary>
    /// Structural scan of the un-expanded http block: request line, then headers until the
    /// first blank line, then body. Deliberately lenient where <see cref="HttpBlockParser"/>
    /// is strict — that parser guards what goes on the wire, this one only describes a file,
    /// and a discovery listing that throws on one odd file hides the whole workspace.
    /// </summary>
    private static RawBlock ScanBlock(string httpBlock)
    {
        var lines = httpBlock.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        if (index >= lines.Length) return new RawBlock(string.Empty, string.Empty, [], null);

        var requestLine = lines[index++].Trim();
        var space = requestLine.IndexOf(' ');
        var (method, url) = space > 0
            ? (requestLine[..space], requestLine[(space + 1)..].Trim())
            : (requestLine, string.Empty);

        var headers = new List<HeaderTemplateDto>();
        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
        {
            var line = lines[index++];
            var colon = line.IndexOf(':');
            if (colon > 0) headers.Add(new HeaderTemplateDto(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        var body = index < lines.Length ? string.Join('\n', lines[index..]).TrimEnd() : null;

        return new RawBlock(method, url, headers, string.IsNullOrEmpty(body) ? null : body);
    }
}
