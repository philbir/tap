using Tap.Studio.Ai;
using Tap.Studio.Contracts;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Execution.Variables;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/ai/*</c> — AI assistant surface. Detects/configures a local AI CLI (GitHub Copilot
/// CLI or Claude Code), and runs the request-crafting assist call. The assistant never writes
/// files: <c>/assist</c> returns a proposed <see cref="RequestSpecDto"/> the UI previews and
/// applies into the editor, where the user's normal Save flow persists it.
/// </summary>
public static class AiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/ai");

        g.MapGet("/status", (AiProviderFactory factory) =>
        {
            var p = factory.Get();
            return Results.Ok(new AiStatusDto(p.Name, p.Configured, p.Model, p.SetupHint));
        });

        g.MapGet("/config", (SystemSettingsStore store) =>
        {
            var ai = store.GetAiSettings();
            return Results.Ok(new AiConfigDto(
                ai?.Provider, ai?.Model, ai?.CopilotCliPath, ai?.ClaudeCliPath, ai is not null));
        });

        g.MapPut("/config", (SaveAiConfigDto body, SystemSettingsStore store, AiProviderFactory factory) =>
        {
            var provider = string.Equals(body.Provider, ClaudeCodeProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? ClaudeCodeProvider.ProviderName
                : CopilotCliProvider.ProviderName;
            store.SetAiSettings(new StoredAiSettings
            {
                Provider = provider,
                Model = Trim(body.Model),
                CopilotCliPath = Trim(body.CopilotCliPath),
                ClaudeCliPath = Trim(body.ClaudeCliPath),
            });
            factory.Invalidate();
            var ai = store.GetAiSettings();
            return Results.Ok(new AiConfigDto(ai?.Provider, ai?.Model, ai?.CopilotCliPath, ai?.ClaudeCliPath, ai is not null));
        });

        g.MapDelete("/config", (SystemSettingsStore store, AiProviderFactory factory) =>
        {
            store.SetAiSettings(null);
            factory.Invalidate();
            return Results.NoContent();
        });

        g.MapPost("/copilot-cli/detect", async (AiCliDetectRequestDto body, CancellationToken ct) =>
        {
            var d = await CopilotCliProvider.DetectAsync(body.Path, ct).ConfigureAwait(false);
            return Results.Ok(new AiCliDetectResponseDto(d.Ok, d.Path, d.Source.ToWire(), d.Version, d.Error));
        });

        g.MapPost("/claude-cli/detect", async (AiCliDetectRequestDto body, CancellationToken ct) =>
        {
            var d = await ClaudeCodeProvider.DetectAsync(body.Path, ct).ConfigureAwait(false);
            return Results.Ok(new AiCliDetectResponseDto(d.Ok, d.Path, d.Source.ToWire(), d.Version, d.Error));
        });

        // Validate draft settings without persisting — builds a one-off provider and probes it.
        g.MapPost("/test", async (SaveAiConfigDto body, CancellationToken ct) =>
        {
            var provider = AiProviderFactory.FromConfig(body.Provider, body.Model, body.CopilotCliPath, body.ClaudeCliPath);
            if (!provider.Configured)
                return Results.Ok(new AiTestResponseDto(false, provider.Name, 0, [], null, provider.SetupHint));
            try
            {
                var diagnostics = await provider.ValidateAsync(ct).ConfigureAwait(false);
                var models = await provider.ListModelsAsync(ct).ConfigureAwait(false);
                return Results.Ok(new AiTestResponseDto(
                    true, provider.Name, models.Count,
                    models.Take(5).Select(m => m.Id).ToArray(), diagnostics, null));
            }
            catch (Exception e)
            {
                return Results.Ok(new AiTestResponseDto(false, provider.Name, 0, [], null, e.Message));
            }
        });

        // Catalogs are provider-specific — Copilot's "auto" and its GPT/Gemini ids mean nothing
        // to Claude Code. `provider` (plus optional draft CLI paths) lets the settings UI preview
        // another provider's models before saving; without it the list would keep showing the
        // persisted provider's models after the user flips the dropdown.
        g.MapGet("/models", async (
            string? provider, string? copilotCliPath, string? claudeCliPath,
            AiProviderFactory factory, SystemSettingsStore store, CancellationToken ct) =>
        {
            IAiProvider p;
            if (string.IsNullOrWhiteSpace(provider))
            {
                p = factory.Get();
            }
            else
            {
                var stored = store.GetAiSettings();
                p = AiProviderFactory.FromConfig(
                    provider,
                    model: null,
                    Trim(copilotCliPath) ?? stored?.CopilotCliPath,
                    Trim(claudeCliPath) ?? stored?.ClaudeCliPath);
            }

            if (!p.Configured)
                return Results.Ok(new AiModelsResponseDto(p.Name, p.Model, [], null));
            try
            {
                var models = await p.ListModelsAsync(ct).ConfigureAwait(false);
                return Results.Ok(new AiModelsResponseDto(
                    p.Name, p.Model, models.Select(m => new AiModelDto(m.Id, m.Name)).ToArray(), null));
            }
            catch (Exception e)
            {
                return Results.Ok(new AiModelsResponseDto(p.Name, p.Model, [], e.Message));
            }
        });

        // Proposes values for the variables an OpenAPI import will generate. Strictly optional:
        // the import works identically without ever calling this, which is the default state
        // since AI is unconfigured until the user sets up a CLI.
        g.MapPost("/openapi/suggest", async (
            OpenApiSuggestRequestDto body,
            AiProviderFactory factory,
            OpenApi.OpenApiDocumentCache cache,
            WorkspaceService svc,
            SystemSettingsStore settings,
            CancellationToken ct) =>
        {
            var provider = factory.Get();
            if (!provider.Configured)
                return Results.Json(new { message = $"AI ({provider.Name}) is not configured. {provider.SetupHint}" }, statusCode: 503);

            if (cache.Get(body.DocumentId) is not { } staged)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    "document-expired", "That OpenAPI document is no longer staged. Fetch it again.", null, null));
            }

            var all = OpenApi.OpenApiOperationMapper.Map(staged.Document);
            var selected = body.OperationKeys is { Count: > 0 } keys
                ? all.Where(o => keys.Contains(o.OpKey, StringComparer.Ordinal)).ToArray()
                : all.ToArray();

            // Only operations that actually declare variables are worth asking about.
            selected = selected.Where(o => o.VariableParameters.Any()).ToArray();
            if (selected.Length == 0)
            {
                return Results.Ok(new OpenApiSuggestResponseDto(
                    [], provider.Name, provider.Model, 0, ["None of the selected operations take path or query parameters."]));
            }

            // The same variable catalog the request assistant gets — names, scopes and secret
            // flags, never values.
            var variables = GatherVariables(settings, svc.Current, collection: null, svc.Current.Environments, null)
                .Select(v => new AiOpenApiAssistant.VariableInfo(v.Name, v.Scope, v.Secret, v.Description, v.Example))
                .ToArray();

            var title = staged.Document.Info?.Title ?? "API";
            var suggestions = new List<OpenApiSuggestionDto>();
            var warnings = new List<string>();
            var considered = 0;

            // Batched because the providers are subprocesses with a fixed timeout — one call
            // carrying 200 operations returns nothing at all when it runs out.
            foreach (var batch in selected.Chunk(AiOpenApiAssistant.BatchSize))
            {
                if (ct.IsCancellationRequested) break;

                var ctx = new AiOpenApiAssistant.Context(title, batch, variables);
                try
                {
                    var result = await provider.ChatAsync(new AiChatInput(
                        AiOpenApiAssistant.BuildSystemPrompt(ctx),
                        [new AiChatMessage("user", "Propose values for these operations.")],
                        body.Model), ct).ConfigureAwait(false);

                    foreach (var m in AiOpenApiAssistant.TryParseMappings(result.Text, batch))
                        suggestions.Add(new OpenApiSuggestionDto(m.OpKey, m.Values, m.Note));

                    considered += batch.Length;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed batch must not lose the batches that already succeeded.
                    warnings.Add($"Could not get suggestions for {batch.Length} operation(s): {e.Message}");
                }
            }

            return Results.Ok(new OpenApiSuggestResponseDto(
                suggestions, provider.Name, provider.Model, considered, warnings));
        });

        g.MapPost("/assist", async (
            AiAssistRequestDto body, AiProviderFactory factory, WorkspaceService svc, SystemSettingsStore settings, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt))
                return Results.BadRequest(new { message = "A prompt is required." });

            var provider = factory.Get();
            if (!provider.Configured)
                return Results.Json(new { message = $"AI ({provider.Name}) is not configured. {provider.SetupHint}" }, statusCode: 503);

            var ctx = BuildContext(svc, settings, body.CurrentSpec, body.RequestPath);
            var systemPrompt = AiRequestAssistant.BuildSystemPrompt(ctx);

            var messages = new List<AiChatMessage>();
            foreach (var m in body.Conversation ?? [])
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                messages.Add(new AiChatMessage(role, m.Content));
            }
            messages.Add(new AiChatMessage("user", body.Prompt));

            try
            {
                var result = await provider.ChatAsync(new AiChatInput(systemPrompt, messages, body.Model), ct).ConfigureAwait(false);
                var path = body.RequestPath ?? body.CurrentSpec?.Path ?? "";
                var proposal = string.IsNullOrEmpty(path)
                    ? null
                    : AiRequestAssistant.TryParseProposal(result.Text, path, body.CurrentSpec);
                var tools = result.ToolCalls.Select(t => new AiToolCallDto(t.Name, t.Summary, t.Success)).ToArray();
                return Results.Ok(new AiAssistResponseDto(result.Text, proposal, result.Model, provider.Name, tools));
            }
            catch (Exception e)
            {
                return Results.Json(new { message = $"AI request failed: {e.Message}" }, statusCode: 502);
            }
        });
    }

    private static AiRequestAssistant.AssistContext BuildContext(
        WorkspaceService svc, SystemSettingsStore settings, RequestSpecDto? currentSpec, string? requestPath)
    {
        var current = svc.Current;
        var path = currentSpec?.Path ?? requestPath;

        var authProfiles = current.Auths
            .OrderBy(a => a.RelativePath, StringComparer.Ordinal)
            .Select(a => new AiRequestAssistant.AuthProfileInfo(
                a.RelativePath, a.Name, a.Type, a.Scopes, a.Headers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()))
            .ToArray();

        var collection = OwningCollection(current, path);
        var slug = collection is null ? null : CollectionLocator.SlugForFile(collection.RelativePath);

        // Only the environments this request could actually be sent under: naming one that is
        // scoped to a different collection would invite the model to reference variables that
        // will never be in the cascade here.
        var scopedEnvs = current.EnvironmentsFor(slug);
        var envNames = scopedEnvs
            .Select(e => e.Name ?? Path.GetFileNameWithoutExtension(e.RelativePath))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        AiRequestAssistant.CollectionInfo? collectionInfo = null;
        if (collection is not null)
        {
            collectionInfo = new AiRequestAssistant.CollectionInfo(
                collection.Name ?? Path.GetFileName(Path.GetDirectoryName(collection.RelativePath) ?? collection.RelativePath),
                collection.RelativePath,
                string.IsNullOrWhiteSpace(collection.BaseUrl) ? null : collection.BaseUrl,
                ResolveRefLabel(current, collection.DefaultAuth, collection.RelativePath),
                collection.DefaultHeaders.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                envNames);
        }

        var variables = GatherVariables(settings, current, collection, scopedEnvs, currentSpec);

        return new AiRequestAssistant.AssistContext(currentSpec, collectionInfo, authProfiles, envNames, variables);
    }

    /// <summary>Finds the collection whose directory is the longest prefix of the request path.</summary>
    private static CollectionFile? OwningCollection(LoadedWorkspace ws, string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath)) return null;
        var norm = requestPath.Replace('\\', '/');
        CollectionFile? best = null;
        var bestLen = -1;
        foreach (var c in ws.Collections)
        {
            var rel = c.RelativePath.Replace('\\', '/');
            var idx = rel.LastIndexOf('/');
            if (idx <= 0) continue;
            var prefix = rel[..(idx + 1)]; // collections/<slug>/
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLen)
            {
                best = c;
                bestLen = prefix.Length;
            }
        }
        return best;
    }

    private static string? ResolveRefLabel(LoadedWorkspace ws, WorkspaceRef? r, string sourceRelativePath)
    {
        if (r is null) return null;
        var sourceDir = Path.GetDirectoryName(sourceRelativePath)?.Replace('\\', '/');
        var hit = ws.Resolve(r, sourceDir);
        return hit?.RelativePath ?? r.RelativePath ?? r.Id;
    }

    /// <summary>Collects variable declarations across the cascade (system → workspace →
    /// collection → env → request), deduped by name with the highest-priority scope kept as
    /// the label. Secret flags accumulate; values are never included.</summary>
    /// <param name="envs">The environments in scope for the request being assisted — every
    /// global one plus the collection's own.</param>
    private static IReadOnlyList<AiRequestAssistant.VariableInfo> GatherVariables(
        SystemSettingsStore settings, LoadedWorkspace ws, CollectionFile? collection,
        IReadOnlyList<EnvFile> envs, RequestSpecDto? currentSpec)
    {
        var acc = new Dictionary<string, AiRequestAssistant.VariableInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string scope, bool secret, string? description, string? example)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            acc.TryGetValue(name, out var existing);
            acc[name] = new AiRequestAssistant.VariableInfo(
                name,
                scope, // iterated low→high priority, so the last write wins as the effective scope
                secret || (existing?.Secret ?? false),
                description ?? existing?.Description,
                example ?? existing?.Example);
        }

        void AddSpecs(IReadOnlyDictionary<string, VarSpec> vars, string scope)
        {
            foreach (var (name, spec) in vars)
                Add(name, scope, spec.Secret, spec.Description, spec.Example);
        }

        // Lowest priority first.
        foreach (var (name, v) in settings.GetVariables())
            Add(name, "system", v.Secret, null, null);
        if (ws.Manifest?.Vars is { } manifestVars)
            AddSpecs(manifestVars, "workspace");
        if (collection is not null) AddSpecs(collection.Vars, "collection");
        foreach (var env in envs)
            AddSpecs(env.Vars, "env");
        if (currentSpec?.Vars is { } specVars)
            foreach (var name in specVars.Keys) Add(name, "request", false, null, null);

        return acc.Values
            .OrderBy(v => ScopeRank(v.Scope))
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
    }

    private static int ScopeRank(string scope) => scope switch
    {
        "request" => 0,
        "env" => 1,
        "collection" => 2,
        "workspace" => 3,
        _ => 5,
    };

    private static string? Trim(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
