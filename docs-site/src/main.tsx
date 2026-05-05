import React from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";

const version = import.meta.env.VITE_TAP_VERSION ?? "v0.1.0";
const repoUrl = import.meta.env.VITE_TAP_REPO_URL ?? "https://github.com/philbir/tap";

type Feature = {
  title: string;
  text: string;
  glyph: string;
};

type DocSection = {
  id: string;
  eyebrow: string;
  title: string;
  body: string;
};

const features: Feature[] = [
  {
    title: "Tunnel without ceremony",
    text: "Use free TryCloudflare URLs for quick sharing, dashboard connector tokens for existing tunnels, or API-managed tunnels when Tap should create DNS for you.",
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
    title: "Aspire-native wiring",
    text: "Attach an inspector or public tunnel to an Aspire resource with extension methods, while allocated ports and hostnames are resolved at startup.",
    glyph: "A",
  },
  {
    title: "Built for local trust",
    text: "Keep the UI local, put auth on public proxy traffic, and combine header, CIDR, country, and OIDC checks when the tunnel exposes sensitive services.",
    glyph: "S",
  },
];

const docs: DocSection[] = [
  {
    id: "use-cases",
    eyebrow: "Why",
    title: "Use cases",
    body: "Tap is for the moments when localhost needs to behave like a real internet endpoint, but you still want full visibility into every request.",
  },
  {
    id: "quickstart",
    eyebrow: "Start",
    title: "Quick start",
    body: "Run Tap from the CLI for ad hoc debugging, or add it to Aspire when tunnel wiring should live beside your app resources.",
  },
  {
    id: "entry-points",
    eyebrow: "Choose",
    title: "CLI or Aspire",
    body: "Both entry points use the same Tap.Server runtime. The difference is where configuration and lifecycle orchestration live.",
  },
  {
    id: "cli",
    eyebrow: "Terminal",
    title: "CLI reference",
    body: "The CLI is for one upstream URL at a time. Start local-only, add a quick tunnel, or let Tap provision Cloudflare state for you.",
  },
  {
    id: "cloudflare",
    eyebrow: "Cloudflare",
    title: "Tunnel setup",
    body: "Existing-tunnel mode uses a Cloudflare Tunnel you have already created. API-managed mode needs a Cloudflare API token that can edit tunnels and DNS.",
  },
  {
    id: "auth",
    eyebrow: "Security",
    title: "Authentication",
    body: "Authentication gates the public proxy path before traffic reaches the upstream. The local inspector UI remains the control plane.",
  },
  {
    id: "architecture",
    eyebrow: "Internals",
    title: "Architecture",
    body: "Tap has two entry points, a shared inspector host, and an optional cloudflared tunnel in front of the proxy port.",
  },
  {
    id: "modes",
    eyebrow: "Routing",
    title: "Tunnel modes",
    body: "Tap scales from a local capture proxy to Cloudflare-managed tunnels with DNS and dynamic hostnames.",
  },
  {
    id: "config",
    eyebrow: "Operations",
    title: "Configuration",
    body: "Most configuration is written by the AppHost, with Cloudflare credentials and optional auth coming from normal .NET configuration.",
  },
];

const commands = {
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
};

const App = () => (
  <>
    <SiteNav />
    <main>
      <Hero />
      <FeatureGrid />
      <FlowSection />
      <Docs />
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
        <a href="#features">Features</a>
        <a href="#use-cases">Use cases</a>
        <a href="#quickstart">Quick start</a>
        <a href="#entry-points">CLI/Aspire</a>
        <a href="#cli">CLI</a>
        <a href="#cloudflare">Cloudflare</a>
        <a href="#auth">Auth</a>
        <a href="#architecture">Architecture</a>
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
        Aspire-friendly Cloudflare Tunnel inspector
      </div>
      <h1>Localhost, ready for real callbacks.</h1>
      <p className="lead">
        Tap gives local services a public URL for mobile hooks, webhooks, auth redirects,
        partner integrations, and temporary demos, then captures the traffic so you can see
        what actually happened.
      </p>
      <div className="hero-actions">
        <a className="button primary" href="#quickstart">
          Start tunneling
        </a>
        <a className="button ghost" href="#architecture">
          See architecture
        </a>
        <a className="button ghost" href={repoUrl}>
          View code
        </a>
      </div>
      <div className="proof-row">
        <span>{version}</span>
        <span>CLI</span>
        <span>Aspire</span>
        <span>TryCloudflare</span>
        <span>Existing tunnels</span>
        <span>API-managed DNS</span>
        <span>HTTP replay</span>
        <span>Free as tap water</span>
      </div>
    </div>
    <picture className="hero-visual" aria-label="Tap tunnel illustration">
      <source srcSet="./tap-hero-dark.png" media="(prefers-color-scheme: dark)" />
      <img src="./tap-hero.png" alt="A tap extracting traffic from a tunnel into an inspector panel" />
    </picture>
  </section>
);

const FeatureGrid = () => (
  <section className="section" id="features">
    <div className="section-heading">
      <span className="kicker">Why Tap</span>
      <h2>Public callbacks and local debugging finally share one workflow.</h2>
      <p>
        Start with free TryCloudflare URLs. Add a free Cloudflare account and your own domain
        when you want stable hostnames. Tap itself is free like tap water: drink as much as you like.
      </p>
    </div>
    <div className="feature-grid">
      {features.map((feature) => (
        <article className="feature-card" key={feature.title}>
          <span className="glyph">{feature.glyph}</span>
          <h3>{feature.title}</h3>
          <p>{feature.text}</p>
        </article>
      ))}
    </div>
  </section>
);

const FlowSection = () => (
  <section className="flow-band">
    <div className="section-heading">
      <span className="kicker">Traffic flow</span>
      <h2>A tunnel path you can reason about.</h2>
      <p>
        CLI and Aspire both converge on the same inspector host. If a tunnel is enabled, Cloudflare
        reaches cloudflared, cloudflared targets Tap's proxy port, and Tap forwards to the local upstream.
      </p>
    </div>
    <div className="entry-flow" aria-label="Tap entry point flow">
      <div className="entry-node">tap CLI</div>
      <div className="entry-node">Aspire AppHost</div>
      <div className="flow-link vertical" />
      <div className="entry-node wide">Tap.Server shared inspector</div>
    </div>
    <div className="flow" aria-label="Tap request flow">
      {["Client", "Cloudflare", "cloudflared", "Tap proxy", "Upstream"].map((step, index) => (
        <React.Fragment key={step}>
          <div className="flow-node">{step}</div>
          {index < 4 ? <div className="flow-link" /> : null}
        </React.Fragment>
      ))}
    </div>
  </section>
);

const Docs = () => (
  <section className="docs-wrap" id="docs">
    <aside className="docs-sidebar" aria-label="Docs navigation">
      <span className="kicker">Docs</span>
      {docs.map((doc) => (
        <a href={`#${doc.id}`} key={doc.id}>
          {doc.title}
        </a>
      ))}
    </aside>
    <div className="docs-content">
      {docs.map((doc) => (
        <DocBlock doc={doc} key={doc.id} />
      ))}
    </div>
  </section>
);

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
          <CodeBlock title="CLI: local inspector" code={commands.cli} />
          <CodeBlock title="CLI: quick public tunnel" code={commands.cliQuick} />
          <CodeBlock title="CLI: existing token tunnel" code={commands.cliToken} />
          <CodeBlock title="CLI: API-managed dynamic hostname" code={commands.cliManaged} />
          <CodeBlock title="Aspire: standalone inspector" code={commands.standalone} />
          <CodeBlock title="Aspire: quick public tunnel" code={commands.quick} />
        </div>
      </section>
    );
  }

  if (doc.id === "entry-points") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="entry-grid">
          <MiniPanel title="CLI">
            Use `tap run` when you have one upstream URL and want a tunnel or inspector immediately.
            The CLI reads flags, environment variables, and optional `tap.config`, then starts
            cloudflared and Tap.Server for the current terminal session.
          </MiniPanel>
          <MiniPanel title="Aspire">
            Use Tap.Hosting when inspectors and tunnels are part of the app model. Aspire resolves
            resource endpoints, shows URLs in the dashboard, and runs lifecycle provisioning before
            cloudflared starts.
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
        <div className="mode-grid">
          {[
            ["Local inspector", "No Cloudflare. Proxy on port 4444, UI on port 4445."],
            ["Quick tunnel", "Creates a temporary trycloudflare.com URL. No account needed."],
            ["Token tunnel", "Runs an existing dashboard-managed Cloudflare Tunnel connector token."],
            ["API-managed", "Tap creates or reuses a named tunnel through the Cloudflare API."],
            ["Dynamic hostname", "Tap mints a fresh hostname under your zone and creates DNS."],
            ["Docker connector", "Use --docker to run cloudflared as cloudflare/cloudflared:latest."],
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
            ["--docker", "Run cloudflared through Docker host networking."],
            ["--auto-install", "Install cloudflared if missing."],
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
        </div>
      </section>
    );
  }

  if (doc.id === "cloudflare") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
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
          ].map(([name, body]) => (
            <article className="mode-card" key={name}>
              <strong>{name}</strong>
              <p>{body}</p>
            </article>
          ))}
        </div>
        <CodeBlock title="API-managed dynamic hostnames" code={commands.managed} />
      </section>
    );
  }

  if (doc.id === "architecture") {
    return (
      <section className="doc-block" id={doc.id}>
        <DocHeader doc={doc} />
        <div className="diagram-card" aria-label="High-level architecture diagram">
          <div className="diagram-row">
            <span>tap CLI</span>
            <span>Aspire AppHost</span>
          </div>
          <div className="diagram-merge" />
          <div className="diagram-node">Tap.Server shared inspector</div>
          <div className="diagram-row">
            <span>cloudflared</span>
            <span>React UI + API</span>
            <span>YARP upstream proxy</span>
          </div>
        </div>
        <div className="architecture-grid">
          <MiniPanel title="Tap.Hosting">
            Aspire extension methods register the inspector project, cloudflared executable, ingress
            metadata, parent relationships, and Cloudflare tunnel annotations.
          </MiniPanel>
          <MiniPanel title="Tap.Cli">
            Terminal entry point for one upstream URL. It builds the same inspector options, can
            provision tunnels, and runs Tap.Server directly.
          </MiniPanel>
          <MiniPanel title="Lifecycle hook">
            Before startup, Tap verifies cloudflared, resolves or creates tunnels, mints hostnames,
            ensures DNS records, and back-fills Aspire URLs.
          </MiniPanel>
          <MiniPanel title="Tap.Server">
            ASP.NET Core binds a proxy port and UI port. The proxy branch captures through middleware,
            then YARP forwards to the upstream.
          </MiniPanel>
          <MiniPanel title="Inspector UI">
            React reads request history, listens to `/api/stream`, replays requests, and surfaces tunnel
            details through the local UI port.
          </MiniPanel>
        </div>
        <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/ARCHITECTURE.md">
          Read the full architecture notes
        </a>
      </section>
    );
  }

  return (
    <section className="doc-block" id={doc.id}>
      <DocHeader doc={doc} />
      <div className="config-table" role="table" aria-label="Configuration">
        {[
          ["Cloudflare:TunnelToken", "Connector token for token tunnels."],
          ["Cloudflare:ApiToken", "API-managed tunnels, DNS, and tunnel details."],
          ["Inspector__ProxyPort", "Captured application traffic."],
          ["Inspector__UiPort", "Local UI, REST API, and SSE stream."],
          ["Inspector__Ingress", "Serialized hostname-to-upstream routing table."],
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
    </section>
  );
};

const DocHeader = ({ doc }: { doc: DocSection }) => (
  <div className="doc-header">
    <span className="kicker">{doc.eyebrow}</span>
    <h2>{doc.title}</h2>
    <p>{doc.body}</p>
  </div>
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

const FinalCta = () => (
  <section className="final-cta">
    <div>
      <span className="kicker">Ready for callbacks</span>
      <h2>Give local services a public URL you can actually debug.</h2>
      <p>Build the docs site, publish it to GitHub Pages, and point users at one clear Tap workflow.</p>
    </div>
    <a className="button primary" href={repoUrl}>
      View on GitHub
    </a>
  </section>
);

createRoot(document.getElementById("root")!).render(<App />);
