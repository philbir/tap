using Tap.Studio.Importing;
using Tap.Studio.OpenApi;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;

namespace Tap.Studio;

/// <summary>
/// On first run, turns each Aspire-referenced API's OpenAPI document into real requests.
///
/// <para><b>Why this is a hosted service and not part of the boot scaffold.</b>
/// <see cref="AspireWorkspaceScaffold"/> runs synchronously before the workspace loads, and that
/// path is already the slowest thing in a cold start. Fetching N documents there would block the
/// Studio from listening on a network call to a service that may still be warming up. This runs
/// after the app is serving, then asks the workspace to reload.</para>
///
/// <para><b>Additive, first-run only, never fatal.</b> A collection that already carries an
/// OpenAPI lock is left completely alone — this is not a re-sync, and re-importing on every
/// AppHost start would fight the developer's edits. If anything fails — the API isn't up, it
/// serves no document, the document doesn't parse — the collection keeps the starter request that
/// the boot scaffold would otherwise have written, and the failure is a log line.</para>
/// </summary>
public sealed class AspireOpenApiScaffold(
    StudioOptions options,
    IConfiguration configuration,
    OpenApiSpecSource source,
    WorkspaceService workspace,
    ILogger<AspireOpenApiScaffold> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsWorkspacePinned) return;

        var apis = AspireWorkspaceScaffold.ReadApis(configuration)
            .Where(a => a.OpenApiRoute is { Length: > 0 })
            .ToArray();
        if (apis.Length == 0) return;

        var store = new OpenApiLockStore(workspace.RootDirectory);
        var imported = 0;

        foreach (var api in apis)
        {
            if (stoppingToken.IsCancellationRequested) return;

            var slug = ImportSlug.Slugify(api.Name);
            if (slug.Length == 0) continue;

            // Already tracked: the developer has this collection, and re-importing would either
            // be a no-op or trample edits. Re-sync is the deliberate, user-driven path for that.
            if (store.Read(slug) is not null) continue;

            try
            {
                if (await ScaffoldAsync(api, slug, store, stoppingToken).ConfigureAwait(false)) imported++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "OpenAPI scaffolding failed for {Api}", api.Name);
                WriteStarter(api, slug);
            }
        }

        if (imported > 0) workspace.ReloadNow();
    }

    private async Task<bool> ScaffoldAsync(
        AspireWorkspaceScaffold.AspireApi api,
        string slug,
        OpenApiLockStore store,
        CancellationToken ct)
    {
        if (ResolveBaseUrl(api.Name) is not { } baseUrl)
        {
            logger.LogDebug("No endpoint for {Api} yet; leaving the starter request in place.", api.Name);
            WriteStarter(api, slug);
            return false;
        }

        var url = baseUrl.TrimEnd('/') + "/" + api.OpenApiRoute!.TrimStart('/');
        var fetched = await source.FetchAsync(url, ct).ConfigureAwait(false);
        if (!fetched.Ok)
        {
            // Expected whenever an API simply doesn't publish a document — debug, not warning.
            logger.LogDebug("No OpenAPI document at {Url}: {Error}", url, fetched.Error);
            WriteStarter(api, slug);
            return false;
        }

        var read = OpenApiDocumentReader.Read(fetched.Text!, url);
        if (!read.Ok)
        {
            logger.LogWarning("The OpenAPI document at {Url} could not be read: {Error}",
                url, read.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message);
            WriteStarter(api, slug);
            return false;
        }

        var options = new OpenApiImportPlanner.Options
        {
            Slug = slug,
            // Ratified for this feature: the audience already has .http open in Visual Studio, and
            // one file per tag keeps a scaffolded workspace readable.
            Layout = OpenApiImportPlanner.Layout.HttpFilePerTag,
            // Never servers[0]. The whole point of the aspire provider is surviving the port Aspire
            // reallocates on every restart, and CI resolving the same workspace from services__*.
            BaseUrl = $"{{{{aspire:{api.Name}}}}}",
        };

        var planned = OpenApiImportPlanner.Plan(read.Document!, options);

        // Merge, never replace: the boot scaffold has already written the collection file, and
        // anything else in that folder belongs to the developer.
        var write = ImportWriter.Write(workspace, planned.Plan, ImportWriter.ExistingCollection.Merge);
        if (!write.Ok)
        {
            logger.LogWarning("Could not scaffold {Api} from OpenAPI: {Message}", api.Name, write.Error!.Message);
            return false;
        }

        store.Write(slug, new OpenApiLock
        {
            Source = new OpenApiLockSource(
                Kind: "aspire",
                Url: url,
                FileName: null,
                FetchedAt: DateTimeOffset.UtcNow,
                DocumentHash: OpenApiImportPlanner.HashContent(fetched.Text!),
                SpecVersion: read.SpecVersion,
                ApiVersion: read.Document!.Info?.Version),
            Layout = "http",
            BaseUrlSource = "aspire",
            Operations = planned.Planned.Select(p => new OpenApiLockOperation(
                p.Operation.OpKey, p.Operation.OperationId, p.Operation.Method, p.Operation.Path,
                p.Operation.SourceHash, p.GeneratedHash, p.FileId, p.RelativePath, p.Fragment)).ToArray(),
        });

        logger.LogInformation(
            "Scaffolded {Count} request(s) for {Api} from {Url}", planned.Plan.RequestCount, api.Name, url);
        return true;
    }

    /// <summary>
    /// Resolves the API's allocated URL from the standard <c>services__{name}__{scheme}__{index}</c>
    /// variables — the same provider that backs <c>{{aspire:name}}</c>, so the document we fetch and
    /// the base URL the requests resolve against can never disagree.
    /// </summary>
    private string? ResolveBaseUrl(string apiName)
    {
        var provider = new AspireVariableProvider(new VariableProviderConfig
        {
            Name = AspireVariableProvider.TypeName,
            Type = AspireVariableProvider.TypeName,
            Origin = ProviderOrigin.System,
        });

        var value = provider.GetAsync(apiName, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(value?.Value) ? null : value.Value;
    }

    /// <summary>The placeholder the boot scaffold skipped because an OpenAPI route was configured.
    /// Written here so a failed fetch still leaves the developer something that sends.</summary>
    private void WriteStarter(AspireWorkspaceScaffold.AspireApi api, string slug)
    {
        try
        {
            AspireWorkspaceScaffold.WriteStarterRequest(workspace.RootDirectory, slug, api.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write the starter request for {Api}", api.Name);
        }
    }
}
