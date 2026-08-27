using Tap.Studio.Wsdl;

namespace Tap.Tests.Wsdl;

/// <summary>
/// Reading a WSDL and turning its operations into SOAP bodies. The documents under test live in
/// <see cref="WsdlFixtures"/>.
/// </summary>
public class WsdlMapperTests
{
    private static WsdlDefinitions Read(string text = WsdlFixtures.Weather)
    {
        var read = WsdlDocumentReader.Read(text, "weather.wsdl");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        return read.Document!;
    }

    private static IReadOnlyList<MappedSoapOperation> Map(string text = WsdlFixtures.Weather)
        => WsdlOperationMapper.Map(Read(text));

    [Fact]
    public void The_document_reports_itself_as_wsdl_1_1()
        => Assert.Equal("1.1", WsdlDocumentReader.Read(WsdlFixtures.Weather, "weather.wsdl").SpecVersion);

    /// <summary>A port bound over SOAP 1.1 and one bound over 1.2 are genuinely two requests: the
    /// envelope namespace and the content type differ. Collapsing them would lose one.</summary>
    [Fact]
    public void Each_soap_port_yields_its_own_operation_and_non_soap_bindings_are_skipped()
    {
        var operations = Map();

        Assert.Equal(
            ["Weather/WeatherSoap/GetWeather", "Weather/WeatherSoap12/GetWeather"],
            operations.Select(o => o.OpKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_body_element_is_the_message_part_element_in_its_own_namespace()
    {
        var operation = Map().First(o => o.PortName == "WeatherSoap");

        Assert.Equal("GetWeather", operation.BodyElement);
        Assert.Equal("http://example.org/weather", operation.BodyNamespace);
        Assert.Equal(SoapStyle.Document, operation.Style);
        Assert.Equal("http://example.org/weather/GetWeather", operation.SoapAction);
        Assert.Equal("GetWeatherResponse", operation.ResponseElement);
        Assert.Equal("Look up the forecast.", operation.Documentation);
    }

    /// <summary>Sample values come from the schema's own types, and an enumeration contributes a
    /// value the service definitely accepts rather than a synthesized placeholder.</summary>
    [Fact]
    public void The_payload_is_built_from_the_schema()
    {
        var payload = Map().First(o => o.PortName == "WeatherSoap").BodyPayload;

        Assert.Contains("<CityName>string</CityName>", payload);
        Assert.Contains("<Days>0</Days>", payload);
        Assert.Contains("<Units>Celsius</Units>", payload);
    }

    /// <summary>The schema is <c>elementFormDefault="qualified"</c>, so every child inherits the
    /// operation element's default namespace and none of them may redeclare one.</summary>
    [Fact]
    public void Qualified_children_inherit_the_body_namespace()
        => Assert.DoesNotContain("xmlns=", Map().First(o => o.PortName == "WeatherSoap").BodyPayload);

    [Fact]
    public void Required_attributes_are_written_and_optional_ones_are_not()
    {
        var payload = Map().First(o => o.PortName == "WeatherSoap").BodyPayload;

        Assert.Contains("<Station kind=\"string\">", payload);
        Assert.DoesNotContain("note=", payload);
    }

    /// <summary><c>Station</c> contains a <c>Station</c>. Recursion has to terminate without
    /// dropping the element that started it.</summary>
    [Fact]
    public void A_self_referencing_type_terminates()
    {
        var payload = Map().First(o => o.PortName == "WeatherSoap").BodyPayload;

        Assert.Contains("<Nearest", payload);
        Assert.True(payload.Length < 4000, "a recursive type should not expand without bound");
    }

    [Fact]
    public void Soap_11_and_soap_12_get_the_envelope_and_content_type_their_binding_declares()
    {
        var operations = Map();
        var eleven = operations.First(o => o.PortName == "WeatherSoap");
        var twelve = operations.First(o => o.PortName == "WeatherSoap12");

        Assert.Equal(SoapVersion.Soap11, eleven.Version);
        Assert.Equal("text/xml; charset=utf-8", SoapEnvelope.ContentType(eleven.Version, eleven.SoapAction));
        Assert.Equal("\"http://example.org/weather/GetWeather\"",
            SoapEnvelope.SoapActionHeader(eleven.Version, eleven.SoapAction));

        Assert.Equal(SoapVersion.Soap12, twelve.Version);
        Assert.Equal(
            "application/soap+xml; charset=utf-8; action=\"http://example.org/weather/GetWeather\"",
            SoapEnvelope.ContentType(twelve.Version, twelve.SoapAction));
        // 1.2 carries the action on the media type instead, so a SOAPAction header would be wrong.
        Assert.Null(SoapEnvelope.SoapActionHeader(twelve.Version, twelve.SoapAction));
    }

    /// <summary>
    /// The envelope Tap writes must be byte-identical to what the Studio's SOAP body editor
    /// produces from the same parts, or opening a generated request and saving it shows up as a
    /// diff. This pins the shape that <c>serializeSoapBody</c> mirrors.
    /// </summary>
    [Fact]
    public void The_envelope_matches_what_the_body_editor_serializes()
    {
        var envelope = SoapEnvelope.Build(
            SoapVersion.Soap11, "GetWeather", "http://example.org/weather",
            "<CityName>string</CityName>", header: null);

        Assert.Equal(
            """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetWeather xmlns="http://example.org/weather">
                  <CityName>string</CityName>
                </GetWeather>
              </soap:Body>
            </soap:Envelope>
            """,
            envelope);
    }

    /// <summary>An operation with no arguments self-closes rather than emitting an empty pair —
    /// again matching the editor, which is what keeps the round-trip clean.</summary>
    [Fact]
    public void An_empty_payload_self_closes_the_operation_element()
    {
        var envelope = SoapEnvelope.Build(SoapVersion.Soap12, "Ping", "urn:x", string.Empty, header: null);

        Assert.Equal(
            """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">
              <soap:Body>
                <Ping xmlns="urn:x" />
              </soap:Body>
            </soap:Envelope>
            """,
            envelope);
    }

    [Fact]
    public void A_username_token_header_declares_the_credentials_as_variables()
    {
        var envelope = SoapEnvelope.Build(
            SoapVersion.Soap11, "GetWeather", "urn:x", "<CityName>string</CityName>",
            SoapEnvelope.UsernameTokenHeader(SoapVersion.Soap11));

        Assert.Contains("<soap:Header>", envelope);
        Assert.Contains("soap:mustUnderstand=\"1\"", envelope);
        Assert.Contains("<wsse:Username>{{wsseUsername}}</wsse:Username>", envelope);
        Assert.Contains("<wsse:Password Type=", envelope);
        Assert.Contains("{{wssePassword}}", envelope);
        // The header sits before the body, indented one level in.
        Assert.True(envelope.IndexOf("<soap:Header>", StringComparison.Ordinal)
                  < envelope.IndexOf("<soap:Body>", StringComparison.Ordinal));
    }

    // --- rpc, unqualified schemas, and the awkward shapes ------------------------------------

    /// <summary>rpc puts a wrapper named after the operation in the binding's namespace, whose
    /// children are one unqualified accessor per message part.</summary>
    [Fact]
    public void Rpc_style_wraps_the_operation_name_around_unqualified_part_accessors()
    {
        var operation = Assert.Single(Map(WsdlFixtures.Rpc));

        Assert.Equal(SoapStyle.Rpc, operation.Style);
        Assert.Equal("Add", operation.BodyElement);
        Assert.Equal("urn:calc", operation.BodyNamespace);
        // The wrapper's xmlns is in scope, so each accessor has to opt back out of it.
        Assert.Contains("<left xmlns=\"\">", operation.BodyPayload);
        Assert.Contains("<value>0</value>", operation.BodyPayload);
        Assert.Contains("<right xmlns=\"\">0</right>", operation.BodyPayload);
    }

    /// <summary>An explicitly empty <c>soapAction=""</c> is not the same as an absent one: several
    /// stacks distinguish the two, so it must survive as a quoted empty header.</summary>
    [Fact]
    public void An_empty_soap_action_is_still_sent()
    {
        var operation = Assert.Single(Map(WsdlFixtures.Rpc));

        Assert.Equal(string.Empty, operation.SoapAction);
        Assert.Equal("\"\"", SoapEnvelope.SoapActionHeader(operation.Version, operation.SoapAction));
    }

    [Fact]
    public void A_wsdl_2_0_document_is_rejected_by_name()
    {
        var read = WsdlDocumentReader.Read(
            """<description xmlns="http://www.w3.org/ns/wsdl" />""", "service.wsdl");

        Assert.False(read.Ok);
        Assert.Contains("WSDL 2.0", read.Diagnostics[0].Message);
    }

    /// <summary>Pointing the importer at the schema instead of the description is a common slip,
    /// and the error should say so rather than "not a WSDL".</summary>
    [Fact]
    public void An_xsd_is_rejected_with_a_hint()
    {
        var read = WsdlDocumentReader.Read(
            """<schema xmlns="http://www.w3.org/2001/XMLSchema" />""", "service.xsd");

        Assert.False(read.Ok);
        Assert.Contains("XML Schema", read.Diagnostics[0].Message);
    }

    /// <summary>A WSDL is untrusted input. A DTD is how an XML parser is talked into reading local
    /// files or expanding an entity bomb, and none is legal in a WSDL.</summary>
    [Fact]
    public void A_document_with_a_dtd_is_refused()
    {
        var read = WsdlDocumentReader.Read(
            """
            <?xml version="1.0"?>
            <!DOCTYPE definitions [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" />
            """, "evil.wsdl");

        Assert.False(read.Ok);
    }

    /// <summary>Following a <c>schemaLocation</c> would let a fetched description fan one
    /// user-approved request out into requests to hostnames named inside the file. It is reported
    /// instead, and the operation still imports with an empty body.</summary>
    [Fact]
    public void An_imported_schema_is_reported_rather_than_fetched()
    {
        const string wsdl = """
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                     xmlns:tns="urn:remote" targetNamespace="urn:remote">
          <types>
            <xsd:schema targetNamespace="urn:remote">
              <xsd:import namespace="urn:other" schemaLocation="https://elsewhere.example/types.xsd" />
            </xsd:schema>
          </types>
          <message name="In"><part name="parameters" element="tns:Go" /></message>
          <portType name="P"><operation name="Go"><input message="tns:In" /></operation></portType>
          <binding name="B" type="tns:P">
            <soap:binding transport="http://schemas.xmlsoap.org/soap/http" style="document" />
            <operation name="Go">
              <soap:operation soapAction="urn:Go" />
              <input><soap:body use="literal" /></input>
            </operation>
          </binding>
          <service name="S">
            <port name="P" binding="tns:B"><soap:address location="https://remote.example/svc" /></port>
          </service>
        </definitions>
        """;

        var read = WsdlDocumentReader.Read(wsdl, "remote.wsdl");
        Assert.True(read.Ok);
        Assert.Contains(read.Diagnostics, d => d.Message.Contains("not inlined", StringComparison.Ordinal));

        var operation = Assert.Single(WsdlOperationMapper.Map(read.Document!));
        Assert.Equal("Go", operation.BodyElement);
        Assert.Equal(string.Empty, operation.BodyPayload);
    }

    /// <summary>A description that declares bindings but no service is a contract, not a
    /// deployment. Its operations are still worth importing, with the address left blank.</summary>
    [Fact]
    public void A_contract_without_a_service_still_yields_operations()
    {
        var wsdl = WsdlFixtures.Rpc.Replace(
            """
              <service name="Calculator">
                <port name="CalcPort" binding="tns:CalcBinding">
                  <soap:address location="https://calc.example.org/soap" />
                </port>
              </service>
            """, string.Empty, StringComparison.Ordinal);

        var warnings = new List<string>();
        var operation = Assert.Single(WsdlOperationMapper.Map(Read(wsdl), warnings));

        Assert.Null(operation.Address);
        Assert.Equal("CalcBinding/CalcBinding/Add", operation.OpKey);
        Assert.Contains(warnings, w => w.Contains("no <service>", StringComparison.Ordinal));
    }

    /// <summary>The hash is what a later comparison uses to answer "did the service change?", so
    /// reading the same bytes twice has to produce the same value.</summary>
    [Fact]
    public void The_source_hash_is_stable_across_reads()
        => Assert.Equal(
            Map().Select(o => o.SourceHash),
            Map().Select(o => o.SourceHash));
}
