using Tap.Studio.OpenApi;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.OpenApi;

/// <summary>
/// Planning an import. The contract that matters most here is the one the rest of this project
/// tests for every emitter: whatever we write must parse, and writing it twice must produce the
/// same bytes.
/// </summary>
public class OpenApiImportPlannerTests
{
    private const string Spec = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Pet Store", "version": "1.4.2", "description": "Everything about pets." },
      "servers": [
        { "url": "https://api.example.com/v1" },
        { "url": "https://staging.example.com/v1", "description": "Staging" }
      ],
      "security": [ { "bearerAuth": [] } ],
      "paths": {
        "/pets": {
          "get": {
            "operationId": "listPets", "summary": "List all pets", "tags": ["pets"],
            "parameters": [
              { "name": "limit", "in": "query", "required": true, "schema": { "type": "integer" } },
              { "name": "cursor", "in": "query", "required": false, "schema": { "type": "string" } }
            ]
          },
          "post": {
            "operationId": "createPet", "summary": "Create a pet", "tags": ["pets"],
            "requestBody": {
              "content": { "application/json": { "schema": {
                "type": "object",
                "properties": { "name": { "type": "string" }, "age": { "type": "integer" } }
              } } }
            }
          }
        },
        "/pets/{petId}": {
          "get": {
            "operationId": "getPetById", "summary": "Get a pet", "tags": ["pets"],
            "parameters": [
              { "name": "petId", "in": "path", "required": true,
                "description": "The pet's id", "schema": { "type": "string" } }
            ]
          }
        },
        "/health": { "get": { "operationId": "health", "summary": "Health check" } }
      },
      "components": {
        "securitySchemes": {
          "bearerAuth": { "type": "http", "scheme": "bearer", "bearerFormat": "JWT" }
        }
      }
    }
    """;

    private static OpenApiImportPlanner.Result Plan(OpenApiImportPlanner.Options? options = null)
    {
        var read = OpenApiDocumentReader.Read(Spec, "petstore.json");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        return OpenApiImportPlanner.Plan(read.Document!, options ?? new OpenApiImportPlanner.Options());
    }

    [Fact]
    public void The_slug_and_collection_come_from_the_api_title()
    {
        var result = Plan();
        Assert.Equal("pet-store", result.Plan.Slug);
        Assert.Equal("collections/pet-store/_collection.tap", result.Plan.CollectionPath);
    }

    [Fact]
    public void One_request_file_is_written_per_operation_grouped_by_tag()
    {
        var paths = Plan().Plan.Files.Select(f => f.RelativePath).ToArray();

        Assert.Contains("collections/pet-store/pets/list-pets.req.tap", paths);
        Assert.Contains("collections/pet-store/pets/create-pet.req.tap", paths);
        Assert.Contains("collections/pet-store/pets/get-pet-by-id.req.tap", paths);
        // Untagged operations sit at the collection root rather than in an invented folder.
        Assert.Contains("collections/pet-store/health.req.tap", paths);
    }

    /// <summary>Everything the planner emits goes through WorkspaceService.Save, which parses
    /// before writing. A file that does not parse is a failed import, not a bad file on disk.</summary>
    [Fact]
    public void Every_emitted_file_parses()
    {
        foreach (var file in Plan().Plan.Files)
        {
            if (file.RelativePath.EndsWith(KindResolver.HttpExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = FileParser.Parse(file.RelativePath, file.Content);
            Assert.NotNull(parsed);
        }
    }

    /// <summary>Re-sync decides "did the user edit this?" by comparing a hash of what we last
    /// generated against the file on disk. A planner that emitted different bytes for the same
    /// input would report every request as edited on every re-sync.</summary>
    [Fact]
    public void Planning_the_same_document_twice_emits_identical_bytes()
    {
        var first = Plan().Plan.Files.ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);
        var second = Plan().Plan.Files.ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, content) in first)
        {
            // Ids are the one deliberately unique value; strip them before comparing.
            Assert.Equal(StripId(content), StripId(second[path]));
        }
    }

    [Fact]
    public void A_path_template_becomes_a_tap_variable_and_required_query_params_are_appended()
    {
        var files = Plan().Plan.Files;

        var byId = (RequestFile)FileParser.Parse(
            "collections/pet-store/pets/get-pet-by-id.req.tap",
            files.Single(f => f.RelativePath.EndsWith("get-pet-by-id.req.tap", StringComparison.Ordinal)).Content);
        Assert.Contains("/pets/{{petId}}", byId.HttpBlock);

        var list = (RequestFile)FileParser.Parse(
            "collections/pet-store/pets/list-pets.req.tap",
            files.Single(f => f.RelativePath.EndsWith("list-pets.req.tap", StringComparison.Ordinal)).Content);
        Assert.Contains("limit={{limit}}", list.HttpBlock);
        Assert.DoesNotContain("cursor={{cursor}}", list.HttpBlock);
    }

    /// <summary>A var that is declared but absent from the URL is an input the user can fill in
    /// that changes nothing. Declarations track the request line exactly.</summary>
    [Fact]
    public void Optional_query_params_are_declared_only_when_they_are_in_the_url()
    {
        var without = RequestVars(new OpenApiImportPlanner.Options());
        Assert.Contains("limit", without);
        Assert.DoesNotContain("cursor", without);

        var with = RequestVars(new OpenApiImportPlanner.Options { IncludeOptionalQueryParams = true });
        Assert.Contains("limit", with);
        Assert.Contains("cursor", with);
    }

    private static IEnumerable<string> RequestVars(OpenApiImportPlanner.Options options)
    {
        const string path = "collections/pet-store/pets/list-pets.req.tap";
        var content = Plan(options).Plan.Files
            .Single(f => f.RelativePath.EndsWith("list-pets.req.tap", StringComparison.Ordinal)).Content;
        return ((RequestFile)FileParser.Parse(path, content)).Vars.Keys;
    }

    /// <summary>OpenAPI's parameter object carries exactly what VarSpec models, which is why
    /// path and query parameters become declared variables rather than opaque text in a URL.</summary>
    [Fact]
    public void Path_and_query_parameters_become_declared_vars()
    {
        var content = Plan().Plan.Files
            .Single(f => f.RelativePath.EndsWith("get-pet-by-id.req.tap", StringComparison.Ordinal)).Content;
        var request = (RequestFile)FileParser.Parse("collections/pet-store/pets/get-pet-by-id.req.tap", content);

        Assert.Contains("petId", request.Vars.Keys);
    }

    [Fact]
    public void Extra_servers_become_environments_scoped_to_the_collection()
    {
        var files = Plan().Plan.Files;

        var collectionContent = files.Single(f => f.RelativePath.EndsWith("_collection.tap", StringComparison.Ordinal)).Content;
        var collection = (CollectionFile)FileParser.Parse("collections/pet-store/_collection.tap", collectionContent);
        Assert.Equal("https://api.example.com/v1", collection.BaseUrl);

        var envFile = Assert.Single(files, f => f.RelativePath.EndsWith(".env.tap", StringComparison.Ordinal));
        var env = (EnvFile)FileParser.Parse(envFile.RelativePath, envFile.Content);

        Assert.Equal("collections/pet-store/staging.env.tap", envFile.RelativePath);
        Assert.Equal("Staging", env.Name);
        // Assigned, not global: the first server's collection is the only one this address means
        // anything to, and an unassigned env would appear in every other collection's picker —
        // and the base URL rides on the assignment, since it is only true for that collection.
        var binding = Assert.Single(env.Collections);
        Assert.Equal("pet-store", binding.Collection);
        Assert.Equal("https://staging.example.com/v1", binding.BaseUrl);
    }

    [Fact]
    public void A_security_scheme_becomes_an_auth_profile_the_collection_points_at()
    {
        var result = Plan(new OpenApiImportPlanner.Options { SecuritySchemeKey = "bearerAuth" });

        Assert.NotNull(result.Plan.AuthPath);
        var authContent = result.Plan.Files.Single(f => f.RelativePath == result.Plan.AuthPath).Content;
        var auth = (AuthFile)FileParser.Parse(result.Plan.AuthPath!, authContent);
        Assert.Equal("bearer", auth.Type);

        var collectionContent = result.Plan.Files.Single(f => f.RelativePath.EndsWith("_collection.tap", StringComparison.Ordinal)).Content;
        var collection = (CollectionFile)FileParser.Parse("collections/pet-store/_collection.tap", collectionContent);
        Assert.NotNull(collection.DefaultAuth);
    }

    /// <summary>A spec only describes the shape of auth, never the credentials. Everything secret
    /// must come out as a variable the user fills in once.</summary>
    [Fact]
    public void A_generated_auth_profile_references_variables_and_never_literal_secrets()
    {
        var result = Plan(new OpenApiImportPlanner.Options { SecuritySchemeKey = "bearerAuth" });
        var authContent = result.Plan.Files.Single(f => f.RelativePath == result.Plan.AuthPath).Content;

        Assert.Contains("{{PET_STORE_TOKEN}}", authContent);
    }

    [Fact]
    public void Selecting_a_subset_imports_only_those_operations()
    {
        var result = Plan(new OpenApiImportPlanner.Options { OperationKeys = ["listPets"] });

        Assert.Equal(1, result.Plan.RequestCount);
        Assert.Single(result.Plan.Files, f => f.RelativePath.EndsWith(".req.tap", StringComparison.Ordinal));
    }

    [Fact]
    public void Selecting_nothing_is_an_error_rather_than_an_empty_collection()
    {
        var read = OpenApiDocumentReader.Read(Spec, "petstore.json");
        var ex = Assert.Throws<OpenApiImportException>(() => OpenApiImportPlanner.Plan(
            read.Document!, new OpenApiImportPlanner.Options { OperationKeys = ["nope"] }));
        Assert.Equal("no-operations", ex.Code);
    }

    // ---- .http layout ---------------------------------------------------------------------

    [Fact]
    public void The_http_layout_writes_one_file_per_tag_and_every_request_parses()
    {
        var result = Plan(new OpenApiImportPlanner.Options { Layout = OpenApiImportPlanner.Layout.HttpFilePerTag });
        var httpFiles = result.Plan.Files
            .Where(f => f.RelativePath.EndsWith(KindResolver.HttpExtension, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, httpFiles.Length); // "pets" and the untagged "api" bucket
        Assert.DoesNotContain(result.Plan.Files, f => f.RelativePath.EndsWith(".req.tap", StringComparison.Ordinal));

        var pets = httpFiles.Single(f => f.RelativePath.EndsWith("pets.http", StringComparison.Ordinal));
        var parsed = HttpFileParser.Parse(pets.RelativePath, pets.Content);
        Assert.DoesNotContain(parsed.Errors, e => e.Code.ToString().StartsWith("E_", StringComparison.Ordinal));
        Assert.Equal(3, parsed.Requests.Count);
    }

    /// <summary>The marker is how re-sync finds a section again after the file has been edited or
    /// reordered. It must be a plain comment: an unknown `# @tap-` key would raise
    /// W_HTTP_UNSUPPORTED_CONSTRUCT once per operation.</summary>
    [Fact]
    public void The_http_layout_marks_each_section_without_raising_a_directive_warning()
    {
        var result = Plan(new OpenApiImportPlanner.Options { Layout = OpenApiImportPlanner.Layout.HttpFilePerTag });
        var pets = result.Plan.Files.Single(f => f.RelativePath.EndsWith("pets.http", StringComparison.Ordinal));

        Assert.Contains("# tap-openapi listPets", pets.Content);

        var parsed = HttpFileParser.Parse(pets.RelativePath, pets.Content);
        Assert.DoesNotContain(parsed.Errors, e => e.Code == WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT);
    }

    /// <summary>Ids are UUIDv7 and unique by design; every other byte must be reproducible.</summary>
    private static string StripId(string content)
        => string.Join('\n', content.Split('\n').Where(l => !l.StartsWith("id:", StringComparison.Ordinal)));
}
