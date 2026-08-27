using System.Text;
using System.Xml.Linq;

namespace Tap.Studio.Wsdl;

/// <summary>
/// Turns an XML Schema declaration into a sample XML payload a developer can actually send.
///
/// <para><b>Output is deterministic</b>, for the same reason <c>SchemaExampleBuilder</c> is: no
/// timestamps, no fresh GUIDs, no ordering that depends on a hash seed. The generated body is
/// hashed into the collection's lock, and a generator that produced a different value on every run
/// would report every request as locally edited, forever.</para>
///
/// <para><b>Optional elements are emitted.</b> A generated envelope is a template to prune, not a
/// minimal valid instance — leaving <c>minOccurs="0"</c> elements out would hide from the user
/// that the operation accepts them at all. Repeating elements are emitted once: a second identical
/// copy teaches nothing and doubles the noise.</para>
///
/// <para><b>Namespaces are tracked, not assumed.</b> Whether a local element is qualified decides
/// whether a service accepts the payload or answers with an empty fault, so the writer carries the
/// default namespace in scope and emits <c>xmlns</c> — including <c>xmlns=""</c> — exactly where
/// the schema says it changes.</para>
/// </summary>
public static class XsdExampleBuilder
{
    /// <summary>Deep enough for realistic payloads, shallow enough that a recursive schema still
    /// produces something a human recognizes.</summary>
    public const int MaxDepth = 6;

    /// <summary>Total elements one payload may contain. Generated schemas nest wide as well as
    /// deep; this is what keeps a 4,000-element message from wedging the wizard.</summary>
    private const int MaxElements = 400;

    private const string Indent = "  ";

    /// <summary>
    /// The inner XML of an element declaration — what goes inside the SOAP body's operation
    /// element. <paramref name="defaultNamespace"/> is the namespace already in scope there, i.e.
    /// the operation element's own <c>xmlns</c>.
    /// </summary>
    public static string BuildContent(
        XsdSchemaSet schemas, XElement declaration, string defaultNamespace, List<string> warnings)
    {
        var writer = new Writer(schemas, warnings);
        var (typeNode, typeName) = TypeOf(schemas, declaration);
        writer.WriteBody(typeNode, typeName, depth: 1, defaultNamespace, indent: 0);
        return writer.Result;
    }

    /// <summary>A whole element, tag included. Used for the rare document-style message that
    /// carries more than one element part, where there is no single wrapper.</summary>
    public static string BuildElement(
        XsdSchemaSet schemas, XElement declaration, string defaultNamespace, List<string> warnings)
    {
        var writer = new Writer(schemas, warnings);
        writer.WriteElement(declaration, depth: 1, defaultNamespace, indent: 0);
        return writer.Result;
    }

    /// <summary>
    /// A whole element with a name Tap chose and a type the schema declares — the shape of an rpc
    /// message part, which is named by the part and typed by a QName.
    /// </summary>
    public static string BuildTypedElement(
        XsdSchemaSet schemas, string name, XName? type, string defaultNamespace, List<string> warnings)
    {
        var writer = new Writer(schemas, warnings);
        // rpc part accessors are unqualified: they are not schema elements at all, they are
        // wrappers the SOAP binding invents around each part's value.
        writer.WriteNamed(
            name, elementNamespace: string.Empty,
            typeNode: type is not null && !IsBuiltIn(type) ? schemas.Type(type) : null,
            typeName: type, depth: 1, defaultNamespace, indent: 0, literal: null);
        return writer.Result;
    }

    /// <summary>The inline type child of a declaration, or the QName it points at. Both null means
    /// <c>xsd:anyType</c> — legal, and rendered as an empty element.</summary>
    private static (XElement? Node, XName? Name) TypeOf(XsdSchemaSet schemas, XElement declaration)
    {
        var inline = declaration.Element(XsdSchemaSet.Xsd + "complexType")
                  ?? declaration.Element(XsdSchemaSet.Xsd + "simpleType");
        if (inline is not null) return (inline, null);

        var named = XsdSchemaSet.ResolveQName(declaration, declaration.Attribute("type")?.Value);
        if (named is null) return (null, null);

        return IsBuiltIn(named) ? (null, named) : (schemas.Type(named), named);
    }

    private static bool IsBuiltIn(XName name) => name.Namespace == XsdSchemaSet.Xsd;

    /// <summary>
    /// Format-aware placeholders, fixed rather than computed — see the determinism note on the
    /// class. Unknown built-ins fall back to <c>string</c>, which is what an unrecognized simple
    /// type almost always is.
    /// </summary>
    private static string BuiltInValue(string localName) => localName switch
    {
        "boolean" => "true",
        "int" or "integer" or "long" or "short" or "byte" or "unsignedInt" or "unsignedLong"
            or "unsignedShort" or "unsignedByte" or "positiveInteger" or "nonNegativeInteger"
            or "negativeInteger" or "nonPositiveInteger" => "0",
        "decimal" or "double" or "float" => "0",
        "dateTime" => "2026-01-01T00:00:00Z",
        "date" => "2026-01-01",
        "time" => "00:00:00",
        "duration" => "P1D",
        "gYear" => "2026",
        "gYearMonth" => "2026-01",
        "gMonth" => "--01",
        "gMonthDay" => "--01-01",
        "gDay" => "---01",
        "base64Binary" or "hexBinary" => "",
        "anyURI" => "https://example.com",
        "language" => "en",
        _ => "string",
    };

    /// <summary>
    /// Accumulates the payload. A class rather than a pile of <c>ref</c> parameters because the
    /// depth cap, the element budget, the type-cycle stack, and the warning list all have to be
    /// shared across the whole recursion.
    /// </summary>
    private sealed class Writer(XsdSchemaSet schemas, List<string> warnings)
    {
        private readonly StringBuilder _sb = new();
        private readonly HashSet<XElement> _typePath = [];
        private int _budget = MaxElements;
        private bool _truncated;

        public string Result => _sb.ToString().TrimEnd('\n');

        // --- elements ---------------------------------------------------------------------

        public void WriteElement(XElement declaration, int depth, string defaultNamespace, int indent)
        {
            // `ref` points at a global declaration, which carries the real name and type.
            if (XsdSchemaSet.ResolveQName(declaration, declaration.Attribute("ref")?.Value) is { } reference)
            {
                if (schemas.Element(reference) is not { } target)
                {
                    WriteNamed(reference.LocalName, reference.NamespaceName, null, null,
                        depth, defaultNamespace, indent, literal: null);
                    Missing(reference);
                    return;
                }
                declaration = target;
            }

            if (declaration.Attribute("name")?.Value is not { Length: > 0 } name) return;

            // Global declarations are qualified by definition; local ones follow `form` /
            // `elementFormDefault`.
            var isGlobal = declaration.Parent?.Name == XsdSchemaSet.Xsd + "schema";
            var elementNs = isGlobal || XsdSchemaSet.IsQualified(declaration)
                ? XsdSchemaSet.TargetNamespaceOf(declaration)
                : string.Empty;

            var (typeNode, typeName) = TypeOf(schemas, declaration);
            var literal = declaration.Attribute("fixed")?.Value ?? declaration.Attribute("default")?.Value;

            WriteNamed(name, elementNs, typeNode, typeName, depth, defaultNamespace, indent, literal);
        }

        /// <summary>Writes one element with a resolved name and type. Every path that produces an
        /// element funnels through here, so the budget and the namespace rule are applied
        /// exactly once.</summary>
        public void WriteNamed(
            string name,
            string elementNamespace,
            XElement? typeNode,
            XName? typeName,
            int depth,
            string defaultNamespace,
            int indent,
            string? literal)
        {
            if (!IsSafeName(name)) return;
            if (_budget-- <= 0) { Truncate(indent); return; }

            var pad = Pad(indent);

            // The one place a namespace is declared: when the element's own differs from what is
            // already in scope. `xmlns=""` is the un-qualifying case and matters just as much.
            var attributes = new StringBuilder();
            var childDefault = defaultNamespace;
            if (!string.Equals(elementNamespace, defaultNamespace, StringComparison.Ordinal))
            {
                attributes.Append(" xmlns=\"").Append(Escape(elementNamespace)).Append('"');
                childDefault = elementNamespace;
            }

            if (typeNode is null && typeName is not null && !IsBuiltIn(typeName))
            {
                // A named type we could not resolve — almost always declared in a schema the
                // document imported and we deliberately did not follow.
                _sb.Append(pad).Append('<').Append(name).Append(attributes).Append("/>\n");
                Missing(typeName);
                return;
            }

            if (typeNode is null)
            {
                // A built-in type, or `anyType` when even the QName was absent.
                var value = typeName is null ? string.Empty : BuiltInValue(typeName.LocalName);
                WriteLeaf(pad, name, attributes.ToString(), literal ?? value);
                return;
            }

            if (typeNode.Name == XsdSchemaSet.Xsd + "simpleType")
            {
                WriteLeaf(pad, name, attributes.ToString(), literal ?? SimpleValue(typeNode, 0));
                return;
            }

            foreach (var (attributeName, value) in CollectAttributes(typeNode, []))
                attributes.Append(' ').Append(attributeName).Append("=\"").Append(Escape(value)).Append('"');

            // simpleContent: the element carries attributes *and* a text value.
            if (typeNode.Element(XsdSchemaSet.Xsd + "simpleContent") is { } simpleContent)
            {
                WriteLeaf(pad, name, attributes.ToString(), literal ?? SimpleContentValue(simpleContent));
                return;
            }

            if (depth > MaxDepth)
            {
                _sb.Append(pad).Append('<').Append(name).Append(attributes).Append("/>\n");
                Warn($"'{name}' nests deeper than {MaxDepth} levels; its content was left empty.");
                return;
            }

            // A type that contains itself — a `Node` with a `Node` child — is ordinary in real
            // schemas. Reference identity on the type node is an exact cycle key here, because
            // every lookup returns the same XElement instance out of the index.
            if (!_typePath.Add(typeNode))
            {
                _sb.Append(pad).Append('<').Append(name).Append(attributes).Append("/>\n");
                return;
            }

            // Write the open tag, then the children, then decide: an element that turned out to
            // have no content is rewritten as self-closing rather than left as an empty pair.
            var openAt = _sb.Length;
            _sb.Append(pad).Append('<').Append(name).Append(attributes).Append(">\n");
            var contentAt = _sb.Length;

            try { WriteBody(typeNode, null, depth + 1, childDefault, indent + 1); }
            finally { _typePath.Remove(typeNode); }

            if (_sb.Length == contentAt)
            {
                _sb.Length = openAt;
                _sb.Append(pad).Append('<').Append(name).Append(attributes).Append("/>\n");
                return;
            }

            _sb.Append(pad).Append("</").Append(name).Append(">\n");
        }

        /// <summary>Writes the children a complex type contributes — its own particles plus, for a
        /// derived type, the base type's, in that order.</summary>
        public void WriteBody(XElement? typeNode, XName? typeName, int depth, string defaultNamespace, int indent)
        {
            if (typeNode is null)
            {
                if (typeName is not null && !IsBuiltIn(typeName)) Missing(typeName);
                return;
            }

            if (typeNode.Name == XsdSchemaSet.Xsd + "simpleType") return;

            if (typeNode.Element(XsdSchemaSet.Xsd + "complexContent") is { } complexContent)
            {
                var derivation = complexContent.Element(XsdSchemaSet.Xsd + "extension")
                              ?? complexContent.Element(XsdSchemaSet.Xsd + "restriction");
                if (derivation is null) return;

                // The base's particles come first: that is the order an instance document of the
                // derived type has to use.
                var baseName = XsdSchemaSet.ResolveQName(derivation, derivation.Attribute("base")?.Value);
                if (baseName is not null && !IsBuiltIn(baseName))
                {
                    if (schemas.Type(baseName) is { } baseType)
                    {
                        if (_typePath.Add(baseType))
                        {
                            try { WriteBody(baseType, baseName, depth, defaultNamespace, indent); }
                            finally { _typePath.Remove(baseType); }
                        }
                    }
                    else Missing(baseName);
                }

                foreach (var particle in derivation.Elements())
                    WriteParticle(particle, depth, defaultNamespace, indent);
                return;
            }

            foreach (var particle in typeNode.Elements())
                WriteParticle(particle, depth, defaultNamespace, indent);
        }

        private void WriteParticle(XElement particle, int depth, string defaultNamespace, int indent)
        {
            if (particle.Name.Namespace != XsdSchemaSet.Xsd) return;

            switch (particle.Name.LocalName)
            {
                case "sequence":
                case "all":
                    foreach (var child in particle.Elements())
                        WriteParticle(child, depth, defaultNamespace, indent);
                    break;

                case "choice":
                {
                    // The first branch, so the choice is stable across runs. The alternatives are
                    // named in a comment rather than dropped — otherwise the generated body reads
                    // as the only shape the operation accepts.
                    var options = particle.Elements()
                        .Where(e => e.Name.Namespace == XsdSchemaSet.Xsd)
                        .ToArray();
                    if (options.Length == 0) break;

                    var alternatives = options.Skip(1)
                        .Select(o => o.Attribute("name")?.Value ?? o.Attribute("ref")?.Value)
                        .Where(n => n is { Length: > 0 })
                        .Take(6)
                        .ToArray();
                    if (alternatives.Length > 0)
                    {
                        _sb.Append(Pad(indent)).Append("<!-- or: ")
                           .Append(Escape(string.Join(", ", alternatives))).Append(" -->\n");
                    }

                    WriteParticle(options[0], depth, defaultNamespace, indent);
                    break;
                }

                case "element":
                    WriteElement(particle, depth, defaultNamespace, indent);
                    break;

                case "group":
                {
                    if (XsdSchemaSet.ResolveQName(particle, particle.Attribute("ref")?.Value) is not { } reference) break;
                    if (schemas.Group(reference) is not { } group) { Missing(reference); break; }
                    if (!_typePath.Add(group)) break;
                    try
                    {
                        foreach (var child in group.Elements())
                            WriteParticle(child, depth, defaultNamespace, indent);
                    }
                    finally { _typePath.Remove(group); }
                    break;
                }

                case "any":
                    _sb.Append(Pad(indent)).Append("<!-- any: this position accepts arbitrary XML -->\n");
                    break;
            }
        }

        // --- attributes -------------------------------------------------------------------

        /// <summary>
        /// The attributes worth writing: the required ones, and any carrying a <c>fixed</c> or
        /// <c>default</c> the service expects to see. Optional attributes are left out — a
        /// generated body should be a starting point, not every switch the schema permits.
        /// </summary>
        private List<(string Name, string Value)> CollectAttributes(XElement owner, HashSet<XElement> seen)
        {
            var result = new List<(string, string)>();

            foreach (var node in owner.Elements())
            {
                if (node.Name == XsdSchemaSet.Xsd + "complexContent"
                    || node.Name == XsdSchemaSet.Xsd + "simpleContent")
                {
                    var derivation = node.Element(XsdSchemaSet.Xsd + "extension")
                                  ?? node.Element(XsdSchemaSet.Xsd + "restriction");
                    if (derivation is null) continue;

                    var baseName = XsdSchemaSet.ResolveQName(derivation, derivation.Attribute("base")?.Value);
                    if (baseName is not null && !IsBuiltIn(baseName)
                        && schemas.Type(baseName) is { } baseType && seen.Add(baseType))
                        result.AddRange(CollectAttributes(baseType, seen));

                    result.AddRange(CollectAttributes(derivation, seen));
                    continue;
                }

                if (node.Name == XsdSchemaSet.Xsd + "attributeGroup")
                {
                    if (XsdSchemaSet.ResolveQName(node, node.Attribute("ref")?.Value) is not { } reference) continue;
                    if (schemas.AttributeGroup(reference) is { } group && seen.Add(group))
                        result.AddRange(CollectAttributes(group, seen));
                    continue;
                }

                if (node.Name != XsdSchemaSet.Xsd + "attribute") continue;

                var declaration = node;
                if (XsdSchemaSet.ResolveQName(node, node.Attribute("ref")?.Value) is { } attributeRef)
                {
                    if (schemas.Attribute(attributeRef) is not { } target) continue;
                    declaration = target;
                }

                var name = declaration.Attribute("name")?.Value;
                if (name is not { Length: > 0 } || !IsSafeName(name)) continue;

                var literal = declaration.Attribute("fixed")?.Value ?? declaration.Attribute("default")?.Value;
                var required = string.Equals(node.Attribute("use")?.Value, "required", StringComparison.Ordinal);
                if (literal is null && !required) continue;

                result.Add((name, literal ?? AttributeValue(declaration)));
            }

            return result;
        }

        private string AttributeValue(XElement declaration)
        {
            if (declaration.Element(XsdSchemaSet.Xsd + "simpleType") is { } inline)
                return SimpleValue(inline, 0);

            var type = XsdSchemaSet.ResolveQName(declaration, declaration.Attribute("type")?.Value);
            if (type is null) return "string";
            if (IsBuiltIn(type)) return BuiltInValue(type.LocalName);
            return schemas.Type(type) is { } named ? SimpleValue(named, 0) : "string";
        }

        // --- simple values ----------------------------------------------------------------

        private string SimpleContentValue(XElement simpleContent)
        {
            var derivation = simpleContent.Element(XsdSchemaSet.Xsd + "extension")
                          ?? simpleContent.Element(XsdSchemaSet.Xsd + "restriction");
            if (derivation is null) return "string";

            if (derivation.Element(XsdSchemaSet.Xsd + "simpleType") is { } inline)
                return SimpleValue(inline, 0);

            var baseName = XsdSchemaSet.ResolveQName(derivation, derivation.Attribute("base")?.Value);
            if (baseName is null) return "string";
            if (IsBuiltIn(baseName)) return BuiltInValue(baseName.LocalName);
            return schemas.Type(baseName) is { } named ? SimpleValue(named, 0) : "string";
        }

        /// <summary>
        /// A value for a simple type. The first <c>enumeration</c> facet wins when there is one —
        /// it is a value the service definitely accepts, which no synthesized placeholder can
        /// claim. <paramref name="hops"/> bounds a chain of simple types deriving from each other.
        /// </summary>
        private string SimpleValue(XElement simpleType, int hops)
        {
            if (hops > 8) return "string";

            if (simpleType.Element(XsdSchemaSet.Xsd + "restriction") is { } restriction)
            {
                if (restriction.Element(XsdSchemaSet.Xsd + "enumeration")?.Attribute("value")?.Value
                    is { } enumeration)
                    return enumeration;

                var baseName = XsdSchemaSet.ResolveQName(restriction, restriction.Attribute("base")?.Value);
                if (baseName is not null)
                {
                    if (IsBuiltIn(baseName)) return BuiltInValue(baseName.LocalName);
                    if (schemas.Type(baseName) is { } named) return SimpleValue(named, hops + 1);
                }

                return restriction.Element(XsdSchemaSet.Xsd + "simpleType") is { } inline
                    ? SimpleValue(inline, hops + 1)
                    : "string";
            }

            // A union takes its first member type; a list takes one item of its item type.
            if (simpleType.Element(XsdSchemaSet.Xsd + "union") is { } union)
            {
                if (union.Element(XsdSchemaSet.Xsd + "simpleType") is { } first)
                    return SimpleValue(first, hops + 1);

                var members = union.Attribute("memberTypes")?.Value.Split(
                    ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (members is { Length: > 0 }
                    && XsdSchemaSet.ResolveQName(union, members[0]) is { } member)
                {
                    if (IsBuiltIn(member)) return BuiltInValue(member.LocalName);
                    if (schemas.Type(member) is { } named) return SimpleValue(named, hops + 1);
                }
                return "string";
            }

            if (simpleType.Element(XsdSchemaSet.Xsd + "list") is { } list)
            {
                if (list.Element(XsdSchemaSet.Xsd + "simpleType") is { } inline)
                    return SimpleValue(inline, hops + 1);
                if (XsdSchemaSet.ResolveQName(list, list.Attribute("itemType")?.Value) is { } item)
                {
                    if (IsBuiltIn(item)) return BuiltInValue(item.LocalName);
                    if (schemas.Type(item) is { } named) return SimpleValue(named, hops + 1);
                }
            }

            return "string";
        }

        // --- plumbing ---------------------------------------------------------------------

        private static string Pad(int indent) => string.Concat(Enumerable.Repeat(Indent, indent));

        private void WriteLeaf(string pad, string name, string attributes, string value)
        {
            if (value.Length == 0)
            {
                _sb.Append(pad).Append('<').Append(name).Append(attributes).Append("/>\n");
                return;
            }
            _sb.Append(pad).Append('<').Append(name).Append(attributes).Append('>')
               .Append(Escape(value))
               .Append("</").Append(name).Append(">\n");
        }

        private void Truncate(int indent)
        {
            if (_truncated) return;
            _truncated = true;
            _sb.Append(Pad(indent)).Append("<!-- truncated: this message declares more than ")
               .Append(MaxElements).Append(" elements -->\n");
            Warn($"The message declares more than {MaxElements} elements; the body was truncated.");
        }

        private void Missing(XName name)
            => Warn($"'{name.LocalName}' is declared in a schema that is not part of this document, "
                  + "so its content was left empty.");

        private void Warn(string message)
        {
            if (!warnings.Contains(message, StringComparer.Ordinal)) warnings.Add(message);
        }
    }

    /// <summary>
    /// Guards the one place schema text becomes markup. Every value the writer emits is escaped,
    /// but a name goes into a tag verbatim — so a name that could break out of one is dropped
    /// rather than written. Real schemas only ever hold NCNames here.
    /// </summary>
    private static bool IsSafeName(string name)
        => name.Length is > 0 and <= 200
        && !name.Any(ch => char.IsWhiteSpace(ch) || ch is '<' or '>' or '&' or '"' or '\'' or '/' or '=');

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
