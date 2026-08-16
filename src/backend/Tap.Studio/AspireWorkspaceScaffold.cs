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
    /// <summary>Names of the APIs the AppHost passed, from <c>Studio__Aspire__Apis</c>.</summary>
    public static IReadOnlyList<string> ReadApiNames(IConfiguration config)
    {
        var raw = config["Studio:Aspire:Apis"];
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>What a scaffold run created, for logging. Empty means the run was a no-op.</summary>
    public sealed record Result(IReadOnlyList<string> Created)
    {
        public bool IsNoOp => Created.Count == 0;
    }

    /// <summary>
    /// Creates whatever is missing under <paramref name="root"/>. Safe to call on every start.
    /// </summary>
    public static Result Run(string root, IReadOnlyList<string> apiNames)
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

        foreach (var api in apiNames)
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
    /// stage's), because a portable variable is the weakest scope in the cascade.</para>
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
        # request keeps working whatever port Aspire allocates — and switching stage moves it.
        @baseUrl = http://localhost:5000

        ### Ping
        # @name ping
        # @tap-assert status 2xx
        GET {{baseUrl}}/
        Accept: application/json

        """.Replace("%API%", api, StringComparison.Ordinal);

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
