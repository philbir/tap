using Tap.Studio.Wsdl;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Wsdl;

/// <summary>
/// Planning a WSDL import. The contract that matters most is the one this project tests for every
/// emitter: whatever we write must parse, and writing it twice must produce the same bytes.
/// </summary>
public class WsdlImportPlannerTests
{
    private static WsdlImportPlanner.Result Plan(
        WsdlImportPlanner.Options? options = null, string wsdl = WsdlFixtures.Weather)
    {
        var read = WsdlDocumentReader.Read(wsdl, "weather.wsdl");
        Assert.True(read.Ok, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        return WsdlImportPlanner.Plan(read.Document!, options ?? new WsdlImportPlanner.Options());
    }

    private static string Content(WsdlImportPlanner.Result result, string suffix)
        => result.Plan.Files.Single(f => f.RelativePath.EndsWith(suffix, StringComparison.Ordinal)).Content;

    [Fact]
    public void The_slug_and_collection_come_from_the_service_name()
    {
        var result = Plan();

        Assert.Equal("weather", result.Plan.Slug);
        Assert.Equal("collections/weather/_collection.tap", result.Plan.CollectionPath);
    }

    /// <summary>Both SOAP ports are selected, so each gets its own folder — otherwise the two
    /// bindings' identically named operations would fight over one filename.</summary>
    [Fact]
    public void A_folder_per_port_appears_only_when_more_than_one_is_imported()
    {
        var both = Plan().Plan.Files.Select(f => f.RelativePath).ToArray();
        Assert.Contains("collections/weather/weather-soap/get-weather.req.tap", both);
        Assert.Contains("collections/weather/weather-soap12/get-weather.req.tap", both);

        var one = Plan(new WsdlImportPlanner.Options
        {
            OperationKeys = ["Weather/WeatherSoap/GetWeather"],
        }).Plan.Files.Select(f => f.RelativePath).ToArray();
        Assert.Contains("collections/weather/get-weather.req.tap", one);
    }

    /// <summary>Everything the planner emits goes through WorkspaceService.Save, which parses
    /// before writing. A file that does not parse is a failed import, not a bad file on disk.</summary>
    [Fact]
    public void Every_emitted_file_parses()
    {
        foreach (var layout in new[] { WsdlImportPlanner.Layout.RequestPerOperation, WsdlImportPlanner.Layout.HttpFilePerPort })
        {
            foreach (var file in Plan(new WsdlImportPlanner.Options { Layout = layout }).Plan.Files)
            {
                if (file.RelativePath.EndsWith(KindResolver.HttpExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                Assert.NotNull(FileParser.Parse(file.RelativePath, file.Content));
            }
        }
    }

    /// <summary>The generated body is hashed into the collection's lock. A planner that emitted
    /// different bytes for the same input would report every request as edited, forever.</summary>
    [Fact]
    public void Planning_the_same_document_twice_emits_identical_bytes()
    {
        var first = Plan().Plan.Files.ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);
        var second = Plan().Plan.Files.ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Equal(
            first.Keys.OrderBy(k => k, StringComparer.Ordinal),
            second.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (path, content) in first)
        {
            // Ids are the one deliberately unique value; strip them before comparing.
            Assert.Equal(StripId(content), StripId(second[path]));
        }
    }

    /// <summary>The base URL is the endpoint's origin only, and each request keeps its own path —
    /// a WSDL routinely binds several ports at different paths on one host.</summary>
    [Fact]
    public void The_base_url_is_the_endpoint_origin_and_requests_stay_relative_to_it()
    {
        var result = Plan();
        Assert.Equal("http://example.org", result.BaseUrl);

        var request = (RequestFile)FileParser.Parse(
            "collections/weather/weather-soap/get-weather.req.tap",
            Content(result, "weather-soap/get-weather.req.tap"));

        Assert.Contains("POST /weather.asmx", request.HttpBlock);
    }

    /// <summary>An explicit base URL is the user saying "send these somewhere else", so the
    /// document's own addresses must not pin the request to the original host.</summary>
    [Fact]
    public void An_explicit_base_url_keeps_every_request_relative()
    {
        var result = Plan(new WsdlImportPlanner.Options { BaseUrl = "{{aspire:weather}}" });
        Assert.Equal("{{aspire:weather}}", result.BaseUrl);

        Assert.Contains("POST /weather.asmx", Content(result, "weather-soap/get-weather.req.tap"));
    }

    [Fact]
    public void A_generated_request_carries_the_envelope_and_the_headers_its_binding_needs()
    {
        var result = Plan();

        var eleven = (RequestFile)FileParser.Parse(
            "collections/weather/weather-soap/get-weather.req.tap",
            Content(result, "weather-soap/get-weather.req.tap"));
        Assert.Contains("Content-Type: text/xml; charset=utf-8", eleven.HttpBlock);
        Assert.Contains("SOAPAction: \"http://example.org/weather/GetWeather\"", eleven.HttpBlock);
        Assert.Contains("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">", eleven.HttpBlock);
        Assert.Contains("<GetWeather xmlns=\"http://example.org/weather\">", eleven.HttpBlock);

        var twelve = (RequestFile)FileParser.Parse(
            "collections/weather/weather-soap12/get-weather.req.tap",
            Content(result, "weather-soap12/get-weather.req.tap"));
        Assert.Contains("application/soap+xml", twelve.HttpBlock);
        Assert.DoesNotContain("SOAPAction:", twelve.HttpBlock);
        Assert.Contains("<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\">", twelve.HttpBlock);
    }

    /// <summary>Credentials belong on the collection: one place to fill them in, and the password
    /// marked secret so it is never written to the file as plain text.</summary>
    [Fact]
    public void The_username_token_option_declares_its_credentials_on_the_collection()
    {
        var result = Plan(new WsdlImportPlanner.Options { AddUsernameToken = true });

        var collection = (CollectionFile)FileParser.Parse(
            "collections/weather/_collection.tap", Content(result, "_collection.tap"));
        Assert.Contains("wsseUsername", collection.Vars.Keys);
        Assert.Contains("wssePassword", collection.Vars.Keys);

        var request = (RequestFile)FileParser.Parse(
            "collections/weather/weather-soap/get-weather.req.tap",
            Content(result, "weather-soap/get-weather.req.tap"));
        Assert.Contains("<wsse:Username>{{wsseUsername}}</wsse:Username>", request.HttpBlock);
    }

    [Fact]
    public void Without_the_option_no_header_is_written()
    {
        var request = Content(Plan(), "weather-soap/get-weather.req.tap");

        Assert.DoesNotContain("wsse:Security", request);
        Assert.DoesNotContain("soap:Header", request);
    }

    [Fact]
    public void The_http_layout_writes_one_portable_file_per_port()
    {
        var result = Plan(new WsdlImportPlanner.Options { Layout = WsdlImportPlanner.Layout.HttpFilePerPort });

        var paths = result.Plan.Files.Select(f => f.RelativePath).ToArray();
        Assert.Contains("collections/weather/weather-soap.http", paths);
        Assert.Contains("collections/weather/weather-soap12.http", paths);

        var file = Content(result, "weather-soap.http");
        Assert.Contains("@baseUrl = http://example.org", file);
        Assert.Contains("POST {{baseUrl}}/weather.asmx", file);
        Assert.Contains("SOAPAction: \"http://example.org/weather/GetWeather\"", file);
        Assert.Contains("<soap:Envelope", file);
    }

    /// <summary>Each operation is tracked back to the file it landed in, which is what a later
    /// comparison against the lock needs.</summary>
    [Fact]
    public void Every_selected_operation_is_planned_exactly_once()
    {
        var result = Plan();

        Assert.Equal(2, result.Plan.RequestCount);
        Assert.Equal(
            result.Operations.Select(o => o.OpKey).OrderBy(k => k, StringComparer.Ordinal),
            result.Planned.Select(p => p.Operation.OpKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(result.Planned, p => Assert.NotNull(p.FileId));
    }

    [Fact]
    public void The_http_layout_hashes_each_section_rather_than_the_whole_file()
    {
        var result = Plan(new WsdlImportPlanner.Options { Layout = WsdlImportPlanner.Layout.HttpFilePerPort });

        Assert.All(result.Planned, p =>
        {
            Assert.EndsWith(".http", p.RelativePath, StringComparison.Ordinal);
            Assert.Equal("get-weather", p.Fragment);
            Assert.Null(p.FileId);
        });
    }

    [Fact]
    public void Selecting_nothing_that_exists_is_an_error_rather_than_an_empty_collection()
        => Assert.Throws<WsdlImportException>(() => Plan(new WsdlImportPlanner.Options
        {
            OperationKeys = ["Nope/Nope/Nope"],
        }));

    /// <summary>A contract-only description has no address to derive a base URL from. The import
    /// still succeeds and says so, rather than silently writing unsendable requests.</summary>
    [Fact]
    public void A_description_without_an_address_warns_instead_of_failing()
    {
        var wsdl = WsdlFixtures.Rpc.Replace(
            """
              <service name="Calculator">
                <port name="CalcPort" binding="tns:CalcBinding">
                  <soap:address location="https://calc.example.org/soap" />
                </port>
              </service>
            """, string.Empty, StringComparison.Ordinal);

        var result = Plan(wsdl: wsdl);

        Assert.Null(result.BaseUrl);
        Assert.Contains(result.Plan.Warnings, w => w.Contains("no base URL", StringComparison.Ordinal));
    }

    private static string StripId(string content)
        => string.Join('\n', content.Split('\n').Where(l => !l.StartsWith("id:", StringComparison.Ordinal)));
}
