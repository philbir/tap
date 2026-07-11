using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;

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
                .Select(a => new AuthSummaryDto(a.RelativePath, a.Name ?? Stem(a.RelativePath), a.Id, a.Type))
                .ToArray()));

        auths.MapGet("/{*path}", (string path, WorkspaceService svc) =>
        {
            if (svc.Current.FindByPath(path) is not AuthFile a) return Results.NotFound();
            return Results.Ok(new AuthDetailDto(
                Path: a.RelativePath,
                Name: a.Name ?? Stem(a.RelativePath),
                Id: a.Id,
                Type: a.Type,
                Fields: a.Fields,
                Headers: a.Headers,
                Query: a.Query,
                Scopes: a.Scopes,
                Tags: a.Tags,
                Body: a.Body,
                Source: svc.ReadSource(a.RelativePath)));
        });

        auths.MapPut("/spec", (AuthSpecDto spec, WorkspaceService svc) => SaveSpec(svc, spec.Path, AuthSpecEmitter.ToFileSource(spec)));

        // ----- Environments -----
        var envs = app.MapGroup("/api/environments");
        envs.MapGet("/", (WorkspaceService svc) => Results.Ok(
            (IReadOnlyList<EnvSummaryDto>)svc.Current.Environments
                .Select(e => new EnvSummaryDto(e.RelativePath, e.Name ?? Stem(e.RelativePath), e.Id))
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
                Source: svc.ReadSource(e.RelativePath)));
        });

        envs.MapPut("/spec", (EnvSpecDto spec, WorkspaceService svc) => SaveSpec(svc, spec.Path, EnvSpecEmitter.ToFileSource(spec)));

        // ----- Workspace manifest -----
        app.MapGet("/api/workspace/manifest", (WorkspaceService svc) =>
        {
            var m = svc.Current.Manifest;
            if (m is null) return Results.NotFound();
            return Results.Ok(new WorkspaceDetailDto(
                Name: m.Name ?? "workspace",
                Id: m.Id,
                DefaultEnv: m.DefaultEnv?.RelativePath,
                VariableProviders: m.VariableProviders.Select(MapProviderConfig).ToArray(),
                DefaultVariableProvider: m.DefaultVariableProvider,
                Vars: m.Vars,
                Tags: m.Tags,
                Body: m.Body,
                Source: svc.ReadSource(m.RelativePath)));
        });

        app.MapPut("/api/workspace/manifest/spec", (WorkspaceSpecDto spec, WorkspaceService svc) =>
            SaveSpec(svc, "tap.md", WorkspaceSpecEmitter.ToFileSource(spec)));
    }

    private static IResult SaveSpec(WorkspaceService svc, string path, string content)
    {
        try { svc.Save(path, content); return Results.NoContent(); }
        catch (WorkspaceParseException ex)
        { return Results.BadRequest(new WorkspaceErrorDto(ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line)); }
    }

    private static ProviderConfigDto MapProviderConfig(Tap.Workspace.Variables.VariableProviderConfig p)
    {
        var masked = new Dictionary<string, string?>(p.Settings.Count);
        foreach (var (k, v) in p.Settings)
        {
            // Sensitive settings are echoed back as "***" so the UI can show "set" without
            // ever seeing the clear text. WorkspaceSpecEmitter recognizes the placeholder
            // and preserves the on-disk value when round-tripping.
            masked[k] = string.Equals(k, "encryptionKey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v)
                ? "***" : v;
        }
        return new ProviderConfigDto(
            Name: p.Name,
            Type: p.Type,
            Settings: masked,
            Origin: p.Origin == Tap.Workspace.Variables.ProviderOrigin.System ? "system" : "workspace");
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
}
