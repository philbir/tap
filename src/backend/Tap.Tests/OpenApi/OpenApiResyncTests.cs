using Tap.Studio.OpenApi;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.OpenApi;

/// <summary>
/// The re-sync diff and merge. These are the tests that matter most in the whole feature: every
/// one of them is a case where getting it wrong silently destroys work someone did by hand.
/// </summary>
public class OpenApiResyncTests
{
    private const string V1 = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Pet Store", "version": "1.0.0" },
      "paths": {
        "/pets":          { "get": { "operationId": "listPets",   "summary": "List all pets", "tags": ["pets"] } },
        "/pets/{petId}":  { "get": { "operationId": "getPetById", "summary": "Get a pet",     "tags": ["pets"],
          "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "string" } } ] } }
      }
    }
    """;

    private static IReadOnlyList<MappedOperation> Map(string spec)
    {
        var read = OpenApiDocumentReader.Read(spec, "spec.json");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        return OpenApiOperationMapper.Map(read.Document!);
    }

    private static OpenApiImportPlanner.Result Plan(string spec, OpenApiImportPlanner.Layout layout)
    {
        var read = OpenApiDocumentReader.Read(spec, "spec.json");
        return OpenApiImportPlanner.Plan(read.Document!, new OpenApiImportPlanner.Options { Layout = layout });
    }

    /// <summary>The lock as it would be written straight after an import.</summary>
    private static OpenApiLock LockFor(OpenApiImportPlanner.Result planned, string layout = "req") => new()
    {
        Source = new OpenApiLockSource("url", "https://example.com/openapi.json", null,
            DateTimeOffset.UnixEpoch, "doc-hash", "3.0", "1.0.0"),
        Layout = layout,
        Operations = planned.Planned.Select(p => new OpenApiLockOperation(
            p.Operation.OpKey, p.Operation.OperationId, p.Operation.Method, p.Operation.Path,
            p.Operation.SourceHash, p.GeneratedHash, p.FileId, p.RelativePath, p.Fragment)).ToArray(),
    };

    /// <summary>Serves the exact bytes the import produced — i.e. nothing was edited locally.</summary>
    private static Func<OpenApiLockOperation, string?> Untouched(OpenApiImportPlanner.Result planned)
        => tracked => planned.Plan.Files.FirstOrDefault(f => f.RelativePath == tracked.RelativePath)?.Content;

    // ---- the six rows of the diff table --------------------------------------------------

    [Fact]
    public void An_unchanged_document_produces_no_work()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(V1), Untouched(planned));

        Assert.All(plan.Changes, c => Assert.Equal(OpenApiResyncPlanner.ChangeKind.Unchanged, c.Kind));
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void A_new_operation_upstream_is_reported_as_added()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = V1.Replace(
            "\"/pets\":          { \"get\":",
            "\"/pets/adopt\": { \"post\": { \"operationId\": \"adoptPet\", \"tags\": [\"pets\"] } },\n    \"/pets\": { \"get\":");

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), Untouched(planned));

        var added = Assert.Single(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Added);
        Assert.Equal("adoptPet", added.OpKey);
        Assert.Null(added.LocalPath);
    }

    [Fact]
    public void An_operation_dropped_upstream_is_reported_as_removed()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Pet Store", "version": "2.0.0" },
          "paths": { "/pets": { "get": { "operationId": "listPets", "summary": "List all pets", "tags": ["pets"] } } }
        }
        """;

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), Untouched(planned));

        var removed = Assert.Single(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Removed);
        Assert.Equal("getPetById", removed.OpKey);
    }

    /// <summary>Upstream moved, the file did not — safe to regenerate.</summary>
    [Fact]
    public void An_upstream_edit_to_an_untouched_file_is_a_clean_change()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = V1.Replace("\"summary\": \"List all pets\"", "\"summary\": \"List every pet\"");

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), Untouched(planned));

        var changed = Assert.Single(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Changed);
        Assert.Equal("listPets", changed.OpKey);
        Assert.False(changed.LocallyEdited);
    }

    /// <summary>Both sides moved. This is the only case a human has to look at.</summary>
    [Fact]
    public void An_upstream_edit_to_a_locally_edited_file_is_a_conflict()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = V1.Replace("\"summary\": \"List all pets\"", "\"summary\": \"List every pet\"");

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), tracked =>
        {
            var original = Untouched(planned)(tracked);
            return tracked.OpKey == "listPets" ? original + "\n<!-- edited by hand -->" : original;
        });

        var conflict = Assert.Single(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Conflict);
        Assert.Equal("listPets", conflict.OpKey);
        Assert.True(conflict.LocallyEdited);
    }

    /// <summary>A local edit with no upstream change is none of our business — still unchanged.</summary>
    [Fact]
    public void A_local_edit_alone_is_not_a_change()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(V1), tracked =>
            Untouched(planned)(tracked) + "\n<!-- edited -->");

        Assert.All(plan.Changes, c => Assert.Equal(OpenApiResyncPlanner.ChangeKind.Unchanged, c.Kind));
        Assert.All(plan.Changes, c => Assert.True(c.LocallyEdited));
    }

    [Fact]
    public void A_tracked_file_that_has_vanished_is_reported_as_orphaned()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(V1), tracked =>
            tracked.OpKey == "getPetById" ? null : Untouched(planned)(tracked));

        var orphan = Assert.Single(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Orphaned);
        Assert.Equal("getPetById", orphan.OpKey);
    }

    // ---- matching ------------------------------------------------------------------------

    /// <summary>Pass 1: the id is stable, the path moved. Renaming a version prefix must not read
    /// as "delete everything and add it back".</summary>
    [Fact]
    public void An_operation_whose_path_moved_is_matched_by_its_operation_id()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = V1.Replace("\"/pets\":", "\"/v2/pets\":");

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), Untouched(planned));

        Assert.DoesNotContain(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Added);
        Assert.DoesNotContain(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Removed);
        var moved = Assert.Single(plan.Changes, c => c.OpKey == "listPets");
        Assert.Equal(OpenApiResyncPlanner.ChangeKind.Changed, moved.Kind);
        Assert.Equal("/v2/pets", moved.Path);
    }

    /// <summary>Pass 2: the id was renamed but the path is the same shape.</summary>
    [Fact]
    public void An_operation_whose_id_was_renamed_is_matched_by_method_and_path()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var v2 = V1.Replace("\"operationId\": \"listPets\"", "\"operationId\": \"listAllPets\"");

        var plan = OpenApiResyncPlanner.Diff(LockFor(planned), Map(v2), Untouched(planned));

        Assert.DoesNotContain(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Added);
        Assert.DoesNotContain(plan.Changes, c => c.Kind == OpenApiResyncPlanner.ChangeKind.Removed);
    }

    // ---- the merge: what must survive ------------------------------------------------------

    /// <summary>
    /// The promise the whole feature rests on. A user adds assertions, a variable value, an auth
    /// override and a tag; upstream changes the URL. Applying the update must move the URL and
    /// keep every one of those.
    /// </summary>
    [Fact]
    public void Merging_an_upstream_change_preserves_assertions_vars_auth_and_tags()
    {
        var edited = """
        ---
        kind: request
        id: 0192-3a4c-bb71-7c1d-9e8f0a1b2c3d
        name: My own name
        auth: ../../auth/custom.auth.tap
        vars:
          petId: '42'
        assertions:
        - status: 200
        - jsonpath: $.name
          exists: true
        tags: [pets, smoke]
        ---

        ```http
        GET /pets/{{petId}}
        Accept: application/json
        X-Mine: keep-me
        ```

        # Notes I wrote myself
        """;

        var request = (RequestFile)FileParser.Parse("collections/pet-store/pets/get-pet-by-id.req.tap", edited);
        var upstream = Map(V1.Replace("/pets/{petId}", "/pets/{petId}/details"))
            .Single(o => o.OpKey == "getPetById");

        var merged = OpenApiResyncMerger.MergeRequest(
            request, upstream, new OpenApiImportPlanner.Options(), preserveProse: true);

        var result = (RequestFile)FileParser.Parse(request.RelativePath, merged);

        // Upstream won where it should.
        Assert.Contains("/pets/{{petId}}/details", result.HttpBlock);

        // Everything the user owns survived.
        Assert.Equal(2, result.Assertions.Count);
        Assert.Equal("../../auth/custom.auth.tap", result.Auth?.RelativePath);
        Assert.Equal("0192-3a4c-bb71-7c1d-9e8f0a1b2c3d", result.Id);
        Assert.Contains("smoke", result.Tags);
        Assert.Equal("42", result.Vars["petId"].Default);
        Assert.Contains("X-Mine: keep-me", result.HttpBlock);
        Assert.Contains("Notes I wrote myself", result.Body);
        Assert.Equal("My own name", result.Name);
    }

    /// <summary>When nothing was edited locally there is nothing to protect, so the generated
    /// name and docs are refreshed too.</summary>
    [Fact]
    public void Merging_into_an_untouched_file_refreshes_the_name_and_docs()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var file = planned.Plan.Files.Single(f => f.RelativePath.EndsWith("list-pets.req.tap", StringComparison.Ordinal));
        var request = (RequestFile)FileParser.Parse(file.RelativePath, file.Content);

        var upstream = Map(V1.Replace("List all pets", "List every pet")).Single(o => o.OpKey == "listPets");

        var merged = OpenApiResyncMerger.MergeRequest(
            request, upstream, new OpenApiImportPlanner.Options(), preserveProse: false);
        var result = (RequestFile)FileParser.Parse(request.RelativePath, merged);

        Assert.Equal("List every pet", result.Name);
        Assert.Contains("List every pet", result.Body);
    }

    /// <summary>A header the user added must never be dropped, even though the importer rewrites
    /// the ones it authored.</summary>
    [Fact]
    public void Merging_never_removes_a_header_the_user_added()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var file = planned.Plan.Files.Single(f => f.RelativePath.EndsWith("list-pets.req.tap", StringComparison.Ordinal));
        var withHeader = file.Content.Replace("Accept: application/json", "Accept: application/json\nX-Trace: abc");
        var request = (RequestFile)FileParser.Parse(file.RelativePath, withHeader);

        var merged = OpenApiResyncMerger.MergeRequest(
            request, Map(V1).Single(o => o.OpKey == "listPets"),
            new OpenApiImportPlanner.Options(), preserveProse: true);

        Assert.Contains("X-Trace: abc", merged);
    }

    // ---- .http section surgery ---------------------------------------------------------------

    [Fact]
    public void Replacing_one_http_section_leaves_the_others_byte_identical()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.HttpFilePerTag);
        var file = planned.Plan.Files.Single(f => f.RelativePath.EndsWith(".http", StringComparison.Ordinal));

        var before = HttpFileSurgeon.ReadSection(file.Content, "getPetById");
        Assert.NotNull(before);

        var updated = HttpFileSurgeon.ReplaceSection(
            file.Content, "listPets", "### Replaced\n# tap-openapi listPets\n# @name list-pets\nGET /replaced\n");

        Assert.Contains("GET /replaced", updated);
        // The untouched section must come back out exactly as it went in.
        Assert.Equal(before, HttpFileSurgeon.ReadSection(updated, "getPetById"));

        // And the file must still parse as a whole.
        var parsed = HttpFileParser.Parse(file.RelativePath, updated);
        Assert.DoesNotContain(parsed.Errors, e => e.Code.ToString().StartsWith("E_", StringComparison.Ordinal));
    }

    [Fact]
    public void Replacing_a_section_that_is_not_there_appends_it()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.HttpFilePerTag);
        var file = planned.Plan.Files.Single(f => f.RelativePath.EndsWith(".http", StringComparison.Ordinal));

        var updated = HttpFileSurgeon.ReplaceSection(
            file.Content, "brandNew", "### Brand new\n# tap-openapi brandNew\n# @name brand-new\nGET /new\n");

        Assert.Contains("GET /new", updated);
        Assert.NotNull(HttpFileSurgeon.ReadSection(updated, "listPets"));
        Assert.NotNull(HttpFileSurgeon.ReadSection(updated, "brandNew"));
    }

    /// <summary>
    /// Assertions must survive the merge <i>semantically</i>. The emitter writes them in canonical
    /// sugar — <c>- jsonpath: $.name</c> is how it spells "exists" — so comparing file text would
    /// report a loss that isn't one. Compare the parsed assertions instead.
    /// </summary>
    [Fact]
    public void Merging_preserves_assertion_semantics_through_canonical_sugar()
    {
        var edited = """
        ---
        kind: request
        name: Get a pet
        vars:
          petId: '42'
        assertions:
        - status: 200
        - jsonpath: $.name
          exists: true
        - header: content-type
          contains: json
        ---

        ```http
        GET /pets/{{petId}}
        ```
        """;

        var request = (RequestFile)FileParser.Parse("collections/c/get-pet-by-id.req.tap", edited);
        var before = request.Assertions
            .Select(a => $"{a.Source}|{a.Selector}|{a.Op}|{a.Expected}").ToArray();

        var upstream = Map(V1.Replace("/pets/{petId}", "/pets/{petId}/details"))
            .Single(o => o.OpKey == "getPetById");

        var merged = OpenApiResyncMerger.MergeRequest(
            request, upstream, new OpenApiImportPlanner.Options(), preserveProse: true);
        var result = (RequestFile)FileParser.Parse(request.RelativePath, merged);

        Assert.Equal(before, result.Assertions
            .Select(a => $"{a.Source}|{a.Selector}|{a.Op}|{a.Expected}").ToArray());
    }

    /// <summary>The projection both the editor and the merge rely on must be lossless for the
    /// fields the merge copies through.</summary>
    [Fact]
    public void The_spec_projection_round_trips_a_request()
    {
        var planned = Plan(V1, OpenApiImportPlanner.Layout.RequestPerOperation);
        var file = planned.Plan.Files.Single(f => f.RelativePath.EndsWith("get-pet-by-id.req.tap", StringComparison.Ordinal));
        var request = (RequestFile)FileParser.Parse(file.RelativePath, file.Content);

        var reEmitted = RequestSpecEmitter.ToFileSource(RequestSpecProjection.ToSpec(request));

        Assert.Equal(file.Content, reEmitted);
    }
}
