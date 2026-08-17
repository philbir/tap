using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;
using Tap.Studio.Contracts;
using Tap.Studio.Importing;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Plans an OpenAPI import: a collection, an optional auth profile, and either one
/// <c>.req.tap</c> per operation or one <c>.http</c> file per tag.
///
/// <para>A pure planner, exactly like <c>PostmanImporter</c> — it returns
/// <see cref="ImportPlan"/> and touches no filesystem. The endpoint writes every file through
/// <c>WorkspaceService.Save</c>, so an imported file passes the same validation as a hand edit.
/// Tap-authored kinds go through the Specs emitters; YAML is never hand-assembled.</para>
/// </summary>
public static class OpenApiImportPlanner
{
    public enum Layout
    {
        /// <summary>One <c>.req.tap</c> per operation — structured, assertion-capable.</summary>
        RequestPerOperation,

        /// <summary>One <c>.http</c> file per tag, N requests inside — compact, portable.</summary>
        HttpFilePerTag,
    }

    /// <summary>Maps the wire value to a layout. Anything other than <c>http</c> — including null
    /// and an unrecognized string — means the structured layout, so a client that omits the field
    /// gets the safer of the two.</summary>
    public static Layout ParseLayout(string? wire)
        => string.Equals(wire, "http", StringComparison.OrdinalIgnoreCase)
            ? Layout.HttpFilePerTag
            : Layout.RequestPerOperation;

    public sealed record Options
    {
        public string? Slug { get; init; }
        public Layout Layout { get; init; } = Layout.RequestPerOperation;

        /// <summary>Null imports everything.</summary>
        public IReadOnlyCollection<string>? OperationKeys { get; init; }

        /// <summary>Overrides the URL derived from <c>servers[]</c>. The Aspire scaffold passes
        /// <c>{{aspire:name}}</c> here so the collection survives reallocated ports.</summary>
        public string? BaseUrl { get; init; }

        /// <summary>Security scheme to generate an auth profile from, or null for none.</summary>
        public string? SecuritySchemeKey { get; init; }

        /// <summary>Point at an existing profile instead of generating one.</summary>
        public string? LinkAuthPath { get; init; }

        public bool IncludeOptionalQueryParams { get; init; }

        /// <summary>Seed values for generated variables, keyed by opKey then variable name.
        /// Where an accepted AI suggestion lands; everything works identically without it.</summary>
        public IReadOnlyDictionary<string, Dictionary<string, string>>? VariableDefaults { get; init; }
    }

    /// <summary>Where one operation ended up, so the lock can point back at it.</summary>
    public sealed record PlannedOperation(
        MappedOperation Operation,
        string RelativePath,
        string? Fragment,
        string? FileId,
        /// <summary>Hash of exactly what was written for this operation — the whole file for
        /// <c>.req.tap</c>, just the <c>###</c> section for <c>.http</c>. Compared against disk
        /// later, this is what distinguishes a user's edit from an untouched generated file.</summary>
        string GeneratedHash);

    public sealed record Result(
        ImportPlan Plan,
        IReadOnlyList<MappedOperation> Operations,
        IReadOnlyList<PlannedOperation> Planned);

    public static Result Plan(OpenApiDocument document, Options options)
    {
        var warnings = new List<string>();
        var all = OpenApiOperationMapper.Map(document, warnings);

        var selected = options.OperationKeys is { Count: > 0 } keys
            ? all.Where(o => keys.Contains(o.OpKey)).ToArray()
            : all.ToArray();

        if (selected.Length == 0)
            throw new OpenApiImportException("no-operations", "No operations were selected for import.");

        foreach (var op in selected)
            warnings.AddRange(op.Warnings.Select(w => $"{op.Method} {op.Path}: {w}"));

        var title = document.Info?.Title is { Length: > 0 } t ? t : "API";
        var slug = ImportSlug.Slugify(options.Slug ?? title);
        if (slug.Length == 0)
            throw new OpenApiImportException("invalid-slug", "Could not derive a slug from the API title.");

        var collectionDir = $"{ImportWriter.CollectionsRoot}/{slug}";
        var files = new List<ImportFile>();

        // --- auth -----------------------------------------------------------------------------
        string? defaultAuth = options.LinkAuthPath;
        string? authPath = null;

        if (defaultAuth is null && options.SecuritySchemeKey is { Length: > 0 } schemeKey)
        {
            var authSpec = OpenApiSecurityMapper.Build(document, schemeKey, slug, warnings);
            if (authSpec is not null)
            {
                // Written inside the collection and referenced as a bare sibling, so deleting the
                // collection never strands an orphan under auth/ — same call the Postman importer makes.
                // Scheme keys are camelCase by convention (`bearerAuth`), so split before
                // slugifying or the filename collapses to `bearerauth`.
                var authFileName = KindResolver.FileNameFor(
                    WorkspaceKind.Auth, ImportSlug.Slugify($"{slug}-{RequestSlug.Humanize(schemeKey)}"));
                authPath = $"{collectionDir}/{authFileName}";
                files.Add(new ImportFile(authPath, AuthSpecEmitter.ToFileSource(authSpec with { Path = authPath })));
                defaultAuth = authFileName;
            }
        }

        // --- collection -----------------------------------------------------------------------
        var servers = document.Servers ?? [];
        var baseUrl = options.BaseUrl ?? servers.FirstOrDefault()?.Url;

        // Extra servers become stages, which is what they are: the same API at another address.
        var stages = options.BaseUrl is not null || servers.Count <= 1
            ? null
            : servers.Skip(1)
                .Select((s, i) => new CollectionStageSpecDto
                {
                    Name = StageName(s, i),
                    BaseUrl = s.Url,
                })
                .ToArray();

        var collectionSpec = new CollectionSpecDto
        {
            Slug = slug,
            Name = title,
            BaseUrl = baseUrl,
            DefaultAuth = defaultAuth,
            Body = CollectionDocs(document, selected.Length),
            Stages = stages,
        };
        var collectionPath = $"{collectionDir}/{KindResolver.CollectionFileName}";
        files.Insert(0, new ImportFile(collectionPath, CollectionSpecEmitter.ToFileSource(collectionSpec)));

        // --- requests ---------------------------------------------------------------------------
        var planned = options.Layout == Layout.HttpFilePerTag
            ? AddHttpFiles(files, selected, collectionDir, defaultAuth, baseUrl)
            : AddRequestFiles(files, selected, collectionDir, options);

        var plan = new ImportPlan(slug, collectionPath, authPath, files, warnings)
        {
            RequestCount = selected.Length,
        };

        return new Result(plan, selected, planned);
    }

    /// <summary>SHA-256 of the exact text written, so a later read can tell "untouched" from
    /// "hand-edited" without keeping a copy of the file. Re-sync hashes the file on disk with this
    /// same function and compares against the lock.</summary>
    public static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static IReadOnlyList<PlannedOperation> AddRequestFiles(
        List<ImportFile> files, IReadOnlyList<MappedOperation> operations, string collectionDir, Options options)
    {
        var planned = new List<PlannedOperation>(operations.Count);
        // One set of taken names per directory, so `-2` suffixes only apply where they must.
        var used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in operations)
        {
            // Group by first tag, matching how the API's own documentation is organized.
            var folder = op.Tags.Count > 0 ? RequestSlug.ForTag(op.Tags[0]) : string.Empty;
            var dir = folder.Length > 0 ? $"{collectionDir}/{folder}" : collectionDir;

            if (!used.TryGetValue(dir, out var siblings))
            {
                siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used[dir] = siblings;
            }

            var slug = ImportSlug.UniqueSlug(RequestSlug.For(op), siblings, "request");
            var relPath = $"{dir}/{KindResolver.FileNameFor(WorkspaceKind.Request, slug)}";

            var spec = BuildRequestSpec(op, relPath, options);
            var content = RequestSpecEmitter.ToFileSource(spec);
            files.Add(new ImportFile(relPath, content));
            planned.Add(new PlannedOperation(op, relPath, null, spec.Id, HashContent(content)));
        }

        return planned;
    }

    public static RequestSpecDto BuildRequestSpec(MappedOperation op, string relPath, Options options)
    {
        var headers = new List<HttpHeaderSpecDto> { new("Accept", "application/json") };
        if (op.RequestContentType is { Length: > 0 } contentType && op.RequestBody is not null)
            headers.Insert(0, new HttpHeaderSpecDto("Content-Type", contentType));

        // Header parameters are part of the request, not variables — declare them with a token the
        // user fills in rather than inventing a value.
        foreach (var p in op.Parameters.Where(p => p.In == ParameterIn.Header))
            headers.Add(new HttpHeaderSpecDto(p.Name, $"{{{{{p.Name}}}}}"));

        // Declare a var for exactly the parameters that appear in the URL. An optional query
        // parameter that was left out of the request line would otherwise show up as an input the
        // user can fill in that changes nothing — turn IncludeOptionalQueryParams on and it lands
        // in both places together.
        var defaults = options.VariableDefaults is not null
            && options.VariableDefaults.TryGetValue(op.OpKey, out var d) ? d : null;

        // Precedence: an accepted suggestion, then the spec's own example, then empty.
        var vars = op.Parameters
            .Where(p => p.In == ParameterIn.Path
                     || (p.In == ParameterIn.Query && (options.IncludeOptionalQueryParams || p.Required)))
            .ToDictionary(
                p => p.Name,
                p => defaults is not null && defaults.TryGetValue(p.Name, out var seed) && seed is { Length: > 0 }
                    ? seed
                    : p.Example ?? string.Empty,
                StringComparer.Ordinal);

        return new RequestSpecDto
        {
            Path = relPath,
            // A UUIDv7 anchors re-sync to this request even after the file is renamed or moved.
            // `id:` is documented as surviving renames but nothing has generated one until now.
            Id = Guid.CreateVersion7().ToString(),
            Name = op.Summary is { Length: > 0 } s ? s : $"{op.Method} {op.Path}",
            Method = op.Method,
            Url = UrlBuilder.Build(op, prefix: null, options.IncludeOptionalQueryParams),
            Headers = headers,
            RequestBody = op.RequestBody,
            Vars = vars.Count > 0 ? vars : null,
            Tags = op.Tags.Count > 0 ? op.Tags : null,
            Body = RequestDocs(op),
        };
    }

    private static IReadOnlyList<PlannedOperation> AddHttpFiles(
        List<ImportFile> files,
        IReadOnlyList<MappedOperation> operations,
        string collectionDir,
        string? authRef,
        string? baseUrl)
    {
        var byTag = operations
            .GroupBy(o => o.Tags.Count > 0 ? o.Tags[0] : "api", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedOperation>(operations.Count);
        var byKey = operations.ToDictionary(o => o.OpKey, StringComparer.Ordinal);

        foreach (var group in byTag)
        {
            var fileSlug = ImportSlug.UniqueSlug(RequestSlug.ForTag(group.Key), used, "api");
            var relPath = $"{collectionDir}/{fileSlug}{KindResolver.HttpExtension}";

            var emitted = HttpFileEmitter.EmitWithSections(group.ToArray(), new HttpFileEmitter.FileOptions(
                CollectionSlug: null,   // the file already lives inside the collection directory
                AuthRef: authRef,
                PortableBaseUrl: PortableBaseUrl(baseUrl),
                Title: group.Key));

            files.Add(new ImportFile(relPath, emitted.Content));

            // Hash per section, not per file: one edited request in a twenty-request file must not
            // mark the other nineteen as locally modified.
            foreach (var section in emitted.Sections)
            {
                planned.Add(new PlannedOperation(
                    byKey[section.OpKey], relPath, section.Name, null, HashContent(section.Text)));
            }
        }

        return planned;
    }

    /// <summary>
    /// The <c>@baseUrl</c> fallback written for other tools. A template such as
    /// <c>{{aspire:demo-api}}</c> means nothing outside Tap, so it is not emitted — the file still
    /// works here, where the collection's base URL wins anyway.
    /// </summary>
    private static string? PortableBaseUrl(string? baseUrl)
        => baseUrl is { Length: > 0 } && !baseUrl.Contains("{{", StringComparison.Ordinal) ? baseUrl : null;

    private static string StageName(OpenApiServer server, int index)
    {
        if (server.Description is { Length: > 0 } d) return d.Trim();
        if (Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)) return uri.Host;
        return $"server-{index + 2}";
    }

    private static string CollectionDocs(OpenApiDocument document, int operationCount)
    {
        var info = document.Info;
        var lines = new List<string> { $"# {info?.Title ?? "API"}" };

        if (info?.Version is { Length: > 0 } version)
        {
            lines.Add("");
            lines.Add($"Version `{version}`.");
        }
        if (info?.Description is { Length: > 0 } description)
        {
            lines.Add("");
            lines.Add(description.Trim());
        }

        lines.Add("");
        lines.Add($"Imported from an OpenAPI description — {operationCount} "
            + (operationCount == 1 ? "operation." : "operations."));

        return string.Join("\n", lines);
    }

    private static string RequestDocs(MappedOperation op)
    {
        var lines = new List<string> { $"# {op.Summary ?? $"{op.Method} {op.Path}"}" };

        if (op.Deprecated)
        {
            lines.Add("");
            lines.Add("> **Deprecated** in the API description.");
        }
        if (op.Description is { Length: > 0 } description)
        {
            lines.Add("");
            lines.Add(description.Trim());
        }

        var parameters = op.Parameters.Where(p => p.Description is { Length: > 0 } || p.TypeHint is not null).ToList();
        if (parameters.Count > 0)
        {
            lines.Add("");
            lines.Add("| Parameter | In | Required | Description |");
            lines.Add("| --- | --- | --- | --- |");
            foreach (var p in parameters)
            {
                var text = p.Description?.Replace('\n', ' ').Replace("|", "\\|").Trim() ?? string.Empty;
                var type = p.TypeHint is { Length: > 0 } t ? $" *({t})*" : string.Empty;
                lines.Add($"| `{p.Name}` | {p.In.ToString().ToLowerInvariant()} | "
                    + $"{(p.Required ? "yes" : "no")} | {text}{type} |");
            }
        }

        if (op.OperationId is { Length: > 0 } id)
        {
            lines.Add("");
            lines.Add($"`operationId: {id}`");
        }

        return string.Join("\n", lines);
    }
}

public sealed class OpenApiImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
