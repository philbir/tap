using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Demo.Api.Endpoints;

/// <summary>
/// A real SOAP 1.1/1.2 service, so Tap's WSDL import has something to import <i>and</i> something
/// to send to. The same reason <c>/graphql</c> and the OpenIddict endpoints are here rather than
/// mocked: an import that produces requests nobody ever sends proves half the feature.
///
/// <para>Shaped like an ASMX service on purpose — one path serving the description at
/// <c>?wsdl</c> and the calls at <c>POST</c>, and the same portType bound over both SOAP versions.
/// That dual binding is the single most common thing a real .NET service does and the thing the
/// import wizard has to get right, so the sample does it too.</para>
/// </summary>
public static class SoapEndpoints
{
    public const string Path = "/demo/soap/weather.asmx";

    private const string Tns = "http://demo.tap.dev/weather";
    private const string Env11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Env12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string WsseNs =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public static void Map(WebApplication app)
    {
        // The description. Served off the same path as the calls, behind `?wsdl`, because that is
        // the URL people paste into an import wizard.
        app.MapGet(Path, (HttpRequest request) =>
        {
            if (!request.Query.ContainsKey("wsdl"))
            {
                return Results.Text(
                    $"Weather SOAP service. Description at {request.Path}?wsdl; POST a SOAP "
                    + "envelope here to call it.", "text/plain");
            }

            // Echo back the address this instance is actually reachable on, exactly as a real
            // stack does — so importing by URL yields a base URL that works without editing.
            var address = $"{request.Scheme}://{request.Host}{request.Path}";
            return Results.Content(Wsdl(address), "text/xml", Encoding.UTF8);
        });

        app.MapPost(Path, async (HttpRequest request) =>
        {
            string body;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            XDocument envelope;
            try
            {
                envelope = LoadSafely(body);
            }
            catch (XmlException ex)
            {
                return Fault(Env11, "Client", $"Request is not well-formed XML: {ex.Message}");
            }

            var root = envelope.Root;
            if (root is null || root.Name.LocalName != "Envelope")
                return Fault(Env11, "Client", "Expected a SOAP Envelope.");

            // The version the caller used decides the version we answer in. A 1.2 client that gets
            // a 1.1 envelope back sees a transport-level error, not a fault it can read.
            var version = root.Name.NamespaceName == Env12 ? Env12 : Env11;
            XNamespace envNs = version;

            var operation = root.Element(envNs + "Body")?.Elements().FirstOrDefault();
            if (operation is null)
                return Fault(version, "Client", "The SOAP Body is empty.");

            // Present only when the caller ticked "Add a WS-Security UsernameToken header" — the
            // response echoes it so the option visibly does something.
            var username = root
                .Element(envNs + "Header")?
                .Descendants(XNamespace.Get(WsseNs) + "Username")
                .FirstOrDefault()?.Value;

            XNamespace tns = Tns;
            return operation.Name.LocalName switch
            {
                "GetWeather" => GetWeather(version, operation, tns, username),
                "ListStations" => ListStations(version, operation, tns, username),
                // Every SOAP client needs a fault to test against, and a service that can only
                // succeed is not one.
                "Boom" => Fault(version, "Server",
                    operation.Element(tns + "Message")?.Value is { Length: > 0 } m
                        ? m
                        : "Boom, as requested."),
                var other => Fault(version, "Client", $"Unknown operation '{other}'."),
            };
        });
    }

    // --- operations ---------------------------------------------------------------------------

    private static IResult GetWeather(string version, XElement operation, XNamespace tns, string? username)
    {
        // The request schema offers a choice, so accept either arm.
        var city = operation.Element(tns + "CityName")?.Value;
        var stationId = operation.Element(tns + "StationId")?.Value;
        if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(stationId))
            return Fault(version, "Client", "Provide either CityName or StationId.");

        var place = string.IsNullOrWhiteSpace(city) ? $"station {stationId}" : city!;
        var fahrenheit = string.Equals(
            operation.Element(tns + "Units")?.Value, "Fahrenheit", StringComparison.Ordinal);

        // Derived from the place rather than randomly, so the same request always answers the
        // same way and a test set can assert on the number. `string.GetHashCode` would not do:
        // it is seeded per process, so it varies between runs of the very sample meant to be
        // reproducible.
        var celsius = 10 + StableHash(place) % 15;
        var days = int.TryParse(operation.Element(tns + "Days")?.Value, out var d) ? Math.Clamp(d, 1, 7) : 1;

        var forecasts = new StringBuilder();
        for (var i = 0; i < days; i++)
        {
            var value = fahrenheit ? celsius * 9 / 5 + 32 : celsius;
            forecasts.Append($"""
            <Day>
              <Offset>{i}</Offset>
              <Temperature unit="{(fahrenheit ? "Fahrenheit" : "Celsius")}">{value + i}</Temperature>
              <Conditions>{(i % 2 == 0 ? "Clear" : "Cloudy")}</Conditions>
            </Day>

            """);
        }

        return Envelope(version, $"""
            <GetWeatherResponse xmlns="{Tns}">
              <GetWeatherResult>
                <Place>{Escape(place)}</Place>
            {Indent(forecasts.ToString().TrimEnd(), 4)}
              </GetWeatherResult>
            {AuthenticatedAs(username)}
            </GetWeatherResponse>
            """);
    }

    private static IResult ListStations(string version, XElement operation, XNamespace tns, string? username)
    {
        var country = operation.Element(tns + "Country")?.Value is { Length: > 0 } c ? c : "NL";

        var stations = new StringBuilder();
        foreach (var (id, elevation) in new[] { ("AMS-01", "-3.5"), ("AMS-02", "1.0"), ("RTM-01", "0.0") })
        {
            stations.Append($"""
            <Station kind="automatic">
              <Id>{Escape($"{country}-{id}")}</Id>
              <Elevation>{elevation}</Elevation>
            </Station>

            """);
        }

        return Envelope(version, $"""
            <ListStationsResponse xmlns="{Tns}">
            {Indent(stations.ToString().TrimEnd(), 2)}
            {AuthenticatedAs(username)}
            </ListStationsResponse>
            """);
    }

    private static string AuthenticatedAs(string? username)
        => username is { Length: > 0 }
            ? $"  <AuthenticatedAs>{Escape(username)}</AuthenticatedAs>"
            : "  <AuthenticatedAs xsi:nil=\"true\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />";

    // --- envelopes ----------------------------------------------------------------------------

    private static IResult Envelope(string version, string payload)
        => Results.Content($"""
            <soap:Envelope xmlns:soap="{version}">
              <soap:Body>
            {Indent(payload, 4)}
              </soap:Body>
            </soap:Envelope>
            """, ContentType(version), Encoding.UTF8);

    /// <summary>
    /// A fault, in whichever dialect the caller speaks. SOAP 1.1 and 1.2 disagree on the element
    /// names <i>and</i> on the status code — 1.1 mandates 500, 1.2 allows 400 for a sender fault —
    /// so both are answered on their own terms.
    /// </summary>
    private static IResult Fault(string version, string code, string reason)
    {
        var body = version == Env12
            ? $"""
               <soap:Fault>
                 <soap:Code><soap:Value>soap:{(code == "Client" ? "Sender" : "Receiver")}</soap:Value></soap:Code>
                 <soap:Reason><soap:Text xml:lang="en">{Escape(reason)}</soap:Text></soap:Reason>
               </soap:Fault>
               """
            : $"""
               <soap:Fault>
                 <faultcode>soap:{code}</faultcode>
                 <faultstring>{Escape(reason)}</faultstring>
               </soap:Fault>
               """;

        return Results.Content($"""
            <soap:Envelope xmlns:soap="{version}">
              <soap:Body>
            {Indent(body, 4)}
              </soap:Body>
            </soap:Envelope>
            """, ContentType(version), Encoding.UTF8, statusCode: 500);
    }

    private static string ContentType(string version)
        => version == Env12 ? "application/soap+xml" : "text/xml";

    /// <summary>DTD prohibited and no resolver: the body is whatever a caller posted, and a DTD is
    /// how an XML parser is talked into reading local files or expanding an entity bomb.</summary>
    private static XDocument LoadSafely(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    /// <summary>Shifts a fragment right without touching its internal shape. Every fragment here
    /// is authored flush-left, so this call is the only thing that decides how deep it sits.</summary>
    private static string Indent(string xml, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', xml.Split('\n').Select(l => l.Trim().Length == 0 ? string.Empty : pad + l));
    }

    /// <summary>FNV-1a. Any stable function would do; what matters is that it is not the
    /// runtime's randomized string hash.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var ch in value) hash = (hash ^ ch) * 16777619;
            return (int)(hash & 0x7fffffff);
        }
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // --- description --------------------------------------------------------------------------

    /// <summary>
    /// The WSDL, hand-written and self-contained.
    ///
    /// <para>Self-contained matters: the schemas are inlined rather than pulled in with
    /// <c>xsd:import</c>, because Tap deliberately never follows a <c>schemaLocation</c> named
    /// inside a document it fetched. A sample that needed a second fetch would demonstrate the
    /// limitation instead of the feature.</para>
    ///
    /// <para>The types are chosen to exercise the generator: a <c>choice</c> (only the first arm is
    /// generated, the rest are named in a comment), an enumerated simple type (the first value is
    /// used, since it is one the service definitely accepts), a required attribute, and a complex
    /// type that contains itself.</para>
    /// </summary>
    private static string Wsdl(string address) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                          xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                          xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                          xmlns:s="http://www.w3.org/2001/XMLSchema"
                          xmlns:tns="{Tns}"
                          targetNamespace="{Tns}">
          <wsdl:documentation>
            Demo.Api's SOAP face. Forecasts, station metadata, and an operation that always faults.
          </wsdl:documentation>

          <wsdl:types>
            <s:schema elementFormDefault="qualified" targetNamespace="{Tns}">

              <s:element name="GetWeather">
                <s:complexType>
                  <s:sequence>
                    <s:choice>
                      <s:element name="CityName" type="s:string" />
                      <s:element name="StationId" type="s:string" />
                    </s:choice>
                    <s:element minOccurs="0" maxOccurs="1" name="Days" type="s:int" />
                    <s:element minOccurs="0" maxOccurs="1" name="Units" type="tns:Unit" />
                    <s:element minOccurs="0" maxOccurs="1" name="Origin" type="tns:Station" />
                  </s:sequence>
                </s:complexType>
              </s:element>
              <s:element name="GetWeatherResponse">
                <s:complexType>
                  <s:sequence>
                    <s:element minOccurs="0" maxOccurs="1" name="GetWeatherResult" type="tns:Forecast" />
                    <s:element minOccurs="0" maxOccurs="1" nillable="true" name="AuthenticatedAs" type="s:string" />
                  </s:sequence>
                </s:complexType>
              </s:element>

              <s:element name="ListStations">
                <s:complexType>
                  <s:sequence>
                    <s:element minOccurs="0" maxOccurs="1" name="Country" type="s:string" />
                  </s:sequence>
                </s:complexType>
              </s:element>
              <s:element name="ListStationsResponse">
                <s:complexType>
                  <s:sequence>
                    <s:element minOccurs="0" maxOccurs="unbounded" name="Station" type="tns:Station" />
                    <s:element minOccurs="0" maxOccurs="1" nillable="true" name="AuthenticatedAs" type="s:string" />
                  </s:sequence>
                </s:complexType>
              </s:element>

              <s:element name="Boom">
                <s:complexType>
                  <s:sequence>
                    <s:element minOccurs="0" maxOccurs="1" name="Message" type="s:string" />
                  </s:sequence>
                </s:complexType>
              </s:element>
              <s:element name="BoomResponse">
                <s:complexType />
              </s:element>

              <s:simpleType name="Unit">
                <s:restriction base="s:string">
                  <s:enumeration value="Celsius" />
                  <s:enumeration value="Fahrenheit" />
                </s:restriction>
              </s:simpleType>

              <s:complexType name="Station">
                <s:sequence>
                  <s:element minOccurs="0" maxOccurs="1" name="Id" type="s:string" />
                  <s:element minOccurs="0" maxOccurs="1" name="Elevation" type="s:decimal" />
                  <s:element minOccurs="0" maxOccurs="1" name="Nearest" type="tns:Station" />
                </s:sequence>
                <s:attribute name="kind" type="s:string" use="required" />
              </s:complexType>

              <s:complexType name="Forecast">
                <s:sequence>
                  <s:element minOccurs="0" maxOccurs="1" name="Place" type="s:string" />
                  <s:element minOccurs="0" maxOccurs="unbounded" name="Day" type="tns:DayForecast" />
                </s:sequence>
              </s:complexType>

              <s:complexType name="DayForecast">
                <s:sequence>
                  <s:element minOccurs="1" maxOccurs="1" name="Offset" type="s:int" />
                  <s:element minOccurs="0" maxOccurs="1" name="Temperature" type="tns:Temperature" />
                  <s:element minOccurs="0" maxOccurs="1" name="Conditions" type="s:string" />
                </s:sequence>
              </s:complexType>

              <s:complexType name="Temperature">
                <s:simpleContent>
                  <s:extension base="s:decimal">
                    <s:attribute name="unit" type="tns:Unit" use="required" />
                  </s:extension>
                </s:simpleContent>
              </s:complexType>

            </s:schema>
          </wsdl:types>

          <wsdl:message name="GetWeatherIn"><wsdl:part name="parameters" element="tns:GetWeather" /></wsdl:message>
          <wsdl:message name="GetWeatherOut"><wsdl:part name="parameters" element="tns:GetWeatherResponse" /></wsdl:message>
          <wsdl:message name="ListStationsIn"><wsdl:part name="parameters" element="tns:ListStations" /></wsdl:message>
          <wsdl:message name="ListStationsOut"><wsdl:part name="parameters" element="tns:ListStationsResponse" /></wsdl:message>
          <wsdl:message name="BoomIn"><wsdl:part name="parameters" element="tns:Boom" /></wsdl:message>
          <wsdl:message name="BoomOut"><wsdl:part name="parameters" element="tns:BoomResponse" /></wsdl:message>

          <wsdl:portType name="WeatherSoap">
            <wsdl:operation name="GetWeather">
              <wsdl:documentation>Forecast for a city or a station, up to seven days out.</wsdl:documentation>
              <wsdl:input message="tns:GetWeatherIn" />
              <wsdl:output message="tns:GetWeatherOut" />
            </wsdl:operation>
            <wsdl:operation name="ListStations">
              <wsdl:documentation>Every weather station in a country.</wsdl:documentation>
              <wsdl:input message="tns:ListStationsIn" />
              <wsdl:output message="tns:ListStationsOut" />
            </wsdl:operation>
            <wsdl:operation name="Boom">
              <wsdl:documentation>Always answers with a SOAP Fault. For testing the failure path.</wsdl:documentation>
              <wsdl:input message="tns:BoomIn" />
              <wsdl:output message="tns:BoomOut" />
            </wsdl:operation>
          </wsdl:portType>

          <wsdl:binding name="WeatherSoap" type="tns:WeatherSoap">
            <soap:binding transport="http://schemas.xmlsoap.org/soap/http" style="document" />
            <wsdl:operation name="GetWeather">
              <soap:operation soapAction="{Tns}/GetWeather" style="document" />
              <wsdl:input><soap:body use="literal" /></wsdl:input>
              <wsdl:output><soap:body use="literal" /></wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="ListStations">
              <soap:operation soapAction="{Tns}/ListStations" style="document" />
              <wsdl:input><soap:body use="literal" /></wsdl:input>
              <wsdl:output><soap:body use="literal" /></wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="Boom">
              <soap:operation soapAction="{Tns}/Boom" style="document" />
              <wsdl:input><soap:body use="literal" /></wsdl:input>
              <wsdl:output><soap:body use="literal" /></wsdl:output>
            </wsdl:operation>
          </wsdl:binding>

          <wsdl:binding name="WeatherSoap12" type="tns:WeatherSoap">
            <soap12:binding transport="http://schemas.xmlsoap.org/soap/http" style="document" />
            <wsdl:operation name="GetWeather">
              <soap12:operation soapAction="{Tns}/GetWeather" style="document" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input>
              <wsdl:output><soap12:body use="literal" /></wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="ListStations">
              <soap12:operation soapAction="{Tns}/ListStations" style="document" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input>
              <wsdl:output><soap12:body use="literal" /></wsdl:output>
            </wsdl:operation>
            <wsdl:operation name="Boom">
              <soap12:operation soapAction="{Tns}/Boom" style="document" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input>
              <wsdl:output><soap12:body use="literal" /></wsdl:output>
            </wsdl:operation>
          </wsdl:binding>

          <wsdl:service name="Weather">
            <wsdl:documentation>Demo.Api weather service.</wsdl:documentation>
            <wsdl:port name="WeatherSoap" binding="tns:WeatherSoap">
              <soap:address location="{address}" />
            </wsdl:port>
            <wsdl:port name="WeatherSoap12" binding="tns:WeatherSoap12">
              <soap12:address location="{address}" />
            </wsdl:port>
          </wsdl:service>
        </wsdl:definitions>
        """;
}
