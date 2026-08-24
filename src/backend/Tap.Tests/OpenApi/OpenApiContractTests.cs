using System.Text.Json;
using Tap.Studio.Contracts;

namespace Tap.Tests.OpenApi;

/// <summary>
/// Source-generated JSON is opt-in per type: a DTO returned by an endpoint but missing from the
/// <c>StudioJson</c> <c>[JsonSerializable]</c> list compiles happily and then throws on the first
/// request that touches it. These tests are the compile-time-ish guard for that runtime hazard.
/// </summary>
public class OpenApiContractTests
{
    /// <summary>Mirrors what <c>ConfigureHttpJsonOptions</c> gives the endpoints: the
    /// source-generated resolver over ASP.NET Core's camelCase default. Testing with the framework
    /// default naming policy instead would pass while the wire format disagreed.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = StudioJson.Default,
    };

    [Fact]
    public void The_staged_document_dto_round_trips_through_the_source_generated_context()
    {
        var dto = new OpenApiDocumentDto(
            DocumentId: "abc",
            Title: "Pet Store",
            ApiVersion: "1.4.2",
            SpecVersion: "3.0",
            Description: "Everything about pets.",
            SuggestedSlug: "pet-store",
            Servers: [new OpenApiServerDto("https://api.example.com", "Prod")],
            SecuritySchemes: [new OpenApiSecuritySchemeDto("bearerAuth", "http bearer", "bearer", "JWT", ["read"], null)],
            Operations:
            [
                new OpenApiOperationDto("listPets", "listPets", "GET", "/pets", "List all pets",
                    ["pets"], false, false, 0, 1),
            ],
            Diagnostics: [new OpenApiDiagnosticDto("warning", "something", "#/paths")]);

        var json = JsonSerializer.Serialize(dto, Options);
        var back = JsonSerializer.Deserialize<OpenApiDocumentDto>(json, Options);

        Assert.NotNull(back);
        Assert.Equal("pet-store", back!.SuggestedSlug);
        Assert.Equal("listPets", Assert.Single(back.Operations).OpKey);
        Assert.Equal("bearer", Assert.Single(back.SecuritySchemes).TapAuthType);
        Assert.Equal("Prod", Assert.Single(back.Servers).Description);
        Assert.Equal("warning", Assert.Single(back.Diagnostics).Severity);
    }

    [Fact]
    public void The_request_dtos_round_trip_through_the_source_generated_context()
    {
        var upload = new OpenApiUploadRequestDto { Text = "{}", FileName = "spec.json" };
        Assert.Equal("spec.json",
            JsonSerializer.Deserialize<OpenApiUploadRequestDto>(
                JsonSerializer.Serialize(upload, Options), Options)!.FileName);

        var fetch = new OpenApiFetchRequestDto { Url = "https://example.com/openapi.json" };
        Assert.Equal(fetch.Url,
            JsonSerializer.Deserialize<OpenApiFetchRequestDto>(
                JsonSerializer.Serialize(fetch, Options), Options)!.Url);

        var import = new OpenApiImportRequestDto
        {
            DocumentId = "abc",
            Slug = "pet-store",
            Layout = "http",
            OperationKeys = ["listPets", "createPet"],
            SecuritySchemeKey = "bearerAuth",
            IncludeOptionalQueryParams = true,
            Overwrite = true,
        };
        var backImport = JsonSerializer.Deserialize<OpenApiImportRequestDto>(
            JsonSerializer.Serialize(import, Options), Options);
        Assert.Equal("http", backImport!.Layout);
        Assert.Equal(2, backImport.OperationKeys!.Count);
        Assert.True(backImport.IncludeOptionalQueryParams);
    }

    [Fact]
    public void The_import_response_dto_round_trips_through_the_source_generated_context()
    {
        var dto = new OpenApiImportResponseDto("pet-store", "collections/pet-store/_collection.tap",
            "collections/pet-store/pet-store-bearer-auth.auth.tap", 12, 14, ["a warning"]);

        var back = JsonSerializer.Deserialize<OpenApiImportResponseDto>(
            JsonSerializer.Serialize(dto, Options), Options);

        Assert.Equal(12, back!.RequestCount);
        Assert.Equal("a warning", Assert.Single(back.Warnings));
    }

    /// <summary>
    /// A client that omits <c>layout</c> must get the structured layout. A property initializer on
    /// the DTO does <i>not</i> survive source-generated deserialization — the value arrives null —
    /// so the default lives in <c>ParseLayout</c> and this pins that behaviour.
    /// </summary>
    [Fact]
    public void An_omitted_layout_means_the_structured_layout()
    {
        var dto = JsonSerializer.Deserialize<OpenApiImportRequestDto>("""{"documentId":"abc"}""", Options);

        Assert.NotNull(dto);
        Assert.Null(dto!.OperationKeys);
        Assert.False(dto.Overwrite);
        Assert.Equal(
            Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.RequestPerOperation,
            Tap.Studio.OpenApi.OpenApiImportPlanner.ParseLayout(dto.Layout));
    }

    [Theory]
    [InlineData("http", Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.HttpFilePerTag)]
    [InlineData("HTTP", Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.HttpFilePerTag)]
    [InlineData("req", Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.RequestPerOperation)]
    [InlineData(null, Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.RequestPerOperation)]
    [InlineData("nonsense", Tap.Studio.OpenApi.OpenApiImportPlanner.Layout.RequestPerOperation)]
    public void Layout_parsing_falls_back_to_the_structured_layout(
        string? wire, Tap.Studio.OpenApi.OpenApiImportPlanner.Layout expected)
        => Assert.Equal(expected, Tap.Studio.OpenApi.OpenApiImportPlanner.ParseLayout(wire));
}
