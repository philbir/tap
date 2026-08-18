using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;
using Tap.Workspace.Variables;

namespace Tap.Tests.Agent;

/// <summary>Builders shared by the agent-surface tests. Files go through the real
/// <see cref="FileParser"/> so what the tests exercise is what a workspace on disk produces.</summary>
internal static class AgentTestData
{
    public static LoadedWorkspace BuildWorkspace(params WorkspaceFile[] files)
        => new("/ws", "/ws", files, []);

    public static WorkspaceFile Parse(string relativePath, string content)
        => FileParser.Parse(relativePath, content);

    public static VariableProviderRegistry Registry(params IVariableProvider[] providers)
        => new(providers, providers.Length > 0 ? providers[0].Name : null);

    public static WorkspaceFile DemoCollection(string baseUrl = "http://api.demo.test", string? defaultAuth = "./bearer.auth.tap")
        => Parse("collections/demo/_collection.tap", $"""
            ---
            kind: collection
            name: Demo
            baseUrl: '{baseUrl}'
            {(defaultAuth is null ? "" : $"defaultAuth: {defaultAuth}\n")}defaultHeaders:
              Accept: application/json
            stages:
            - name: uat
              baseUrl: http://uat.demo.test
            ---

            Demo collection.
            """);

    public static readonly WorkspaceFile BearerAuth = Parse("collections/demo/bearer.auth.tap", """
        ---
        kind: auth
        name: Demo Bearer
        type: bearer
        token: super-secret-token-value
        ---
        """);
}

/// <summary>The minimal host a <see cref="Tap.Execution.Workspace.RequestPipeline"/> needs:
/// a loaded workspace, provider-less registries, and no auth tokens.</summary>
internal sealed class FakeWorkspaceHost(LoadedWorkspace workspace) : Tap.Execution.Workspace.IWorkspaceHost
{
    public LoadedWorkspace Workspace => workspace;
    public string RootDirectory => workspace.RootDirectory;
    public VariableProviderRegistry CreateRegistry(EnvFile? env) => new([], null);
    public Tap.Execution.Workspace.IAuthTokenSource Tokens => Tap.Execution.Workspace.NoAuthTokenSource.Instance;
}

/// <summary>In-memory provider so render tests can exercise provider-resolved secrets
/// without touching a real vault.</summary>
internal sealed class StubVariableProvider(string name, params VariableValue[] values) : IVariableProvider
{
    private readonly Dictionary<string, VariableValue> _values =
        values.ToDictionary(v => v.Name, StringComparer.Ordinal);

    public string Name => name;
    public ProviderMode Mode => ProviderMode.Read;
    public VariableProviderConfig Config { get; } = new()
    {
        Name = name,
        Type = "stub",
        Origin = ProviderOrigin.Workspace,
    };

    public ValueTask<VariableValue?> GetAsync(string variable, CancellationToken ct)
        => ValueTask.FromResult(_values.GetValueOrDefault(variable));

    public ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<VariableValue>>(_values.Values.ToArray());

    public ValueTask SetAsync(string variable, string value, bool isSecret, CancellationToken ct)
        => throw new NotSupportedException();

    public ValueTask<bool> DeleteAsync(string variable, CancellationToken ct)
        => throw new NotSupportedException();
}
