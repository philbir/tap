using Microsoft.OpenApi;
using Tap.Studio.Importing;

namespace Tap.Studio.OpenApi;

/// <summary>
/// Staging for OpenAPI documents between <c>POST /api/openapi/documents</c> and the import that
/// references the id it returns. All of the behaviour — the entry count, the byte budget, the
/// sliding expiry — lives in <see cref="StagedDocumentCache{TDocument}"/>; this exists so the
/// container can resolve "the OpenAPI one" separately from the WSDL one.
/// </summary>
public sealed class OpenApiDocumentCache : StagedDocumentCache<OpenApiDocument>;
