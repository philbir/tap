using Tap.Studio.Contracts;
using Tap.Workspace.Variables;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/variable-providers</c> — lists the variable providers currently active for the
/// workspace, combining system-level and workspace-level configurations. Sensitive setting
/// keys (currently: <c>encryptionKey</c>) are masked in the response so the UI can render
/// settings without leaking material from disk.
/// </summary>
public static class ProviderEndpoints
{
    private static readonly HashSet<string> SensitiveSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encryptionKey",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/variable-providers", async (WorkspaceService svc, CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry();
            var summaries = new List<ProviderSummaryDto>(registry.Providers.Count);
            foreach (var provider in registry.Providers)
            {
                int? count = null;
                string? error = null;
                try
                {
                    var list = await provider.ListAsync(ct).ConfigureAwait(false);
                    count = list.Count;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                summaries.Add(new ProviderSummaryDto(
                    Name: provider.Name,
                    Type: provider.Config.Type,
                    Mode: provider.Mode == ProviderMode.ReadWrite ? "readwrite" : "read",
                    Origin: provider.Config.Origin == ProviderOrigin.System ? "system" : "workspace",
                    Settings: MaskSettings(provider.Config.Settings),
                    VariableCount: count,
                    Error: error));
            }
            return Results.Ok(summaries);
        });
    }

    private static IReadOnlyDictionary<string, string?> MaskSettings(IReadOnlyDictionary<string, string?> settings)
    {
        var masked = new Dictionary<string, string?>(settings.Count);
        foreach (var (k, v) in settings)
        {
            masked[k] = SensitiveSettingKeys.Contains(k) && !string.IsNullOrEmpty(v) ? "***" : v;
        }
        return masked;
    }
}
