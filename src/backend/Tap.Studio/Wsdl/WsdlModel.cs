using System.Xml.Linq;

namespace Tap.Studio.Wsdl;

/// <summary>Which SOAP version a binding speaks. It decides the envelope namespace, the
/// <c>Content-Type</c>, and whether the action travels in a <c>SOAPAction</c> header or as a
/// media-type parameter — so it is carried per binding, never assumed.</summary>
public enum SoapVersion
{
    /// <summary>The <c>http://schemas.xmlsoap.org/wsdl/soap/</c> binding. Still the overwhelming
    /// majority of deployed services.</summary>
    Soap11,

    /// <summary>The <c>http://schemas.xmlsoap.org/wsdl/soap12/</c> binding.</summary>
    Soap12,
}

/// <summary>
/// <c>document</c> or <c>rpc</c>. The two put completely different content in
/// <c>&lt;soap:Body&gt;</c>: document/literal puts one global element declared in the schema, rpc
/// puts a wrapper named after the operation whose children are the message's parts.
/// </summary>
public enum SoapStyle { Document, Rpc }

/// <summary>Namespaces a WSDL 1.1 document is built from.</summary>
public static class WsdlNs
{
    public static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    public static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
    public static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
    public static readonly XNamespace Http = "http://schemas.xmlsoap.org/wsdl/http/";

    /// <summary>WSDL 2.0's namespace. Recognized only so the reader can say "this is WSDL 2.0"
    /// instead of "this is not a WSDL".</summary>
    public static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";

    /// <summary>The only transport Tap can send over. A JMS or SMTP binding parses fine and is
    /// then skipped with a warning, which is more useful than failing the whole import.</summary>
    public const string HttpTransport = "http://schemas.xmlsoap.org/soap/http";

    public const string Envelope11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Envelope12 = "http://www.w3.org/2003/05/soap-envelope";

    public static readonly XNamespace WsSecurityExt =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public static readonly XNamespace Policy = "http://schemas.xmlsoap.org/ws/2004/09/policy";
    public static readonly XNamespace Policy15 = "http://www.w3.org/ns/ws-policy";
}

/// <summary>One <c>&lt;message&gt;</c> part: either an element reference (document/literal) or a
/// type reference (rpc). Exactly one of the two is set in a well-formed document.</summary>
public sealed record WsdlPart(string Name, XName? Element, XName? Type);

public sealed record WsdlMessage(XName Name, IReadOnlyList<WsdlPart> Parts);

public sealed record WsdlPortTypeOperation(
    string Name,
    string? Documentation,
    XName? InputMessage,
    XName? OutputMessage);

public sealed record WsdlPortType(XName Name, IReadOnlyList<WsdlPortTypeOperation> Operations);

/// <summary>
/// One operation as the binding describes it. <see cref="Style"/> and <see cref="InputNamespace"/>
/// are nullable because both are inherited from the binding when the operation omits them, and
/// the mapper — not the reader — is where that fallback belongs.
/// </summary>
public sealed record WsdlBindingOperation(
    string Name,
    string? SoapAction,
    SoapStyle? Style,
    /// <summary><c>literal</c> or <c>encoded</c>. SOAP encoding is legacy and generates a body
    /// without the <c>enc:</c> type attributes a strict server wants, so it earns a warning.</summary>
    string? InputUse,
    /// <summary><c>soap:body/@namespace</c>, which names the rpc wrapper element. Meaningless for
    /// document style.</summary>
    string? InputNamespace);

public sealed record WsdlBinding(
    XName Name,
    XName Type,
    SoapVersion? SoapVersion,
    SoapStyle Style,
    string? Transport,
    IReadOnlyList<WsdlBindingOperation> Operations);

public sealed record WsdlPort(string Name, XName Binding, string? Address);

public sealed record WsdlService(string Name, string? Documentation, IReadOnlyList<WsdlPort> Ports);

/// <summary>
/// A parsed WSDL 1.1 document, reduced to the four sections an importer reads plus the inlined
/// schemas its messages point into.
///
/// <para>The nested collections are keyed by <see cref="XName"/> rather than by the document's own
/// <c>tns:Foo</c> spelling: a WSDL is free to bind whatever prefix it likes to a namespace, and
/// resolving to the expanded name at parse time is what keeps every later lookup from having to
/// carry the prefix map around.</para>
/// </summary>
public sealed record WsdlDefinitions
{
    public string? TargetNamespace { get; init; }
    public string? Name { get; init; }
    public string? Documentation { get; init; }

    public required XsdSchemaSet Schemas { get; init; }
    public required IReadOnlyDictionary<XName, WsdlMessage> Messages { get; init; }
    public required IReadOnlyDictionary<XName, WsdlPortType> PortTypes { get; init; }
    public required IReadOnlyDictionary<XName, WsdlBinding> Bindings { get; init; }
    public required IReadOnlyList<WsdlService> Services { get; init; }

    /// <summary><c>&lt;wsdl:import location="…"&gt;</c> targets that were not followed. External
    /// documents are never fetched — see <see cref="WsdlDocumentReader"/> — so this is surfaced as
    /// a diagnostic instead.</summary>
    public IReadOnlyList<string> UnresolvedImports { get; init; } = [];

    /// <summary>True when a WS-Security policy asking for a <c>UsernameToken</c> was found. It
    /// only decides whether the import wizard pre-ticks "add a UsernameToken header" — the header
    /// itself is generated from a fixed template, not from the policy.</summary>
    public bool WantsUsernameToken { get; init; }
}
