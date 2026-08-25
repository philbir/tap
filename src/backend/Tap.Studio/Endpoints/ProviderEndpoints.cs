using System.Diagnostics;
using Tap.Studio.Contracts;
using Tap.Studio.Variables;
using Tap.Workspace.Model;
using Tap.Workspace.Security;
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
///   <item><c>PUT /{name}/variables/{key}</c> / <c>DELETE</c> — the manage half, for
///     ReadWrite providers. A PUT with a null value keeps whatever is stored and changes only
///     the secret flag, so flipping a plain value to encrypted never round-trips its clear
///     text through the browser.</item>
///   <item><c>GET /{name}/source</c> / <c>PUT</c> — the store file behind a file-backed
///     provider, for the Manage tab's Source view. Writes are validated by the provider
///     itself, so a file it could not read back never lands on disk.</item>
///   <item><c>GET /api/encryption-key</c> / <c>POST /api/encryption-key/generate</c> — whether
///     this machine can encrypt at all, and a way to make it so. Status and generation only;
///     no endpoint returns or accepts a passphrase.</item>
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
                    Error: error,
                    SourcePath: provider is IFileBackedVariableProvider backed
                        ? DisplayPath(svc.RootDirectory, backed.StorePath)
                        : null));
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
            IEncryptionKeySource keySource,
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
                var provider = factory.Create(config, new ProviderFactoryContext(svc.RootDirectory) { KeySource = keySource });
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
                        $"Connected, but {undecrypted} secret value(s) could not be decrypted — this machine's encryption key does not match the one they were written with.",
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

        // Upsert. PUT rather than POST because the key is in the URL and the write is
        // idempotent — the provider editor replays a row's save without accumulating entries.
        app.MapPut("/api/variable-providers/{name}/variables/{key}", async (
            string name,
            string key,
            ProviderVariableWriteDto body,
            WorkspaceService svc,
            CancellationToken ct) =>
        {
            var (provider, failure) = ResolveWritable(svc, name, body.Env);
            if (failure is not null) return failure;

            try
            {
                // A null value means "flag change only": re-read what's stored and write it
                // back under the new secret flag. That is how a plain value becomes encrypted
                // without the clear text ever making the round trip through the browser.
                var value = body.Value;
                if (value is null)
                {
                    var existing = await provider!.GetAsync(key, ct).ConfigureAwait(false);
                    if (existing is null)
                    {
                        return Results.BadRequest(new TestProviderResultDto(
                            false, $"'{key}' is not present in provider '{name}', so there is no value to keep.", 0, null));
                    }
                    value = existing.Value;
                }

                await provider!.SetAsync(key, value, body.IsSecret, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });

        app.MapDelete("/api/variable-providers/{name}/variables/{key}", async (
            string name,
            string key,
            string? env,
            WorkspaceService svc,
            CancellationToken ct) =>
        {
            var (provider, failure) = ResolveWritable(svc, name, env);
            if (failure is not null) return failure;

            try
            {
                await provider!.DeleteAsync(key, ct).ConfigureAwait(false);
                // Deleting an absent name still lands on 204: the caller wanted it gone.
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });

        // --- Store source ---------------------------------------------------------------
        //
        // The same file the variable table writes, handed over whole. Only file-backed
        // providers have one: a vault's contents are not a file this process may rewrite, and
        // pretending otherwise would give the editor a document with nowhere to save to.

        app.MapGet("/api/variable-providers/{name}/source", (
            string name, string? env, WorkspaceService svc) =>
        {
            var (provider, failure) = ResolveFileBacked(svc, name, env);
            if (failure is not null) return failure;

            try
            {
                return Results.Ok(new ProviderSourceDto(
                    Path: DisplayPath(svc.RootDirectory, provider!.StorePath),
                    FullPath: provider.StorePath,
                    Content: provider.ReadSource()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });

        app.MapPut("/api/variable-providers/{name}/source", (
            string name, ProviderSourceWriteDto body, WorkspaceService svc) =>
        {
            var (provider, failure) = ResolveFileBacked(svc, name, body.Env);
            if (failure is not null) return failure;

            try
            {
                provider!.WriteSource(body.Content ?? string.Empty);
                return Results.NoContent();
            }
            catch (WorkspaceParseException ex)
            {
                // Same shape every Source tab already renders: code, message, and the line to
                // put the marker on.
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });

        // --- Encryption key -------------------------------------------------------------
        //
        // Status and generation only. There is deliberately no endpoint that returns the
        // passphrase, and none that accepts one: a key typed into a browser is a key in a
        // request log.

        app.MapGet("/api/encryption-key", (IEncryptionKeySource keySource) =>
            Results.Ok(KeyStatus(keySource)));

        app.MapPost("/api/encryption-key/generate", (IEncryptionKeySource keySource) =>
        {
            if (keySource is not MachineEncryptionKeySource machine)
            {
                return Results.BadRequest(new TestProviderResultDto(
                    false, "This host supplies its encryption key itself; Tap cannot generate one for it.", 0, null));
            }
            if (keySource.Origin is EncryptionKeyOrigin.Environment)
            {
                return Results.BadRequest(new TestProviderResultDto(
                    false,
                    $"{MachineEncryptionKeySource.EnvVar} is set, and it wins over the key file — generating one now would "
                    + "write a key nothing reads. Unset the variable first if you want a generated key.",
                    0, null));
            }

            try
            {
                machine.GenerateKeyFile();
                return Results.Ok(KeyStatus(keySource));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new TestProviderResultDto(false, ex.Message, 0, null));
            }
        });
    }

    private static EncryptionKeyStatusDto KeyStatus(IEncryptionKeySource keySource) => new(
        Configured: keySource.GetPassphrase() is not null,
        Origin: keySource.Origin switch
        {
            EncryptionKeyOrigin.Environment => "env",
            EncryptionKeyOrigin.KeyFile => "file",
            _ => "none",
        },
        EnvVarName: MachineEncryptionKeySource.EnvVar,
        KeyFilePath: keySource is MachineEncryptionKeySource m ? m.KeyFilePath : string.Empty);

    /// <summary>Resolves a provider for a write, or the response explaining why not. Read-only
    /// providers are rejected here rather than deeper down so the message names the provider
    /// instead of surfacing a raw <see cref="NotSupportedException"/>.</summary>
    private static (IVariableProvider? Provider, IResult? Failure) ResolveWritable(
        WorkspaceService svc, string name, string? env)
    {
        var provider = svc.CreateRegistry(env).Get(name);
        if (provider is null)
        {
            return (null, Results.NotFound(new TestProviderResultDto(
                false, $"Provider '{name}' is not registered.", 0, null)));
        }
        if (provider.Mode != ProviderMode.ReadWrite)
        {
            return (null, Results.BadRequest(new WorkspaceErrorDto(
                WorkspaceErrorCode.E_PROVIDER_NOT_WRITABLE,
                $"Variable provider '{provider.Name}' is read-only.", null, null)));
        }
        return (provider, null);
    }

    /// <summary>Resolves a provider that keeps its state in one file, or the response
    /// explaining why this one doesn't. Read-only-ness isn't checked: file-backed is the
    /// stronger condition, and every provider that satisfies it today is writable.</summary>
    private static (IFileBackedVariableProvider? Provider, IResult? Failure) ResolveFileBacked(
        WorkspaceService svc, string name, string? env)
    {
        var provider = svc.CreateRegistry(env).Get(name);
        if (provider is null)
        {
            return (null, Results.NotFound(new TestProviderResultDto(
                false, $"Provider '{name}' is not registered.", 0, null)));
        }
        if (provider is not IFileBackedVariableProvider backed)
        {
            return (null, Results.BadRequest(new TestProviderResultDto(
                false, $"Provider '{name}' ({provider.Config.Type}) doesn't keep its variables in a file, so there is no source to edit.", 0, null)));
        }
        return (backed, null);
    }

    /// <summary>Workspace-relative, forward-slashed — what the UI shows. Falls back to the
    /// absolute path for a store outside the workspace, because half a path is worse than a
    /// long one when the point is to say where the file is.</summary>
    private static string DisplayPath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return fullPath;
        return relative.Replace(Path.DirectorySeparatorChar, '/');
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
