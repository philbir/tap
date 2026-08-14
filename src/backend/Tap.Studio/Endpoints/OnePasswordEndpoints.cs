using Tap.Studio.Ai;
using Tap.Studio.Contracts;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;
using Tap.Execution.Variables;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/onepassword/*</c> — discovery for the 1Password provider's settings form:
/// <list type="bullet">
///   <item><c>POST /detect</c> — locate the <c>op</c> binary and read its version, so the
///     <c>cliPath</c> field can fill itself in (mirrors <c>/api/ai/*-cli/detect</c>).</item>
///   <item><c>POST /vaults</c> — vaults visible to the current sign-in, behind the vault
///     picker dialog.</item>
/// </list>
///
/// <para>Both are POSTs rather than GETs because they carry the <i>draft</i> settings being
/// edited — including a service-account token that must not land in a query string or a
/// server log. Masked (<c>***</c>) values are restored from the stored config with the same
/// provider name, exactly as <c>/api/variable-providers/test</c> does.</para>
/// </summary>
public static class OnePasswordEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/onepassword/detect", async (
            OnePasswordProbeRequestDto body,
            WorkspaceService svc,
            SystemSettingsStore store,
            IEnumerable<IVariableProviderFactory> factories,
            CancellationToken ct) =>
        {
            var settings = Resolve(body, svc, store, factories);
            var d = await OnePasswordCli.DetectAsync(Get(settings, "cliPath"), ct).ConfigureAwait(false);
            return Results.Ok(new OnePasswordDetectResponseDto(d.Ok, d.Path, d.Source.ToWire(), d.Version, d.Error));
        });

        app.MapPost("/api/onepassword/vaults", async (
            OnePasswordProbeRequestDto body,
            WorkspaceService svc,
            SystemSettingsStore store,
            IEnumerable<IVariableProviderFactory> factories,
            CancellationToken ct) =>
        {
            var settings = Resolve(body, svc, store, factories);
            try
            {
                var vaults = await OnePasswordCli.ListVaultsAsync(
                    Get(settings, "cliPath"),
                    Get(settings, "account"),
                    Get(settings, "serviceAccountToken"),
                    ct).ConfigureAwait(false);

                return Results.Ok(vaults
                    .Where(v => !string.IsNullOrEmpty(v.Name))
                    .Select(v => new OnePasswordVaultDto(v.Id ?? string.Empty, v.Name!, v.Items))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            }
            catch (OnePasswordCliException ex)
            {
                return Results.BadRequest(new { code = "onepassword-cli-failed", message = ex.Message });
            }
        });
    }

    /// <summary>Draft settings with any <c>***</c> placeholders swapped back for the stored
    /// values, so a picker opened on a saved provider doesn't need the token retyped.</summary>
    private static IReadOnlyDictionary<string, string?> Resolve(
        OnePasswordProbeRequestDto body,
        WorkspaceService svc,
        SystemSettingsStore store,
        IEnumerable<IVariableProviderFactory> factories)
    {
        var incoming = body.Settings ?? new Dictionary<string, string?>();
        var descriptor = ProviderSettingsMask.DescriptorFor(factories, OnePasswordVariableProvider.ProviderType);
        var stored = body.Name is { Length: > 0 } name ? FindStoredConfig(svc, store, name) : null;
        return ProviderSettingsMask.RestoreMasked(descriptor, incoming, stored?.Settings);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>The stored config a draft with this name would replace: workspace-scope
    /// shadows system-scope, mirroring the registry's own precedence.</summary>
    private static VariableProviderConfig? FindStoredConfig(WorkspaceService svc, SystemSettingsStore store, string name)
    {
        var workspace = svc.Current.Manifest?.VariableProviders
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (workspace is not null) return workspace;
        return store.GetProviderConfigs()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
