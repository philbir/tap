using Tap.Execution.Variables;
namespace Tap.Studio.Ai;

/// <summary>
/// Resolves the active <see cref="IAiProvider"/> from persisted AI settings, with a small
/// cache keyed on the settings signature (mirrors mango's <c>getProvider</c> /
/// <c>invalidateProvider</c>). Also exposes <see cref="FromConfig"/> so the <c>/test</c>
/// endpoint can validate draft settings without persisting them.
/// </summary>
public sealed class AiProviderFactory
{
    private readonly SystemSettingsStore _settings;
    private readonly Lock _gate = new();
    private IAiProvider? _cached;
    private string? _signature;

    public AiProviderFactory(SystemSettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += Invalidate;
    }

    public static IAiProvider FromConfig(string provider, string? model, string? copilotCliPath, string? claudeCliPath)
        => provider switch
        {
            ClaudeCodeProvider.ProviderName => new ClaudeCodeProvider(model, claudeCliPath),
            _ => new CopilotCliProvider(model, copilotCliPath),
        };

    public IAiProvider Get()
    {
        var stored = _settings.GetAiSettings();
        var name = stored?.Provider ?? CopilotCliProvider.ProviderName;
        var signature = string.Join('|', name, stored?.Model, stored?.CopilotCliPath, stored?.ClaudeCliPath);
        lock (_gate)
        {
            if (_cached is not null && _signature == signature) return _cached;
            _signature = signature;
            _cached = FromConfig(name, stored?.Model, stored?.CopilotCliPath, stored?.ClaudeCliPath);
            return _cached;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _signature = null;
        }
    }
}
