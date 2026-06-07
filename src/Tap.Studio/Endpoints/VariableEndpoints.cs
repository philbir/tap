using Tap.Studio.Contracts;
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
        app.MapPost("/api/variables/views", async (VariableContextDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry();
            var view = await VariableViewBuilder.BuildAsync(
                svc.Current,
                registry,
                ct,
                requestPath: body.RequestPath,
                collectionPath: body.CollectionPath,
                envPath: body.EnvPath,
                stageName: body.Stage).ConfigureAwait(false);

            return Results.Ok(new VariableViewDto(
                Sets: view.Sets.Select(s => new VariableSetDto(
                    Scope: ScopeName(s.Scope),
                    SourcePath: s.SourcePath,
                    Label: s.Label,
                    Count: s.Variables.Count,
                    ProviderName: s.ProviderName,
                    Variables: s.Variables.Select(MapVariable).ToArray())).ToArray(),
                Result: view.Result.Select(MapVariable).ToArray(),
                DefaultProvider: registry.DefaultProviderName));
        });

        app.MapPost("/api/variables/compile", async (CompileTemplateRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            var registry = svc.CreateRegistry();
            var view = await VariableViewBuilder.BuildAsync(
                svc.Current,
                registry,
                ct,
                requestPath: body.Context.RequestPath,
                collectionPath: body.Context.CollectionPath,
                envPath: body.Context.EnvPath,
                stageName: body.Context.Stage).ConfigureAwait(false);

            var scope = view.Result.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
            var result = VariableCompiler.Compile(body.Template ?? string.Empty, scope, view.Sets);

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
            var registry = svc.CreateRegistry();
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
    }

    private static VariableDto MapVariable(Variable v)
        => new(v.Name, v.IsSensitive ? null : v.Value, v.IsSensitive, ScopeName(v.Scope), v.SourcePath, v.ProviderName);

    private static string ScopeName(VariableScope s) => s switch
    {
        VariableScope.Provider => "provider",
        VariableScope.Workspace => "workspace",
        VariableScope.Collection => "collection",
        VariableScope.Stage => "stage",
        VariableScope.Env => "env",
        VariableScope.Request => "request",
        _ => "unknown",
    };
}
