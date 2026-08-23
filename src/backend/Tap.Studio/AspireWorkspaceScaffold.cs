using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace;
using Tap.Workspace.Parsing;

namespace Tap.Studio;

/// <summary>
/// First-run scaffolding for an Aspire-hosted Studio: a manifest, and one collection per API the
/// AppHost pointed at, so opening the browser lands on something that already works instead of an
/// empty folder and a "create your first collection" prompt.
///
/// <para><b>Additive only, always.</b> Nothing that exists is ever touched, moved, or removed.
/// This code runs on every start of every AppHost, against a folder the developer has committed
/// to their repository — a scaffolder that "fixed up" files there would be editing source without
/// being asked. Adding a <c>WithApi</c> adds that collection; removing one deletes nothing.</para>
///
/// <para>Starter requests are written as <c>.http</c>, not <c>.req.tap</c>: it is the format the
/// audience for this feature already has open in Visual Studio, and it demonstrates the
/// <c># @tap-*</c> directives in the one place a developer is guaranteed to read them. Collections
/// and the manifest are Tap-authored kinds, so those go through the canonical Specs emitters
/// rather than hand-assembled YAML.</para>
/// </summary>
public static class AspireWorkspaceScaffold
{
    /// <summary>One API the AppHost pointed the Studio at.</summary>
    /// <param name="OpenApiRoute">Path to its OpenAPI document, or null if it publishes none.</param>
    public sealed record AspireApi(string Name, string? OpenApiRoute);

    /// <summary>
    /// The APIs the AppHost passed, from <c>Studio__Aspire__Apis</c>.
    ///
    /// <para>Accepts both shapes: the current array of objects, and the bare
    /// <c>["orders-api"]</c> array older AppHosts emit. An AppHost and a Studio can be different
    /// versions mid-upgrade, and failing to scaffold is a worse outcome than ignoring a route.</para>
    /// </summary>
    public static IReadOnlyList<AspireApi> ReadApis(IConfiguration config)
    {
        var raw = config["Studio:Aspire:Apis"];
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var apis = new List<AspireApi>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String when element.GetString() is { Length: > 0 } legacy:
                        apis.Add(new AspireApi(legacy, null));
                        break;
                    case JsonValueKind.Object:
                        var name = Property(element, "name");
                        if (name is { Length: > 0 }) apis.Add(new AspireApi(name, Property(element, "openApiRoute")));
                        break;
                }
            }
            return apis;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Property(JsonElement element, string name)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
        return null;
    }

    /// <summary>Names only. Kept for callers that don't care about the OpenAPI route.</summary>
    public static IReadOnlyList<string> ReadApiNames(IConfiguration config)
        => ReadApis(config).Select(a => a.Name).ToArray();

    /// <summary>What a scaffold run created, for logging. Empty means the run was a no-op.</summary>
    public sealed record Result(IReadOnlyList<string> Created)
    {
        public bool IsNoOp => Created.Count == 0;
    }

    /// <summary>
    /// Creates whatever is missing under <paramref name="root"/>. Safe to call on every start.
    /// </summary>
    public static Result Run(string root, IReadOnlyList<string> apiNames)
        => Run(root, apiNames.Select(n => new AspireApi(n, null)).ToArray());

    /// <summary>
    /// Creates whatever is missing under <paramref name="root"/>. Safe to call on every start.
    ///
    /// <para>An API with an OpenAPI route gets its collection but <i>no</i> starter request: the
    /// post-startup scaffold fetches the document and writes real requests instead, falling back
    /// to the starter only if that fails. Writing both would leave a stray placeholder next to the
    /// generated requests forever.</para>
    /// </summary>
    public static Result Run(string root, IReadOnlyList<AspireApi> apis)
    {
        var created = new List<string>();
        Directory.CreateDirectory(root);

        // Dual-read: a workspace that still has a legacy tap.md already has a manifest, and
        // adding workspace.tap beside it would produce two.
        if (!WorkspaceLoader.HasManifest(root))
        {
            var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Write(root, KindResolver.ManifestFileName,
                WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "workspace" : name,
                }),
                created);
        }

        foreach (var (api, openApiRoute) in apis)
        {
            var slug = Slug(api);
            if (slug.Length == 0) continue;

            var directory = Path.Combine(root, "collections", slug);

            // Either spelling of the collection file counts as "this collection exists".
            var hasCollection =
                File.Exists(Path.Combine(directory, KindResolver.CollectionFileName))
                || File.Exists(Path.Combine(directory, KindResolver.LegacyCollectionFileName));

            if (!hasCollection)
            {
                Write(directory, KindResolver.CollectionFileName,
                    CollectionSpecEmitter.ToFileSource(new CollectionSpecDto
                    {
                        Slug = slug,
                        Name = api,
                        // The whole point of the aspire provider: this survives every restart's
                        // reallocated port, and CI resolves it from an exported variable.
                        BaseUrl = $"{{{{aspire:{api}}}}}",
                    }),
                    created);
            }

            if (openApiRoute is { Length: > 0 }) continue;

            var starter = Path.Combine(directory, "smoke.http");
            if (!File.Exists(starter)) Write(directory, "smoke.http", StarterRequest(api), created);
        }

        return new Result(created);
    }

    /// <summary>
    /// The starter request. Deliberately a GET at the root with an assertion attached: it is the
    /// one request that is meaningful before anyone knows the API's shape, and it shows the
    /// directive syntax in place rather than in a comment saying "see the docs".
    ///
    /// <para>It is written to run <em>outside</em> Tap as well. A bare <c>GET /</c> would only
    /// work here, because only Tap knows to prepend the collection's base URL — so the file
    /// declares <c>@baseUrl</c> and builds the request line from it. Visual Studio and REST
    /// Client resolve that declaration; Tap overrides it with the collection's (and the selected
    /// environment's), because a portable variable is the weakest scope in the cascade.</para>
    /// </summary>
    private static string StarterRequest(string api) =>
        // Not an interpolated string: the template is mostly {{double braces}}, which every
        // interpolation form in C# would turn into an escaping puzzle for the next reader.
        """
        # Starter request for '%API%', scaffolded on first run.
        #
        # This is an ordinary .http file — it opens and sends in Visual Studio, VS Code REST
        # Client, JetBrains, httpyac, and Kulala. The '# @tap-*' lines are inert comments
        # there and Tap features here.
        #
        # @baseUrl below is the fallback for those tools, which have no idea this file sits in
        # a Tap collection. Inside Tap the collection's baseUrl ({{aspire:%API%}}) wins, so the
        # request keeps working whatever port Aspire allocates — and switching environment moves it.
        @baseUrl = http://localhost:5000

        ### Ping
        # @name ping
        # @tap-assert status 2xx
        GET {{baseUrl}}/
        Accept: application/json

        """.Replace("%API%", api, StringComparison.Ordinal);

    /// <summary>Writes the placeholder for one API if it isn't already there. Used by the
    /// post-startup OpenAPI scaffold when a fetch fails, so the collection is never left empty.</summary>
    public static bool WriteStarterRequest(string root, string slug, string apiName)
    {
        var directory = Path.Combine(root, "collections", slug);
        var starter = Path.Combine(directory, "smoke.http");
        if (File.Exists(starter)) return false;

        // A collection that already holds requests doesn't need a placeholder alongside them.
        if (Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "*" + KindResolver.HttpExtension).Any())
        {
            return false;
        }

        Write(directory, "smoke.http", StarterRequest(apiName), []);
        return true;
    }

    private static void Write(string directory, string fileName, string content, List<string> created)
    {
        Directory.CreateDirectory(directory);
        var full = Path.Combine(directory, fileName);
        File.WriteAllText(full, content);
        created.Add(fileName == KindResolver.ManifestFileName
            ? fileName
            : $"{Path.GetFileName(directory)}/{fileName}");
    }

    /// <summary>Aspire resource names are already URL-ish, but they are not guaranteed to be
    /// path-safe, and a collection slug becomes a directory name.</summary>
    private static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
