import React from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";

const version = import.meta.env.VITE_TAP_VERSION ?? "v0.1.0";
const repoUrl = import.meta.env.VITE_TAP_REPO_URL ?? "https://github.com/philbir/tap";

type Product = "inspector" | "studio";

type Feature = {
  title: string;
  text: string;
  glyph: string;
};

type DocSection = {
  id: string;
  product: Product;
  eyebrow: string;
  title: string;
  body: string;
};

const principles: Feature[] = [
  {
    title: "Local-first, no account",
    text: "Everything runs on your machine. No cloud workspace, no sync service, no telemetry. Quick tunnels are free; stable hostnames just need a domain you already own.",
    glyph: "L",
  },
  {
    title: "Plain text, in your repo",
    text: "Studio's workspace is Markdown with YAML frontmatter — requests, collections, auth profiles, and environments all review as ordinary git diffs.",
    glyph: "T",
  },
  {
    title: "Secrets stay out of files",
    text: "The Inspector never persists credentials. Studio resolves secret references at execute time and keeps OAuth tokens in your OS state folder, never in the workspace.",
    glyph: "S",
  },
  {
    title: "Explicit boundaries",
    text: "Tailscale routes stay tailnet-only by default, public exposure is opt-in, and proxy auth (header, CIDR, country, OIDC) gates traffic before it reaches your service.",
    glyph: "B",
  },
];

const inspectorFeatures: Feature[] = [
  {
    title: "Tunnel without ceremony",
    text: "Use free TryCloudflare URLs, dashboard connector tokens, API-managed Cloudflare DNS, or Tailscale Serve/Funnel when your tailnet is the right boundary.",
    glyph: "T",
  },
  {
    title: "Built for real dev loops",
    text: "Mobile hooks, webhook deliveries, auth redirects, partner callbacks, and temporary demos all become one inspectable tunnel.",
    glyph: "U",
  },
  {
    title: "Run as CLI or Aspire",
    text: "Use `tap run` for one-off local tunnels, or model inspectors and tunnels directly in your .NET Aspire AppHost.",
    glyph: "C",
  },
  {
    title: "Inspect every hop",
    text: "Tap captures method, host, path, headers, status, timing, request bodies, response bodies, and image payload previews before forwarding to your upstream.",
    glyph: "I",
  },
  {
    title: "Live streaming protocols",
    text: "WebSockets and Server-Sent Events stream through the same proxy and the inspector UI renders a live, direction-tagged frame/event timeline in dedicated WS and SSE tabs — watch messages append while the connection is open.",
    glyph: "L",
  },
  {
    title: "Aspire-native wiring",
    text: "Attach an inspector or public tunnel to an Aspire resource with extension methods, while allocated ports and hostnames are resolved at startup.",
    glyph: "A",
  },
  {
    title: "Built for local trust",
    text: "Keep the UI local, default Tailscale to tailnet-only Serve, put auth on public proxy traffic, and combine header, CIDR, country, and OIDC checks.",
    glyph: "S",
  },
];

const studioFeatures: Feature[] = [
  {
    title: "Full request composition",
    text: "Method, URL, query params, headers, and bodies as None / Form / Multipart / Raw / Binary / GraphQL — with JSON and XML formatting, multi-file uploads, and a GraphQL editor backed by the live schema.",
    glyph: "R",
  },
  {
    title: "Many authentication flows",
    text: "OAuth 2.0 / OIDC with PKCE, client credentials, ROPC and device code; Microsoft Entra; Azure CLI direct and on-behalf-of; GitHub PAT, gh CLI, App and OAuth App; AWS SigV4; signed JWT; bearer, basic, API key, custom headers.",
    glyph: "A",
  },
  {
    title: "AI assistance",
    text: "Hand the request to GitHub Copilot CLI or Claude Code — running locally under your existing CLI login — and get a proposed edit, with documentation, that you review before saving.",
    glyph: "AI",
  },
  {
    title: "Responses you can read",
    text: "Status, duration, size, a syntax-highlighted body with image and binary previews, the exact request that went on the wire, the auth and variable flow behind it, and which secrets were resolved.",
    glyph: "V",
  },
  {
    title: "Streaming built in",
    text: "Server-Sent Events stream into a live timeline, and requests marked protocol: websocket open a real socket that appends frames as they arrive.",
    glyph: "S",
  },
  {
    title: "Git-native workspace",
    text: "Every request, collection, auth profile, and environment is a Markdown file in your repo. Branch, diff, stage, and commit without leaving the app.",
    glyph: "G",
  },
  {
    title: "Variables and secrets",
    text: "A six-level cascade over pluggable providers: allow-listed host environment, an encrypted file in the repo, Azure Key Vault, 1Password, and machine-local system variables. Files hold references; values arrive at execute time.",
    glyph: "X",
  },
  {
    title: "Desktop app",
    text: "A native shell for macOS, Windows, and Linux that self-updates and registers a stable tap-studio:// redirect URI for OAuth sign-in.",
    glyph: "D",
  },
];

const docs: DocSection[] = [
  {
    id: "use-cases",
    product: "inspector",
    eyebrow: "Why",
    title: "Use cases",
    body: "For the moments when localhost needs to behave like a real internet endpoint, but you still want full visibility into every request.",
  },
  {
    id: "quickstart",
    product: "inspector",
    eyebrow: "Start",
    title: "Quick start",
    body: "Run Tap from the CLI for ad hoc debugging, or add it to Aspire when tunnel wiring should live beside your app resources.",
  },
  {
    id: "entry-points",
    product: "inspector",
    eyebrow: "Choose",
    title: "CLI or Aspire",
    body: "Both entry points use the same Tap.Server runtime. The difference is where configuration and lifecycle orchestration live.",
  },
  {
    id: "cli",
    product: "inspector",
    eyebrow: "Terminal",
    title: "CLI reference",
    body: "The CLI is for one upstream URL at a time. Start local-only, add Cloudflare, or route through Tailscale Serve/Funnel.",
  },
  {
    id: "cloudflare",
    product: "inspector",
    eyebrow: "Cloudflare",
    title: "Tunnel setup",
    body: "Existing-tunnel mode uses a Cloudflare Tunnel you have already created. API-managed mode needs a Cloudflare API token that can edit tunnels and DNS.",
  },
  {
    id: "tailscale",
    product: "inspector",
    eyebrow: "Tailscale",
    title: "Serve and Funnel",
    body: "Tailscale support routes Tap through your tailnet. Serve is private by default; Funnel is public and should be paired with auth.",
  },
  {
    id: "auth",
    product: "inspector",
    eyebrow: "Security",
    title: "Proxy authentication",
    body: "Authentication gates the public proxy path before traffic reaches the upstream. The local inspector UI remains the control plane.",
  },
  {
    id: "modes",
    product: "inspector",
    eyebrow: "Routing",
    title: "Tunnel modes",
    body: "Tap scales from a local capture proxy to Cloudflare-managed tunnels, Tailscale private Serve, and public Funnel.",
  },
  {
    id: "architecture",
    product: "inspector",
    eyebrow: "Internals",
    title: "Architecture",
    body: "Tap has two entry points, a shared inspector host, and optional Cloudflare or Tailscale tunnel providers in front of the proxy port.",
  },
  {
    id: "config",
    product: "inspector",
    eyebrow: "Operations",
    title: "Configuration",
    body: "Most configuration is written by the AppHost, with provider credentials, profile fields, and optional auth coming from normal .NET configuration.",
  },
  {
    id: "studio-compose",
    product: "studio",
    eyebrow: "Compose",
    title: "The request composer",
    body: "One URL bar and eight tabs: params, headers, body, auth, variables, meta, docs, and the generated Markdown source.",
  },
  {
    id: "studio-auth",
    product: "studio",
    eyebrow: "Identity",
    title: "Authentication flows",
    body: "Auth profiles are reusable files. Start from a template, fill only the fields that flow needs, and prove it works before wiring it to a request.",
  },
  {
    id: "studio-ai",
    product: "studio",
    eyebrow: "Assist",
    title: "AI assistance",
    body: "Studio spawns an AI coding CLI you already have installed, hands it the real shape of your workspace, and applies what it proposes as an unsaved edit.",
  },
  {
    id: "studio-workspace",
    product: "studio",
    eyebrow: "Format",
    title: "The workspace is your repo",
    body: "Five file kinds, all Markdown with YAML frontmatter. A runnable request is the composition of workspace, collection, stage, auth, environment, and request.",
  },
  {
    id: "studio-variables",
    product: "studio",
    eyebrow: "Values",
    title: "Variables and secrets",
    body: "A cascade of scopes over pluggable providers, so a workspace file only ever contains a reference — never a secret value.",
  },
  {
    id: "studio-providers",
    product: "studio",
    eyebrow: "Providers",
    title: "Variable providers",
    body: "Five backends behind the same {{token}} syntax: the allow-listed host environment, an encrypted file in the repo, Azure Key Vault, 1Password, and Studio's machine-local store.",
  },
  {
    id: "studio-install",
    product: "studio",
    eyebrow: "Install",
    title: "Get Tap Studio",
    body: "Studio ships as a native desktop app wrapping the self-contained backend, and runs from source through the Aspire dev loop.",
  },
];

const commands = {
  install: `dotnet tool install -g Tap`,
  installCurl: `curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh`,
  cli: `tap run http://localhost:3000`,
  cliQuick: `tap run http://localhost:3000 --quick`,
  cliToken: `tap run http://localhost:3000 \\
  --token "$CLOUDFLARE_TUNNEL_TOKEN" \\
  --hostname api-local.example.com`,
  cliManaged: `tap run http://localhost:3000 \\
  --api-token "$CLOUDFLARE_API_TOKEN" \\
  --account "$CLOUDFLARE_ACCOUNT_ID" \\
  --api-managed tap-cli \\
  --dynamic example.com`,
  cliTailscaleServe: `tap run http://localhost:3000 --tailscale`,
  cliTailscalePublic: `tap run http://localhost:3000 --tailscale --tailscale-public \\
  --auth-header "X-Tap-Key=$TAP_KEY"`,
  cliTailscaleEphemeral: `export TAILSCALE_AUTHKEY=tskey-...
tap run http://localhost:3000 --tailscale --tailscale-port 8443`,
  cliTailscaleDocker: `export TAILSCALE_AUTHKEY=tskey-...
tap run http://localhost:3000 --tailscale --docker`,
  cliConfig: `{
  "upstream": "http://localhost:3000"
}`,
  cliAuth: `tap run http://localhost:3000 --quick \\
  --auth-header "X-Tap-Key=$TAP_KEY" \\
  --auth-cidr "203.0.113.0/24" \\
  --auth-country "CH"`,
  cliOidc: `tap run http://localhost:3000 --quick \\
  --auth-oidc-authority "https://issuer.example.com" \\
  --auth-oidc-client-id "$OIDC_CLIENT_ID" \\
  --auth-oidc-client-secret "$OIDC_CLIENT_SECRET"`,
  standalone: `using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>();
api.WithTap(tap);

builder.Build().Run();`,
  quick: `var tap = builder.AddTap<Projects.Tap_Server>(
        name: "tap-quick",
        proxyPort: 5307,
        uiPort: 5306)
    .WithQuickTunnel();

api.WithTap(tap);`,
  token: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t =>
        t.WithExistingTunnel(builder.Configuration["Cloudflare:TunnelToken"]));

api.WithTap(tap, "api-local.example.com");`,
  managed: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t => t
        .WithApiManagedTunnel(
            builder.Configuration["Cloudflare:ApiToken"]!,
            builder.Configuration["Cloudflare:AccountId"]!,
            tunnelName: "tap-dev")
        .WithDynamicHostname("example.com", prefix: "api-", suffix: "-tap"));

api.WithTap(tap);`,
  tailscaleServe: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-serve", t => t.WithSystemDaemon());

api.WithTap(tap);`,
  tailscalePublic: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t.WithSystemDaemon())
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!);

api.WithTap(tap);`,
  tailscaleEphemeral: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-ts-ephemeral", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(8443));

api.WithTap(tap);`,
  tailscaleDocker: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-ts-docker", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(10000),
        hostMode: TailscaleHostMode.Docker);

api.WithTap(tap);`,
  aspireAuth: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!)
    .WithIpAllowList("203.0.113.0/24")
    .WithCountryAllowList("CH")
    .WithOidcAuth(
        authority: builder.Configuration["Auth:Authority"]!,
        clientId: builder.Configuration["Auth:ClientId"]!,
        clientSecret: builder.Configuration["Auth:ClientSecret"]);

api.WithTap(tap);`,
  secrets: `dotnet user-secrets set Cloudflare:TunnelToken "<connector-token>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Cloudflare:ApiToken "<api-token>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Cloudflare:AccountId "<account-id>" \\
  --project samples/Sample.AppHost`,
  tailscaleSecrets: `dotnet user-secrets set Tailscale:UseSystem "true" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Tailscale:AuthKey "<tskey-...>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Tailscale:UseDocker "true" \\
  --project samples/Sample.AppHost`,
  sampleScenarios: `dotnet run --project samples/Sample.AppHost -- --scenarios tailscale
dotnet run --project samples/Sample.AppHost -- --scenarios cloudflare
dotnet run --project samples/Sample.AppHost -- --scenarios all`,
  studioRequest: `---
kind: request
name: Create customer
auth: ../../auth/stripe-bearer.auth.md
tags: [customer, write]
---

# Create customer

Called during signup. Idempotent on \`email\`.

\`\`\`http
POST /v1/customers
Content-Type: application/json

{ "email": "{{customer.email}}", "name": "{{customer.name}}" }
\`\`\``,
  studioCollection: `---
kind: collection
name: Stripe
baseUrl: https://api.stripe.com
defaultAuth: ../../auth/stripe-bearer.auth.md
defaultHeaders:
  Accept: application/json
stages:
- name: live
- name: test
  defaultAuth: ../../auth/stripe-test.auth.md
---`,
  studioAuth: `---
kind: auth
name: Corp Entra
type: oauth2
flow: authorization_code
authority: https://login.microsoftonline.com/{{tenant}}/v2.0
clientId: '{{env:ENTRA_CLIENT_ID}}'
scopes:
  - https://graph.microsoft.com/.default
---`,
  studioWorkspace: `---
kind: workspace
name: acme-billing
defaultEnv: environments/local.env.md
variableProviders:
- name: env
  type: env
- name: vault
  type: azkv
  settings:
    vaultUrl: https://acme-prod.vault.azure.net
---`,
  studioEnv: `---
kind: env
name: Production
vars:
  api.baseUrl: https://api.stripe.com
  STRIPE_KEY: '{{vault:stripe-live-key}}'
---`,
  studioAllowlist: `# Names whose values the UI may show in clear text
export TAP_VARS_ALLOWED="DEMO_*,ASPNETCORE_ENVIRONMENT"

# Names that resolve at execute time but stay masked everywhere
export TAP_SECRETS_ALLOWED="ACME_*_TOKEN,AZURE_*"

# Neither set? The env provider exposes nothing at all.`,
  studioProviders: `---
kind: workspace
name: acme-billing
defaultEnv: environments/local.env.md
defaultVariableProvider: kv-dev
variableProviders:
- name: env
  type: env
- name: local
  type: file
- name: kv-dev
  type: azkv
  settings:
    vaultName: acme-dev
- name: kv-prod
  type: azkv
  settings:
    vaultName: acme-prod
- name: 1p
  type: 1password
  settings:
    mode: vault
    vault: Acme Dev
---`,
  studioFileStore: `# .vars/local.yml — written by Studio; commit it
variables:
  api.baseUrl: https://localhost:5001
  STRIPE_KEY:
    value: 'enc:v1:<iv>:<ciphertext>:<tag>'
    secret: true`,
  studioAzkv: `- name: kv-prod
  type: azkv
  settings:
    # -> https://acme-prod.vault.azure.net/
    vaultName: acme-prod
    # optional: pin one tenant
    tenantId: <tenant-guid>
    # optional: tokens keep the unprefixed name
    prefix: billing-`,
  studioAzkvLogin: `az login
az account set --subscription "<subscription>"

# DefaultAzureCredential also accepts environment
# credentials, workload and managed identity, and
# your IDE's Azure sign-in — nothing about the
# credential lives in the workspace file.`,
  studio1pEnvironment: `- name: 1p
  type: 1password
  settings:
    mode: environment
    # 1Password app: Developer
    #   -> View Environments
    #   -> Manage environment
    #   -> Copy environment ID
    environment: <environment-id>`,
  studio1pVault: `- name: 1p
  type: 1password
  settings:
    mode: vault
    # every item title becomes a variable name
    vault: Acme Dev
    # default: password, then credential, then
    # the first populated concealed field
    field: credential`,
  studio1pItem: `- name: acme-api
  type: 1password
  settings:
    mode: item
    vault: Acme Dev
    # this item's fields become the variables:
    # {{acme-api:username}}, {{acme-api:api-key}}
    item: Acme API`,
  studio1pCli: `# Nothing to configure on a normal desktop: op
# reuses the 1Password app integration or your
# existing "op signin" session.

# Environments need op 2.38.2-beta.01 or later:
op --version

# Only when op is not on PATH:
export TAP_OP_CLI=/opt/homebrew/bin/op`,
  studioEnvBinding: `---
kind: env
name: Production
defaultVariableProvider: kv-prod
strictVariables: true
providerAliases:
  kv: kv-prod
vars:
  api.baseUrl: https://api.acme.com
---`,
  studioRun: `cd samples
aspire run

# your own repo instead of the sample workspace
STUDIO_WORKSPACE=/path/to/your/repo aspire run

# add the native desktop window
RunDesktop=true aspire run`,
  studioBuild: `scripts/build-desktop.sh          # publish sidecar + tauri build
scripts/build-desktop.sh --dev    # publish sidecar + tauri dev`,
};

const App = () => (
  <>
    <SiteNav />
    <main>
      <Hero />
      <ProductSplit />
      <Principles />
      <InspectorProduct />
      <StudioProduct />
      <FinalCta />
    </main>
  </>
);

const SiteNav = () => (
  <header className="site-nav">
    <a className="brand" href="#top" aria-label="Tap home">
      <img src="./tap-mark.svg" alt="" />
      <span>Tap</span>
    </a>
    <nav aria-label="Main">
      <a href="#products">Products</a>
      <a href="#inspector">Tunnel + Inspector</a>
      <a href="#cli">CLI</a>
      <a href="#cloudflare">Cloudflare</a>
      <a href="#tailscale">Tailscale</a>
      <a href="#studio">Studio</a>
      <a href="#studio-auth">Auth flows</a>
      <a href="#studio-providers">Variables</a>
      <a href="#studio-ai">AI</a>
      <a className="nav-cta" href={repoUrl}>
        GitHub
      </a>
    </nav>
  </header>
);

const Hero = () => (
  <section className="hero" id="top">
    <div className="hero-copy">
      <div className="eyebrow">
        <span className="spark" />
        Two local-first tools for HTTP
      </div>
      <h1>See what arrives. Shape what you send.</h1>
      <p className="lead">
        Tap gives local services a public URL and captures everything that hits it — and Tap
        Studio is the workbench where you compose those requests, run real authentication
        flows, and keep the whole workspace in your repo as plain Markdown.
      </p>
      <div className="hero-actions">
        <a className="button primary" href="#products">
          See both products
        </a>
        <a className="button ghost" href="#quickstart">
          Start tunneling
        </a>
        <a className="button ghost" href={repoUrl}>
          View code
        </a>
      </div>
      <div className="proof-row">
        <span>{version}</span>
        <span>CLI</span>
        <span>Aspire</span>
        <span>Desktop app</span>
        <span>TryCloudflare</span>
        <span>Tailscale Serve</span>
        <span>OAuth 2.0 / OIDC</span>
        <span>AI assistant</span>
        <span>Git-native</span>
        <span>Free as tap water</span>
      </div>
    </div>
    <picture className="hero-visual" aria-label="Tap tunnel illustration">
      <source srcSet="./tap-hero-dark.png" media="(prefers-color-scheme: dark)" />
      <img src="./tap-hero.png" alt="A tap extracting traffic from a tunnel into an inspector panel" />
    </picture>
  </section>
);

const ProductSplit = () => (
  <section className="section" id="products">
    <div className="section-heading">
      <span className="kicker">The big picture</span>
      <h2>Local HTTP development has two halves.</h2>
      <p>
        Sometimes the internet needs to reach your laptop and you need to see exactly what
        arrived. Other times you are the client, and you need to compose a request,
        authenticate properly, send it, and keep it somewhere your team can find next month.
        Tap is one product for each half — use either alone, or both together.
      </p>
    </div>
    <div className="product-split">
      <article className="product-card">
        <span className="product-index">01</span>
        <h3>Tap Tunnel + Inspector</h3>
        <p>
          A public URL for localhost through Cloudflare Tunnel or Tailscale, with every request,
          response, SSE event, and WebSocket frame captured on the way through. Runs as the{" "}
          <code>tap</code> CLI or as .NET Aspire resources.
        </p>
        <div className="product-tags">
          <span>CLI</span>
          <span>Aspire</span>
          <span>Cloudflare</span>
          <span>Tailscale</span>
          <span>Replay</span>
          <span>QR</span>
        </div>
        <a className="button primary" href="#inspector">
          Explore the Inspector
        </a>
      </article>
      <article className="product-card">
        <span className="product-index">02</span>
        <h3>Tap Studio</h3>
        <p>
          An HTTP workbench: full request composition, a catalogue of real authentication flows,
          an AI assistant running on your own machine, and a workspace that lives in your git
          repo as Markdown. Ships as a desktop app.
        </p>
        <div className="product-tags">
          <span>Desktop</span>
          <span>OAuth 2.0</span>
          <span>Entra</span>
          <span>AWS SigV4</span>
          <span>GraphQL</span>
          <span>Key Vault</span>
          <span>1Password</span>
          <span>AI</span>
        </div>
        <a className="button primary" href="#studio">
          Explore Studio
        </a>
      </article>
    </div>
  </section>
);

const Principles = () => (
  <section className="flow-band" id="principles">
    <div className="section-heading">
      <span className="kicker">Shared foundations</span>
      <h2>Different jobs, same rules.</h2>
      <p>
        The two products share a philosophy more than they share code. Everything is local,
        everything is inspectable, and nothing quietly leaves your machine.
      </p>
    </div>
    <div className="feature-grid">
      {principles.map((item) => (
        <article className="feature-card" key={item.title}>
          <span className="glyph">{item.glyph}</span>
          <h3>{item.title}</h3>
          <p>{item.text}</p>
        </article>
      ))}
    </div>
  </section>
);

const InspectorProduct = () => (
  <>
    <ProductBand
      id="inspector"
      index="01"
      title="Tap Tunnel + Inspector"
      body="Give a local service a real public URL for mobile hooks, webhooks, auth redirects, partner integrations, and temporary demos — then read back exactly what happened."
    />
    <PublicTunnelWarning />
    <section className="section" id="inspector-features">
      <div className="section-heading">
        <span className="kicker">What it does</span>
        <h2>Public callbacks and local debugging in one workflow.</h2>
        <p>
          Start with free TryCloudflare URLs. Add a free Cloudflare account and your own domain
          when you want stable hostnames. Tap itself is free like tap water: drink as much as
          you like.
        </p>
      </div>
      <div className="feature-grid">
        {inspectorFeatures.map((feature) => (
          <article className="feature-card" key={feature.title}>
            <span className="glyph">{feature.glyph}</span>
            <h3>{feature.title}</h3>
            <p>{feature.text}</p>
          </article>
        ))}
      </div>
    </section>
    <FlowSection />
    <Docs product="inspector" label="Inspector docs" />
  </>
);

const StudioProduct = () => (
  <>
    <ProductBand
      id="studio"
      index="02"
      title="Tap Studio"
      body="The HTTP workbench: compose requests, authenticate against real identity providers, execute, document — and keep the whole thing in your repository as reviewable Markdown."
    />
    <section className="section" id="studio-features">
      <Screenshot
        src="./screenshots/studio-request.png"
        alt="Tap Studio composing a POST request and showing the response"
        caption="One URL bar, eight tabs, and a response panel that shows the status, timing, body, the exact request that went on the wire, and which secrets were resolved."
      />
      <div className="section-heading section-gap">
        <span className="kicker">What it does</span>
        <h2>A request client that behaves like part of your codebase.</h2>
        <p>
          Studio is the other direction of travel: you are the client. It composes the call,
          runs the authentication flow behind it, executes it, and stores the result as text
          your team can review.
        </p>
      </div>
      <div className="feature-grid">
        {studioFeatures.map((feature) => (
          <article className="feature-card" key={feature.title}>
            <span className="glyph">{feature.glyph}</span>
            <h3>{feature.title}</h3>
            <p>{feature.text}</p>
          </article>
        ))}
      </div>
    </section>
    <Docs product="studio" label="Studio docs" />
  </>
);

const ProductBand = ({
  id,
  index,
  title,
  body,
}: {
  id: string;
  index: string;
  title: string;
  body: string;
}) => (
  <section className="product-band" id={id}>
    <span className="product-index large">{index}</span>
    <div>
      <span className="kicker">Product {index}</span>
      <h2>{title}</h2>
      <p>{body}</p>
    </div>
  </section>
);

const PublicTunnelWarning = () => (
  <section className="public-warning" aria-labelledby="public-warning-title">
    <div className="public-warning-copy">
      <div className="public-warning-icon" aria-hidden="true">
        !
      </div>
      <div>
        <span className="kicker">Public tunnel warning</span>
        <h2 id="public-warning-title">Avoid public tunnels when they are not necessary.</h2>
        <p>
          Tap keeps Tailscale routes tailnet-only by default. When you opt into public Cloudflare tunnels
          or Tailscale Funnel URLs, they can be probed within seconds of coming online. Put Tap auth in
          front of the proxy, or use Cloudflare Access and WAF rules before traffic reaches your upstream.
          Those unwanted requests are not abstract: you can watch scanner and attack attempts land directly
          in the Inspector as soon as the tunnel is reachable.
        </p>
      </div>
    </div>
    <img
      className="public-warning-art"
      src="./public-tunnel-warning.png"
      alt="A suspicious actor sending malicious traffic through a public tunnel into a computer"
    />
  </section>
);

const FlowSection = () => (
  <section className="flow-band">
    <div className="section-heading">
      <span className="kicker">Traffic flow</span>
      <h2>A tunnel path you can reason about.</h2>
      <p>
        CLI and Aspire both converge on the same inspector host. If a tunnel is enabled, Cloudflare
        reaches cloudflared or Tailscale reaches tailscaled; the connector targets Tap's proxy port,
        and Tap forwards to the local upstream.
      </p>
    </div>
    <div className="entry-flow" aria-label="Tap entry point flow">
      <div className="entry-node">tap CLI</div>
      <div className="entry-node">Aspire AppHost</div>
      <div className="flow-link vertical" />
      <div className="entry-node wide">Tap.Server shared inspector</div>
    </div>
    <div className="flow" aria-label="Tap request flow">
      {["Client", "Cloudflare/Tailscale", "connector/daemon", "Tap proxy", "Upstream"].map((step, index) => (
        <React.Fragment key={step}>
          <div className="flow-node">{step}</div>
          {index < 4 ? <div className="flow-link" /> : null}
        </React.Fragment>
      ))}
    </div>
  </section>
);

const Docs = ({ product, label }: { product: Product; label: string }) => {
  const sections = docs.filter((doc) => doc.product === product);
  return (
    <section className="docs-wrap" id={`${product}-docs`}>
      <aside className="docs-sidebar" aria-label={label}>
        <span className="kicker">{label}</span>
        {sections.map((doc) => (
          <a href={`#${doc.id}`} key={doc.id}>
            {doc.title}
          </a>
        ))}
      </aside>
      <div className="docs-content">
        {sections.map((doc) => (
          <DocBlock doc={doc} key={doc.id} />
        ))}
      </div>
    </section>
  );
};

const DocBlock = ({ doc }: { doc: DocSection }) => {
  if (doc.id === "use-cases") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="mode-grid">
          {[
            ["Mobile app hooks", "Point native or emulator builds at a public HTTPS URL while the service still runs on localhost."],
            ["Webhook development", "Capture provider deliveries, inspect headers and bodies, and replay requests while iterating."],
            ["Auth callbacks", "Test OAuth/OIDC redirect URIs against a real hostname without deploying a staging app."],
            ["Temporary sharing", "Send someone a URL to work running on your machine, then close the tunnel when the moment is over."],
            ["Team demos", "Put Tap in Aspire so the whole app graph has repeatable tunnel and inspector wiring."],
            ["Cheap stable hostnames", "TryCloudflare is free. For stable URLs, bring a domain you own; .dev domains are a tidy fit for developer projects."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="callout">
          <strong>Free as tap water</strong>
          <p>
            Quick tunnels cost nothing and need no account. Stable hostnames use Cloudflare plus a
            domain you control. Tap stays small, local, and reusable, so you can open another tunnel
            whenever the next callback needs a place to land.
          </p>
        </div>
      </section>
    );
  }

  if (doc.id === "quickstart") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="code-grid">
          <CodeBlock title="Install (.NET global tool)" code={commands.install} />
          <CodeBlock title="Install (Linux/macOS, self-contained)" code={commands.installCurl} />
          <CodeBlock title="CLI: local inspector" code={commands.cli} />
          <CodeBlock title="CLI: quick public tunnel" code={commands.cliQuick} />
          <CodeBlock title="CLI: existing token tunnel" code={commands.cliToken} />
          <CodeBlock title="CLI: API-managed dynamic hostname" code={commands.cliManaged} />
          <CodeBlock title="CLI: Tailscale Serve" code={commands.cliTailscaleServe} />
          <CodeBlock title="CLI: Tailscale ephemeral" code={commands.cliTailscaleEphemeral} />
          <CodeBlock title="Aspire: standalone inspector" code={commands.standalone} />
          <CodeBlock title="Aspire: quick public tunnel" code={commands.quick} />
          <CodeBlock title="Aspire: Tailscale Serve" code={commands.tailscaleServe} />
        </div>
      </section>
    );
  }

  if (doc.id === "entry-points") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/tap-aspire.png"
          alt="Aspire dashboard showing Tap inspector and cloudflared resources"
          caption="In Aspire, Tap resources show up beside your app, with public URLs and Inspector UI links on each tunnel mode."
        />
        <div className="entry-grid">
          <MiniPanel title="CLI">
            Use `tap run` when you have one upstream URL and want a tunnel or inspector immediately.
            The CLI reads flags, environment variables, and optional `tap.config`, then starts
            cloudflared or Tailscale, then starts Tap.Server for the current terminal session.
          </MiniPanel>
          <MiniPanel title="Aspire">
            Use Tap.Hosting when inspectors and tunnels are part of the app model. Aspire resolves
            resource endpoints, shows URLs in the dashboard, and runs provider lifecycle provisioning
            before cloudflared or the Tailscale bootstrapper starts.
          </MiniPanel>
        </div>
        <div className="code-grid">
          <CodeBlock title="CLI: dynamic hostname" code={commands.cliManaged} />
          <CodeBlock title="Aspire: dynamic hostname" code={commands.managed} />
        </div>
      </section>
    );
  }

  if (doc.id === "cli") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/tap-cli.png"
          alt="Tap CLI running a quick TryCloudflare tunnel"
          caption="The CLI prints the public URL, upstream, inspector UI, proxy port, and a live tail of recent requests."
        />
        <div className="mode-grid">
          {[
            ["Local inspector", "No Cloudflare. Proxy on port 4444, UI on port 4445."],
            ["Quick tunnel", "Creates a temporary trycloudflare.com URL. No account needed."],
            ["Token tunnel", "Runs an existing dashboard-managed Cloudflare Tunnel connector token."],
            ["API-managed", "Tap creates or reuses a named tunnel through the Cloudflare API."],
            ["Dynamic hostname", "Tap mints a fresh hostname under your zone and creates DNS."],
            ["Tailscale Serve", "Private tailnet-only HTTPS via tailscale serve. This is the default for --tailscale."],
            ["Tailscale Funnel", "Public internet URL via tailscale funnel. Pair it with auth."],
            ["Tailscale ephemeral", "Spawn a userspace tailscaled per run from --tailscale-authkey or TAILSCALE_AUTHKEY."],
            ["Docker connector", "Use --docker to run cloudflare/cloudflared or tailscale/tailscale, depending on the active provider."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="config-table" role="table" aria-label="CLI options">
          {[
            ["<upstream>", "Target URL to inspect, for example http://localhost:3000."],
            ["--proxy-port", "Captured traffic port. Default: 4444."],
            ["--ui-port", "Inspector UI/API port. Default: 4445."],
            ["--quick", "Start a TryCloudflare quick tunnel."],
            ["--token", "Connector token for an existing Cloudflare Tunnel."],
            ["--hostname", "Public hostname for token or API-managed mode."],
            ["--api-token", "Cloudflare API token for managed tunnel/DNS operations."],
            ["--account", "Cloudflare account id."],
            ["--api-managed", "Named tunnel to create or reuse."],
            ["--dynamic", "Zone where Tap should mint a fresh hostname."],
            ["--docker", "Run the active provider in Docker: cloudflare/cloudflared or tailscale/tailscale."],
            ["--auto-install", "Install cloudflared if missing."],
            ["--tailscale", "Route through Tailscale. Default exposure is tailnet-only Serve."],
            ["--tailscale-public", "Switch from private Serve to public Funnel. Pair with auth flags."],
            ["--tailscale-port", "Tailscale HTTPS port. Allowed: 443, 8443, 10000."],
            ["--tailscale-authkey", "Auth key for ephemeral userspace tailscaled. Env: TAILSCALE_AUTHKEY."],
            ["--tailscale-system", "Force system mode even when an auth key exists in env or profile."],
            ["--tailscale-login-server", "Override the coordination server for Headscale or other control planes."],
            ["--auth-header / --auth-cidr / --auth-country", "Static checks on the public proxy path."],
            ["--auth-oidc-*", "Require browser sign-in through an OpenID Connect issuer."],
            ["--config", "Load defaults from a JSON tap.config file."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="code-grid">
          <CodeBlock title="tap.config" code={commands.cliConfig} />
          <CodeBlock title="Install cloudflared" code="tap install-cloudflared" />
          <CodeBlock title="Tailscale public Funnel" code={commands.cliTailscalePublic} />
          <CodeBlock title="Tailscale Docker mode" code={commands.cliTailscaleDocker} />
        </div>
      </section>
    );
  }

  if (doc.id === "cloudflare") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <PublicTunnelInlineWarning provider="Cloudflare" />
        <Screenshot
          src="./screenshots/tab-tunnel-dialog.png"
          alt="Tap tunnel dialog showing Cloudflare edge, cloudflared, inspector, and upstream"
          caption="The tunnel dialog makes the Cloudflare path explicit, from public hostname through cloudflared to the inspector proxy and local upstream."
        />
        <div className="callout">
          <strong>Existing-tunnel mode uses a dashboard tunnel</strong>
          <p>
            `--token` and `WithExistingTunnel(...)` do not create a Cloudflare Tunnel. Create the
            tunnel in the Cloudflare dashboard first, copy its connector token, and pass that token
            to Tap.
          </p>
        </div>
        <div className="step-list">
          <MiniPanel title="Create a dashboard tunnel">
            In Cloudflare Zero Trust, go to Networks, then Tunnels, create a cloudflared tunnel,
            and copy the connector command. The `eyJ...` value after `--token` is the connector token.
          </MiniPanel>
          <MiniPanel title="Use existing-tunnel mode">
            Pass the connector token to `tap run --token` or `WithExistingTunnel(...)`, then attach
            the hostname you already routed to that tunnel.
          </MiniPanel>
          <MiniPanel title="Create an API token">
            For API-managed mode, create a Cloudflare API token with tunnel edit permission on the
            account and DNS edit permission on the zone Tap will manage.
          </MiniPanel>
          <MiniPanel title="Use API-managed mode">
            Pass the API token and account id to Tap. Tap can create or reuse the named tunnel,
            write local credentials, mint hostnames, and ensure CNAME records.
          </MiniPanel>
        </div>
        <div className="code-grid">
          <CodeBlock title="CLI: token mode" code={commands.cliToken} />
          <CodeBlock title="CLI: API-managed mode" code={commands.cliManaged} />
          <CodeBlock title="Aspire user-secrets" code={commands.secrets} />
          <CodeBlock title="Aspire: existing token tunnel" code={commands.token} />
        </div>
        <p className="doc-note">
          Cloudflare docs: <a href="https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/">tunnel tokens</a>
          {" "}and <a href="https://developers.cloudflare.com/fundamentals/api/reference/permissions/">API token permissions</a>.
        </p>
      </section>
    );
  }

  if (doc.id === "tailscale") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <PublicTunnelInlineWarning provider="Tailscale Funnel" />
        <div className="callout warning-callout">
          <strong>Default to private</strong>
          <p>
            `tap run --tailscale` and `WithTailscaleServe(...)` use `tailscale serve`, which is
            reachable only from devices in your tailnet. Switch to `tailscale funnel` only when you
            need an internet-facing URL, and pair public Funnel with Tap auth.
          </p>
        </div>
        <div className="mode-grid">
          {[
            ["What Tailscale adds", "Tailscale gives each machine a stable HTTPS name like https://machine.tailnet.ts.net. Tap points that HTTPS endpoint at the inspector proxy, then forwards captured traffic to your upstream."],
            ["Serve", "Private HTTPS inside your tailnet. This is the safe default for CLI and Aspire."],
            ["Funnel", "Public HTTPS on the internet through your tailnet node. New public hostnames are scanned quickly, so protect them with header, CIDR, country, or OIDC auth."],
            ["One node, one upstream", "A Tailscale tunnel resource represents one endpoint and one upstream. Register a second Tailscale resource when you need another upstream."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="step-list">
          <MiniPanel title="System daemon">
            Reuse the host's logged-in `tailscaled`. Install the `tailscale` CLI, run `tailscale up`,
            enable HTTPS Certificates in the admin console, then use `--tailscale` or
            `WithTailscaleServe(..., t =&gt; t.WithSystemDaemon())`.
          </MiniPanel>
          <MiniPanel title="Ephemeral userspace">
            Supply an auth key and Tap starts a per-session userspace `tailscaled` with
            `--tun=userspace-networking`. The node joins for the run and disappears when Tap stops.
          </MiniPanel>
          <MiniPanel title="Docker host mode">
            Pair an auth key with `--docker` in the CLI or `hostMode: TailscaleHostMode.Docker` in
            Aspire to run `tailscale/tailscale`. This is the portable choice when no host
            `tailscaled` binary is available.
          </MiniPanel>
          <MiniPanel title="Profiles and Config tab">
            Saved tunnel profiles now include Tailscale exposure, daemon mode, auth key, login
            server, and port. The inspector's Config tab exposes those fields alongside the
            Cloudflare guide and profile list.
          </MiniPanel>
        </div>
        <div className="config-table" role="table" aria-label="Tailscale setup checklist">
          {[
            ["Install", "Host modes need tailscale on PATH. Docker mode needs Docker and an auth key."],
            ["HTTPS Certificates", "Enable once under Tailscale admin DNS. Required for Serve and Funnel."],
            ["Funnel ACL", "For public Funnel, grant nodeAttrs attr funnel to the node or tag."],
            ["Auth key", "Required for ephemeral process and Docker modes. Prefer reusable, ephemeral keys with the tags you need."],
            ["Ports", "Tailscale allows 443, 8443, and 10000."],
            ["Windows", "System mode is supported. Ephemeral process mode is not; use Docker mode with an auth key."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="code-grid section-gap">
          <CodeBlock title="CLI: private Serve" code={commands.cliTailscaleServe} />
          <CodeBlock title="CLI: public Funnel" code={commands.cliTailscalePublic} />
          <CodeBlock title="CLI: ephemeral userspace" code={commands.cliTailscaleEphemeral} />
          <CodeBlock title="CLI: Docker mode" code={commands.cliTailscaleDocker} />
          <CodeBlock title="Aspire: private Serve" code={commands.tailscaleServe} />
          <CodeBlock title="Aspire: public Funnel + auth" code={commands.tailscalePublic} />
          <CodeBlock title="Aspire: ephemeral userspace" code={commands.tailscaleEphemeral} />
          <CodeBlock title="Aspire: Docker mode" code={commands.tailscaleDocker} />
          <CodeBlock title="Sample AppHost secrets" code={commands.tailscaleSecrets} />
          <CodeBlock title="Sample scenario filter" code={commands.sampleScenarios} />
        </div>
        <p className="doc-note">
          The inspector Tunnel dialog reads live Tailscale state from the active daemon: backend
          state, MagicDNS name, tailnet, IPs, version, and current `serve` / `funnel` rules. The QR
          tab polls until fresh Tailscale hostnames resolve, then renders scannable links for phone
          testing.
        </p>
      </section>
    );
  }

  if (doc.id === "auth") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="mode-grid">
          {[
            ["Header", "Require a static API key in a request header before proxying."],
            ["CIDR", "Allowlist client IP ranges using CF-Connecting-IP, X-Forwarded-For, then remote IP."],
            ["Country", "Allowlist ISO country codes using Cloudflare's CF-IPCountry header."],
            ["OIDC", "Require browser sign-in with cookie session and OpenID Connect code flow."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="callout">
          <strong>Checks are combined</strong>
          <p>
            If you configure header auth, CIDR allowlist, country allowlist, and OIDC together, every
            request must satisfy all enabled checks. Machine clients usually use header auth; browsers
            can use OIDC.
          </p>
        </div>
        <div className="code-grid">
          <CodeBlock title="CLI: static checks" code={commands.cliAuth} />
          <CodeBlock title="CLI: OIDC" code={commands.cliOidc} />
          <CodeBlock title="Aspire: proxy auth" code={commands.aspireAuth} />
        </div>
        <p className="doc-note">
          This gate protects the tunnelled proxy path. It is unrelated to Tap Studio's
          authentication profiles, which are about signing <em>your own</em> outbound requests —
          see <a href="#studio-auth">Studio auth flows</a>.
        </p>
      </section>
    );
  }

  if (doc.id === "modes") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="mode-grid">
          {[
            ["Standalone", "Local capture proxy, no Cloudflare required."],
            ["Quick", "Random trycloudflare.com URL, no account setup."],
            ["Token", "Dashboard-managed connector token."],
            ["API-managed", "Named tunnel, credentials file, DNS CNAMEs."],
            ["Dynamic", "Fresh per-run hostnames under your zone."],
            ["Tailscale Serve", "Tailnet-only HTTPS through the node's MagicDNS name. Default for Tailscale."],
            ["Tailscale Funnel", "Public HTTPS through Tailscale. Opt in only when internet access is needed."],
            ["Tailscale ephemeral", "Temporary userspace node from an auth key, as a host process or Docker container."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <CodeBlock title="API-managed dynamic hostnames" code={commands.managed} />
        <CodeBlock title="Tailscale private Serve" code={commands.tailscaleServe} />
      </section>
    );
  }

  if (doc.id === "architecture") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/tap-inspector.png"
          alt="Tap inspector UI showing captured GraphQL requests, headers, and response body"
          caption="The local inspector captures traffic before forwarding it, then lets you inspect headers, bodies, timings, status, and replay details."
        />
        <div className="diagram-card" aria-label="High-level architecture diagram">
          <div className="diagram-row">
            <span>tap CLI</span>
            <span>Aspire AppHost</span>
          </div>
          <div className="diagram-merge" />
          <div className="diagram-node">Tap.Server shared inspector</div>
          <div className="diagram-row">
            <span>cloudflared</span>
            <span>tailscaled</span>
            <span>React UI + API</span>
            <span>YARP upstream proxy</span>
          </div>
        </div>
        <div className="architecture-grid">
          <MiniPanel title="Tap.Hosting">
            Aspire extension methods register the inspector project, provider executable resources,
            ingress metadata, parent relationships, and provider tunnel annotations.
          </MiniPanel>
          <MiniPanel title="Tap.Cli">
            Terminal entry point for one upstream URL. It builds the same inspector options, can
            provision tunnels, and runs Tap.Server directly.
          </MiniPanel>
          <MiniPanel title="Lifecycle hook">
            Before startup, Tap verifies provider CLIs, resolves or creates Cloudflare tunnels,
            mints hostnames, configures Tailscale Serve/Funnel, and back-fills Aspire URLs.
          </MiniPanel>
          <MiniPanel title="Tap.Server">
            ASP.NET Core binds a proxy port and UI port. The proxy branch captures through middleware,
            then YARP forwards to the upstream.
          </MiniPanel>
          <MiniPanel title="Inspector UI">
            React reads request history, listens to `/api/stream`, replays requests, and surfaces tunnel
            details, QR links, and provider-specific status through the local UI port.
          </MiniPanel>
        </div>
        <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/ARCHITECTURE.md">
          Read the full architecture notes
        </a>
      </section>
    );
  }

  if (doc.id === "config") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="config-table" role="table" aria-label="Configuration">
          {[
            ["Cloudflare:TunnelToken", "Connector token for token tunnels."],
            ["Cloudflare:ApiToken", "API-managed tunnels, DNS, and tunnel details."],
            ["Tailscale:AuthKey", "Auth key for ephemeral userspace or Docker Tailscale nodes."],
            ["Tailscale:UseSystem", "Sample AppHost flag that enables the system-daemon Tailscale scenario."],
            ["Tailscale:UseDocker", "Sample AppHost flag that enables the Tailscale Docker scenario."],
            ["Inspector__ProxyPort", "Captured application traffic."],
            ["Inspector__UiPort", "Local UI, REST API, and SSE stream."],
            ["Inspector__Provider", "cloudflare or tailscale; drives provider-specific UI and endpoints."],
            ["Inspector__Ingress", "Serialized hostname-to-upstream routing table."],
            ["Inspector__Tunnel__*", "Provider context for tunnel details, Tailscale socket path, mode, and status."],
            ["Inspector__Auth__*", "Optional header, CIDR, country, and OIDC checks."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="callout">
          <strong>Security boundary</strong>
          <p>
            Tap keeps the UI local and gates the public proxy path. Treat any tunnel as an
            internet-facing endpoint and add an explicit access boundary for sensitive services.
          </p>
        </div>
        <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/inspector.md">
          Full Inspector reference
        </a>
      </section>
    );
  }

  if (doc.id === "studio-compose") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="config-table" role="table" aria-label="Request editor tabs">
          {[
            ["Params", "Query string as key/value rows, kept in sync with the URL bar."],
            ["Headers", "Request headers, with completion for the common ones."],
            ["Body", "None, Form, Multipart, Raw, Binary, or GraphQL. Raw bodies get JSON/Text/XML modes and a Format button; multipart handles multiple files."],
            ["Auth", "Which profile applies: inherited from the collection, overridden here, or none."],
            ["Variables", "Request-scoped variables with descriptions, defaults, and required flags."],
            ["Meta", "Name, tags, and protocol — http or websocket."],
            ["Docs", "Markdown documentation, rendered under the request. This is the file's body."],
            ["Source", "The generated *.req.md, read-only. Studio is the only thing that writes YAML."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="mode-grid section-gap">
          {[
            ["Response panel", "Status, duration, size, and content type, with tabs for the body, headers, the exact request sent, the auth and variable flow, and the secrets resolved."],
            ["Streaming", "text/event-stream responses stream into a live timeline; protocol: websocket opens a real socket and appends frames as they arrive."],
            ["GraphQL", "The GraphQL body mode fetches the endpoint schema, so queries, mutations, and variables get completion and validation against the live server."],
            ["Base URL and stages", "A request stores a path; the collection contributes the base URL, and a named stage can override it for dev, uat, or prod."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
      </section>
    );
  }

  if (doc.id === "studio-auth") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/studio-auth-wizard.png"
          alt="Tap Studio's auth template catalogue"
          caption="Creating an auth profile starts from a template catalogue; the wizard then asks only for the fields that flow actually needs."
        />
        <div className="config-table" role="table" aria-label="Authentication templates">
          {[
            ["GitHub PAT", "Classic or fine-grained personal access token."],
            ["GitHub CLI", "Reuses whichever account gh auth login is signed in as."],
            ["GitHub App", "Mints an RS256 JWT from the App key, exchanges for an installation token."],
            ["GitHub OAuth App", "Interactive sign-in for tools acting as a user."],
            ["OAuth 2.0 / OIDC", "Authorization code with PKCE, client credentials, ROPC, or device code — endpoints by hand or via .well-known discovery."],
            ["Microsoft Entra", "Pre-filled AAD v2 authority; supply tenant and client id."],
            ["Azure CLI", "az account get-access-token, direct or on-behalf-of."],
            ["AWS Signature V4", "Signs requests with AWS access keys."],
            ["Signed JWT", "Mints a self-signed JWT per call for service-to-service patterns."],
            ["Bearer / Basic / API key", "Static credentials, referenced from a variable provider."],
            ["Custom headers", "Free-form header and cookie injection."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <Screenshot
          src="./screenshots/studio-auth-oauth2.png"
          alt="An OAuth 2.0 authorization code with PKCE profile in Tap Studio"
          caption="Turn on discovery and Studio fetches /.well-known/openid-configuration to fill in the endpoints. Try it runs the flow on its own, so you can prove a profile works before wiring it to a request."
        />
        <div className="mode-grid">
          {[
            ["Tokens stay out of the repo", "Access tokens, refresh tokens, and expiry live in your OS state folder, keyed by workspace and profile, tightened to user-only permissions. Refresh is automatic."],
            ["The redirect URI is runtime-owned", "Studio derives it from its own base URL and shows it read-only. The desktop app registers the stable tap-studio:// deep link instead of a random loopback port."],
            ["Pick your browser", "Interactive flows let you choose which installed browser and profile handles the sign-in, so a work tenant doesn't land in your personal session."],
            ["Profiles are inheritable", "Set a default auth on the collection and override it per request — or opt out entirely with auth: none."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <CodeBlock title="auth/corp-entra.auth.md" code={commands.studioAuth} />
      </section>
    );
  }

  if (doc.id === "studio-ai") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/studio-assistant.png"
          alt="The Tap Studio AI assistant proposing an edit to a request"
          caption="The assistant proposes; you apply. Its edit lands in the editor as an unsaved change you review and save — or discard."
        />
        <div className="mode-grid">
          {[
            ["Your CLI, your login", "Studio spawns GitHub Copilot CLI or Claude Code from your machine per request instead of bundling an SDK, so there is no extra native dependency and no second set of credentials."],
            ["It sees the workspace", "The prompt carries the current request, the collection's base URL, default auth and shared headers, the available auth profiles, the environment names, and the variable catalogue with scopes and secret flags."],
            ["It proposes, you apply", "The assistant never writes files. It returns a structured request that the UI applies as an unsaved edit; re-apply is one click."],
            ["It documents as it goes", "New or meaningfully changed requests come back with Markdown documentation, and it refines existing notes rather than overwriting them."],
            ["No inlined secrets", "Anything sensitive has to be a {{variable}} reference — the assistant is told never to hardcode a token or key."],
            ["Model choice", "Pick the provider and model in Settings; the status endpoint reports what was detected and what still needs setup."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
      </section>
    );
  }

  if (doc.id === "studio-workspace") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <Screenshot
          src="./screenshots/studio-git.png"
          alt="A request edit shown as an ordinary git diff inside Tap Studio"
          caption="Because a request is a couple of lines of Markdown, review, blame, cherry-pick, and revert all work the way they do for code. Branch, stage, diff, and commit without leaving the app."
        />
        <div className="config-table" role="table" aria-label="Workspace file kinds">
          {[
            ["tap.md", "The workspace: name, default environment, variable providers, workspace-wide variables."],
            ["_collection.md", "A collection: base URL, named stages, default auth, default headers, collection variables."],
            ["*.req.md", "One request, as a fenced http block plus Markdown documentation."],
            ["*.auth.md", "A reusable authentication profile."],
            ["*.env.md", "A named environment: a set of variables and secret references."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="callout">
          <strong>A runnable request is a composition</strong>
          <p>
            Workspace + collection (+ stage) + auth + environment + request. No single file is the
            whole story, and sub-folders inside a collection are pure grouping — they carry no
            metadata and no inheritance.
          </p>
        </div>
        <div className="code-grid section-gap">
          <CodeBlock title="collections/stripe/create-customer.req.md" code={commands.studioRequest} />
          <CodeBlock title="collections/stripe/_collection.md" code={commands.studioCollection} />
          <CodeBlock title="tap.md" code={commands.studioWorkspace} />
          <CodeBlock title="environments/prod.env.md" code={commands.studioEnv} />
        </div>
        <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/workspace-format.md">
          Read the workspace format spec
        </a>
      </section>
    );
  }

  if (doc.id === "studio-variables") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="callout">
          <strong>Two token shapes</strong>
          <p>
            `{"{{name}}"}` resolves from the scope cascade first, then the default provider, then
            the remaining providers in registration order — first hit wins.{" "}
            `{"{{provider:name}}"}` resolves only against that provider, with no fall-through.
            Write a backslash before the braces for a literal.
          </p>
        </div>
        <div className="flow section-gap" aria-label="Variable cascade">
          {["workspace", "collection", "stage", "env", "request", "run"].map((step, index) => (
            <React.Fragment key={step}>
              <div className="flow-node">{step}</div>
              {index < 5 ? <div className="flow-link" /> : null}
            </React.Fragment>
          ))}
        </div>
        <div className="mode-grid section-gap">
          {[
            ["Scopes carry the boring values", "Base URLs, tenant names, page sizes — the things that differ per environment and are fine to read in a diff. They live in the vars block of the workspace, collection, stage, env, or request file."],
            ["Providers carry everything else", "A provider is a named, typed connection to somewhere values actually live. The workspace file records the name and the type; the value is fetched when the request runs."],
            ["Declared in two places", "Workspace providers live in tap.md and travel with the repo. System providers live in ~/.tap/system.json and follow the machine. A workspace provider shadows a system one with the same name."],
            ["Every read is recorded", "Each execution logs which provider answered which name and whether it was secret, so the response's Secrets tab can show what a request depends on without surfacing a value."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="code-grid">
          <CodeBlock title="tap.md — declaring providers" code={commands.studioProviders} />
          <CodeBlock title="environments/prod.env.md" code={commands.studioEnv} />
        </div>
        <p className="doc-note">
          Providers are read on demand and cached per render, so one variables panel doesn't spawn
          a vault round trip per row. Each type is covered below in{" "}
          <a href="#studio-providers">Variable providers</a>.
        </p>
      </section>
    );
  }

  if (doc.id === "studio-providers") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="config-table" role="table" aria-label="Provider types">
          {[
            ["env", "Read", "Allow-listed variables from the process Studio runs in."],
            ["file", "Read / write", "An encrypted YAML file inside the workspace."],
            ["azkv", "Read / write", "Azure Key Vault via DefaultAzureCredential."],
            ["1password", "Read / write", "A 1Password Environment, vault, or item via the op CLI."],
            ["system", "Read / write", "Studio's own machine-local variable list."],
          ].map(([key, mode, value]) => (
            <div className="config-row wide" role="row" key={key}>
              <code role="cell">{key}</code>
              <span className="provider-chip mode" role="cell">
                {mode}
              </span>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="step-list section-gap">
          <MiniPanel title="Pick a scope">
            Add a provider from Settings → Variable providers when it belongs to the machine, or
            from the workspace editor when it belongs to the repo. Same shape either way: a name,
            a type, and a settings bag.
          </MiniPanel>
          <MiniPanel title="The form comes from the provider">
            Each type publishes its own field list, so the dialog shows exactly the settings that
            type understands — with vault pickers, CLI auto-detection, and fields that appear only
            when the mode they belong to is selected.
          </MiniPanel>
          <MiniPanel title="Test before you save">
            Test runs a real listing against the draft settings — bounded to 20 seconds so a stalled
            credential probe fails loudly — and reports how many variables came back. A file
            provider whose passphrase is wrong is reported as a failure, not an empty vault.
          </MiniPanel>
          <MiniPanel title="Then browse it">
            Saved providers list their variable names in the Variables panel with a secret badge and
            a count. Values stay masked until you ask for one explicitly, and Refresh drops the
            provider's cached listing.
          </MiniPanel>
        </div>

        <ProviderDetail
          id="provider-env"
          name="Host environment"
          type="env"
          mode="Read-only"
          blurb={
            <>
              Exposes variables from the process Studio runs in — the natural fit for values your
              shell, CI job, or `direnv` setup already exports. Membership is decided by two host
              environment variables rather than by the workspace, so no file in the repo can widen
              what a provider can reach. Both take comma-separated globs, and a name on both lists
              is treated as secret. Set neither and the provider exposes nothing.
            </>
          }
          settings={[
            ["TAP_VARS_ALLOWED", "Names exposed as plain variables — values may be displayed."],
            ["TAP_SECRETS_ALLOWED", "Names that resolve at execute time but stay masked in every UI surface."],
          ]}
        >
          <CodeBlock title="Allowlisting host environment variables" code={commands.studioAllowlist} />
        </ProviderDetail>

        <ProviderDetail
          id="provider-file"
          name="Encrypted file"
          type="file"
          mode="Read / write"
          blurb={
            <>
              A YAML store at `.vars/&lt;provider&gt;.yml` inside the workspace, written by Studio
              rather than by hand. Values marked secret are encrypted at rest with AES-256-GCM under
              a key derived from a passphrase (PBKDF2-HMAC-SHA256, 200k iterations), so the file
              itself is safe to commit. Plain values work without a passphrase; storing a secret
              requires one. Keep the passphrase on a system-scope provider so it stays in
              `~/.tap/system.json` instead of the repo.
            </>
          }
          settings={[
            ["encryptionKey", "Passphrase the AES key is derived from. Required before any secret value can be stored."],
          ]}
        >
          <CodeBlock title=".vars/local.yml" code={commands.studioFileStore} />
        </ProviderDetail>

        <ProviderDetail
          id="provider-azkv"
          name="Azure Key Vault"
          type="azkv"
          mode="Read / write"
          blurb={
            <>
              Reads and writes secrets in an Azure Key Vault through `DefaultAzureCredential`, so it
              picks up `az login`, environment credentials, workload and managed identity, or your
              IDE's Azure sign-in — no client secret in the workspace file. Listing returns names
              and metadata only; a value is fetched when a token actually references it. Names Key
              Vault cannot hold (anything outside `A-Z a-z 0-9 -`) count as a miss rather than an
              error, so a bare token keeps falling through to the next provider.
            </>
          }
          settings={[
            ["vaultName", "Required. Short vault name; expands to https://<name>.vault.azure.net/. The settings form can pick from the vaults you can see."],
            ["tenantId", "Pins authentication to one tenant when your account can reach several."],
            ["prefix", "Prepended to every Key Vault lookup. Tokens keep using the unprefixed name."],
          ]}
        >
          <div className="code-grid">
            <CodeBlock title="tap.md entry" code={commands.studioAzkv} />
            <CodeBlock title="Signing in" code={commands.studioAzkvLogin} />
          </div>
        </ProviderDetail>

        <ProviderDetail
          id="provider-1password"
          name="1Password"
          type="1password"
          mode="Read / write"
          blurb={
            <>
              Backed by the local `op` CLI, which is already authenticated on your machine — through
              the 1Password desktop app integration and biometric unlock, or an existing `op signin`
              session. Like Key Vault, the provider carries no credentials of its own. Three shapes,
              chosen by the mode: an Environment, a whole vault, or a single item.
            </>
          }
          settings={[
            ["mode", "environment, vault, or item. Decides which of the fields below apply."],
            ["environment", "Environment mode. The Environment ID copied from the 1Password app."],
            ["vault", "Vault mode and item mode. Vault name or ID; every lookup is scoped to it."],
            ["item", "Item mode. The item whose fields become this provider's variables."],
            ["field", "Vault mode. Which field holds the value. Empty falls back to password, then credential, then the first concealed field."],
            ["account", "Account shorthand or sign-in address — only needed when op is signed in to several."],
            ["serviceAccountToken", "For headless hosts. Empty uses whatever session op already has."],
            ["cliPath", "Override when op isn't on PATH. Otherwise TAP_OP_CLI, then the usual install locations, then PATH."],
          ]}
        >
          <div className="mode-grid section-gap">
            {[
              ["Environment mode", "A 1Password Environment is already a flat name-to-value namespace, which is exactly what a variable provider is — so its variables become the provider's, in one call. Read-only: the CLI can't write an Environment back. Environments are still beta and need op 2.38.2-beta.01 or later."],
              ["Vault mode", "Every item in the vault becomes one variable, named after the item's title and valued by its field. This mirrors the Key Vault model: vault, name, value. Writing a name that isn't there yet creates the item."],
              ["Item mode", "One item's fields become the variables — username, password, and any custom field you added. Section-scoped fields are addressed as section.field, the same spelling op's own assignment syntax uses."],
              ["Set-up help built in", "The settings form detects the op binary for you and can browse the vaults your session can see, so the vault field is a picker rather than a string you have to spell correctly."],
            ].map(([name, body]) => (
              <article className="mode-card" key={name}>
                <strong>{name}</strong>
                <p>{body}</p>
              </article>
            ))}
          </div>
          <div className="code-grid">
            <CodeBlock title="Environment mode" code={commands.studio1pEnvironment} />
            <CodeBlock title="Vault mode" code={commands.studio1pVault} />
            <CodeBlock title="Item mode" code={commands.studio1pItem} />
            <CodeBlock title="The op CLI" code={commands.studio1pCli} />
          </div>
          <p className="doc-note">
            1Password docs: <a href="https://developer.1password.com/docs/cli/get-started/">get started with the CLI</a>
            {" "}and <a href="https://developer.1password.com/docs/environments/">Environments</a>.
          </p>
        </ProviderDetail>

        <ProviderDetail
          id="provider-system"
          name="System variables"
          type="system"
          mode="Read / write"
          blurb={
            <>
              Always registered, and backed by the same `~/.tap/system.json` the Settings screen
              edits — so a value you type into Settings is a variable every workspace on this machine
              can resolve. This is where a personal token belongs when it should never travel with
              the repo. Point `TAP_SYSTEM_DIR` somewhere else to relocate the file.
            </>
          }
          settings={[]}
        />

        <div className="section-heading section-gap">
          <span className="kicker">Per environment</span>
          <h2>One vault per environment, one token in the request.</h2>
          <p>
            A request shouldn't have to know whether it is running against dev or prod. An
            environment can bind provider names, so the same token reads from a different vault
            depending on which environment is active.
          </p>
        </div>
        <div className="config-table" role="table" aria-label="Environment provider binding">
          {[
            ["defaultVariableProvider", "The provider bare {{name}} tokens hit first, and the one that receives writes made without naming a target. Precedence: environment, then workspace, then system."],
            ["providerAliases", "Alias-to-provider bindings. Requests use a stable prefix like {{kv:stripe-key}}; each environment points kv at its own provider."],
            ["strictVariables", "With a default provider set, a bare token that misses it fails instead of falling through — so a prod run can never silently pick up a dev value."],
          ].map(([key, value]) => (
            <div className="config-row" role="row" key={key}>
              <code role="cell">{key}</code>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="code-grid section-gap">
          <CodeBlock title="environments/prod.env.md" code={commands.studioEnvBinding} />
          <CodeBlock title="tap.md — both vaults registered" code={commands.studioProviders} />
        </div>
        <div className="callout">
          <strong>What never reaches a file</strong>
          <p>
            Workspace files hold provider names and references, not values. Settings the provider
            marks sensitive leave the API as `***` and are only ever sent back as that same mask,
            so the browser never receives one. Secret values are masked in every listing until you
            ask for a specific one, and the execution trace records provider, name, and secret flag
            — never the value. Keep secret-bearing settings on system-scope providers; workspace
            providers are written into `tap.md` verbatim.
          </p>
        </div>
      </section>
    );
  }

  if (doc.id === "studio-install") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="mode-grid">
          {[
            ["macOS", "A signed .dmg / .app for Apple Silicon and Intel."],
            ["Windows", "An .msi and an NSIS setup .exe."],
            ["Linux", "A .deb package."],
            ["Auto-update", "Each release publishes a signed manifest the updater client polls, so the app keeps itself current."],
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <div className="code-grid">
          <CodeBlock title="Run from source" code={commands.studioRun} />
          <CodeBlock title="Build the desktop bundle" code={commands.studioBuild} />
        </div>
        <p className="doc-note">
          The dev loop starts a demo API that exercises every verb, content type, SSE, WebSockets,
          GraphQL, and a real OAuth2/OIDC server — so every Studio feature has something to talk to
          out of the box.
        </p>
        <a className="text-link" href={`${repoUrl}/releases`}>
          Download Tap Studio
        </a>
      </section>
    );
  }

  return (
    <section className="doc-block" id={doc.id}>
      <DocHeader doc={doc} />
    </section>
  );
};

const PublicTunnelInlineWarning = ({ provider }: { provider: string }) => (
  <div className="callout danger-callout" role="alert">
    <strong>{provider} public exposure needs auth or edge rules</strong>
    <p>
      Avoid running public tunnels without Tap auth, Cloudflare Access, or Cloudflare WAF rules.
      Automated probes often arrive seconds after the hostname is live, and every attempt is visible
      in the Inspector request log.
    </p>
  </div>
);

const DocHeader = ({ doc }: { doc: DocSection }) => (
  <div className="doc-header">
    <span className="kicker">{doc.eyebrow}</span>
    <h2>{doc.title}</h2>
    <p>{doc.body}</p>
  </div>
);

const ProviderDetail = ({
  id,
  name,
  type,
  mode,
  blurb,
  settings,
  children,
}: {
  id: string;
  name: string;
  type: string;
  mode: string;
  blurb: React.ReactNode;
  settings: string[][];
  children?: React.ReactNode;
}) => (
  <article className="provider-block" id={id}>
    <header className="provider-block-head">
      <h3>{name}</h3>
      <code className="provider-chip">{type}</code>
      <span className="provider-chip mode">{mode}</span>
    </header>
    <p>{blurb}</p>
    {settings.length > 0 ? (
      <div className="config-table" role="table" aria-label={`${name} settings`}>
        {settings.map(([key, value]) => (
          <div className="config-row" role="row" key={key}>
            <code role="cell">{key}</code>
            <span role="cell">{value}</span>
          </div>
        ))}
      </div>
    ) : null}
    {children}
  </article>
);

const MiniPanel = ({ title, children }: { title: string; children: React.ReactNode }) => (
  <article className="mini-panel">
    <h3>{title}</h3>
    <p>{children}</p>
  </article>
);

const CodeBlock = ({ title, code }: { title: string; code: string }) => (
  <figure className="code-block">
    <figcaption>{title}</figcaption>
    <pre>
      <code>{code}</code>
    </pre>
  </figure>
);

const Screenshot = ({ src, alt, caption }: { src: string; alt: string; caption: string }) => (
  <figure className="screenshot">
    <img src={src} alt={alt} loading="lazy" />
    <figcaption>{caption}</figcaption>
  </figure>
);

const FinalCta = () => (
  <section className="final-cta">
    <div>
      <span className="kicker">Two tools, one workflow</span>
      <h2>Compose the call in Studio. Watch it land in the Inspector.</h2>
      <p>
        Both halves are free, local, and open source. Install the CLI, grab the desktop app, or
        read the code.
      </p>
    </div>
    <a className="button primary" href={repoUrl}>
      View on GitHub
    </a>
  </section>
);

createRoot(document.getElementById("root")!).render(<App />);
