using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Projects a <see cref="WsdlDefinitions"/> onto <see cref="MappedSoapOperation"/>. The only place
/// in the codebase that walks the service → port → binding → portType → message → schema chain.
/// </summary>
public static class WsdlOperationMapper
{
    public static IReadOnlyList<MappedSoapOperation> Map(WsdlDefinitions definitions, List<string>? warnings = null)
    {
        warnings ??= [];
        var operations = new List<MappedSoapOperation>();

        foreach (var service in definitions.Services)
        {
            foreach (var port in service.Ports)
            {
                if (!definitions.Bindings.TryGetValue(port.Binding, out var binding))
                {
                    warnings.Add($"Port '{service.Name}/{port.Name}' points at binding "
                        + $"'{port.Binding.LocalName}', which this document does not declare.");
                    continue;
                }

                // A .NET service publishes HttpGet/HttpPost bindings alongside its SOAP ones as a
                // matter of course. Skipping those silently is the right call — a warning per
                // non-SOAP port would fire on almost every real document and say nothing useful.
                if (binding.SoapVersion is not { } version) continue;

                if (binding.Transport is { Length: > 0 } transport
                    && !string.Equals(transport, WsdlNs.HttpTransport, StringComparison.Ordinal))
                {
                    warnings.Add($"Port '{service.Name}/{port.Name}' binds SOAP over '{transport}', "
                        + "which Tap cannot send. Only the HTTP transport is imported.");
                    continue;
                }

                AddPortOperations(
                    definitions, operations, warnings, binding, version,
                    service.Name, port.Name, port.Address);
            }
        }

        // A description that declares bindings but no <service> is a contract, not a deployment —
        // common when the .wsdl comes out of a repo rather than off a running endpoint. Recovering
        // its operations and leaving the address for the user to fill in beats importing nothing.
        if (definitions.Services.Count == 0)
        {
            foreach (var binding in definitions.Bindings.Values)
            {
                if (binding.SoapVersion is not { } version) continue;
                AddPortOperations(
                    definitions, operations, warnings, binding, version,
                    binding.Name.LocalName, binding.Name.LocalName, address: null);
            }

            if (operations.Count > 0)
            {
                warnings.Add("This description declares no <service>, so no endpoint address was "
                    + "found. The generated requests are relative to the collection's base URL — "
                    + "set it to the service's address.");
            }
        }

        if (operations.Count == 0 && definitions.Bindings.Count > 0)
        {
            warnings.Add("None of the bindings in this description are SOAP over HTTP, so there is "
                + "nothing Tap can send.");
        }

        return operations;
    }

    private static void AddPortOperations(
        WsdlDefinitions definitions,
        List<MappedSoapOperation> operations,
        List<string> warnings,
        WsdlBinding binding,
        SoapVersion version,
        string serviceName,
        string portName,
        string? address)
    {
        if (!definitions.PortTypes.TryGetValue(binding.Type, out var portType))
        {
            warnings.Add($"Binding '{binding.Name.LocalName}' implements portType "
                + $"'{binding.Type.LocalName}', which this document does not declare.");
            return;
        }

        foreach (var bound in binding.Operations)
        {
            if (operations.Count >= WsdlDocumentReader.MaxOperations)
            {
                warnings.Add($"Document declares more than {WsdlDocumentReader.MaxOperations} "
                    + "operations; the rest were skipped.");
                return;
            }

            // WSDL 1.1 permits overloading one name with different messages. Taking the first
            // match is arbitrary but deterministic, and overloads are vanishingly rare in
            // anything that ships.
            var declared = portType.Operations.FirstOrDefault(
                o => string.Equals(o.Name, bound.Name, StringComparison.Ordinal));
            if (declared is null)
            {
                warnings.Add($"Operation '{bound.Name}' is bound in '{binding.Name.LocalName}' but "
                    + $"not declared in portType '{portType.Name.LocalName}'.");
                continue;
            }

            operations.Add(MapOne(definitions, binding, bound, declared, version, serviceName, portName, address));
        }
    }

    private static MappedSoapOperation MapOne(
        WsdlDefinitions definitions,
        WsdlBinding binding,
        WsdlBindingOperation bound,
        WsdlPortTypeOperation declared,
        SoapVersion version,
        string serviceName,
        string portName,
        string? address)
    {
        var operationWarnings = new List<string>();
        var style = bound.Style ?? binding.Style;

        if (string.Equals(bound.InputUse, "encoded", StringComparison.Ordinal))
        {
            operationWarnings.Add(
                "This operation uses SOAP encoding (use=\"encoded\"). The generated body carries no "
                + "encoding type attributes — add them by hand if the service insists on them.");
        }

        var input = declared.InputMessage is { } inputRef
            ? definitions.Messages.GetValueOrDefault(inputRef)
            : null;

        if (declared.InputMessage is not null && input is null)
        {
            operationWarnings.Add(
                $"Input message '{declared.InputMessage.LocalName}' is not declared in this "
                + "document, so the body was left empty.");
        }

        var (element, elementNamespace, payload) = style == SoapStyle.Rpc
            ? BuildRpcBody(definitions, bound, declared, input, operationWarnings)
            : BuildDocumentBody(definitions, declared, input, operationWarnings);

        var mapped = new MappedSoapOperation
        {
            OpKey = $"{serviceName}/{portName}/{declared.Name}",
            ServiceName = serviceName,
            PortName = portName,
            Name = declared.Name,
            Address = address,
            Version = version,
            Style = style,
            SoapAction = bound.SoapAction,
            Documentation = declared.Documentation,
            BodyElement = element,
            BodyNamespace = elementNamespace,
            BodyPayload = payload,
            ResponseElement = ResponseElement(definitions, declared),
            SourceHash = string.Empty,
            Warnings = operationWarnings,
        };

        return mapped with { SourceHash = HashOperation(mapped) };
    }

    /// <summary>
    /// document/literal: the body <i>is</i> the element the message's part points at. Its own
    /// name and target namespace are the wrapper, and the payload is that element's content.
    /// </summary>
    private static (string Element, string? Namespace, string Payload) BuildDocumentBody(
        WsdlDefinitions definitions, WsdlPortTypeOperation declared, WsdlMessage? input, List<string> warnings)
    {
        var parts = input?.Parts ?? [];
        var elementParts = parts.Where(p => p.Element is not null).ToArray();

        if (elementParts.Length == 1)
        {
            var reference = elementParts[0].Element!;
            if (definitions.Schemas.Element(reference) is not { } declaration)
            {
                warnings.Add($"Element '{reference.LocalName}' is not declared in the schemas "
                    + "inlined in this document, so the body was left empty.");
                return (reference.LocalName, reference.NamespaceName, string.Empty);
            }

            var name = declaration.Attribute("name")!.Value;
            var ns = XsdSchemaSet.TargetNamespaceOf(declaration);
            return (name, ns, XsdExampleBuilder.BuildContent(definitions.Schemas, declaration, ns, warnings));
        }

        if (elementParts.Length > 1)
        {
            // "bare" document style: several top-level elements and no wrapper. The Studio's body
            // editor represents exactly this as an empty operation name with everything in the
            // payload, so the shape round-trips rather than needing a special case there.
            warnings.Add($"'{declared.Name}' sends {elementParts.Length} top-level body elements "
                + "rather than one wrapper, so the body has no single operation element.");

            var blocks = elementParts
                .Select(p => definitions.Schemas.Element(p.Element!) is { } d
                    ? XsdExampleBuilder.BuildElement(definitions.Schemas, d, string.Empty, warnings)
                    : $"<{p.Element!.LocalName} xmlns=\"{p.Element!.NamespaceName}\" />")
                .Where(b => b.Length > 0);

            return (string.Empty, null, string.Join("\n", blocks));
        }

        if (parts.Count > 0)
        {
            // Type-typed parts in a document binding. Legal, unusual, and there is no wrapper
            // element declared anywhere — the operation's own name is the only sensible one.
            warnings.Add($"'{declared.Name}' declares its body by type rather than by element, so "
                + "the wrapper element was named after the operation. Check it against the service.");

            var ns = definitions.TargetNamespace ?? string.Empty;
            var blocks = parts.Select(p => XsdExampleBuilder.BuildTypedElement(
                definitions.Schemas, p.Name, p.Type, ns, warnings));
            return (declared.Name, ns, string.Join("\n", blocks));
        }

        // No input parts at all: the operation takes an empty body. Inventing a wrapper here would
        // be a guess, and a wrong one is harder to spot than an obviously empty envelope.
        return (string.Empty, null, string.Empty);
    }

    /// <summary>
    /// rpc: the body is a wrapper named after the operation, in the namespace the binding's
    /// <c>soap:body</c> declares, whose children are one unqualified accessor per message part.
    /// </summary>
    private static (string Element, string? Namespace, string Payload) BuildRpcBody(
        WsdlDefinitions definitions,
        WsdlBindingOperation bound,
        WsdlPortTypeOperation declared,
        WsdlMessage? input,
        List<string> warnings)
    {
        var ns = bound.InputNamespace is { Length: > 0 } declaredNs
            ? declaredNs
            : definitions.TargetNamespace ?? string.Empty;

        var blocks = (input?.Parts ?? [])
            .Select(part => part.Element is { } element
                ? definitions.Schemas.Element(element) is { } declaration
                    ? XsdExampleBuilder.BuildElement(definitions.Schemas, declaration, ns, warnings)
                    : $"<{part.Name} />"
                : XsdExampleBuilder.BuildTypedElement(definitions.Schemas, part.Name, part.Type, ns, warnings))
            .Where(b => b.Length > 0);

        return (declared.Name, ns, string.Join("\n", blocks));
    }

    /// <summary>The response element's local name, for documentation only.</summary>
    private static string? ResponseElement(WsdlDefinitions definitions, WsdlPortTypeOperation declared)
    {
        if (declared.OutputMessage is not { } reference) return null;
        if (!definitions.Messages.TryGetValue(reference, out var message)) return null;
        return message.Parts.FirstOrDefault(p => p.Element is not null)?.Element?.LocalName;
    }

    /// <summary>
    /// A stable fingerprint of everything about the operation that Tap turns into file content, so
    /// a re-sync can answer "did the service change?" without keeping the previous WSDL. Field
    /// order is fixed and every value is already normalized, so re-reading the same document
    /// always produces the same hash.
    /// </summary>
    private static string HashOperation(MappedSoapOperation op)
    {
        var sb = new StringBuilder();
        sb.Append(op.ServiceName).Append('\n').Append(op.PortName).Append('\n').Append(op.Name).Append('\n');
        sb.Append(op.Address).Append('\n');
        sb.Append(op.Version).Append('\n').Append(op.Style).Append('\n');
        sb.Append(op.SoapAction).Append('\n');
        sb.Append(op.Documentation).Append('\n');
        sb.Append(op.BodyElement).Append('\n').Append(op.BodyNamespace).Append('\n').Append(op.BodyPayload).Append('\n');
        sb.Append(op.ResponseElement).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
