using Tap.Studio.Contracts;
using Tap.Studio.Importing;
using Tap.Studio.Wsdl;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/wsdl/*</c> — stage a WSDL description, then import selected operations into a
/// collection as SOAP requests.
///
/// <para>The same two-phase shape as <see cref="OpenApiEndpoints"/>, deliberately: staging is what
/// makes the wizard honest — the operation list the user picks from and the document that gets
/// written are provably the same one, and a URL is fetched exactly once. Nothing is written to
/// disk until <c>/api/collections/import/wsdl</c>.</para>
/// </summary>
public static class WsdlEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/wsdl");

        // Upload: the client posts the file's text verbatim. Parsing is server-side because
        // resolving a message through its binding into an inlined schema must not exist in two
        // places that can disagree — and the browser has no XSD walker.
        g.MapPost("/documents", (WsdlUploadRequestDto body, WsdlDocumentCache cache) =>
            Stage(body.Text, sourceUrl: null, body.FileName ?? "document", cache));

        g.MapPost("/documents/fetch", async (
            WsdlFetchRequestDto body, WsdlSpecSource source, WsdlDocumentCache cache, CancellationToken ct) =>
        {
            var fetched = await source.FetchAsync(body.Url, ct).ConfigureAwait(false);
            if (!fetched.Ok)
                return Results.BadRequest(new WorkspaceErrorDto("fetch-failed", fetched.Error!, null, null));

            return Stage(fetched.Text!, body.Url, body.Url, cache);
        });

        // The recorded link, so the import wizard can say what this collection is tied to and
        // offer to fetch the same description again.
        app.MapGet("/api/collections/{slug}/wsdl", (string slug, WorkspaceService svc) =>
        {
            if (!ImportWriter.IsValidSlug(slug))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-slug", "Bad collection slug.", null, null));

            if (new WsdlLockStore(svc.RootDirectory).Read(slug) is not { } lockFile)
                return Results.NotFound();

            return Results.Ok(new WsdlLinkDto(
                Slug: slug,
                SourceKind: lockFile.Source.Kind,
                Url: lockFile.Source.Url,
                FileName: lockFile.Source.FileName,
                FetchedAt: lockFile.Source.FetchedAt,
                ServiceName: lockFile.Source.ServiceName,
                TargetNamespace: lockFile.Source.TargetNamespace,
                DocumentHash: lockFile.Source.DocumentHash,
                Layout: lockFile.Layout,
                UsernameTokenHeader: lockFile.UsernameTokenHeader,
                TrackedOperations: lockFile.Operations.Count));
        });

        // Import lives under /api/collections to sit beside the OpenAPI and Postman importers —
        // same shape, same write path, different input format.
        app.MapPost("/api/collections/import/wsdl", (
            WsdlImportRequestDto body, WsdlDocumentCache cache, WorkspaceService svc) =>
        {
            if (cache.Get(body.DocumentId) is not { } staged)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    "document-expired",
                    "That WSDL is no longer staged. Upload or fetch it again.", null, null));
            }

            var options = new WsdlImportPlanner.Options
            {
                Slug = body.Slug,
                Layout = WsdlImportPlanner.ParseLayout(body.Layout),
                OperationKeys = body.OperationKeys is { Count: > 0 } keys
                    ? new HashSet<string>(keys, StringComparer.Ordinal)
                    : null,
                BaseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? null : body.BaseUrl.Trim(),
                LinkAuthPath = string.IsNullOrWhiteSpace(body.LinkAuthPath) ? null : body.LinkAuthPath,
                AddUsernameToken = body.AddUsernameToken,
            };

            WsdlImportPlanner.Result planned;
            try
            {
                planned = WsdlImportPlanner.Plan(staged.Document, options);
            }
            catch (WsdlImportException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(ex.Code, ex.Message, null, null));
            }

            var store = new WsdlLockStore(svc.RootDirectory);
            var existing = store.Read(planned.Plan.Slug);
            var mode = ResolveMode(body.Mode);

            // Importing a *different* description over a tracked collection would leave the lock
            // describing files that no longer came from it. Replacing is still allowed — that's an
            // explicit "start over".
            if (mode == ImportWriter.ExistingCollection.Merge
                && existing is not null
                && !SameSource(existing, staged))
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    "different-source",
                    $"Collection '{planned.Plan.Slug}' is already linked to "
                    + $"{Describe(existing.Source)}. Choose Replace to start over from this "
                    + "description.",
                    $"{ImportWriter.CollectionsRoot}/{planned.Plan.Slug}", null));
            }

            var write = ImportWriter.Write(svc, planned.Plan, mode);
            if (!write.Ok) return Results.BadRequest(write.Error);

            // Written after the files land, so a failed import never leaves a lock pointing at
            // content that was not written.
            store.Write(planned.Plan.Slug, BuildLock(planned, staged, options, body));

            // Beat the file-watcher debounce so the client's follow-up reload sees the new files.
            svc.ReloadNow();

            return Results.Ok(new WsdlImportResponseDto(
                Slug: planned.Plan.Slug,
                CollectionPath: planned.Plan.CollectionPath,
                RequestCount: planned.Plan.RequestCount,
                FileCount: planned.Plan.Files.Count,
                BaseUrl: planned.BaseUrl,
                Warnings: planned.Plan.Warnings));
        });
    }

    private static ImportWriter.ExistingCollection ResolveMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "merge" => ImportWriter.ExistingCollection.Merge,
        "replace" => ImportWriter.ExistingCollection.Replace,
        _ => ImportWriter.ExistingCollection.Reject,
    };

    /// <summary>Same document, by URL when we have one, else by content hash or file name.</summary>
    private static bool SameSource(WsdlLock existing, StagedDocument<WsdlDefinitions> staged)
    {
        if (existing.Source.Url is { Length: > 0 } url)
            return string.Equals(url, staged.SourceUrl, StringComparison.OrdinalIgnoreCase);
        return string.Equals(existing.Source.DocumentHash, staged.ContentHash, StringComparison.Ordinal)
            || string.Equals(existing.Source.FileName, staged.SourceFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(WsdlLockSource source)
        => source.Url is { Length: > 0 } url ? url : source.FileName ?? "another description";

    private static WsdlLock BuildLock(
        WsdlImportPlanner.Result planned,
        StagedDocument<WsdlDefinitions> staged,
        WsdlImportPlanner.Options options,
        WsdlImportRequestDto body)
        => new()
        {
            Source = new WsdlLockSource(
                Kind: staged.SourceUrl is not null ? "url" : "file",
                Url: staged.SourceUrl,
                FileName: staged.SourceFileName,
                FetchedAt: DateTimeOffset.UtcNow,
                DocumentHash: staged.ContentHash,
                SpecVersion: staged.SpecVersion,
                ServiceName: staged.Document.Services.FirstOrDefault()?.Name,
                TargetNamespace: staged.Document.TargetNamespace),
            Layout = options.Layout == WsdlImportPlanner.Layout.HttpFilePerPort ? "http" : "req",
            BaseUrlSource = body.BaseUrl is { Length: > 0 } ? "manual" : "address",
            UsernameTokenHeader = options.AddUsernameToken,
            Operations = planned.Planned
                .Select(p => new WsdlLockOperation(
                    OpKey: p.Operation.OpKey,
                    Service: p.Operation.ServiceName,
                    Port: p.Operation.PortName,
                    Name: p.Operation.Name,
                    UpstreamHash: p.Operation.SourceHash,
                    GeneratedHash: p.GeneratedHash,
                    FileId: p.FileId,
                    RelativePath: p.RelativePath,
                    Fragment: p.Fragment))
                .ToArray(),
        };

    private static IResult Stage(string text, string? sourceUrl, string sourceName, WsdlDocumentCache cache)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > WsdlDocumentReader.MaxDocumentBytes)
            return Results.BadRequest(new WorkspaceErrorDto(
                "document-too-large", RemoteDocumentSource.TooLarge(WsdlDocumentReader.MaxDocumentBytes),
                null, null));

        var read = WsdlDocumentReader.Read(text, sourceName);
        if (!read.Ok)
        {
            var message = read.Diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message
                ?? "The document could not be read as a WSDL description.";
            return Results.BadRequest(new WorkspaceErrorDto("invalid-document", message, null, null));
        }

        var definitions = read.Document!;
        var staged = cache.Add(definitions, text, read.SpecVersion, sourceUrl, sourceUrl is null ? sourceName : null);

        var warnings = new List<string>();
        var operations = WsdlOperationMapper.Map(definitions, warnings);
        var ports = DescribePorts(operations);

        var title = definitions.Services.FirstOrDefault()?.Name
            ?? definitions.Name
            ?? operations.FirstOrDefault()?.ServiceName
            ?? "SOAP service";

        return Results.Ok(new WsdlDocumentDto(
            DocumentId: staged.DocumentId,
            Title: title,
            SpecVersion: read.SpecVersion,
            TargetNamespace: definitions.TargetNamespace,
            Description: definitions.Documentation,
            SuggestedSlug: ImportSlug.Slugify(title),
            Addresses: operations
                .Select(o => o.Address)
                .Where(a => a is { Length: > 0 })
                .Select(a => a!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            WantsUsernameToken: definitions.WantsUsernameToken,
            Ports: ports,
            Operations: operations.Select(o => new WsdlOperationDto(
                OpKey: o.OpKey,
                PortKey: PortKey(o),
                Service: o.ServiceName,
                Port: o.PortName,
                Name: o.Name,
                SoapAction: o.SoapAction,
                Documentation: o.Documentation,
                SoapVersion: VersionLabel(o.Version),
                Style: o.Style.ToString().ToLowerInvariant(),
                BodyElement: o.BodyElement,
                HasBody: o.BodyPayload.Length > 0)).ToArray(),
            Diagnostics: read.Diagnostics
                .Select(d => new WsdlDiagnosticDto(d.Severity, d.Message, d.Pointer))
                .Concat(warnings.Select(w => new WsdlDiagnosticDto("warning", w, null)))
                .ToArray()));
    }

    /// <summary>
    /// One row per port, with the flag the wizard needs to avoid the single most common WSDL
    /// footgun: a .NET service binds the same operations over SOAP 1.1 <i>and</i> 1.2, and
    /// importing both produces two of every request.
    /// </summary>
    private static WsdlPortDto[] DescribePorts(IReadOnlyList<MappedSoapOperation> operations)
    {
        var groups = operations
            .GroupBy(PortKey, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, First: g.First(), Count: g.Count(),
                          Names: g.Select(o => o.Name).ToHashSet(StringComparer.Ordinal)))
            .ToArray();

        return groups
            .Select(g => new WsdlPortDto(
                Key: g.Key,
                Service: g.First.ServiceName,
                Port: g.First.PortName,
                Address: g.First.Address,
                SoapVersion: VersionLabel(g.First.Version),
                Style: g.First.Style.ToString().ToLowerInvariant(),
                OperationCount: g.Count,
                HasSibling: groups.Any(other => other.Key != g.Key && other.Names.SetEquals(g.Names))))
            .ToArray();
    }

    private static string PortKey(MappedSoapOperation operation)
        => $"{operation.ServiceName}/{operation.PortName}";

    private static string VersionLabel(SoapVersion version)
        => version == SoapVersion.Soap12 ? "1.2" : "1.1";
}
