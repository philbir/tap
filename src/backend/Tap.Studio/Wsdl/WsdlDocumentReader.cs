using System.Xml;
using System.Xml.Linq;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Parses a WSDL 1.1 document from raw text and reports what went wrong without throwing for
/// anything recoverable — a description with one unbindable port is still worth importing the
/// other five operations from.
/// </summary>
public static class WsdlDocumentReader
{
    /// <summary>Documents larger than this are refused. Generated WSDLs with large inlined schemas
    /// get big, so this is looser than a hand-written file needs; it exists to stop parsing from
    /// becoming a denial of service.</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>Operations beyond this are dropped with a diagnostic rather than silently.</summary>
    public const int MaxOperations = 2000;

    public sealed record Diagnostic(string Severity, string Message, string? Pointer);

    public sealed record ReadOutcome(
        WsdlDefinitions? Document,
        string SpecVersion,
        IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool Ok => Document is not null;
    }

    /// <summary>
    /// Reads the document. <paramref name="sourceName"/> only labels diagnostics.
    /// </summary>
    public static ReadOutcome Read(string text, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ReadOutcome(null, "unknown", [Error("The document is empty.")]);

        XDocument xml;
        try
        {
            xml = LoadSafely(text);
        }
        catch (XmlException ex)
        {
            return new ReadOutcome(null, "unknown",
                [Error($"{sourceName} is not well-formed XML: {ex.Message} (line {ex.LineNumber}).")]);
        }

        var root = xml.Root;
        if (root is null)
            return new ReadOutcome(null, "unknown", [Error("The document has no root element.")]);

        if (root.Name.Namespace == WsdlNs.Wsdl20)
        {
            return new ReadOutcome(null, "2.0", [Error(
                $"{sourceName} is a WSDL 2.0 description. Tap imports WSDL 1.1, which is what "
                + "virtually every deployed SOAP service publishes — ask the service for its 1.1 "
                + "description, or build the request by hand.")]);
        }

        if (root.Name != WsdlNs.Wsdl + "definitions")
        {
            // A very common mistake is pointing the importer at the *schema* rather than the WSDL.
            var hint = root.Name.Namespace == XsdSchemaSet.Xsd
                ? " That is an XML Schema, not a WSDL — the WSDL is usually the same URL without "
                  + "the '?xsd=' parameter."
                : string.Empty;
            return new ReadOutcome(null, "unknown", [Error(
                $"{sourceName} has a <{root.Name.LocalName}> root, not <wsdl:definitions>.{hint}")]);
        }

        var diagnostics = new List<Diagnostic>();

        var schemas = XsdSchemaSet.Build(
            root.Elements(WsdlNs.Wsdl + "types").Elements(XsdSchemaSet.Xsd + "schema"));

        var unresolvedWsdlImports = root.Elements(WsdlNs.Wsdl + "import")
            .Select(e => e.Attribute("location")?.Value)
            .Where(v => v is { Length: > 0 })
            .Select(v => v!)
            .ToArray();

        var definitions = new WsdlDefinitions
        {
            TargetNamespace = Trim(root.Attribute("targetNamespace")?.Value),
            Name = Trim(root.Attribute("name")?.Value),
            Documentation = Documentation(root),
            Schemas = schemas,
            Messages = ReadMessages(root),
            PortTypes = ReadPortTypes(root),
            Bindings = ReadBindings(root, diagnostics),
            Services = ReadServices(root),
            UnresolvedImports = [.. unresolvedWsdlImports, .. schemas.UnresolvedImports],
            WantsUsernameToken = DeclaresUsernameToken(root),
        };

        // Never fetched, always reported: without the imported schema the message elements cannot
        // be expanded, and a silently empty payload reads as a Tap bug rather than a missing file.
        if (definitions.UnresolvedImports.Count > 0)
        {
            var list = string.Join(", ", definitions.UnresolvedImports.Distinct(StringComparer.Ordinal).Take(4));
            diagnostics.Add(new Diagnostic("warning",
                $"{sourceName} imports schemas that are not inlined ({list}). Tap never follows a "
                + "location named inside a document it fetched, so any type declared there is "
                + "generated as an empty element. Save the service's self-contained description "
                + "instead — WCF publishes one at '?singleWsdl'.", null));
        }

        if (definitions.Services.Count == 0)
        {
            diagnostics.Add(new Diagnostic("warning",
                "The description declares no <service>, so there is no endpoint address to send "
                + "to. You can still import and set the URL by hand.", null));
        }

        return new ReadOutcome(definitions, "1.1", diagnostics);
    }

    /// <summary>
    /// <c>DtdProcessing.Prohibit</c> plus a null resolver: a WSDL is untrusted input, and a DTD is
    /// how an XML parser is talked into reading local files (XXE) or expanding a billion-laughs
    /// entity. Neither is legal in a WSDL, so refusing costs nothing.
    /// </summary>
    private static XDocument LoadSafely(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static Dictionary<XName, WsdlMessage> ReadMessages(XElement root)
    {
        var messages = new Dictionary<XName, WsdlMessage>();
        XNamespace tns = root.Attribute("targetNamespace")?.Value ?? string.Empty;

        foreach (var element in root.Elements(WsdlNs.Wsdl + "message"))
        {
            if (element.Attribute("name")?.Value is not { Length: > 0 } name) continue;

            var parts = element.Elements(WsdlNs.Wsdl + "part")
                .Select(p => new WsdlPart(
                    p.Attribute("name")?.Value ?? "parameters",
                    XsdSchemaSet.ResolveQName(p, p.Attribute("element")?.Value),
                    XsdSchemaSet.ResolveQName(p, p.Attribute("type")?.Value)))
                .ToArray();

            messages.TryAdd(tns + name, new WsdlMessage(tns + name, parts));
        }

        return messages;
    }

    private static Dictionary<XName, WsdlPortType> ReadPortTypes(XElement root)
    {
        var portTypes = new Dictionary<XName, WsdlPortType>();
        XNamespace tns = root.Attribute("targetNamespace")?.Value ?? string.Empty;

        foreach (var element in root.Elements(WsdlNs.Wsdl + "portType"))
        {
            if (element.Attribute("name")?.Value is not { Length: > 0 } name) continue;

            var operations = element.Elements(WsdlNs.Wsdl + "operation")
                .Where(o => o.Attribute("name")?.Value is { Length: > 0 })
                .Select(o => new WsdlPortTypeOperation(
                    o.Attribute("name")!.Value,
                    Documentation(o),
                    MessageRef(o, "input"),
                    MessageRef(o, "output")))
                .ToArray();

            portTypes.TryAdd(tns + name, new WsdlPortType(tns + name, operations));
        }

        return portTypes;

        static XName? MessageRef(XElement operation, string direction)
        {
            var child = operation.Element(WsdlNs.Wsdl + direction);
            return child is null ? null : XsdSchemaSet.ResolveQName(child, child.Attribute("message")?.Value);
        }
    }

    private static Dictionary<XName, WsdlBinding> ReadBindings(XElement root, List<Diagnostic> diagnostics)
    {
        var bindings = new Dictionary<XName, WsdlBinding>();
        XNamespace tns = root.Attribute("targetNamespace")?.Value ?? string.Empty;

        foreach (var element in root.Elements(WsdlNs.Wsdl + "binding"))
        {
            if (element.Attribute("name")?.Value is not { Length: > 0 } name) continue;
            if (XsdSchemaSet.ResolveQName(element, element.Attribute("type")?.Value) is not { } type) continue;

            // The extension element decides the version. A binding with neither is HTTP GET/POST
            // or MIME — real WSDL constructs Tap has no SOAP request to build for.
            var soapBinding = element.Element(WsdlNs.Soap11 + "binding");
            var version = soapBinding is not null ? SoapVersion.Soap11 : (SoapVersion?)null;
            if (soapBinding is null)
            {
                soapBinding = element.Element(WsdlNs.Soap12 + "binding");
                if (soapBinding is not null) version = SoapVersion.Soap12;
            }

            var soapNs = version == SoapVersion.Soap12 ? WsdlNs.Soap12 : WsdlNs.Soap11;

            var operations = element.Elements(WsdlNs.Wsdl + "operation")
                .Where(o => o.Attribute("name")?.Value is { Length: > 0 })
                .Select(o => ReadBindingOperation(o, soapNs))
                .ToArray();

            bindings.TryAdd(tns + name, new WsdlBinding(
                Name: tns + name,
                Type: type,
                SoapVersion: version,
                Style: ParseStyle(soapBinding?.Attribute("style")?.Value) ?? SoapStyle.Document,
                Transport: soapBinding?.Attribute("transport")?.Value,
                Operations: operations));
        }

        if (bindings.Count == 0)
            diagnostics.Add(new Diagnostic("warning", "The description declares no <binding>.", null));

        return bindings;
    }

    private static WsdlBindingOperation ReadBindingOperation(XElement operation, XNamespace soapNs)
    {
        var soapOperation = operation.Element(soapNs + "operation");
        var input = operation.Element(WsdlNs.Wsdl + "input");
        var body = input?.Element(soapNs + "body");

        return new WsdlBindingOperation(
            Name: operation.Attribute("name")!.Value,
            // An absent soapAction is legal and means "no action". An empty one is explicit and
            // must still be sent as `SOAPAction: ""` — the two are not the same to a .NET service,
            // so null and "" are kept distinct all the way to the header.
            SoapAction: soapOperation?.Attribute("soapAction")?.Value,
            Style: ParseStyle(soapOperation?.Attribute("style")?.Value),
            InputUse: body?.Attribute("use")?.Value,
            InputNamespace: body?.Attribute("namespace")?.Value);
    }

    private static List<WsdlService> ReadServices(XElement root)
    {
        var services = new List<WsdlService>();

        foreach (var element in root.Elements(WsdlNs.Wsdl + "service"))
        {
            if (element.Attribute("name")?.Value is not { Length: > 0 } name) continue;

            var ports = element.Elements(WsdlNs.Wsdl + "port")
                .Where(p => p.Attribute("name")?.Value is { Length: > 0 })
                .Select(p => new WsdlPort(
                    p.Attribute("name")!.Value,
                    XsdSchemaSet.ResolveQName(p, p.Attribute("binding")?.Value) ?? XName.Get("unbound"),
                    Address(p)))
                .ToArray();

            services.Add(new WsdlService(name, Documentation(element), ports));
        }

        return services;

        // The address element is namespaced by the binding's SOAP version, so both are tried;
        // an HTTP binding's <http:address> is accepted too, since the URL is all we take from it.
        static string? Address(XElement port)
            => Trim(port.Element(WsdlNs.Soap11 + "address")?.Attribute("location")?.Value)
            ?? Trim(port.Element(WsdlNs.Soap12 + "address")?.Attribute("location")?.Value)
            ?? Trim(port.Element(WsdlNs.Http + "address")?.Attribute("location")?.Value);
    }

    /// <summary>
    /// Whether any WS-Policy attached to this description asks for a UsernameToken. Matched on the
    /// local name alone: the assertion lives in one of four <c>ws-securitypolicy</c> namespace
    /// revisions, and the answer only pre-ticks a checkbox — a false positive costs the user one
    /// click, while enumerating revisions costs a missed match on the next one.
    /// </summary>
    private static bool DeclaresUsernameToken(XElement root)
        => root.Descendants().Any(e => e.Name.LocalName == "UsernameToken");

    private static SoapStyle? ParseStyle(string? value) => value switch
    {
        "rpc" => SoapStyle.Rpc,
        "document" => SoapStyle.Document,
        _ => null,
    };

    private static string? Documentation(XElement element)
        => Trim(element.Element(WsdlNs.Wsdl + "documentation")?.Value);

    private static string? Trim(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static Diagnostic Error(string message) => new("error", message, null);
}
