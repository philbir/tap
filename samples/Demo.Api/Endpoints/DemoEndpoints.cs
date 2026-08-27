namespace Demo.Api.Endpoints;

/// <summary>Catalog endpoint — emits a flat list of every demo route so an unfamiliar
/// user can poke around without consulting the source.</summary>
public static class DemoEndpoints
{
    public static void MapIndex(WebApplication app)
    {
        app.MapGet("/demo/index", () => Results.Json(new
        {
            methods = new[]
            {
                "GET    /demo/methods/",
                "POST   /demo/methods/",
                "PUT    /demo/methods/",
                "PATCH  /demo/methods/",
                "DELETE /demo/methods/",
                "HEAD   /demo/methods/",
                "OPTIONS /demo/methods/",
                "GET    /demo/methods/status/{code}",
                "GET    /demo/methods/headers",
                "GET    /demo/methods/query",
            },
            content = new[]
            {
                "GET /demo/content/json",
                "GET /demo/content/xml",
                "GET /demo/content/yaml",
                "GET /demo/content/text",
                "GET /demo/content/html",
                "GET /demo/content/css",
                "GET /demo/content/javascript",
                "GET /demo/content/csv",
                "GET /demo/content/markdown",
                "GET /demo/content/png",
                "GET /demo/content/jpeg",
                "GET /demo/content/svg",
                "GET /demo/content/binary",
                "GET /demo/content/problem",
                "GET /demo/content/empty",
                "GET /demo/content/large/{kib}",
                "GET /demo/content/slow/{ms}",
            },
            uploads = new[]
            {
                "POST /demo/upload/json",
                "POST /demo/upload/form",
                "POST /demo/upload/multipart",
                "POST /demo/upload/text",
                "POST /demo/upload/raw",
            },
            streaming = new[]
            {
                "GET /demo/stream/sse?count=5&interval=500",
                "GET /demo/stream/ws    (WebSocket upgrade)",
            },
            soap = new[]
            {
                $"GET  {Endpoints.SoapEndpoints.Path}?wsdl   (WSDL 1.1, SOAP 1.1 + 1.2 bindings)",
                $"POST {Endpoints.SoapEndpoints.Path}        (GetWeather, ListStations, Boom)",
            },
            graphql = new[]
            {
                "POST /graphql",
                "GET  /graphql (Nitro UI)",
            },
            auth = new
            {
                discovery = "/.well-known/openid-configuration",
                token = "/connect/token",
                userinfo = "/connect/userinfo",
                introspection = "/connect/introspect",
                client_id = Demo.Api.Auth.DemoAuth.ClientId,
                client_secret = Demo.Api.Auth.DemoAuth.ClientSecret,
                test_user = Demo.Api.Auth.DemoAuth.TestUser,
                test_password = Demo.Api.Auth.DemoAuth.TestPassword,
                protected_sample = "/demo/auth/whoami",
            },
        }));
    }
}
