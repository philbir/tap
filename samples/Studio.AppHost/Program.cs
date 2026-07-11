// Studio.AppHost — local dev runner for Tap Studio.
//
//   studio-api          REST + SSE backend (Tap.Studio, ASP.NET Core)
//   studio-ui           Vite dev server hosting the React UI (src/ui-studio/)
//
// Aspire allocates the ports for both resources; the Vite proxy URL is wired up via
// a reference to studio-api so the UI always points at the right place.
//
// Override the workspace path with:
//   STUDIO_WORKSPACE=/path/to/your/repo aspire run

var builder = DistributedApplication.CreateBuilder(args);

// Resolve the workspace root. Default: the bundled sample-workspace under samples/.
var workspaceRoot = Environment.GetEnvironmentVariable("STUDIO_WORKSPACE")
    ?? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "sample-workspace"));

// Demo.Api — the canonical upstream for trying out Tap Studio. Exercises every
// content type / HTTP verb plus SSE, WebSockets, GraphQL, and OAuth2/OIDC. Runs
// independently of Studio so workspace requests can target it (no .WithReference —
// the studio doesn't talk to the demo API directly; the user's request files do).
// Port is dynamic; the resolved host:port (no scheme) is forwarded to studio-api as
// DEMO_API_URL so sample-workspace/apis/demo.api.md can pick it up via
// `{{DEMO_API_URL}}`. The renderer prepends `http://` for HTTP requests and `ws://`
// for WebSocket requests, so the same variable serves both transports.
var demoApi = builder.AddProject<Projects.Demo_Api>("demo-api")
    .WithHttpEndpoint(name: "http")
    .WithExternalHttpEndpoints();

// Dev-only fallbacks for the sample auth profiles. Each one is referenced by a
// `{{env:NAME}}` token in sample-workspace/auth/*; without a value the env
// provider raises E_PROVIDER_RESOLUTION_FAILED on first execute. Real usage should
// set these in the user's shell; we honor that and only inject a deterministic dev
// fallback when missing so the sample is runnable out of the box.
static string DemoSecret(string name, string fallback)
    => Environment.GetEnvironmentVariable(name) ?? fallback;

var jwtSecret = DemoSecret("DEMO_JWT_SECRET", "studio-demo-jwt-secret-not-for-production-use");
var demoBearer = DemoSecret("DEMO_BEARER_TOKEN", "studio-demo-bearer-token-not-for-production-use");
var demoBasicPassword = DemoSecret("DEMO_BASIC_PASSWORD", "studio-demo-basic-password-not-for-production-use");
var demoApiKey = DemoSecret("DEMO_API_KEY", "studio-demo-api-key-not-for-production-use");

// Tap reads from the host environment via the workspace's `env` provider (declared in
// tap.md). Two allowlists gate which names that provider exposes: TAP_VARS_ALLOWED
// carries names whose values may be displayed; TAP_SECRETS_ALLOWED carries names whose
// values stay masked but resolve normally when referenced as `{{env:NAME}}`. The sample
// workspace pulls DEMO_* + USER + AZURE_*; only DEMO_API_URL + USER are "showable" — the
// rest are credentials. Override these on your shell to broaden the surface.
var studio = builder.AddProject<Projects.Tap_Studio>("studio-api")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("Studio__WorkspaceRoot", workspaceRoot)
    .WithEnvironment("DEMO_API_URL", demoApi.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
    .WithEnvironment("DEMO_JWT_SECRET", jwtSecret)
    .WithEnvironment("DEMO_BEARER_TOKEN", demoBearer)
    .WithEnvironment("DEMO_BASIC_PASSWORD", demoBasicPassword)
    .WithEnvironment("DEMO_API_KEY", demoApiKey)
    .WithEnvironment("TAP_VARS_ALLOWED", "DEMO_API_URL,USER")
    .WithEnvironment("TAP_SECRETS_ALLOWED", "DEMO_BEARER_TOKEN,DEMO_BASIC_PASSWORD,DEMO_API_KEY,DEMO_JWT_SECRET,AZURE_*")
    .WaitFor(demoApi)
    .WithExternalHttpEndpoints();

// Demo.Api must register Studio's OAuth callback URL on its seeded client(s). The
// callback lives at <studio-base>/api/auth/callback, and the Studio base URL is
// Aspire-allocated per run — so we forward it via env var and Demo.Api reads it at
// seed time. Without this, the client's registered redirect_uri wouldn't match the
// one Studio sends on the authorize request, and OpenIddict would reject the flow.
demoApi.WithEnvironment("STUDIO_CALLBACK_URL",
    ReferenceExpression.Create($"{studio.GetEndpoint("http")}/api/auth/callback"));

// Vite UI — VITE_STUDIO_API_URL is resolved at start time from the studio-api endpoint.
var uiDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "src", "ui-studio"));
builder.AddViteApp("studio-ui", uiDir, "dev")
    .WithYarn()
    .WithEnvironment("VITE_STUDIO_API_URL", studio.GetEndpoint("http"))
    .WaitFor(studio)
    .WithExternalHttpEndpoints();

builder.Build().Run();
