// Demo.Api — exercises the surface area Tap captures and replays:
//   * /demo/methods/*    — every HTTP verb (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS)
//   * /demo/content/*    — common response content types (json, xml, text, html, png, binary, …)
//   * /demo/upload/*     — request content types (json, form-urlencoded, multipart, raw)
//   * /demo/stream/sse   — Server-Sent Events
//   * /demo/stream/ws    — WebSocket echo + heartbeat
//   * /graphql           — HotChocolate GraphQL (query + mutation + subscription over WS)
//   * /connect/token,    — OpenIddict OAuth2/OIDC (client_credentials + ROPC) with discovery,
//     /connect/userinfo,   userinfo, and introspection so Tap auth flows have a real OIDC IDP
//     /connect/introspect, to talk to.
//     /.well-known/openid-configuration

using Demo.Api;
using Demo.Api.Auth;
using Demo.Api.Endpoints;
using Demo.Api.GraphQL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Publishes /openapi/v1.json. AddTapStudio().WithApi(demoApi) reads it on first run and
// scaffolds a request per operation, which is what makes the Aspire sample worth running.
builder.Services.AddOpenApi();

DemoAuth.AddServices(builder);
DemoGraphQL.AddServices(builder);

var app = builder.Build();

app.MapOpenApi();

app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

// Seed OpenIddict client + a test user on first start.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DemoDbContext>().Database.EnsureCreatedAsync();
    await DemoAuth.SeedAsync(scope.ServiceProvider);
}

app.MapGet("/", () => Results.Json(new
{
    name = "Demo.Api",
    docs = "/demo/index",
    graphql = "/graphql",
    oidc = "/.well-known/openid-configuration",
}));

DemoEndpoints.MapIndex(app);
MethodsEndpoints.Map(app);
ContentEndpoints.Map(app);
UploadEndpoints.Map(app);
StreamingEndpoints.Map(app);
DemoAuth.MapEndpoints(app);
DemoGraphQL.MapEndpoints(app);

app.Run();
