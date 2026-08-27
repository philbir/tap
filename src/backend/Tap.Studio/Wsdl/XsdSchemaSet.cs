using System.Xml.Linq;

namespace Tap.Studio.Wsdl;

/// <summary>
/// An index over the <c>&lt;xsd:schema&gt;</c> documents inlined in a WSDL's <c>&lt;types&gt;</c>
/// section: global elements, types, groups and attribute groups, each keyed by expanded name.
///
/// <para><b>Only what is inlined.</b> <c>&lt;xsd:import&gt;</c> and <c>&lt;xsd:include&gt;</c>
/// carry a <c>schemaLocation</c> the document chooses, and following one would let a fetched WSDL
/// fan a single user-approved request out into requests to hostnames named inside the file. The
/// same rule already governs OpenAPI <c>$ref</c> resolution. Unresolved locations are recorded in
/// <see cref="UnresolvedImports"/> so the importer can tell the user what is missing and why the
/// generated payload is thinner than they expected.</para>
/// </summary>
public sealed class XsdSchemaSet
{
    public static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private readonly Dictionary<XName, XElement> _elements = [];
    private readonly Dictionary<XName, XElement> _types = [];
    private readonly Dictionary<XName, XElement> _groups = [];
    private readonly Dictionary<XName, XElement> _attributes = [];
    private readonly Dictionary<XName, XElement> _attributeGroups = [];
    private readonly List<string> _unresolved = [];

    public static readonly XsdSchemaSet Empty = new();

    public IReadOnlyList<string> UnresolvedImports => _unresolved;

    public bool IsEmpty => _elements.Count == 0 && _types.Count == 0;

    public static XsdSchemaSet Build(IEnumerable<XElement> schemas)
    {
        var set = new XsdSchemaSet();
        foreach (var schema in schemas) set.AddSchema(schema);
        return set;
    }

    private void AddSchema(XElement schema)
    {
        var target = schema.Attribute("targetNamespace")?.Value ?? string.Empty;
        XNamespace ns = target;

        foreach (var child in schema.Elements())
        {
            var name = child.Attribute("name")?.Value;

            if (child.Name == Xsd + "import" || child.Name == Xsd + "include"
                || child.Name == Xsd + "redefine" || child.Name == Xsd + "override")
            {
                if (child.Attribute("schemaLocation")?.Value is { Length: > 0 } location)
                    _unresolved.Add(location);
                continue;
            }

            if (name is not { Length: > 0 }) continue;

            // First declaration wins. A duplicate global name is invalid XSD; picking the first
            // deterministically beats letting the last one silently redefine the payload.
            if (child.Name == Xsd + "element") _elements.TryAdd(ns + name, child);
            else if (child.Name == Xsd + "complexType" || child.Name == Xsd + "simpleType") _types.TryAdd(ns + name, child);
            else if (child.Name == Xsd + "group") _groups.TryAdd(ns + name, child);
            else if (child.Name == Xsd + "attribute") _attributes.TryAdd(ns + name, child);
            else if (child.Name == Xsd + "attributeGroup") _attributeGroups.TryAdd(ns + name, child);
        }
    }

    public XElement? Element(XName name) => _elements.GetValueOrDefault(name);
    public XElement? Type(XName name) => _types.GetValueOrDefault(name);
    public XElement? Group(XName name) => _groups.GetValueOrDefault(name);
    public XElement? Attribute(XName name) => _attributes.GetValueOrDefault(name);
    public XElement? AttributeGroup(XName name) => _attributeGroups.GetValueOrDefault(name);

    /// <summary>The <c>targetNamespace</c> of the schema document a node was declared in, or the
    /// empty string for a no-namespace schema.</summary>
    public static string TargetNamespaceOf(XElement node)
        => node.AncestorsAndSelf(Xsd + "schema").FirstOrDefault()?.Attribute("targetNamespace")?.Value
           ?? string.Empty;

    /// <summary>
    /// Whether a <i>local</i> element declaration is namespace-qualified in an instance document:
    /// its own <c>form</c> attribute when it has one, otherwise the owning schema's
    /// <c>elementFormDefault</c>. Global elements are always qualified and never go through here.
    ///
    /// <para>This is the difference between a payload a service accepts and one it rejects with a
    /// blank fault, so it is read per declaration rather than assumed per document.</para>
    /// </summary>
    public static bool IsQualified(XElement localElement)
    {
        if (localElement.Attribute("form")?.Value is { Length: > 0 } form)
            return string.Equals(form, "qualified", StringComparison.Ordinal);

        var schema = localElement.AncestorsAndSelf(Xsd + "schema").FirstOrDefault();
        return string.Equals(
            schema?.Attribute("elementFormDefault")?.Value, "qualified", StringComparison.Ordinal);
    }

    /// <summary>
    /// Expands a <c>prefix:local</c> attribute value against the namespaces in scope at
    /// <paramref name="context"/>. An unprefixed value resolves against the default namespace,
    /// which is what a schema written with <c>xmlns="…targetNamespace"</c> relies on.
    /// </summary>
    public static XName? ResolveQName(XElement context, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var colon = text.IndexOf(':');

        if (colon < 0)
        {
            var @default = context.GetDefaultNamespace();
            return @default + text;
        }

        var prefix = text[..colon];
        var local = text[(colon + 1)..];
        if (local.Length == 0) return null;

        var ns = context.GetNamespaceOfPrefix(prefix);
        return ns is null ? null : ns + local;
    }
}
