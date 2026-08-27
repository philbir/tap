using System.Security.Cryptography;
using System.Text;
using Tap.Studio.Contracts;
using Tap.Studio.Importing;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Plans a WSDL import: a collection plus either one <c>.req.tap</c> per operation or one
/// <c>.http</c> file per port.
///
/// <para>A pure planner, exactly like <c>OpenApiImportPlanner</c> and <c>PostmanImporter</c> — it
/// returns an <see cref="ImportPlan"/> and touches no filesystem. The endpoint writes every file
/// through <c>WorkspaceService.Save</c>, so an imported file passes the same validation as a hand
/// edit, and Tap-authored kinds go through the Specs emitters rather than assembling YAML.</para>
/// </summary>
public static class WsdlImportPlanner
{
    public enum Layout
    {
        /// <summary>One <c>.req.tap</c> per operation — structured, assertion-capable.</summary>
        RequestPerOperation,

        /// <summary>One <c>.http</c> file per port, N requests inside — compact, portable.</summary>
        HttpFilePerPort,
    }

    /// <summary>Maps the wire value to a layout. Anything other than <c>http</c> — including null
    /// and an unrecognized string — means the structured layout, so a client that omits the field
    /// gets the safer of the two.</summary>
    public static Layout ParseLayout(string? wire)
        => string.Equals(wire, "http", StringComparison.OrdinalIgnoreCase)
            ? Layout.HttpFilePerPort
            : Layout.RequestPerOperation;

    public sealed record Options
    {
        public string? Slug { get; init; }
        public Layout Layout { get; init; } = Layout.RequestPerOperation;

        /// <summary>Null imports everything.</summary>
        public IReadOnlyCollection<string>? OperationKeys { get; init; }

        /// <summary>Overrides the base URL derived from the port addresses.</summary>
        public string? BaseUrl { get; init; }

        /// <summary>Point the collection at an existing auth profile. There is no WSDL equivalent
        /// of <c>securitySchemes</c> to generate one from — WS-Policy describes message-level
        /// security, not an HTTP credential — so linking is the only offer.</summary>
        public string? LinkAuthPath { get; init; }

        /// <summary>Put a WS-Security <c>UsernameToken</c> header in every generated envelope, with
        /// the credentials declared as collection variables.</summary>
        public bool AddUsernameToken { get; init; }
    }

    /// <summary>Where one operation ended up, so the lock can point back at it.</summary>
    public sealed record PlannedOperation(
        MappedSoapOperation Operation,
        string RelativePath,
        string? Fragment,
        string? FileId,
        /// <summary>Hash of exactly what was written for this operation — the whole file for
        /// <c>.req.tap</c>, just the <c>###</c> section for <c>.http</c>.</summary>
        string GeneratedHash);

    public sealed record Result(
        ImportPlan Plan,
        IReadOnlyList<MappedSoapOperation> Operations,
        IReadOnlyList<PlannedOperation> Planned,
        string? BaseUrl);

    public static Result Plan(WsdlDefinitions definitions, Options options)
    {
        var warnings = new List<string>();
        var all = WsdlOperationMapper.Map(definitions, warnings);

        var selected = options.OperationKeys is { Count: > 0 } keys
            ? all.Where(o => keys.Contains(o.OpKey)).ToArray()
            : all.ToArray();

        if (selected.Length == 0)
            throw new WsdlImportException("no-operations", "No operations were selected for import.");

        foreach (var operation in selected)
            warnings.AddRange(operation.Warnings.Select(w => $"{operation.Name}: {w}"));

        var title = ServiceTitle(definitions, selected);
        var slug = ImportSlug.Slugify(options.Slug ?? title);
        if (slug.Length == 0)
            throw new WsdlImportException("invalid-slug", "Could not derive a slug from the service name.");

        var collectionDir = $"{ImportWriter.CollectionsRoot}/{slug}";
        var files = new List<ImportFile>();

        var baseUrl = options.BaseUrl ?? DeriveBaseUrl(selected);
        if (baseUrl is null)
        {
            warnings.Add("No endpoint address was found and none was given, so the collection has "
                + "no base URL. Set one before sending.");
        }

        // --- collection -----------------------------------------------------------------------
        var vars = options.AddUsernameToken
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SoapEnvelope.UsernameVariable] = string.Empty,
                [SoapEnvelope.PasswordVariable] = string.Empty,
            }
            : null;

        var collectionSpec = new CollectionSpecDto
        {
            Slug = slug,
            Name = title,
            BaseUrl = baseUrl,
            DefaultAuth = string.IsNullOrWhiteSpace(options.LinkAuthPath) ? null : options.LinkAuthPath,
            // Credentials belong on the collection, not on each request: one place to fill them in,
            // and the password is marked secret so it never lands in the file as plain text.
            Vars = vars,
            Secrets = options.AddUsernameToken ? [SoapEnvelope.PasswordVariable] : null,
            Body = CollectionDocs(definitions, selected),
        };
        var collectionPath = $"{collectionDir}/{KindResolver.CollectionFileName}";
        files.Insert(0, new ImportFile(collectionPath, CollectionSpecEmitter.ToFileSource(collectionSpec)));

        // --- requests -------------------------------------------------------------------------
        Func<SoapVersion, string>? header = options.AddUsernameToken ? SoapEnvelope.UsernameTokenHeader : null;
        var urls = BuildUrls(selected, baseUrl, options.BaseUrl is not null, warnings);

        var planned = options.Layout == Layout.HttpFilePerPort
            ? AddHttpFiles(files, selected, collectionDir, urls, header, baseUrl, options)
            : AddRequestFiles(files, selected, collectionDir, urls, header);

        var plan = new ImportPlan(slug, collectionPath, AuthPath: null, files, warnings)
        {
            RequestCount = selected.Length,
        };

        return new Result(plan, selected, planned, baseUrl);
    }

    /// <summary>SHA-256 of the exact text written, so a later read can tell "untouched" from
    /// "hand-edited" without keeping a copy of the file.</summary>
    public static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // --- URLs ---------------------------------------------------------------------------------

    /// <summary>
    /// The collection's base URL: the origin of the first endpoint address. Only the origin, so
    /// that a request keeps its own path — a WSDL routinely binds several ports at different paths
    /// on one host, and folding a path into the base would send half of them to the wrong place.
    /// </summary>
    private static string? DeriveBaseUrl(IReadOnlyList<MappedSoapOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (operation.Address is { Length: > 0 } address
                && Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.GetLeftPart(UriPartial.Authority);
        }
        return null;
    }

    /// <summary>
    /// The request line for each operation, keyed by opKey.
    ///
    /// <para>Relative wherever it can be, so the collection's base URL — and therefore the
    /// selected environment — actually moves the request. An address on a different host than the
    /// base is written absolute instead, because Tap skips the base-URL join for an absolute URL
    /// and sending it to the wrong host would be worse than an unmovable request.</para>
    /// </summary>
    private static Dictionary<string, string> BuildUrls(
        IReadOnlyList<MappedSoapOperation> operations, string? baseUrl, bool baseUrlWasGiven, List<string> warnings)
    {
        var origin = baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
            ? b.GetLeftPart(UriPartial.Authority)
            : null;

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        var offHost = new List<string>();

        foreach (var operation in operations)
        {
            if (operation.Address is not { Length: > 0 } address)
            {
                urls[operation.OpKey] = "/";
                continue;
            }

            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                // Not a URL we can take apart — a relative or templated location. Pass it through.
                urls[operation.OpKey] = address;
                continue;
            }

            // An explicit base URL is the user saying "send these somewhere else", so every
            // request stays relative to it whatever the document's own addresses say.
            if (baseUrlWasGiven
                || origin is null
                || string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                urls[operation.OpKey] = uri.PathAndQuery;
                continue;
            }

            urls[operation.OpKey] = address;
            offHost.Add($"{operation.ServiceName}/{operation.PortName}");
        }

        if (offHost.Count > 0)
        {
            warnings.Add($"{string.Join(", ", offHost.Distinct(StringComparer.Ordinal))} "
                + $"{(offHost.Count == 1 ? "is" : "are")} hosted somewhere other than {baseUrl}, so "
                + "those requests carry an absolute URL and ignore the collection's base URL.");
        }

        return urls;
    }

    // --- file layouts -------------------------------------------------------------------------

    private static IReadOnlyList<PlannedOperation> AddRequestFiles(
        List<ImportFile> files,
        IReadOnlyList<MappedSoapOperation> operations,
        string collectionDir,
        IReadOnlyDictionary<string, string> urls,
        Func<SoapVersion, string>? header)
    {
        // A folder per port only when there is more than one — a single-port service would
        // otherwise get a pointless directory holding everything.
        var manyPorts = operations.Select(o => o.OpKey[..o.OpKey.LastIndexOf('/')])
            .Distinct(StringComparer.Ordinal).Count() > 1;

        var planned = new List<PlannedOperation>(operations.Count);
        // One set of taken names per directory, so `-2` suffixes only apply where they must.
        var used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            var dir = manyPorts ? $"{collectionDir}/{SoapRequestSlug.ForPort(operation)}" : collectionDir;

            if (!used.TryGetValue(dir, out var siblings))
            {
                siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used[dir] = siblings;
            }

            var slug = ImportSlug.UniqueSlug(SoapRequestSlug.For(operation), siblings, "operation");
            var relativePath = $"{dir}/{KindResolver.FileNameFor(WorkspaceKind.Request, slug)}";

            var spec = BuildRequestSpec(operation, relativePath, urls[operation.OpKey], header);
            var content = RequestSpecEmitter.ToFileSource(spec);
            files.Add(new ImportFile(relativePath, content));
            planned.Add(new PlannedOperation(operation, relativePath, null, spec.Id, HashContent(content)));
        }

        return planned;
    }

    public static RequestSpecDto BuildRequestSpec(
        MappedSoapOperation operation, string relativePath, string url, Func<SoapVersion, string>? header)
    {
        // No Accept header. SOAP clients conventionally send none, and a few stacks answer 406 to
        // one they did not expect — an omitted header is the safer default than a guessed one.
        var headers = new List<HttpHeaderSpecDto>
        {
            new("Content-Type", SoapEnvelope.ContentType(operation.Version, operation.SoapAction)),
        };

        if (SoapEnvelope.SoapActionHeader(operation.Version, operation.SoapAction) is { } action)
            headers.Add(new HttpHeaderSpecDto("SOAPAction", action));

        return new RequestSpecDto
        {
            Path = relativePath,
            // A UUIDv7 anchors the lock to this request even after the file is renamed or moved.
            Id = Guid.CreateVersion7().ToString(),
            Name = operation.Name,
            Method = "POST",
            Url = url,
            Headers = headers,
            RequestBody = SoapEnvelope.Build(
                operation.Version, operation.BodyElement, operation.BodyNamespace, operation.BodyPayload,
                header?.Invoke(operation.Version)),
            Body = RequestDocs(operation),
        };
    }

    private static IReadOnlyList<PlannedOperation> AddHttpFiles(
        List<ImportFile> files,
        IReadOnlyList<MappedSoapOperation> operations,
        string collectionDir,
        IReadOnlyDictionary<string, string> urls,
        Func<SoapVersion, string>? header,
        string? baseUrl,
        Options options)
    {
        var byPort = operations
            .GroupBy(o => o.OpKey[..o.OpKey.LastIndexOf('/')], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedOperation>(operations.Count);
        var byKey = operations.ToDictionary(o => o.OpKey, StringComparer.Ordinal);

        foreach (var group in byPort)
        {
            var first = group.First();
            var fileSlug = ImportSlug.UniqueSlug(SoapRequestSlug.ForPort(first), used, "service");
            var relativePath = $"{collectionDir}/{fileSlug}{KindResolver.HttpExtension}";

            // A `.http` file cannot hold a collection variable, so a UsernameToken header written
            // into one would carry `{{wsseUsername}}` tokens that only resolve inside Tap. That is
            // the same trade the portable base URL makes, and it is worth naming in the file.
            var emitted = WsdlHttpFileEmitter.Emit(
                [.. group],
                operation => urls[operation.OpKey],
                header?.Invoke(first.Version),
                new WsdlHttpFileEmitter.FileOptions(
                    AuthRef: string.IsNullOrWhiteSpace(options.LinkAuthPath) ? null : options.LinkAuthPath,
                    PortableBaseUrl: PortableBaseUrl(baseUrl),
                    Title: $"{first.ServiceName} · {first.PortName}"));

            files.Add(new ImportFile(relativePath, emitted.Content));

            // Hash per section, not per file: one edited request in a twenty-request file must not
            // mark the other nineteen as locally modified.
            foreach (var section in emitted.Sections)
            {
                planned.Add(new PlannedOperation(
                    byKey[section.OpKey], relativePath, section.Name, null, HashContent(section.Text)));
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

    // --- documentation ------------------------------------------------------------------------

    private static string ServiceTitle(WsdlDefinitions definitions, IReadOnlyList<MappedSoapOperation> selected)
    {
        if (definitions.Services.Count > 0) return definitions.Services[0].Name;
        if (definitions.Name is { Length: > 0 } name) return name;
        return selected.Count > 0 ? selected[0].ServiceName : "SOAP service";
    }

    private static string CollectionDocs(
        WsdlDefinitions definitions, IReadOnlyList<MappedSoapOperation> selected)
    {
        var title = ServiceTitle(definitions, selected);
        var lines = new List<string> { $"# {title}" };

        if (definitions.Documentation is { Length: > 0 } documentation)
        {
            lines.Add("");
            lines.Add(documentation.Trim());
        }

        if (definitions.TargetNamespace is { Length: > 0 } targetNamespace)
        {
            lines.Add("");
            lines.Add($"Target namespace `{targetNamespace}`.");
        }

        lines.Add("");
        lines.Add($"Imported from a WSDL description — {selected.Count} "
            + (selected.Count == 1 ? "operation." : "operations."));

        var ports = selected
            .Select(o => (o.ServiceName, o.PortName, o.Version, o.Style))
            .Distinct()
            .ToArray();
        if (ports.Length > 0)
        {
            lines.Add("");
            lines.Add("| Port | SOAP | Style |");
            lines.Add("| --- | --- | --- |");
            foreach (var (service, port, version, style) in ports)
            {
                lines.Add($"| `{service}/{port}` | {(version == SoapVersion.Soap12 ? "1.2" : "1.1")} "
                    + $"| {style.ToString().ToLowerInvariant()} |");
            }
        }

        return string.Join("\n", lines);
    }

    private static string RequestDocs(MappedSoapOperation operation)
    {
        var lines = new List<string> { $"# {operation.Name}" };

        if (operation.Documentation is { Length: > 0 } documentation)
        {
            lines.Add("");
            lines.Add(documentation.Trim());
        }

        lines.Add("");
        lines.Add($"`{operation.ServiceName}/{operation.PortName}` · SOAP "
            + $"{(operation.Version == SoapVersion.Soap12 ? "1.2" : "1.1")} · "
            + $"{operation.Style.ToString().ToLowerInvariant()}/literal");

        if (operation.SoapAction is { Length: > 0 } action)
        {
            lines.Add("");
            lines.Add($"Action `{action}`.");
        }

        if (operation.ResponseElement is { Length: > 0 } response)
        {
            lines.Add("");
            lines.Add($"Responds with `{response}`.");
        }

        return string.Join("\n", lines);
    }
}

public sealed class WsdlImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
