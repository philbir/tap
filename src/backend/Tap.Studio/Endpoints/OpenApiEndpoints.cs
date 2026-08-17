using Tap.Studio.Contracts;
using Tap.Studio.Importing;
using Tap.Studio.OpenApi;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/openapi/*</c> — stage an OpenAPI document, then import selected operations into a
/// collection.
///
/// <para>Staging first is what makes the wizard honest: the operation list the user picks from and
/// the document that gets written are provably the same one, and a URL is fetched exactly once.
/// Nothing is written to disk until <c>/api/collections/import/openapi</c>.</para>
/// </summary>
public static class OpenApiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/openapi");

        // Upload: the client posts the file's text verbatim. Parsing is server-side because the
        // UI has no YAML parser, and because $ref/allOf/Swagger-2.0 normalization must not exist
        // in two places that can disagree.
        g.MapPost("/documents", (OpenApiUploadRequestDto body, OpenApiDocumentCache cache) =>
            Stage(body.Text, sourceUrl: null, body.FileName ?? "document", cache));

        g.MapPost("/documents/fetch", async (
            OpenApiFetchRequestDto body, OpenApiSpecSource source, OpenApiDocumentCache cache, CancellationToken ct) =>
        {
            var fetched = await source.FetchAsync(body.Url, ct).ConfigureAwait(false);
            if (!fetched.Ok)
                return Results.BadRequest(new WorkspaceErrorDto("fetch-failed", fetched.Error!, null, null));

            return Stage(fetched.Text!, body.Url, body.Url, cache);
        });

        // The recorded link, so the collection editor and the import wizard can say what this
        // collection is tied to — and so re-sync knows where to look.
        app.MapGet("/api/collections/{slug}/openapi", (string slug, WorkspaceService svc) =>
        {
            if (!ImportWriter.IsValidSlug(slug))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug", "Bad collection slug.", null, null));

            if (new OpenApiLockStore(svc.RootDirectory).Read(slug) is not { } lockFile)
                return Results.NotFound();

            return Results.Ok(new OpenApiLinkDto(
                Slug: slug,
                SourceKind: lockFile.Source.Kind,
                Url: lockFile.Source.Url,
                FileName: lockFile.Source.FileName,
                FetchedAt: lockFile.Source.FetchedAt,
                SpecVersion: lockFile.Source.SpecVersion,
                ApiVersion: lockFile.Source.ApiVersion,
                DocumentHash: lockFile.Source.DocumentHash,
                Layout: lockFile.Layout,
                TrackedOperations: lockFile.Operations.Count));
        });

        // --- re-sync -------------------------------------------------------------------------
        // Preview writes nothing. Apply takes an explicit decision per operation, so nothing
        // destructive can happen because the user clicked through a dialog too fast.

        app.MapPost("/api/collections/{slug}/openapi/resync/preview", (
            string slug, OpenApiResyncRequestDto body, OpenApiDocumentCache cache, WorkspaceService svc) =>
        {
            if (LoadContext(slug, body.DocumentId, cache, svc, out var lockFile, out var staged) is { } failure)
                return failure;

            var plan = new OpenApiResyncService(svc).Diff(lockFile!, staged!.Document);

            return Results.Ok(new OpenApiResyncPreviewDto(
                Slug: slug,
                Layout: lockFile!.Layout,
                SourceUrl: lockFile.Source.Url,
                PreviouslyFetchedAt: lockFile.Source.FetchedAt,
                PreviousApiVersion: lockFile.Source.ApiVersion,
                NewApiVersion: staged.Document.Info?.Version,
                DocumentUnchanged: string.Equals(
                    lockFile.Source.DocumentHash, staged.ContentHash, StringComparison.Ordinal),
                Added: plan.Added,
                Changed: plan.Changed,
                Conflicts: plan.Conflicts,
                Removed: plan.Removed,
                Changes: plan.Changes.Select(ToDto).ToArray()));
        });

        app.MapPost("/api/collections/{slug}/openapi/resync", (
            string slug, OpenApiResyncApplyRequestDto body, OpenApiDocumentCache cache, WorkspaceService svc) =>
        {
            if (LoadContext(slug, body.DocumentId, cache, svc, out var lockFile, out var staged) is { } failure)
                return failure;

            var decisions = new Dictionary<string, OpenApiResyncService.DecisionAction>(StringComparer.Ordinal);
            foreach (var d in body.Decisions ?? [])
            {
                if (ParseAction(d.Action) is { } action) decisions[d.OpKey] = action;
            }

            // Re-sync rewrites individual requests, never the collection file, so the base URL the
            // user (or the Aspire scaffold) set is untouched by construction — an `{{aspire:name}}`
            // template keeps following the allocated port.
            var options = new OpenApiImportPlanner.Options
            {
                Slug = slug,
                Layout = OpenApiImportPlanner.ParseLayout(lockFile!.Layout),
            };

            var result = new OpenApiResyncService(svc).Apply(
                slug, lockFile, staged!.Document, options, decisions, staged.ContentHash);

            return Results.Ok(new OpenApiResyncResultDto(
                result.Added, result.Updated, result.Deprecated, result.Untracked, result.Skipped,
                result.WrittenPaths, result.Warnings));
        });

        // Import lives under /api/collections to sit beside the Postman importer — same shape,
        // same write path, different input format.
        app.MapPost("/api/collections/import/openapi", (
            OpenApiImportRequestDto body, OpenApiDocumentCache cache, WorkspaceService svc) =>
        {
            if (cache.Get(body.DocumentId) is not { } staged)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    "document-expired",
                    "That OpenAPI document is no longer staged. Upload or fetch it again.", null, null));
            }

            var options = new OpenApiImportPlanner.Options
            {
                Slug = body.Slug,
                Layout = OpenApiImportPlanner.ParseLayout(body.Layout),
                OperationKeys = body.OperationKeys is { Count: > 0 } keys
                    ? new HashSet<string>(keys, StringComparer.Ordinal)
                    : null,
                BaseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? null : body.BaseUrl.Trim(),
                SecuritySchemeKey = string.IsNullOrWhiteSpace(body.SecuritySchemeKey) ? null : body.SecuritySchemeKey,
                LinkAuthPath = string.IsNullOrWhiteSpace(body.LinkAuthPath) ? null : body.LinkAuthPath,
                IncludeOptionalQueryParams = body.IncludeOptionalQueryParams,
                VariableDefaults = body.VariableDefaults,
            };

            OpenApiImportPlanner.Result planned;
            try
            {
                planned = OpenApiImportPlanner.Plan(staged.Document, options);
            }
            catch (OpenApiImportException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Code, ex.Message, null, null));
            }

            var store = new OpenApiLockStore(svc.RootDirectory);
            var existing = store.Read(planned.Plan.Slug);
            var mode = ResolveMode(body);

            // Importing a *different* document over a tracked collection would leave the lock
            // describing files that no longer came from it, so re-sync would compare against the
            // wrong upstream. Replacing is still allowed — that's an explicit "start over".
            if (mode == ImportWriter.ExistingCollection.Merge
                && existing is not null
                && !SameSource(existing, staged))
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    "different-source",
                    $"Collection '{planned.Plan.Slug}' is already linked to "
                    + $"{Describe(existing.Source)}. Re-sync it from there, or choose Replace to "
                    + "start over from this document.",
                    $"{ImportWriter.CollectionsRoot}/{planned.Plan.Slug}", null));
            }

            var write = ImportWriter.Write(svc, planned.Plan, mode);
            if (!write.Ok) return Results.BadRequest(write.Error);

            // The link is what makes re-sync possible later; write it after the files land so a
            // failed import never leaves a lock pointing at content that was not written.
            store.Write(planned.Plan.Slug, BuildLock(planned, staged, options, body));

            // Beat the file-watcher debounce so the client's follow-up reload sees the new files.
            svc.ReloadNow();

            return Results.Ok(new OpenApiImportResponseDto(
                Slug: planned.Plan.Slug,
                CollectionPath: planned.Plan.CollectionPath,
                AuthPath: planned.Plan.AuthPath,
                RequestCount: planned.Plan.RequestCount,
                FileCount: planned.Plan.Files.Count,
                Warnings: planned.Plan.Warnings));
        });
    }

    /// <summary>Loads the lock and the staged document, or returns the error result to send back.</summary>
    private static IResult? LoadContext(
        string slug,
        string documentId,
        OpenApiDocumentCache cache,
        WorkspaceService svc,
        out OpenApiLock? lockFile,
        out OpenApiDocumentCache.Staged? staged)
    {
        lockFile = null;
        staged = null;

        if (!ImportWriter.IsValidSlug(slug))
            return Results.BadRequest(new WorkspaceErrorDto("invalid-slug", "Bad collection slug.", null, null));

        lockFile = new OpenApiLockStore(svc.RootDirectory).Read(slug);
        if (lockFile is null)
        {
            return Results.BadRequest(new WorkspaceErrorDto(
                "not-linked",
                $"Collection '{slug}' is not linked to an OpenAPI document, so there is nothing to "
                + "re-sync. Import one into it first.", null, null));
        }

        staged = cache.Get(documentId);
        if (staged is null)
        {
            return Results.BadRequest(new WorkspaceErrorDto(
                "document-expired",
                "That OpenAPI document is no longer staged. Fetch it again.", null, null));
        }

        return null;
    }

    private static OpenApiResyncService.DecisionAction? ParseAction(string? action) => action?.ToLowerInvariant() switch
    {
        "add" => OpenApiResyncService.DecisionAction.Add,
        "update" => OpenApiResyncService.DecisionAction.Update,
        "deprecate" => OpenApiResyncService.DecisionAction.Deprecate,
        "untrack" => OpenApiResyncService.DecisionAction.Untrack,
        "skip" => OpenApiResyncService.DecisionAction.Skip,
        _ => null,
    };

    private static OpenApiChangeDto ToDto(OpenApiResyncPlanner.Change change)
    {
        var kind = change.Kind switch
        {
            OpenApiResyncPlanner.ChangeKind.Added => "added",
            OpenApiResyncPlanner.ChangeKind.Changed => "changed",
            OpenApiResyncPlanner.ChangeKind.Conflict => "conflict",
            OpenApiResyncPlanner.ChangeKind.Orphaned => "orphaned",
            OpenApiResyncPlanner.ChangeKind.Removed => "removed",
            _ => "unchanged",
        };

        // Never pre-select something destructive. A conflict defaults to keeping the user's file;
        // a vanished operation defaults to a deprecation tag rather than deletion.
        var defaultAction = change.Kind switch
        {
            OpenApiResyncPlanner.ChangeKind.Added => "add",
            OpenApiResyncPlanner.ChangeKind.Changed => "update",
            OpenApiResyncPlanner.ChangeKind.Conflict => "skip",
            OpenApiResyncPlanner.ChangeKind.Removed => "deprecate",
            OpenApiResyncPlanner.ChangeKind.Orphaned => "skip",
            _ => "skip",
        };

        return new OpenApiChangeDto(
            kind, change.OpKey, change.Method, change.Path, change.Summary,
            change.LocalPath, change.Fragment, change.LocallyEdited, defaultAction);
    }

    /// <summary>Legacy <c>overwrite</c> still means replace; otherwise the explicit mode wins.</summary>
    private static ImportWriter.ExistingCollection ResolveMode(OpenApiImportRequestDto body)
    {
        if (body.Overwrite) return ImportWriter.ExistingCollection.Replace;
        return body.Mode?.ToLowerInvariant() switch
        {
            "merge" => ImportWriter.ExistingCollection.Merge,
            "replace" => ImportWriter.ExistingCollection.Replace,
            _ => ImportWriter.ExistingCollection.Reject,
        };
    }

    /// <summary>Same document, by content hash when we have one, else by where it came from.</summary>
    private static bool SameSource(OpenApiLock existing, OpenApiDocumentCache.Staged staged)
    {
        if (existing.Source.Url is { Length: > 0 } url)
            return string.Equals(url, staged.SourceUrl, StringComparison.OrdinalIgnoreCase);
        return string.Equals(existing.Source.DocumentHash, staged.ContentHash, StringComparison.Ordinal)
            || string.Equals(existing.Source.FileName, staged.SourceFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(OpenApiLockSource source)
        => source.Url is { Length: > 0 } url ? url : source.FileName ?? "another document";

    private static OpenApiLock BuildLock(
        OpenApiImportPlanner.Result planned,
        OpenApiDocumentCache.Staged staged,
        OpenApiImportPlanner.Options options,
        OpenApiImportRequestDto body)
        => new()
        {
            Source = new OpenApiLockSource(
                Kind: staged.SourceUrl is not null ? "url" : "file",
                Url: staged.SourceUrl,
                FileName: staged.SourceFileName,
                FetchedAt: DateTimeOffset.UtcNow,
                DocumentHash: staged.ContentHash,
                SpecVersion: staged.SpecVersion,
                ApiVersion: staged.Document.Info?.Version),
            Layout = options.Layout == OpenApiImportPlanner.Layout.HttpFilePerTag ? "http" : "req",
            // A `{{aspire:name}}` base URL must survive re-sync — overwriting it with servers[0]
            // would break the moment Aspire reallocated the port.
            BaseUrlSource = body.BaseUrl is { Length: > 0 } manual
                ? (manual.Contains("{{aspire:", StringComparison.Ordinal) ? "aspire" : "manual")
                : "servers[0]",
            Operations = planned.Planned
                .Select(p => new OpenApiLockOperation(
                    OpKey: p.Operation.OpKey,
                    OperationId: p.Operation.OperationId,
                    Method: p.Operation.Method,
                    Path: p.Operation.Path,
                    UpstreamHash: p.Operation.SourceHash,
                    GeneratedHash: p.GeneratedHash,
                    FileId: p.FileId,
                    RelativePath: p.RelativePath,
                    Fragment: p.Fragment))
                .ToArray(),
        };

    private static IResult Stage(string text, string? sourceUrl, string sourceName, OpenApiDocumentCache cache)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > OpenApiDocumentReader.MaxDocumentBytes)
        {
            return Results.BadRequest(new WorkspaceErrorDto(
                "document-too-large",
                $"The document is larger than {OpenApiDocumentReader.MaxDocumentBytes / (1024 * 1024)} MB.",
                null, null));
        }

        var read = OpenApiDocumentReader.Read(text, sourceName);
        if (!read.Ok)
        {
            var message = read.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message
                ?? "The document could not be read as an OpenAPI description.";
            return Results.BadRequest(new WorkspaceErrorDto("invalid-document", message, null, null));
        }

        var document = read.Document!;
        var staged = cache.Add(document, text, read.SpecVersion, sourceUrl,
            sourceUrl is null ? sourceName : null, read.Diagnostics);

        var warnings = new List<string>();
        var operations = OpenApiOperationMapper.Map(document, warnings);

        var title = document.Info?.Title is { Length: > 0 } t ? t : "API";

        return Results.Ok(new OpenApiDocumentDto(
            DocumentId: staged.DocumentId,
            Title: title,
            ApiVersion: document.Info?.Version,
            SpecVersion: read.SpecVersion,
            Description: document.Info?.Description,
            SuggestedSlug: ImportSlug.Slugify(title),
            Servers: (document.Servers ?? [])
                .Select(s => new OpenApiServerDto(s.Url ?? string.Empty, s.Description))
                .Where(s => s.Url.Length > 0)
                .ToArray(),
            SecuritySchemes: OpenApiSecurityMapper.Describe(document)
                .Select(s => new OpenApiSecuritySchemeDto(
                    s.Key, s.Type, s.TapAuthType, s.Description, s.Scopes, s.Warning))
                .ToArray(),
            Operations: operations
                .Select(o => new OpenApiOperationDto(
                    OpKey: o.OpKey,
                    OperationId: o.OperationId,
                    Method: o.Method,
                    Path: o.Path,
                    Summary: o.Summary,
                    Tags: o.Tags,
                    Deprecated: o.Deprecated,
                    HasRequestBody: o.RequestBody is not null,
                    PathParamCount: o.Parameters.Count(p => p.In == ParameterIn.Path),
                    QueryParamCount: o.Parameters.Count(p => p.In == ParameterIn.Query)))
                .ToArray(),
            Diagnostics: read.Diagnostics
                .Select(d => new OpenApiDiagnosticDto(d.Severity, d.Message, d.Pointer))
                .Concat(warnings.Select(w => new OpenApiDiagnosticDto("warning", w, null)))
                .ToArray()));
    }
}
