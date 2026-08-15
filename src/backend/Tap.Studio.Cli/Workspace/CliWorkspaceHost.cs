using Microsoft.Extensions.Logging.Abstractions;
using Tap.Execution.Variables;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;

namespace Tap.Studio.Cli.Workspace;

/// <summary>
/// The CLI's side of <see cref="IWorkspaceHost"/>: load a folder once, resolve variables
/// through the same providers the Studio registers, and get tokens from something that will
/// never open a browser.
///
/// <para>No watcher and no reload — a run reads the workspace as it was when the process
/// started. Anything else would mean a test set could quietly change underneath a run that had
/// already reported half its results.</para>
/// </summary>
public sealed class CliWorkspaceHost : IWorkspaceHost
{
    private readonly ProviderRegistryBuilder _registryBuilder;
    private readonly SystemSettingsStore _systemSettings;

    private IAuthTokenSource _tokens = NoAuthTokenSource.Instance;

    private CliWorkspaceHost(
        LoadedWorkspace workspace,
        string rootDirectory,
        ProviderRegistryBuilder registryBuilder,
        SystemSettingsStore systemSettings)
    {
        Workspace = workspace;
        RootDirectory = rootDirectory;
        _registryBuilder = registryBuilder;
        _systemSettings = systemSettings;
    }

    public LoadedWorkspace Workspace { get; }
    public string RootDirectory { get; }
    public IAuthTokenSource Tokens => _tokens;

    /// <summary>Attaches the token source once the run's auth policy is known. Separate from
    /// construction because the source needs the loaded workspace and the registry factory —
    /// this host — to resolve a profile's fields before it can mint anything.</summary>
    public CliWorkspaceHost WithTokens(Func<CliWorkspaceHost, IAuthTokenSource> factory)
    {
        _tokens = factory(this);
        return this;
    }

    public VariableProviderRegistry CreateRegistry(EnvFile? env)
        => _registryBuilder.Build(
            Workspace,
            RootDirectory,
            _systemSettings.GetProviderConfigs(),
            _systemSettings.DefaultVariableProvider,
            env);

    /// <summary>
    /// Loads the workspace at <paramref name="rootDirectory"/> and composes the provider set.
    ///
    /// <para>The factory list mirrors the Studio's exactly. A CLI that quietly supported fewer
    /// providers would resolve a different value for the same <c>{{token}}</c> and produce a
    /// different verdict — which is the one thing this whole split exists to prevent.</para>
    /// </summary>
    public static CliWorkspaceHost Load(string rootDirectory)
    {
        var loaded = new WorkspaceLoader().Load(rootDirectory);

        IVariableProviderFactory[] factories =
        [
            new EnvVariableProviderFactory(),
            new FileVariableProviderFactory(),
            new AzureKeyVaultVariableProviderFactory(),
            new OnePasswordVariableProviderFactory(),
            .. SystemFactory(out var systemSettings),
        ];

        return new CliWorkspaceHost(
            loaded,
            Path.GetFullPath(rootDirectory),
            new ProviderRegistryBuilder(factories, NullLogger<ProviderRegistryBuilder>.Instance),
            systemSettings);

        static IVariableProviderFactory[] SystemFactory(out SystemSettingsStore store)
        {
            // Reads the same ~/.tap/system.json the Studio's Settings screen writes, so a
            // developer running the CLI locally sees the variables they already configured.
            // On a CI runner the file simply doesn't exist and the provider is empty.
            store = new SystemSettingsStore(
                new SystemSettingsOptions { SystemDir = SystemSettingsOptions.DefaultSystemDir() },
                NullLogger<SystemSettingsStore>.Instance);
            return [new SystemVariableProviderFactory(store)];
        }
    }
}
