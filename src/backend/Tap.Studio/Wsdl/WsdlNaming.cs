using Tap.Studio.Importing;

namespace Tap.Studio.Wsdl;

/// <summary>Derives the file and request slugs for a SOAP operation. Shared so a collection
/// imported as <c>.req.tap</c> and the same one imported as <c>.http</c> name their requests
/// identically.</summary>
public static class SoapRequestSlug
{
    /// <summary>
    /// The operation's own name, which is the name the service's authors chose and the one every
    /// generated client uses. WSDL operation names are PascalCase by near-universal convention
    /// (<c>GetWeatherByZipCode</c>), so they are split before slugifying or the filename collapses
    /// to <c>getweatherbyzipcode</c>.
    /// </summary>
    public static string For(MappedSoapOperation operation)
    {
        var slug = ImportSlug.Slugify(ImportSlug.SplitCamelCase(operation.Name));
        return slug.Length > 0 ? slug : "operation";
    }

    /// <summary>
    /// Slug for a port — the folder in the structured layout, and the filename in the
    /// <c>.http</c> one. The service name is folded in only when it differs from the port's, which
    /// keeps the overwhelmingly common <c>Weather</c> / <c>WeatherSoap</c> pair from producing a
    /// stuttering <c>weather-weather-soap</c>.
    /// </summary>
    public static string ForPort(MappedSoapOperation operation)
    {
        var port = operation.PortName;
        var name = port.StartsWith(operation.ServiceName, StringComparison.OrdinalIgnoreCase)
            ? port
            : $"{operation.ServiceName} {port}";

        var slug = ImportSlug.Slugify(ImportSlug.SplitCamelCase(name));
        return slug.Length > 0 ? slug : "service";
    }
}
