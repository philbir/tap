using Tap.Studio.Importing;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Staging for WSDL documents between <c>POST /api/wsdl/documents</c> and the import that
/// references the id it returns. All of the behaviour — the entry count, the byte budget, the
/// sliding expiry — lives in <see cref="StagedDocumentCache{TDocument}"/>.
/// </summary>
public sealed class WsdlDocumentCache : StagedDocumentCache<WsdlDefinitions>;
