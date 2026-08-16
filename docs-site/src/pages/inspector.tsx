import React from "react";
import {
  Callout,
  CodeBlock,
  ConfigTable,
  DocList,
  FeatureGrid,
  MiniPanel,
  ModeGrid,
  PublicTunnelInlineWarning,
  Screenshot,
  SectionHeading,
  type DocSection,
} from "../components/ui";
import { commands } from "../data/commands";
import { inspectorFeatures } from "../data/features";
import { href } from "../router";

const docs: DocSection[] = [
  {
    id: "use-cases",
    eyebrow: "Why",
    title: "Use cases",
    body: "For the moments when localhost needs to behave like a real internet endpoint, but you still want full visibility into every request.",
    content: () => (
      <>
        <ModeGrid
          items={[
            ["Mobile app hooks", "Point native or emulator builds at a public HTTPS URL while the service still runs on localhost."],
            ["Webhook development", "Capture provider deliveries, inspect headers and bodies, and replay requests while iterating."],
            ["Auth callbacks", "Test OAuth/OIDC redirect URIs against a real hostname without deploying a staging app."],
            ["Temporary sharing", "Send someone a URL to work running on your machine, then close the tunnel when the moment is over."],
            ["Team demos", "Put Tap in Aspire so the whole app graph has repeatable tunnel and inspector wiring."],
            ["Cheap stable hostnames", "TryCloudflare is free. For stable URLs, bring a domain you own; .dev domains are a tidy fit for developer projects."],
          ]}
        />
        <Callout title="Free as tap water">
          Quick tunnels cost nothing and need no account. Stable hostnames use Cloudflare plus a
          domain you control. Tap stays small, local, and reusable, so you can open another tunnel
          whenever the next callback needs a place to land.
        </Callout>
      </>
    ),
  },
  {
    id: "quickstart",
    eyebrow: "Start",
    title: "Quick start",
    body: "Run Tap from the CLI for ad hoc debugging, or add it to Aspire when tunnel wiring should live beside your app resources.",
    content: () => (
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
    ),
  },
  {
    id: "entry-points",
    eyebrow: "Choose",
    title: "CLI or Aspire",
    body: "Both entry points use the same Tap.Server runtime. The difference is where configuration and lifecycle orchestration live.",
    content: () => (
      <>
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
      </>
    ),
  },
  {
    id: "cli",
    eyebrow: "Terminal",
    title: "CLI reference",
    body: "The CLI is for one upstream URL at a time. Start local-only, add Cloudflare, or route through Tailscale Serve/Funnel.",
    content: () => (
      <>
        <Screenshot
          src="./screenshots/tap-cli.png"
          alt="Tap CLI running a quick TryCloudflare tunnel"
          caption="The CLI prints the public URL, upstream, inspector UI, proxy port, and a live tail of recent requests."
        />
        <ModeGrid
          items={[
            ["Local inspector", "No Cloudflare. Proxy on port 4444, UI on port 4445."],
            ["Quick tunnel", "Creates a temporary trycloudflare.com URL. No account needed."],
            ["Token tunnel", "Runs an existing dashboard-managed Cloudflare Tunnel connector token."],
            ["API-managed", "Tap creates or reuses a named tunnel through the Cloudflare API."],
            ["Dynamic hostname", "Tap mints a fresh hostname under your zone and creates DNS."],
            ["Tailscale Serve", "Private tailnet-only HTTPS via tailscale serve. This is the default for --tailscale."],
            ["Tailscale Funnel", "Public internet URL via tailscale funnel. Pair it with auth."],
            ["Tailscale ephemeral", "Spawn a userspace tailscaled per run from --tailscale-authkey or TAILSCALE_AUTHKEY."],
            ["Docker connector", "Use --docker to run cloudflare/cloudflared or tailscale/tailscale, depending on the active provider."],
          ]}
        />
        <ConfigTable
          label="CLI options"
          rows={[
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
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title="tap.config" code={commands.cliConfig} />
          <CodeBlock title="Install cloudflared" code="tap install-cloudflared" />
          <CodeBlock title="Tailscale public Funnel" code={commands.cliTailscalePublic} />
          <CodeBlock title="Tailscale Docker mode" code={commands.cliTailscaleDocker} />
        </div>
      </>
    ),
  },
  {
    id: "cloudflare",
    eyebrow: "Cloudflare",
    title: "Tunnel setup",
    body: "Existing-tunnel mode uses a Cloudflare Tunnel you have already created. API-managed mode needs a Cloudflare API token that can edit tunnels and DNS.",
    content: () => (
      <>
        <PublicTunnelInlineWarning provider="Cloudflare" />
        <Screenshot
          src="./screenshots/tab-tunnel-dialog.png"
          alt="Tap tunnel dialog showing Cloudflare edge, cloudflared, inspector, and upstream"
          caption="The tunnel dialog makes the Cloudflare path explicit, from public hostname through cloudflared to the inspector proxy and local upstream."
        />
        <Callout title="Existing-tunnel mode uses a dashboard tunnel">
          `--token` and `WithExistingTunnel(...)` do not create a Cloudflare Tunnel. Create the
          tunnel in the Cloudflare dashboard first, copy its connector token, and pass that token to
          Tap.
        </Callout>
        <div className="step-list section-gap">
          <MiniPanel title="Create a dashboard tunnel">
            In Cloudflare Zero Trust, go to Networks, then Tunnels, create a cloudflared tunnel, and
            copy the connector command. The `eyJ...` value after `--token` is the connector token.
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
            Pass the API token and account id to Tap. Tap can create or reuse the named tunnel, write
            local credentials, mint hostnames, and ensure CNAME records.
          </MiniPanel>
        </div>
        <div className="code-grid">
          <CodeBlock title="CLI: token mode" code={commands.cliToken} />
          <CodeBlock title="CLI: API-managed mode" code={commands.cliManaged} />
          <CodeBlock title="Aspire user-secrets" code={commands.secrets} />
          <CodeBlock title="Aspire: existing token tunnel" code={commands.token} />
        </div>
        <p className="doc-note">
          A free Cloudflare account covers all of this. Cloudflare docs:{" "}
          <a href="https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/">tunnel tokens</a>{" "}
          and{" "}
          <a href="https://developers.cloudflare.com/fundamentals/api/reference/permissions/">
            API token permissions
          </a>
          .
        </p>
      </>
    ),
  },
  {
    id: "tailscale",
    eyebrow: "Tailscale",
    title: "Serve and Funnel",
    body: "Tailscale support routes Tap through your tailnet. Serve is private by default; Funnel is public and should be paired with auth.",
    content: () => (
      <>
        <PublicTunnelInlineWarning provider="Tailscale Funnel" />
        <Callout tone="warning" title="Default to private">
          `tap run --tailscale` and `WithTailscaleServe(...)` use `tailscale serve`, which is
          reachable only from devices in your tailnet. Switch to `tailscale funnel` only when you
          need an internet-facing URL, and pair public Funnel with Tap auth.
        </Callout>
        <ModeGrid
          gap
          items={[
            ["What Tailscale adds", "Tailscale gives each machine a stable HTTPS name like https://machine.tailnet.ts.net. Tap points that HTTPS endpoint at the inspector proxy, then forwards captured traffic to your upstream."],
            ["Serve", "Private HTTPS inside your tailnet. This is the safe default for CLI and Aspire."],
            ["Funnel", "Public HTTPS on the internet through your tailnet node. New public hostnames are scanned quickly, so protect them with header, CIDR, country, or OIDC auth."],
            ["One node, one upstream", "A Tailscale tunnel resource represents one endpoint and one upstream. Register a second Tailscale resource when you need another upstream."],
          ]}
        />
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
            Saved tunnel profiles include Tailscale exposure, daemon mode, auth key, login server,
            and port. The inspector's Config tab exposes those fields alongside the Cloudflare guide
            and profile list.
          </MiniPanel>
        </div>
        <ConfigTable
          label="Tailscale setup checklist"
          rows={[
            ["Install", "Host modes need tailscale on PATH. Docker mode needs Docker and an auth key."],
            ["HTTPS Certificates", "Enable once under Tailscale admin DNS. Required for Serve and Funnel."],
            ["Funnel ACL", "For public Funnel, grant nodeAttrs attr funnel to the node or tag."],
            ["Auth key", "Required for ephemeral process and Docker modes. Prefer reusable, ephemeral keys with the tags you need."],
            ["Ports", "Tailscale allows 443, 8443, and 10000."],
            ["Windows", "System mode is supported. Ephemeral process mode is not; use Docker mode with an auth key."],
          ]}
        />
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
      </>
    ),
  },
  {
    id: "auth",
    eyebrow: "Security",
    title: "Proxy authentication",
    body: "Authentication gates the public proxy path before traffic reaches the upstream. The local inspector UI remains the control plane.",
    content: () => (
      <>
        <ModeGrid
          items={[
            ["Header", "Require a static API key in a request header before proxying."],
            ["CIDR", "Allowlist client IP ranges using CF-Connecting-IP, X-Forwarded-For, then remote IP."],
            ["Country", "Allowlist ISO country codes using Cloudflare's CF-IPCountry header."],
            ["OIDC", "Require browser sign-in with cookie session and OpenID Connect code flow."],
          ]}
        />
        <Callout title="Checks are combined">
          If you configure header auth, CIDR allowlist, country allowlist, and OIDC together, every
          request must satisfy all enabled checks. Machine clients usually use header auth; browsers
          can use OIDC.
        </Callout>
        <div className="code-grid section-gap">
          <CodeBlock title="CLI: static checks" code={commands.cliAuth} />
          <CodeBlock title="CLI: OIDC" code={commands.cliOidc} />
          <CodeBlock title="Aspire: proxy auth" code={commands.aspireAuth} />
        </div>
        <p className="doc-note">
          This gate protects the tunnelled proxy path. It is unrelated to Tap Studio's authentication
          profiles, which are about signing <em>your own</em> outbound requests — see{" "}
          <a href={href("studio", "studio-auth")}>Studio auth flows</a>.
        </p>
      </>
    ),
  },
  {
    id: "modes",
    eyebrow: "Routing",
    title: "Tunnel modes",
    body: "Tap scales from a local capture proxy to Cloudflare-managed tunnels, Tailscale private Serve, and public Funnel.",
    content: () => (
      <>
        <ModeGrid
          items={[
            ["Standalone", "Local capture proxy, no Cloudflare required."],
            ["Quick", "Random trycloudflare.com URL, no account setup."],
            ["Token", "Dashboard-managed connector token."],
            ["API-managed", "Named tunnel, credentials file, DNS CNAMEs."],
            ["Dynamic", "Fresh per-run hostnames under your zone."],
            ["Tailscale Serve", "Tailnet-only HTTPS through the node's MagicDNS name. Default for Tailscale."],
            ["Tailscale Funnel", "Public HTTPS through Tailscale. Opt in only when internet access is needed."],
            ["Tailscale ephemeral", "Temporary userspace node from an auth key, as a host process or Docker container."],
          ]}
        />
        <div className="code-grid">
          <CodeBlock title="API-managed dynamic hostnames" code={commands.managed} />
          <CodeBlock title="Tailscale private Serve" code={commands.tailscaleServe} />
        </div>
      </>
    ),
  },
  {
    id: "architecture",
    eyebrow: "Internals",
    title: "Architecture",
    body: "Tap has two entry points, a shared inspector host, and optional Cloudflare or Tailscale tunnel providers in front of the proxy port.",
    content: () => (
      <>
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
            Before startup, Tap verifies provider CLIs, resolves or creates Cloudflare tunnels, mints
            hostnames, configures Tailscale Serve/Funnel, and back-fills Aspire URLs.
          </MiniPanel>
          <MiniPanel title="Tap.Server">
            ASP.NET Core binds a proxy port and UI port. The proxy branch captures through
            middleware, then YARP forwards to the upstream.
          </MiniPanel>
          <MiniPanel title="Inspector UI">
            React reads request history, listens to `/api/stream`, replays requests, and surfaces
            tunnel details, QR links, and provider-specific status through the local UI port.
          </MiniPanel>
        </div>
        <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/ARCHITECTURE.md">
          Read the full architecture notes
        </a>
      </>
    ),
  },
  {
    id: "config",
    eyebrow: "Operations",
    title: "Configuration",
    body: "Most configuration is written by the AppHost, with provider credentials, profile fields, and optional auth coming from normal .NET configuration.",
    content: () => (
      <>
        <ConfigTable
          label="Configuration"
          rows={[
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
          ]}
        />
        <Callout title="Security boundary">
          Tap keeps the UI local and gates the public proxy path. Treat any tunnel as an
          internet-facing endpoint and add an explicit access boundary for sensitive services.
        </Callout>
        <p className="doc-note">
          <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/inspector.md">
            Full Inspector reference
          </a>
        </p>
      </>
    ),
  },
];

export const InspectorPage = () => (
  <>
    <section className="product-hero">
      <div className="product-hero-copy">
        <span className="kicker product-eyebrow">
          <span className="product-index large">01</span> Product
        </span>
        <h1>Tunnel + Inspector</h1>
        <p className="lead">
          Give a local service a real public URL for mobile hooks, webhooks, auth redirects, partner
          integrations, and temporary demos — then read back exactly what happened, down to the
          individual WebSocket frame.
        </p>
        <div className="hero-actions">
          <a className="button primary" href={href("inspector", "quickstart")}>
            Quick start
          </a>
          <a className="button ghost" href={href("inspector", "cli")}>
            CLI reference
          </a>
        </div>
        <div className="proof-row">
          <span>Free</span>
          <span>CLI</span>
          <span>Aspire</span>
          <span>Cloudflare</span>
          <span>Tailscale</span>
          <span>WebSockets</span>
          <span>SSE</span>
          <span>Replay</span>
          <span>QR</span>
        </div>
      </div>
      <picture className="product-hero-visual">
        <img
          src="./screenshots/tap-inspector.png"
          alt="The Tap inspector showing captured requests, headers, and a response body"
        />
      </picture>
    </section>

    <PublicTunnelWarning />

    <section className="section" id="inspector-features">
      <SectionHeading kicker="What it does" title="Public callbacks and local debugging in one workflow.">
        Start with free TryCloudflare URLs. Add a free Cloudflare account and your own domain when
        you want stable hostnames — or route through your tailnet with Tailscale instead. No plan,
        no seat, no request cap on any of it.
      </SectionHeading>
      <FeatureGrid features={inspectorFeatures} />
    </section>

    <FlowSection />

    <section className="docs-wrap" id="inspector-docs">
      <DocList docs={docs} />
    </section>
  </>
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
          Tap keeps Tailscale routes tailnet-only by default. When you opt into public Cloudflare
          tunnels or Tailscale Funnel URLs, they can be probed within seconds of coming online. Put
          Tap auth in front of the proxy, or use Cloudflare Access and WAF rules before traffic
          reaches your upstream. Those unwanted requests are not abstract: you can watch scanner and
          attack attempts land directly in the Inspector as soon as the tunnel is reachable.
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
  <section className="flow-band" id="flow">
    <SectionHeading kicker="Traffic flow" title="A tunnel path you can reason about.">
      CLI and Aspire both converge on the same inspector host. If a tunnel is enabled, Cloudflare
      reaches cloudflared or Tailscale reaches tailscaled; the connector targets Tap's proxy port,
      and Tap forwards to the local upstream.
    </SectionHeading>
    <div className="entry-flow" aria-label="Tap entry point flow">
      <div className="entry-node">tap CLI</div>
      <div className="entry-node">Aspire AppHost</div>
      <div className="flow-link vertical" />
      <div className="entry-node wide">Tap.Server shared inspector</div>
    </div>
    <div className="flow" aria-label="Tap request flow">
      {["Client", "Cloudflare/Tailscale", "connector/daemon", "Tap proxy", "Upstream"].map(
        (step, index) => (
          <React.Fragment key={step}>
            <div className="flow-node">{step}</div>
            {index < 4 ? <div className="flow-link" /> : null}
          </React.Fragment>
        ),
      )}
    </div>
  </section>
);
