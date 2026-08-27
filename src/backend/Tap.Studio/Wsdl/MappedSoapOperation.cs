namespace Tap.Studio.Wsdl;

/// <summary>
/// One SOAP operation, reduced to exactly what Tap needs to write a request.
///
/// <para><b>This type is the seam.</b> Nothing downstream of <see cref="WsdlOperationMapper"/> —
/// neither the emitters, nor the import planner, nor the wire DTOs — touches
/// <c>System.Xml.Linq</c> or a WSDL element. Resolving a message through its binding, its
/// portType, and the schema its parts point into is fiddly enough to be worth doing exactly once,
/// in one place.</para>
///
/// <para>The body is carried split into <see cref="BodyElement"/> / <see cref="BodyNamespace"/> /
/// <see cref="BodyPayload"/> rather than as a finished envelope, because that is the same split
/// the Studio's SOAP body editor works in. The envelope is assembled by
/// <see cref="SoapEnvelope"/>, from these three fields plus whichever header the import options
/// ask for.</para>
/// </summary>
public sealed record MappedSoapOperation
{
    /// <summary>Upstream identity: <c>service/port/operation</c>. A WSDL routinely binds the same
    /// portType twice (SOAP 1.1 and 1.2), and those are genuinely two different requests — the
    /// endpoint, the envelope namespace, and the content type all differ — so the port is part of
    /// the key rather than something to collapse away.</summary>
    public required string OpKey { get; init; }

    public required string ServiceName { get; init; }
    public required string PortName { get; init; }
    public required string Name { get; init; }

    /// <summary>The port's <c>soap:address</c>. Null when the description declares no service and
    /// the operation was recovered from its binding alone — the request is then written relative
    /// to the collection's base URL, for the user to point somewhere real.</summary>
    public string? Address { get; init; }

    public required SoapVersion Version { get; init; }
    public required SoapStyle Style { get; init; }

    /// <summary>As declared. Null means the binding named no action, which is not the same as an
    /// empty one — an empty <c>soapAction=""</c> still has to be sent as <c>SOAPAction: ""</c>.</summary>
    public string? SoapAction { get; init; }

    public string? Documentation { get; init; }

    /// <summary>Name of the element inside <c>&lt;soap:Body&gt;</c>. Empty in the uncommon case of
    /// a document/literal message with several element parts, where the body has no single
    /// wrapper — <see cref="BodyPayload"/> then holds all of them.</summary>
    public required string BodyElement { get; init; }

    /// <summary>The default <c>xmlns</c> on the body element.</summary>
    public string? BodyNamespace { get; init; }

    /// <summary>Inner XML of the body element, flush-left.</summary>
    public required string BodyPayload { get; init; }

    /// <summary>The response element's local name, for the generated documentation. Tap does not
    /// assert on it — the OpenAPI importer writes no assertions either, and a guess about the
    /// response is a worse starting point than none.</summary>
    public string? ResponseElement { get; init; }

    /// <summary>Hash of the normalized operation, so a later re-sync can answer "did upstream
    /// change?" without keeping the previous document around.</summary>
    public required string SourceHash { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
