using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Studio.Variables;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;
using Tap.Workspace.Variables;
using Tap.Workspace;

namespace Tap.Studio.Endpoints;

/// <summary>
/// Catalog endpoints for auth / env profiles plus the workspace manifest. Collections live
/// at their own group (<c>/api/collections/*</c>).
///
/// Each kind supports: list, structured read, and typed-spec save. Clients ship a typed DTO
/// (<c>*SpecDto</c>) and the server emits the canonical YAML via <see cref="Specs"/>.
/// </summary>
public static class CatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // ----- Auths -----
        var auths = app.MapGroup("/api/auths");
        auths.MapGet("/", (WorkspaceService svc) => Results.Ok(
            (IReadOnlyList<AuthSummaryDto>)svc.Current.Auths
                .Select(a => new AuthSummaryDto(
                    a.RelativePath, a.Name ?? Stem(a.RelativePath), a.Id, a.Type,
                    CollectionLocator.SlugForFile(a.RelativePath)))
                .ToArray()));

        auths.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not AuthFile a) return Results.NotFound();
            return Results.Ok(new AuthDetailDto(
                Path: a.RelativePath,
                Name: a.Name ?? Stem(a.RelativePath),
                Id: a.Id,
                Type: a.Type,
                Collection: CollectionLocator.SlugForFile(a.RelativePath),
                Fields: a.Fields,
                Headers: a.Headers,
                Query: a.Query,
                Scopes: a.Scopes,
                Tags: a.Tags,
                Body: a.Body,
                Source: svc.ReadSource(a.RelativePath)));
        });

        auths.MapPut("/spec", (AuthSpecDto spec, WorkspaceService svc) =>
        {
            var id = SpecIds.Ensure(spec.Id);
            return SaveSpec(svc, spec.Path, AuthSpecEmitter.ToFileSource(spec with { Id = id }), id);
        });

        // ----- Environments -----
        var envs = app.MapGroup("/api/environments");
        envs.MapGet("/", (WorkspaceService svc) => Results.Ok(
            (IReadOnlyList<EnvSummaryDto>)svc.Current.Environments
                .Select(e => new EnvSummaryDto(
                    e.RelativePath, e.Name ?? Stem(e.RelativePath), e.Id, ToBindings(e)))
                .ToArray()));

        envs.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not EnvFile e) return Results.NotFound();
            return Results.Ok(new EnvDetailDto(
                Path: e.RelativePath,
                Name: e.Name ?? Stem(e.RelativePath),
                Id: e.Id,
                Vars: e.Vars,
                Tags: e.Tags,
                Body: e.Body,
                Source: svc.ReadSource(e.RelativePath),
                Collections: ToBindings(e),
                DefaultVariableProvider: e.DefaultVariableProvider,
                ProviderAliases: e.ProviderAliases,
                StrictVariables: e.StrictVariables));
        });

        envs.MapPut("/spec", (EnvSpecDto spec, WorkspaceService svc) =>
        {
            var id = SpecIds.Ensure(spec.Id);
            return SaveSpec(svc, spec.Path, EnvSpecEmitter.ToFileSource(spec with { Id = id }), id);
        });

        // ----- Workspace manifest -----
        app.MapGet("/api/workspace/manifest", (WorkspaceService svc, IEnumerable<IVariableProviderFactory> factories) =>
        {
            var m = svc.Current.Manifest;
            if (m is null) return Results.NotFound();
            return Results.Ok(new WorkspaceDetailDto(
                Name: m.Name ?? "workspace",
                Id: m.Id,
                DefaultEnv: m.DefaultEnv?.RelativePath,
                VariableProviders: m.VariableProviders.Select(p => MapProviderConfig(p, factories)).ToArray(),
                DefaultVariableProvider: m.DefaultVariableProvider,
                Vars: m.Vars,
                Tags: m.Tags,
                Body: m.Body,
                Source: svc.ReadSource(m.RelativePath),
                Response: m.Response.IsEmpty
                    ? null
                    : new ResponseLimitsDto(m.Response.MaxBytes, m.Response.MaxRetainedBytes),
                History: HistoryOptionsMapper.ToDto(m.History)));
        });

        app.MapPut("/api/workspace/manifest/spec", (
            WorkspaceSpecDto spec,
            WorkspaceService svc,
            IEnumerable<IVariableProviderFactory> factories) =>
        {
            // Masked (***) secret settings round-trip from the GET above — restore the
            // on-disk values before emitting, and hold saves to the descriptor's required
            // fields so a typo'd provider doesn't land in workspace.tap.
            if (spec.VariableProviders is { Count: > 0 } incoming)
            {
                var stored = svc.Current.Manifest?.VariableProviders ?? [];
                var restored = new List<ProviderConfigDto>(incoming.Count);
                foreach (var p in incoming)
                {
                    var descriptor = ProviderSettingsMask.DescriptorFor(factories, p.Type);
                    var prev = stored.FirstOrDefault(s => string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    var settings = ProviderSettingsMask.RestoreMasked(descriptor, p.Settings, prev?.Settings);

                    if (descriptor is not null
                        && ProviderSettingsMask.FirstMissingRequired(descriptor, settings) is { } missing)
                    {
                        return Results.BadRequest(new WorkspaceErrorDto(
                            WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                            $"Provider '{p.Name}': required setting '{missing}' is empty.",
                            WorkspaceLoader.ManifestFileName, null));
                    }
                    restored.Add(p with { Settings = settings });
                }
                spec = spec with { VariableProviders = restored };
            }

            var id = SpecIds.Ensure(spec.Id);
            return SaveSpec(svc, WorkspaceLoader.ManifestFileName,
                WorkspaceSpecEmitter.ToFileSource(spec with { Id = id }), id);
        });
    }

    /// <summary>Writes a spec and hands back the id it was stored under. The id matters to the
    /// client: a file created without one gets a fresh id here, and a Send fired before the
    /// watcher-driven reload lands would otherwise carry <c>id: null</c> and go unrecorded by
    /// request history.</summary>
    private static IResult SaveSpec(WorkspaceService svc, string path, string content, string id)
    {
        try { svc.Save(path, content); return Results.Ok(new SavedSpecDto(id)); }
        catch (WorkspaceParseException ex)
        { return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line)); }
    }

    private static ProviderConfigDto MapProviderConfig(VariableProviderConfig p, IEnumerable<IVariableProviderFactory> factories)
    {
        // Sensitive settings (descriptor-declared secret fields) are echoed back as "***"
        // so the UI can show "set" without ever seeing the clear text. The manifest PUT
        // recognizes the placeholder and restores the on-disk value when round-tripping.
        var descriptor = ProviderSettingsMask.DescriptorFor(factories, p.Type);
        return new ProviderConfigDto(
            Name: p.Name,
            Type: p.Type,
            Settings: ProviderSettingsMask.Apply(descriptor, p.Settings),
            Origin: p.Origin == ProviderOrigin.System ? "system" : "workspace");
    }

    /// <summary>Projects an env's collection assignments onto the wire. Refs travel as written
    /// (relative to the env file) so a round-trip through the editor re-emits them unchanged.</summary>
    private static IReadOnlyList<EnvCollectionDto> ToBindings(EnvFile env)
        => env.Collections
            .Select(b => new EnvCollectionDto(b.Collection, b.BaseUrl, b.DefaultAuth?.RelativePath ?? b.DefaultAuth?.Id))
            .ToArray();

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
}
