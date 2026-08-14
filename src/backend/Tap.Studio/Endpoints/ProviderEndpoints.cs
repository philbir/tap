using System.Diagnostics;
using Tap.Studio.Contracts;
using Tap.Studio.Variables;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;
using Tap.Execution.Variables;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/variable-providers</c> — the provider management surface:
/// <list type="bullet">
///   <item><c>GET /</c> — providers active for the workspace (system + workspace scopes,
///     with the active env's bindings applied when <c>?env=</c> is passed). Sensitive
///     settings are masked via the type descriptors.</item>
///   <item><c>GET /types</c> — type descriptors (display name, icon, typed settings schema)
///     for the picker + generated settings forms. The built-in <c>system</c> type is
///     excluded — users never add it themselves.</item>
///   <item><c>POST /test</c> — connectivity check for a <b>draft</b> config (unsaved editor
///     state). Masked values are restored from the stored config with the same name.</item>
///   <item><c>GET /{name}/variables</c> — browse listing (values masked for secrets);
///     <c>GET /{name}/variables/{key}</c> reveals one value on explicit request.</item>
/// </list>
/// </summary>
public static class ProviderEndpoints
{
    /// <summary>Cap on how long a provider test may spin before we call it a failure —
    /// DefaultAzureCredential's probe chain can otherwise hang for minutes.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/variable-providers", async (
            string? env,
            WorkspaceService svc,
            IEnumerable<IVariableProviderFactory> factories,
            CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry(env);
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

                var descriptor = ProviderSettingsMask.DescriptorFor(factories, provider.Config.Type);
                summaries.Add(new ProviderSummaryDto(
                    Name: provider.Name,
                    Type: provider.Config.Type,
                    TypeDisplayName: descriptor?.DisplayName,
                    Icon: descriptor?.Icon,
                    Mode: provider.Mode == ProviderMode.ReadWrite ? "readwrite" : "read",
                    Origin: provider.Config.Origin == ProviderOrigin.System ? "system" : "workspace",
                    Settings: ProviderSettingsMask.Apply(descriptor, provider.Config.Settings),
                    VariableCount: count,
                    Error: error));
            }
            return Results.Ok(summaries);
        });

        app.MapGet("/api/variable-providers/types", (IEnumerable<IVariableProviderFactory> factories) =>
        {
            var types = factories
                .Where(f => !string.Equals(f.Type, SystemVariableProvider.ProviderType, StringComparison.OrdinalIgnoreCase))
                .Select(f => ToDto(f.Descriptor))
                .OrderBy(d => d.DisplayName, StringComparer.Ordinal)
                .ToArray();
            return Results.Ok(types);
        });

        app.MapPost("/api/variable-providers/test", async (
            TestProviderRequestDto body,
            WorkspaceService svc,
            SystemSettingsStore store,
            IEnumerable<IVariableProviderFactory> factories,
            CancellationToken ct) =>
        {
            var factory = factories.FirstOrDefault(f => string.Equals(f.Type, body.Type, StringComparison.OrdinalIgnoreCase));
            if (factory is null)
                return Results.BadRequest(new TestProviderResultDto(false, $"Unknown provider type '{body.Type}'.", 0, null));

            var incoming = body.Settings ?? new Dictionary<string, string?>();
            var stored = body.Name is { Length: > 0 } name ? FindStoredConfig(svc, store, name) : null;
            var settings = ProviderSettingsMask.RestoreMasked(factory.Descriptor, incoming, stored?.Settings);

            if (ProviderSettingsMask.FirstMissingRequired(factory.Descriptor, settings) is { } missing)
                return Results.Ok(new TestProviderResultDto(false, $"Required setting '{missing}' is empty.", 0, null));

            var config = new VariableProviderConfig
            {
                Name = string.IsNullOrWhiteSpace(body.Name) ? "(draft)" : body.Name!,
                Type = factory.Type,
                Settings = settings,
                Origin = ProviderOrigin.System,
            };

            // Transient instance on purpose: the test must exercise the draft settings, not
            // whatever cached instance the registry holds for the saved config.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TestTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                var provider = factory.Create(config, new ProviderFactoryContext(svc.RootDirectory));
                var list = await provider.ListAsync(cts.Token).ConfigureAwait(false);
                sw.Stop();

                // The file provider doesn't throw on a wrong passphrase — it surfaces the raw
                // encrypted envelope as the value. Count those so "test" catches a bad key.
                var undecrypted = list.Count(v =>
                    v.IsSecret && v.Value.StartsWith(FileVariableProvider.EnvelopePrefix, StringComparison.Ordinal));
                if (undecrypted > 0)
                {
                    return Results.Ok(new TestProviderResultDto(
                        false,
                        $"Connected, but {undecrypted} secret value(s) could not be decrypted — check the encryption passphrase.",
                        sw.Elapsed.TotalMilliseconds,
                        list.Count));
                }

                return Results.Ok(new TestProviderResultDto(
                    true,
                    $"OK — {list.Count} variable(s) visible.",
                    sw.Elapsed.TotalMilliseconds,
                    list.Count));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                return Results.Ok(new TestProviderResultDto(
                    false, $"Timed out after {TestTimeout.TotalSeconds:0}s.", sw.Elapsed.TotalMilliseconds, null));
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Results.Ok(new TestProviderResultDto(false, ex.Message, sw.Elapsed.TotalMilliseconds, null));
            }
        });

        app.MapGet("/api/variable-providers/{name}/variables", async (
            string name,
            bool refresh,
            string? env,
            WorkspaceService svc,
            CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry(env);
            var provider = registry.Get(name);
            if (provider is null)
                return Results.NotFound(new TestProviderResultDto(false, $"Provider '{name}' is not registered.", 0, null));

            if (refresh && provider is IRefreshableVariableProvider refreshable)
                refreshable.InvalidateListCache();

            try
            {
                var list = await provider.ListAsync(ct).ConfigureAwait(false);
                var rows = list
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new ProviderVariableDto(v.Name, v.IsSecret, v.IsSecret ? null : v.Value))
                    .ToArray();
                return Results.Ok(rows);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });

        app.MapGet("/api/variable-providers/{name}/variables/{key}", async (
            string name,
            string key,
            string? env,
            WorkspaceService svc,
            CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry(env);
            var provider = registry.Get(name);
            if (provider is null)
                return Results.NotFound(new TestProviderResultDto(false, $"Provider '{name}' is not registered.", 0, null));

            try
            {
                var value = await provider.GetAsync(key, ct).ConfigureAwait(false);
                if (value is null)
                    return Results.NotFound(new TestProviderResultDto(false, $"'{key}' is not present in provider '{name}'.", 0, null));
                return Results.Ok(new ProviderVariableValueDto(value.Name, value.Value, value.IsSecret));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });
    }

    private static ProviderTypeDescriptorDto ToDto(ProviderTypeDescriptor d) => new(
        Type: d.Type,
        DisplayName: d.DisplayName,
        Icon: d.Icon,
        Description: d.Description,
        Mode: d.Mode == ProviderMode.ReadWrite ? "readwrite" : "read",
        Fields: d.Fields.Select(f => new ProviderSettingFieldDto(
            Key: f.Key,
            Label: f.Label,
            Description: f.Description,
            Kind: f.Kind switch
            {
                ProviderFieldKind.Secret => "secret",
                ProviderFieldKind.Select => "select",
                _ => "text",
            },
            Required: f.Required,
            Placeholder: f.Placeholder,
            Picker: f.Picker,
            Options: f.Options.Select(o => new ProviderFieldOptionDto(o.Value, o.Label, o.Description)).ToArray(),
            DefaultValue: f.DefaultValue,
            VisibleWhen: f.VisibleWhen is { } v ? new ProviderFieldVisibilityDto(v.Key, v.Values) : null,
            Note: f.Note is { } n ? new ProviderFieldNoteDto(n.Text, n.Url, n.UrlLabel) : null)).ToArray());

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
