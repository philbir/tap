using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tap.Studio.Contracts;
using Tap.Workspace.Model;
using Tap.Execution.Http;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>POST /api/graphql/schema</c> — render the request (URL + headers + auth come from the
/// usual pipeline) but swap in either a standard introspection POST body or a
/// <c>?sdl</c> GET, then return whatever the upstream answered. The UI builds a
/// <c>GraphQLSchema</c> from the result to drive autocomplete / validation.
/// </summary>
public static class GraphQLSchemaEndpoint
{
    private const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
            directives { name description locations args { ...InputValue } }
          }
        }
        fragment FullType on __Type {
          kind name description
          fields(includeDeprecated: true) {
            name description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue { name description type { ...TypeRef } defaultValue }
        fragment TypeRef on __Type {
          kind name
          ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } }
        }
        """;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        // See ExecuteEndpoint: hops are followed manually so each new host is re-validated.
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/graphql/schema",
            async (GraphQLSchemaRequestDto body, WorkspaceService svc, CancellationToken ct) =>
        {
            try
            {
                var rendered = await svc.RenderAsync(body.Path, body.Env, null, ct).ConfigureAwait(false);
                HttpExecutionHelpers.ValidateScheme(rendered);

                var isSdl = string.Equals(body.Mode, "sdl", StringComparison.OrdinalIgnoreCase);

                var url = rendered.Url;
                string method;
                string? requestBody;

                if (isSdl)
                {
                    method = "GET";
                    requestBody = null;
                    url += url.Contains('?') ? "&sdl" : "?sdl";
                }
                else
                {
                    method = "POST";
                    requestBody = JsonSerializer.Serialize(
                        new IntrospectionBody(IntrospectionQuery, "IntrospectionQuery"),
                        StudioJson.Default.IntrospectionBody);
                }

                using var req = new HttpRequestMessage(new HttpMethod(method), url);

                if (requestBody is not null)
                {
                    var content = new StringContent(requestBody, Encoding.UTF8);
                    content.Headers.Clear();
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    req.Content = content;
                }

                foreach (var (k, v) in rendered.Headers)
                {
                    // Skip headers that don't make sense for the introspection probe.
                    if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isSdl && k.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.TryAddWithoutValidation("Accept", "application/json, */*");
                        continue;
                    }
                    req.Headers.TryAddWithoutValidation(k, v);
                }
                if (!isSdl && !req.Headers.Accept.Any())
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (!req.Headers.Contains("User-Agent"))
                    req.Headers.TryAddWithoutValidation("User-Agent", StudioVersion.UserAgent);

                // ResponseHeadersRead + a capped read: an introspection reply is JSON we hand
                // straight to the UI, so a multi-gigabyte body from a hostile endpoint has to be
                // cut off at the same 2 MiB the execute endpoints enforce, not buffered whole.
                using var resp = await HttpExecutionHelpers.SendFollowingRedirectsAsync(
                    HttpClient, req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var text = await HttpExecutionHelpers.ReadCappedStringAsync(resp, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Ok(new GraphQLSchemaResponseDto(
                        Schema: null,
                        Error: $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(text, 4000)}"));
                }

                return Results.Ok(new GraphQLSchemaResponseDto(Schema: text, Error: null));
            }
            catch (WorkspaceParseException ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto(
                    ex.Error.Code, ex.Error.Message, ex.Error.RelativePath, ex.Error.Line));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new GraphQLSchemaResponseDto(Schema: null, Error: ex.Message));
            }
        });
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public sealed record IntrospectionBody(string Query, string OperationName);
}
