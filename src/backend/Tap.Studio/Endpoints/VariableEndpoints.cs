using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Studio.Variables;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/variables/*</c> — drives the Studio's variable picker UI.
///
/// <list type="bullet">
///   <item><c>POST /api/variables/views</c> — layered cascade + provider catalog + merged result.</item>
///   <item><c>POST /api/variables/compile</c> — soft-render a template (returns rendered string +
///     per-token replacement annotations for the Test-It preview).</item>
///   <item><c>POST /api/variables/set</c> — writes a single variable via the requested
///     provider (or the default writable provider when null).</item>
///   <item><c>POST /api/variables/declare-targets</c> — which file each cascade tier's
///     <c>vars:</c> block lives in, for one editor context.</item>
///   <item><c>POST /api/variables/declare</c> — turns a literal into a declared variable at a
///     chosen tier, moving a secret out to a provider on the way.</item>
/// </list>
///
/// <para>Variable values that came from a sensitive source (<c>secret: true</c> in YAML or an
/// azkv provider entry) are stripped before leaving the server: <see cref="VariableDto.Value"/>
/// is null and the UI knows to show <c>"***"</c>. Internal execution paths read the same
/// values through the renderer, never through this endpoint.</para>
/// </summary>
public static class VariableEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/variables/views", async (
            VariableContextDto body,
            WorkspaceService svc,
            IEnumerable<IVariableProviderFactory> factories,
            CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry(body.EnvPath);
            var view = await VariableViewBuilder.BuildAsync(
                svc.Current,
                registry,
                ct,
                requestPath: body.RequestPath,
                collectionPath: body.CollectionPath,
                envPath: body.EnvPath).ConfigureAwait(false);

            // Provider sets carry provenance (origin + type display name + icon) so the
            // variables panel can render one labelled group per provider instead of an
            // undifferentiated pile of names.
            VariableSetDto MapSet(VariableSet s)
            {
                string? origin = null, typeDisplayName = null, icon = null;
                if (s.ProviderName is not null && registry.Get(s.ProviderName) is { } provider)
                {
                    origin = provider.Config.Origin == ProviderOrigin.System ? "system" : "workspace";
                    var descriptor = ProviderSettingsMask.DescriptorFor(factories, provider.Config.Type);
                    typeDisplayName = descriptor?.DisplayName;
                    icon = descriptor?.Icon;
                }
                return new VariableSetDto(
                    Scope: ScopeName(s.Scope),
                    SourcePath: s.SourcePath,
                    Label: s.Label,
                    Count: s.Variables.Count,
                    ProviderName: s.ProviderName,
                    Variables: s.Variables.Select(MapVariable).ToArray(),
                    Origin: origin,
                    TypeDisplayName: typeDisplayName,
                    Icon: icon);
            }

            return Results.Ok(new VariableViewDto(
                Sets: view.Sets.Select(MapSet).ToArray(),
                Result: view.Result.Select(MapVariable).ToArray(),
                DefaultProvider: registry.DefaultProviderName,
                Aliases: registry.Aliases));
        });

        app.MapPost("/api/variables/compile", async (CompileTemplateRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry(body.Context.EnvPath);
            var view = await VariableViewBuilder.BuildAsync(
                svc.Current,
                registry,
                ct,
                requestPath: body.Context.RequestPath,
                collectionPath: body.Context.CollectionPath,
                envPath: body.Context.EnvPath).ConfigureAwait(false);

            var scope = view.Result.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
            var result = VariableCompiler.Compile(body.Template ?? string.Empty, scope, view.Sets, registry.Aliases);

            return Results.Ok(new CompileTemplateResponseDto(
                Value: result.Value,
                Replacements: result.Replacements.Select(r => new ReplacementDto(
                    Token: r.Token,
                    Name: r.Name,
                    StartIndex: r.StartIndex,
                    Length: r.Length,
                    Resolved: r.Resolved,
                    IsSensitive: r.IsSensitive,
                    Scope: r.Scope is { } s ? ScopeName(s) : null,
                    SourcePath: r.SourcePath,
                    ProviderName: r.ProviderName)).ToArray()));
        });

        app.MapPost("/api/variables/set", async (SetVariableDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            // Env matters here: a write without an explicit target lands on the active
            // env's default provider (its vault), not a global one.
            var registry = svc.CreateRegistry(body.EnvPath);
            var provider = body.VariableProvider is null
                ? registry.DefaultWritableProvider
                : registry.Get(body.VariableProvider);

            if (provider is null)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    WorkspaceErrorCode.E_UNKNOWN_PROVIDER,
                    body.VariableProvider is null
                        ? "No writable variable provider configured. Register a file (or other ReadWrite) provider."
                        : $"Variable provider '{body.VariableProvider}' is not registered.",
                    null, null));
            }
            if (provider.Mode != ProviderMode.ReadWrite)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    WorkspaceErrorCode.E_PROVIDER_NOT_WRITABLE,
                    $"Variable provider '{provider.Name}' is read-only.",
                    null, null));
            }

            try
            {
                await provider.SetAsync(body.Name, body.Value, body.IsSecret, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });

        // Which file each cascade tier's `vars:` lives in for this editor context. The
        // "convert to variable" panel names the file before the user commits, so the write is
        // never a surprise — and the same resolution the view builder uses guarantees the
        // value lands where `{{name}}` will actually read it back.
        app.MapPost("/api/variables/declare-targets", (VariableContextDto body, WorkspaceService svc) =>
            Results.Ok(VariableDeclarationTargets
                .Resolve(svc.Current, body.RequestPath, body.CollectionPath, body.EnvPath)
                .Select(t => new VariableTargetDto(t.Scope, t.Path, t.Label, t.Unavailable))
                .ToArray()));

        // Converts a literal into a declared variable. Two writes, in this order: the secret
        // reaches its provider first, then the declaration referencing it lands in the file.
        // Reversed, a failure between them would leave a workspace file pointing at a secret
        // that was never stored — a broken reference committed to Git. This way the worst case
        // is an orphaned provider entry, which resolves fine and can simply be overwritten.
        app.MapPost("/api/variables/declare", async (
            DeclareVariableDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? string.Empty;
            // The token grammar of §3.2: `{{name}}` stops at the first `}`, and a leading
            // `word:` would be read as a provider qualifier rather than part of the name.
            if (name.Length == 0 || name.Contains('{') || name.Contains('}') || name.Contains(':'))
            {
                return Results.BadRequest(new { message = "A variable name is required, and cannot contain ':', '{' or '}'." });
            }

            var target = VariableDeclarationTargets.For(
                svc.Current, body.Scope, body.RequestPath, body.CollectionPath, body.EnvPath);
            if (target is null) return Results.BadRequest(new { message = $"Unknown scope '{body.Scope}'." });
            if (target.Path is null) return Results.BadRequest(new { message = target.Unavailable });

            // What the `vars:` entry ends up holding. A secret's value must not be the thing
            // written to the file, so it goes to a provider and the entry keeps the reference.
            var declaredValue = body.Value;
            string? providerName = null;

            if (body.IsSecret)
            {
                var registry = svc.CreateRegistry(body.EnvPath);
                var provider = body.VariableProvider is null
                    ? registry.DefaultWritableProvider
                    : registry.Get(body.VariableProvider);

                if (provider is null)
                {
                    return Results.BadRequest(new WorkspaceErrorDto(
                        WorkspaceErrorCode.E_UNKNOWN_PROVIDER,
                        body.VariableProvider is null
                            ? "No writable variable provider configured. Register a file (or other ReadWrite) provider."
                            : $"Variable provider '{body.VariableProvider}' is not registered.",
                        null, null));
                }
                if (provider.Mode != ProviderMode.ReadWrite)
                {
                    return Results.BadRequest(new WorkspaceErrorDto(
                        WorkspaceErrorCode.E_PROVIDER_NOT_WRITABLE,
                        $"Variable provider '{provider.Name}' is read-only.",
                        null, null));
                }

                try
                {
                    await provider.SetAsync(name, body.Value, true, ct).ConfigureAwait(false);
                }
                catch (WorkspaceParseException ex)
                {
                    return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
                }

                providerName = provider.Name;
                declaredValue = $"{{{{{provider.Name}:{name}}}}}";
            }

            try
            {
                var source = svc.ReadSource(target.Path);
                var written = svc.Save(
                    target.Path,
                    VarDeclarationWriter.Apply(source, target.Path, name, declaredValue, body.IsSecret));
                // The watcher's debounced reload is too slow for a client that immediately
                // refetches its variable view to repaint the chip it just created.
                svc.ReloadNow();
                return Results.Ok(new DeclareVariableResultDto($"{{{{{name}}}}}", written, declaredValue, providerName));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
        });
    }

    private static VariableDto MapVariable(Variable v)
        => new(v.Name, v.IsSensitive ? null : v.Value, v.IsSensitive, ScopeName(v.Scope), v.SourcePath, v.ProviderName);

    private static string ScopeName(VariableScope s) => s switch
    {
        VariableScope.Provider => "provider",
        VariableScope.Portable => "portable",
        VariableScope.Workspace => "workspace",
        VariableScope.Collection => "collection",
        VariableScope.Env => "env",
        VariableScope.Request => "request",
        _ => "unknown",
    };
}
