namespace Tap.Tests.Wsdl;

/// <summary>
/// WSDL documents the mapper and the planner tests both read. Inline rather than a fixture
/// directory, matching the rest of this project — the document <i>is</i> the specification for
/// what Tap extracts, so it belongs next to the assertions about it.
/// </summary>
internal static class WsdlFixtures
{
    /// <summary>
    /// A .NET-shaped description: one portType bound twice (SOAP 1.1 and 1.2) plus the HttpGet
    /// binding a real ASMX service publishes alongside them, document/literal, qualified schema.
    /// </summary>
    public const string Weather = """
    <?xml version="1.0" encoding="utf-8"?>
    <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                      xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                      xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                      xmlns:http="http://schemas.xmlsoap.org/wsdl/http/"
                      xmlns:s="http://www.w3.org/2001/XMLSchema"
                      xmlns:tns="http://example.org/weather"
                      targetNamespace="http://example.org/weather">
      <wsdl:documentation>Weather lookups.</wsdl:documentation>
      <wsdl:types>
        <s:schema elementFormDefault="qualified" targetNamespace="http://example.org/weather">
          <s:element name="GetWeather">
            <s:complexType>
              <s:sequence>
                <s:element minOccurs="0" maxOccurs="1" name="CityName" type="s:string" />
                <s:element minOccurs="1" maxOccurs="1" name="Days" type="s:int" />
                <s:element minOccurs="0" maxOccurs="1" name="Units" type="tns:Unit" />
                <s:element minOccurs="0" maxOccurs="1" name="Station" type="tns:Station" />
              </s:sequence>
            </s:complexType>
          </s:element>
          <s:element name="GetWeatherResponse">
            <s:complexType>
              <s:sequence>
                <s:element minOccurs="0" maxOccurs="1" name="GetWeatherResult" type="s:string" />
              </s:sequence>
            </s:complexType>
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
              <s:element minOccurs="0" maxOccurs="1" name="Nearest" type="tns:Station" />
            </s:sequence>
            <s:attribute name="kind" type="s:string" use="required" />
            <s:attribute name="note" type="s:string" />
          </s:complexType>
        </s:schema>
      </wsdl:types>
      <wsdl:message name="GetWeatherSoapIn">
        <wsdl:part name="parameters" element="tns:GetWeather" />
      </wsdl:message>
      <wsdl:message name="GetWeatherSoapOut">
        <wsdl:part name="parameters" element="tns:GetWeatherResponse" />
      </wsdl:message>
      <wsdl:portType name="WeatherSoap">
        <wsdl:operation name="GetWeather">
          <wsdl:documentation>Look up the forecast.</wsdl:documentation>
          <wsdl:input message="tns:GetWeatherSoapIn" />
          <wsdl:output message="tns:GetWeatherSoapOut" />
        </wsdl:operation>
      </wsdl:portType>
      <wsdl:binding name="WeatherSoap" type="tns:WeatherSoap">
        <soap:binding transport="http://schemas.xmlsoap.org/soap/http" style="document" />
        <wsdl:operation name="GetWeather">
          <soap:operation soapAction="http://example.org/weather/GetWeather" style="document" />
          <wsdl:input><soap:body use="literal" /></wsdl:input>
          <wsdl:output><soap:body use="literal" /></wsdl:output>
        </wsdl:operation>
      </wsdl:binding>
      <wsdl:binding name="WeatherSoap12" type="tns:WeatherSoap">
        <soap12:binding transport="http://schemas.xmlsoap.org/soap/http" style="document" />
        <wsdl:operation name="GetWeather">
          <soap12:operation soapAction="http://example.org/weather/GetWeather" style="document" />
          <wsdl:input><soap12:body use="literal" /></wsdl:input>
          <wsdl:output><soap12:body use="literal" /></wsdl:output>
        </wsdl:operation>
      </wsdl:binding>
      <wsdl:binding name="WeatherHttpGet" type="tns:WeatherHttpGet">
        <http:binding verb="GET" />
      </wsdl:binding>
      <wsdl:service name="Weather">
        <wsdl:port name="WeatherSoap" binding="tns:WeatherSoap">
          <soap:address location="http://example.org/weather.asmx" />
        </wsdl:port>
        <wsdl:port name="WeatherSoap12" binding="tns:WeatherSoap12">
          <soap12:address location="http://example.org/weather.asmx" />
        </wsdl:port>
        <wsdl:port name="WeatherHttpGet" binding="tns:WeatherHttpGet">
          <http:address location="http://example.org/weather.asmx" />
        </wsdl:port>
      </wsdl:service>
    </wsdl:definitions>
    """;

    /// <summary>rpc/literal over an unqualified schema, with one part typed by QName rather
    /// than declared as an element — the shape a Java or gSOAP service publishes.</summary>
    public const string Rpc = """
    <?xml version="1.0" encoding="utf-8"?>
    <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                 xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                 xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                 xmlns:tns="urn:calc"
                 targetNamespace="urn:calc">
      <types>
        <xsd:schema targetNamespace="urn:calc" elementFormDefault="unqualified">
          <xsd:complexType name="Operand">
            <xsd:sequence>
              <xsd:element name="value" type="xsd:decimal" />
            </xsd:sequence>
          </xsd:complexType>
        </xsd:schema>
      </types>
      <message name="AddRequest">
        <part name="left" type="tns:Operand" />
        <part name="right" type="xsd:int" />
      </message>
      <message name="AddResponse">
        <part name="result" type="xsd:int" />
      </message>
      <portType name="CalcPort">
        <operation name="Add">
          <input message="tns:AddRequest" />
          <output message="tns:AddResponse" />
        </operation>
      </portType>
      <binding name="CalcBinding" type="tns:CalcPort">
        <soap:binding transport="http://schemas.xmlsoap.org/soap/http" style="rpc" />
        <operation name="Add">
          <soap:operation soapAction="" style="rpc" />
          <input><soap:body use="literal" namespace="urn:calc" /></input>
          <output><soap:body use="literal" namespace="urn:calc" /></output>
        </operation>
      </binding>
      <service name="Calculator">
        <port name="CalcPort" binding="tns:CalcBinding">
          <soap:address location="https://calc.example.org/soap" />
        </port>
      </service>
    </definitions>
    """;
}
