// Studio.AppHost — local dev runner for Tap Studio.
//
//   studio-api          REST + SSE backend (Tap.Studio, ASP.NET Core)
//   studio-ui           Vite dev server hosting the React UI (src/ui-studio/)
//   docs-site           Vite dev server hosting the public docs/marketing site (docs-site/)
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
// workspace.tap). Two allowlists gate which names that provider exposes: TAP_VARS_ALLOWED
// carries names whose values may be displayed; TAP_SECRETS_ALLOWED carries names whose
// values stay masked but resolve normally when referenced as `{{env:NAME}}`. The sample
// workspace pulls DEMO_* + USER + AZURE_*; only DEMO_API_URL + USER are "showable" — the
// rest are credentials. Override these on your shell to broaden the surface.
// AddTapStudio is the feature this sample exists to prove: one call runs Studio pinned to a
// workspace folder and pointed at the APIs under development. It supplies the endpoint, the
// health check, the dashboard URL + icon, Studio__Mode=aspire, Studio__WorkspaceRoot, and —
// via WithApi — the standard services__* variables that make `{{aspire:demo-api}}` resolve,
// plus the WaitFor. Everything left below is this sample's own business.
//
// Note the workspace folder here is NOT empty: it points at the committed sample-workspace,
// which exercises aspire mode against an existing workspace rather than a scaffolded one.
var studio = builder.AddTapStudio<Projects.Tap_Studio>("studio-api")
    .WithWorkspaceFolder(workspaceRoot)
    .WithApi(demoApi)
    .WithEnvironment("DEMO_API_URL", demoApi.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
    .WithEnvironment("DEMO_JWT_SECRET", jwtSecret)
    .WithEnvironment("DEMO_BEARER_TOKEN", demoBearer)
    .WithEnvironment("DEMO_BASIC_PASSWORD", demoBasicPassword)
    .WithEnvironment("DEMO_API_KEY", demoApiKey)
    .WithEnvironment("TAP_VARS_ALLOWED", "DEMO_API_URL,USER")
    .WithEnvironment("TAP_SECRETS_ALLOWED", "DEMO_BEARER_TOKEN,DEMO_BASIC_PASSWORD,DEMO_API_KEY,DEMO_JWT_SECRET,AZURE_*")
    .WithExternalHttpEndpoints();

// Demo.Api must register Studio's OAuth callback URL on its seeded client(s). The
// callback lives at <studio-base>/api/auth/callback, and the Studio base URL is
// Aspire-allocated per run — so we forward it via env var and Demo.Api reads it at
// seed time. Without this, the client's registered redirect_uri wouldn't match the
// one Studio sends on the authorize request, and OpenIddict would reject the flow.
demoApi.WithEnvironment("STUDIO_CALLBACK_URL", studio.CallbackUrl);

// Vite UI — VITE_STUDIO_API_URL is resolved at start time from the studio-api endpoint.
var uiDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "src", "ui-studio"));
var studioUi = builder.AddViteApp("studio-ui", uiDir, "dev")
    .WithYarn()
    .WithEnvironment("VITE_STUDIO_API_URL", studio.Endpoint)
    .WaitFor(builder.CreateResourceBuilder(studio.Resource))
    .WithExternalHttpEndpoints();

// docs-site — the public docs/marketing site. It is a purely static Vite app: no
// backend, no reference to studio-api, nothing to wait for. It rides along here so
// `aspire run` gives it an allocated port, a dashboard link, and hot reload in the
// same loop as everything else.
var docsDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "docs-site"));
builder.AddViteApp("docs-site", docsDir, "dev")
    .WithYarn()
    .WithExternalHttpEndpoints();

// Optionally launch the Tauri desktop shell as a native window over the running
// studio-ui, so `aspire run` can bring up the whole desktop dev loop. Off by
// default — enable with RunDesktop=true (env var, user-secrets, or appsettings):
//   RunDesktop=true aspire run
// The shell reads STUDIO_DESKTOP_URL, skips its bundled sidecar, and points the
// webview straight at studio-ui — so you get Vite hot reload and the same
// Aspire-managed studio-api/demo-api the browser dev loop uses.
if (bool.TryParse(builder.Configuration["RunDesktop"], out var runDesktop) && runDesktop)
{
    var desktopDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "src", "desktop"));
    builder.AddExecutable("studio-desktop", "yarn", desktopDir, "dev")
        .WithEnvironment("STUDIO_DESKTOP_URL", studioUi.GetEndpoint("http"))
        // DCP spawns executables with a sanitized environment; forward the real
        // HOME so corepack (~/.cache), cargo (~/.cargo) and rustup (~/.rustup)
        // resolve for `tauri dev`. (src/desktop also uses the node-modules linker
        // so Yarn doesn't depend on a HOME-derived global PnP cache.)
        .WithEnvironment(ctx =>
        {
            if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
                ctx.EnvironmentVariables["HOME"] = home;
        })
        .WaitFor(studioUi);
}

builder.Build().Run();
