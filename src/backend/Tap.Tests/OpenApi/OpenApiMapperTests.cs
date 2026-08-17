using Tap.Studio.OpenApi;

namespace Tap.Tests.OpenApi;

/// <summary>
/// Reading and mapping an OpenAPI document. The inline documents here are the specification for
/// what Tap extracts — there is no fixture directory, matching the rest of this project.
/// </summary>
public class OpenApiMapperTests
{
    private const string PetstoreJson = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Petstore", "version": "1.4.2", "description": "A sample API." },
      "servers": [ { "url": "https://api.example.com/v1" }, { "url": "https://staging.example.com/v1" } ],
      "paths": {
        "/pets": {
          "get": {
            "operationId": "listPets",
            "summary": "List all pets",
            "tags": ["pets"],
            "parameters": [
              { "name": "limit", "in": "query", "required": false,
                "description": "How many items to return", "schema": { "type": "integer" } }
            ]
          },
          "post": {
            "operationId": "createPet",
            "tags": ["pets"],
            "requestBody": {
              "required": true,
              "content": {
                "application/json": {
                  "schema": { "$ref": "#/components/schemas/Pet" }
                }
              }
            }
          }
        },
        "/pets/{petId}": {
          "parameters": [
            { "name": "petId", "in": "path", "required": true,
              "description": "Pet id", "schema": { "type": "string", "format": "uuid" } }
          ],
          "get": { "operationId": "getPetById", "tags": ["pets"] },
          "delete": { "tags": ["pets"], "deprecated": true }
        }
      },
      "components": {
        "schemas": {
          "Pet": {
            "type": "object",
            "required": ["id", "name"],
            "properties": {
              "id": { "type": "integer" },
              "name": { "type": "string" },
              "tag": { "type": "string" },
              "category": { "$ref": "#/components/schemas/Category" }
            }
          },
          "Category": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "parent": { "$ref": "#/components/schemas/Category" }
            }
          }
        }
      }
    }
    """;

    private static IReadOnlyList<MappedOperation> MapPetstore()
    {
        var read = OpenApiDocumentReader.Read(PetstoreJson, "petstore.json");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        return OpenApiOperationMapper.Map(read.Document!);
    }

    [Fact]
    public void Every_operation_in_the_document_is_mapped()
    {
        var ops = MapPetstore();
        Assert.Equal(4, ops.Count);
        Assert.Contains(ops, o => o is { Method: "GET", Path: "/pets" });
        Assert.Contains(ops, o => o is { Method: "POST", Path: "/pets" });
        Assert.Contains(ops, o => o is { Method: "GET", Path: "/pets/{petId}" });
        Assert.Contains(ops, o => o is { Method: "DELETE", Path: "/pets/{petId}" });
    }

    [Fact]
    public void OperationId_is_the_identity_when_the_document_declares_one()
    {
        var op = MapPetstore().Single(o => o.Path == "/pets" && o.Method == "GET");
        Assert.Equal("listPets", op.OpKey);
        Assert.Equal("listPets", op.OperationId);
        Assert.Equal("List all pets", op.Summary);
    }

    /// <summary>Plenty of real specs omit operationId. Falling back to method + path keeps the
    /// operation addressable, and keeps the braces so a renamed path parameter reads as a change.</summary>
    [Fact]
    public void A_missing_operationId_falls_back_to_method_and_path()
    {
        var op = MapPetstore().Single(o => o.Method == "DELETE");
        Assert.Equal("DELETE /pets/{petId}", op.OpKey);
        Assert.Null(op.OperationId);
        Assert.True(op.Deprecated);
    }

    /// <summary>Path-level parameters apply to every operation on that path. Merging them in the
    /// mapper means no emitter has to know the rule.</summary>
    [Fact]
    public void Path_level_parameters_are_merged_into_each_operation()
    {
        var op = MapPetstore().Single(o => o.OpKey == "getPetById");
        var petId = Assert.Single(op.Parameters);
        Assert.Equal("petId", petId.Name);
        Assert.Equal(ParameterIn.Path, petId.In);
        Assert.True(petId.Required);
        Assert.Equal("Pet id", petId.Description);
    }

    [Fact]
    public void Query_parameters_carry_the_metadata_a_var_spec_needs()
    {
        var op = MapPetstore().Single(o => o.OpKey == "listPets");
        var limit = Assert.Single(op.Parameters);
        Assert.Equal("limit", limit.Name);
        Assert.Equal(ParameterIn.Query, limit.In);
        Assert.False(limit.Required);
        Assert.Equal("How many items to return", limit.Description);
        Assert.Equal("integer", limit.TypeHint);
    }

    [Fact]
    public void A_request_body_is_synthesized_from_the_referenced_schema()
    {
        var op = MapPetstore().Single(o => o.OpKey == "createPet");
        Assert.Equal("application/json", op.RequestContentType);
        Assert.NotNull(op.RequestBody);
        Assert.Contains("\"id\"", op.RequestBody);
        Assert.Contains("\"name\"", op.RequestBody);
        Assert.Contains("\"category\"", op.RequestBody);
    }

    /// <summary>Category references itself. Real specs do this constantly (the published Petstore
    /// does), and an example generator without a bound never returns.</summary>
    [Fact]
    public void A_self_referencing_schema_terminates()
    {
        var op = MapPetstore().Single(o => o.OpKey == "createPet");
        Assert.NotNull(op.RequestBody);
        // Bounded by SchemaExampleBuilder.MaxDepth, so the nesting cannot run away.
        Assert.True(op.RequestBody!.Split("\"parent\"").Length - 1 <= SchemaExampleBuilder.MaxDepth);
    }

    /// <summary>The hash is what re-sync uses to decide "did upstream change". If it were not
    /// stable across identical reads, every re-sync would report every operation as changed.</summary>
    [Fact]
    public void The_source_hash_is_stable_across_identical_reads()
    {
        var first = MapPetstore().OrderBy(o => o.OpKey, StringComparer.Ordinal).Select(o => o.SourceHash);
        var second = MapPetstore().OrderBy(o => o.OpKey, StringComparer.Ordinal).Select(o => o.SourceHash);
        Assert.Equal(first, second);
    }

    [Fact]
    public void The_source_hash_changes_when_the_operation_changes()
    {
        var before = MapPetstore().Single(o => o.OpKey == "listPets").SourceHash;

        var edited = PetstoreJson.Replace("\"summary\": \"List all pets\"", "\"summary\": \"List every pet\"");
        var read = OpenApiDocumentReader.Read(edited, "petstore.json");
        var after = OpenApiOperationMapper.Map(read.Document!).Single(o => o.OpKey == "listPets").SourceHash;

        Assert.NotEqual(before, after);
    }

    /// <summary>YAML is the common on-disk form for hand-written specs, and the client cannot
    /// parse it — so the server reading both formats is the whole reason parsing lives here.</summary>
    [Fact]
    public void A_yaml_document_reads_the_same_as_json()
    {
        const string yaml = """
        openapi: 3.0.3
        info:
          title: Petstore
          version: 1.0.0
        paths:
          /pets:
            get:
              operationId: listPets
              summary: List all pets
        """;

        var read = OpenApiDocumentReader.Read(yaml, "petstore.yaml");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));

        var op = Assert.Single(OpenApiOperationMapper.Map(read.Document!));
        Assert.Equal("listPets", op.OpKey);
        Assert.Equal("GET", op.Method);
    }

    /// <summary>A YAML alias bomb expands ~9x per nesting level against the pinned YamlDotNet.
    /// The screen runs on the event stream, before anything builds a representation model.</summary>
    [Fact]
    public void A_yaml_alias_bomb_is_refused_before_it_is_expanded()
    {
        const string bomb = """
        openapi: 3.0.3
        a: &a ["lol","lol","lol","lol","lol","lol","lol","lol","lol"]
        b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]
        c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]
        d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]
        """;

        var read = OpenApiDocumentReader.Read(bomb, "bomb.yaml");
        Assert.False(read.Ok);
        Assert.Contains(read.Diagnostics, d => d.Message.Contains("anchors or aliases"));
    }

    [Fact]
    public void An_empty_document_is_reported_not_thrown()
    {
        var read = OpenApiDocumentReader.Read("   ", "empty.json");
        Assert.False(read.Ok);
        Assert.Contains(read.Diagnostics, d => d.Severity == "error");
    }
}
