using Tap.Workspace.Variables;

namespace Tap.Execution.Variables;

/// <summary>
/// Built-in variable provider that exposes the variables stored in <c>system.json</c>. Always
/// registered (the <see cref="SystemSettingsStore"/> ships an implicit
/// <see cref="VariableProviderConfig"/> for it), read/write, and intentionally backed by the
/// same store the Settings UI edits — so writes through this provider land in the same file
/// the user sees.
/// </summary>
public sealed class SystemVariableProvider : IVariableProvider
{
    public const string ProviderName = "system";
    public const string ProviderType = "system";

    private readonly SystemSettingsStore _store;
    private readonly VariableProviderConfig _config;

    public SystemVariableProvider(SystemSettingsStore store, VariableProviderConfig config)
    {
        _store = store;
        _config = config;
    }

    public string Name => _config.Name;
    public ProviderMode Mode => ProviderMode.ReadWrite;
    public VariableProviderConfig Config => _config;

    public ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
    {
        var vars = _store.GetVariables();
        if (!vars.TryGetValue(name, out var entry)) return ValueTask.FromResult<VariableValue?>(null);
        return ValueTask.FromResult<VariableValue?>(new VariableValue(name, entry.Value, entry.Secret, Name));
    }

    public ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        var vars = _store.GetVariables();
        var list = new List<VariableValue>(vars.Count);
        foreach (var (name, entry) in vars.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            list.Add(new VariableValue(name, entry.Value, entry.Secret, Name));
        }
        return ValueTask.FromResult<IReadOnlyList<VariableValue>>(list);
    }

    public ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
    {
        _store.SetVariable(name, value, isSecret);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string name, CancellationToken ct)
        => ValueTask.FromResult(_store.RemoveVariable(name));
}

public sealed class SystemVariableProviderFactory : IVariableProviderFactory
{
    private readonly SystemSettingsStore _store;

    public SystemVariableProviderFactory(SystemSettingsStore store) => _store = store;

    public string Type => SystemVariableProvider.ProviderType;

    // Never shown in the add-provider picker (the Studio filters the `system` type out),
    // but the descriptor still exists so masking/labelling code can treat all factories
    // uniformly.
    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = SystemVariableProvider.ProviderType,
        DisplayName = "System variables",
        Icon = "settings",
        Description = "Built-in provider backed by the user-level system.json variable list.",
        Mode = ProviderMode.ReadWrite,
        Fields = [],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new SystemVariableProvider(_store, config);
}
