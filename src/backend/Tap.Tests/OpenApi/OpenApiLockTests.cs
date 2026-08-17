using Tap.Studio.OpenApi;

namespace Tap.Tests.OpenApi;

/// <summary>
/// The link a collection keeps to the document it came from. Everything here exists to make a
/// later re-sync possible, so the properties under test are the ones re-sync depends on:
/// operations are addressable, upstream change is detectable, and local edits are detectable.
/// </summary>
public class OpenApiLockTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-openapi-lock").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Spec = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Pet Store", "version": "1.4.2" },
      "servers": [ { "url": "https://api.example.com/v1" } ],
      "paths": {
        "/pets": { "get": { "operationId": "listPets", "summary": "List all pets", "tags": ["pets"] } },
        "/pets/{petId}": {
          "get": {
            "operationId": "getPetById", "tags": ["pets"],
            "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "string" } } ]
          }
        }
      }
    }
    """;

    private static OpenApiImportPlanner.Result Plan(OpenApiImportPlanner.Layout layout)
    {
        var read = OpenApiDocumentReader.Read(Spec, "petstore.json");
        Assert.True(read.Ok);
        return OpenApiImportPlanner.Plan(read.Document!, new OpenApiImportPlanner.Options { Layout = layout });
    }

    private static OpenApiLock BuildLock(OpenApiImportPlanner.Result planned, string layout) => new()
    {
        Source = new OpenApiLockSource("url", "https://api.example.com/openapi.json", null,
            DateTimeOffset.UnixEpoch, "doc-hash", "3.0", "1.4.2"),
        Layout = layout,
        Operations = planned.Planned.Select(p => new OpenApiLockOperation(
            p.Operation.OpKey, p.Operation.OperationId, p.Operation.Method, p.Operation.Path,
            p.Operation.SourceHash, p.GeneratedHash, p.FileId, p.RelativePath, p.Fragment)).ToArray(),
    };

    [Fact]
    public void The_lock_round_trips_through_disk()
    {
        var store = new OpenApiLockStore(_root);
        var original = BuildLock(Plan(OpenApiImportPlanner.Layout.RequestPerOperation), "req");

        store.Write("pet-store", original);
        var read = store.Read("pet-store");

        Assert.NotNull(read);
        Assert.Equal("req", read!.Layout);
        Assert.Equal("https://api.example.com/openapi.json", read.Source.Url);
        Assert.Equal("1.4.2", read.Source.ApiVersion);
        Assert.Equal(2, read.Operations.Count);
        Assert.Equal(original.Operations[0].UpstreamHash, read.Operations[0].UpstreamHash);
        Assert.Equal(original.Operations[0].GeneratedHash, read.Operations[0].GeneratedHash);
    }

    /// <summary>The loader globs only *.tap, *.md and *.http, so the lock must never be picked up
    /// as a workspace file — and it must be a file, not a directory, or the explorer shows a node.</summary>
    [Fact]
    public void The_lock_file_is_not_a_recognised_workspace_file()
    {
        Assert.Null(Tap.Workspace.Parsing.KindResolver.Resolve(OpenApiLock.FileName));
    }

    [Fact]
    public void A_hand_mangled_lock_disables_resync_rather_than_breaking_the_collection()
    {
        var dir = Path.Combine(_root, "collections", "pet-store");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OpenApiLock.FileName), "{ not json");

        Assert.Null(new OpenApiLockStore(_root).Read("pet-store"));
    }

    [Fact]
    public void Reading_a_collection_that_was_never_imported_returns_null()
        => Assert.Null(new OpenApiLockStore(_root).Read("never-imported"));

    /// <summary>`id:` is what keeps tracking attached across a rename or a move — the move endpoint
    /// is a raw File.Move that rewrites no refs, so a path-keyed lock would break on the first drag.</summary>
    [Fact]
    public void Request_layout_records_a_stable_file_id_per_operation()
    {
        var planned = Plan(OpenApiImportPlanner.Layout.RequestPerOperation);

        Assert.All(planned.Planned, p =>
        {
            Assert.NotNull(p.FileId);
            Assert.True(Guid.TryParse(p.FileId, out _));
            Assert.Null(p.Fragment);
        });
        Assert.Equal(planned.Planned.Select(p => p.FileId).Distinct().Count(), planned.Planned.Count);
    }

    /// <summary>A .http file holds N operations, so tracking has to address the section, not the
    /// file — otherwise editing one request marks every other request in the file as modified.</summary>
    [Fact]
    public void Http_layout_records_a_fragment_and_hashes_each_section_separately()
    {
        var planned = Plan(OpenApiImportPlanner.Layout.HttpFilePerTag);

        Assert.All(planned.Planned, p =>
        {
            Assert.NotNull(p.Fragment);
            Assert.Null(p.FileId);
            Assert.EndsWith(".http", p.RelativePath, StringComparison.Ordinal);
        });

        // Both operations live in the same file but must not share a hash.
        Assert.Single(planned.Planned.Select(p => p.RelativePath).Distinct());
        Assert.Equal(2, planned.Planned.Select(p => p.GeneratedHash).Distinct().Count());
    }

    /// <summary>The generated hash is compared against the file on disk to detect a user's edit.
    /// It only works if it is the hash of exactly what was written.</summary>
    [Fact]
    public void The_generated_hash_matches_the_content_that_was_written()
    {
        var planned = Plan(OpenApiImportPlanner.Layout.RequestPerOperation);

        foreach (var p in planned.Planned)
        {
            var file = planned.Plan.Files.Single(f => f.RelativePath == p.RelativePath);
            Assert.Equal(OpenApiImportPlanner.HashContent(file.Content), p.GeneratedHash);
        }
    }

    /// <summary>Upstream change detection: the same document must hash identically, and a changed
    /// operation must not.</summary>
    [Fact]
    public void The_upstream_hash_tracks_the_document_not_the_run()
    {
        var first = Plan(OpenApiImportPlanner.Layout.RequestPerOperation);
        var second = Plan(OpenApiImportPlanner.Layout.RequestPerOperation);
        Assert.Equal(
            first.Planned.Select(p => p.Operation.SourceHash),
            second.Planned.Select(p => p.Operation.SourceHash));

        var edited = Spec.Replace("\"summary\": \"List all pets\"", "\"summary\": \"List every pet\"");
        var read = OpenApiDocumentReader.Read(edited, "petstore.json");
        var changed = OpenApiImportPlanner.Plan(read.Document!, new OpenApiImportPlanner.Options());

        Assert.NotEqual(
            first.Planned.Single(p => p.Operation.OpKey == "listPets").Operation.SourceHash,
            changed.Planned.Single(p => p.Operation.OpKey == "listPets").Operation.SourceHash);
    }
}
