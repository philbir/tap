import { channels, marks } from "../components/icons";
import { FeatureGrid, FlowDiagram, SectionHeading } from "../components/ui";
import type { Feature } from "../data/features";
import { shipGroups } from "../data/shipped";
import { useCases, type UseCaseProduct } from "../data/usecases";
import { href } from "../router";
import { pages, repoUrl, version } from "../site";

const principles: Feature[] = [
  {
    title: "Local-first, no account",
    text: "Everything runs on your machine. No cloud workspace, no sync service, no telemetry, nothing to sign up for. Quick tunnels are free; stable hostnames just need a domain you already own.",
    glyph: "L",
  },
  {
    title: "Plain text, in your repo",
    text: "Studio's workspace is Markdown with YAML frontmatter — requests, collections, auth profiles, and environments all review as ordinary git diffs. Your API knowledge belongs in your repository, not in someone's cloud.",
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

/** The things that tend to live behind a paid tier elsewhere. All included here. */
const included: [string, string][] = [
  ["Unlimited tunnels", "Open as many as you like, for as long as you like."],
  ["Your own hostname", "Bring a domain you already own. No reserved-domain add-on."],
  ["Full request capture", "Every header, body, timing, and status — no retention cap."],
  ["WebSockets and SSE", "Live frame and event timelines, not a truncated preview."],
  ["Replay and QR", "Re-fire any captured request; scan the tunnel onto a phone."],
  ["Proxy auth", "Header, CIDR, country, and OIDC checks in front of a public tunnel."],
  ["OAuth 2.0 / OIDC", "PKCE, client credentials, ROPC, and device code."],
  ["Microsoft Entra", "Corporate identity, not an enterprise upsell."],
  ["Azure CLI + on-behalf-of", "Reuses the sign-in you already have."],
  ["GitHub App / OAuth App", "Installation tokens minted for you."],
  ["AWS Signature V4", "Signed requests out of the box."],
  ["Azure Key Vault", "Read and write secrets through DefaultAzureCredential."],
  ["1Password", "Environments, vaults, and items through the local op CLI."],
  ["Git, natively", "Branch, diff, stage, and commit from inside the app."],
  ["Flows and test sets", "Multi-step exchanges with values carried between steps."],
  ["Tests in CI", "JUnit, TRX, JSON, and Markdown reports from a .NET tool."],
  ["AI assistance", "Runs on the coding CLI you already pay nothing extra for."],
  ["Agents and MCP", "Credential-free agent access to your workspace."],
  ["Desktop app", "macOS, Windows, and Linux, self-updating."],
  ["Aspire integration", "Model tunnels, inspectors, and Studio in your AppHost."],
];

export const HomePage = () => (
  <>
    <Hero />
    <UseCases />
    <Promise />
    <Highlights />
    <ProductSplit />
    <Paywall />
    <Pricing />
    <Included />
    <Ships />
    <Principles />
    <Together />
    <FinalCta />
  </>
);

const Hero = () => (
  <section className="hero" id="top">
    <div className="hero-copy">
      <div className="eyebrow">
        <span className="spark" />
        Free forever · built by developers, for developers
      </div>
      <h1>Tap Platform. Your local-first HTTP workbench.</h1>
      <p className="lead">
        <strong>Tap Platform</strong> brings two focused products into one developer story.
        <strong> Tap Studio</strong> crafts authenticated HTTP requests, and
        <strong> Tap Tunnels</strong> brings traffic to local services with inspection built in.
        Use either product alone or use both to close the loop from request to arrival.
      </p>
      <div className="hero-actions">
        <a className="button primary" href={href("studio")}>
          Explore Tap Studio
        </a>
        <a className="button primary" href={href("tunnels")}>
          Explore Tap Tunnels
        </a>
        <a className="button ghost" href={repoUrl}>
          View code
        </a>
      </div>
      <div className="proof-row">
        <span>{version}</span>
        <span>$0</span>
        <span>No account</span>
        <span>No seats</span>
        <span>No telemetry</span>
        <span>Open source</span>
        <span>CLI</span>
        <span>Aspire</span>
        <span>Desktop app</span>
      </div>
    </div>
    <picture className="hero-visual" aria-label="Tap Platform illustration">
      <img
        src="./tap-platform-hero.png"
        alt="Tap Platform distributing HTTP flow between a request craft studio and an inspected tunnel"
      />
    </picture>
  </section>
);

/** The badge each use case wears. Icons and names come from the pages themselves. */
const productBadge: Record<UseCaseProduct, { label: string; icon: string }> = {
  studio: { label: "Tap Studio", icon: pages.studio.icon ?? "" },
  tunnels: { label: "Tap Tunnels", icon: pages.tunnels.icon ?? "" },
  both: { label: "Studio + Tunnels", icon: pages.home.icon ?? "" },
};

const UseCases = () => (
  <section className="section" id="use-cases">
    <SectionHeading kicker="Use cases" title="Start from the job, not from the product.">
      Two products cover two directions of travel, and most days only one of them is the answer to
      the thing in front of you. Every card below names the job, the product that does it, and the
      pieces involved — follow the badge to the docs for it.
    </SectionHeading>
    <div className="usecase-grid">
      {useCases.map((useCase) => {
        const badge = productBadge[useCase.product];
        return (
          <article className="usecase-card" key={useCase.title}>
            <a className={`product-badge ${useCase.product}`} href={useCase.section}>
              <img src={badge.icon} alt="" />
              {badge.label}
            </a>
            <h3>{useCase.title}</h3>
            <p>{useCase.text}</p>
            <div className="product-tags">
              {useCase.chips.map((chip) => (
                <span key={chip}>{chip}</span>
              ))}
            </div>
          </article>
        );
      })}
    </div>
  </section>
);

const Ships = () => (
  <section className="section" id="ships">
    <SectionHeading kicker="What ships" title="Everything the platform puts on your machine.">
      One release tag, four channels: a desktop app, two command-line tools, the NuGet packages they
      are built from, and a container image per product. Everything below is published by the same
      pipeline from the same commit, so a version means the same thing whichever channel you took it
      from.
    </SectionHeading>
    <div className="ship-groups">
      {shipGroups.map((group) => {
        const ChannelMark = channels[group.channel];
        const SourceMark = group.source ? channels[group.source.channel] : null;
        return (
          <article className="ship-group" key={group.id} id={group.id}>
            <header className="ship-group-head">
              <span className="ship-group-icon" aria-hidden="true">
                <ChannelMark />
              </span>
              <div className="ship-group-copy">
                <h3>{group.title}</h3>
                <p>{group.blurb}</p>
              </div>
              {group.source && SourceMark ? (
                <a className="ship-source" href={group.source.href}>
                  <SourceMark />
                  {group.source.label}
                </a>
              ) : null}
            </header>

            <ul className="ship-list">
              {group.items.map((item) => {
                const ItemMark = item.mark ? marks[item.mark].Mark : null;
                const chips = [...(item.platforms ?? []), ...(item.badges ?? [])];
                return (
                <li className="ship-item" key={item.name}>
                  <div className="ship-item-copy">
                    <div className="ship-item-head">
                      {ItemMark ? (
                        <span className="ship-mark" aria-hidden="true">
                          <ItemMark />
                        </span>
                      ) : null}
                      <strong>{item.name}</strong>
                      {item.product ? <span className="ship-product">{item.product}</span> : null}
                      {item.formats?.map((format) => (
                        <code className="ship-format" key={format}>
                          {format}
                        </code>
                      ))}
                    </div>
                    <p>{item.note}</p>
                    {item.cmd ? <code className="ship-cmd">{item.cmd}</code> : null}
                  </div>
                  <div className="ship-item-side">
                    {chips.length > 0 ? (
                      <div className="plat-row">
                        {chips.map((id) => {
                          const { label, Mark } = marks[id];
                          return (
                            <span className="plat-chip" key={id}>
                              <Mark />
                              {label}
                            </span>
                          );
                        })}
                      </div>
                    ) : null}
                    {item.link ? (
                      <a className="text-link" href={item.link.href}>
                        {item.link.label}
                      </a>
                    ) : null}
                  </div>
                </li>
                );
              })}
            </ul>

            {group.cmds ? (
              <div className="ship-cmds">
                {group.cmds.map((cmd) => (
                  <code className="ship-cmd" key={cmd}>
                    {cmd}
                  </code>
                ))}
              </div>
            ) : null}

            {group.note || group.install ? (
              <footer className="ship-group-foot">
                {group.note ? (
                  <p>
                    {group.noteMark
                      ? (() => {
                          const { Mark } = marks[group.noteMark];
                          return (
                            <span className="ship-note-mark" aria-hidden="true">
                              <Mark />
                            </span>
                          );
                        })()
                      : null}
                    {group.note}
                  </p>
                ) : null}
                {group.install ? (
                  <a className="text-link" href={group.install.href}>
                    {group.install.label}
                  </a>
                ) : null}
              </footer>
            ) : null}
          </article>
        );
      })}
    </div>
  </section>
);

const Promise = () => (
  <section className="promise" id="promise">
    <div className="promise-panel">
      <div className="promise-copy">
        <span className="kicker">The promise</span>
        <h2>Free forever — not free for now.</h2>
        <p>
          Developer tools have a pattern: the free tier is fine until the work gets real, and then
          the thing you actually need is on a paid plan. Tap is built the other way round. The local
          tools stay free, permanently, and no capability is held back to create a reason to upgrade.
          There is no paid tier to graduate to.
        </p>
      </div>
      <ul className="promise-list">
        <li>
          <strong>Every local tool stays free.</strong> That's the promise — not an introductory
          price, not a free tier with an asterisk.
        </li>
        <li>
          <strong>Nothing is held back.</strong> Enterprise identity, secret managers, git, CI, and
          the agent surface are in the same build as everything else.
        </li>
        <li>
          <strong>No account, no seats, no telemetry.</strong> Nothing to sign up for, nothing
          phoning home, nothing counted per developer.
        </li>
        <li>
          <strong>Built by developers, for developers.</strong> The features exist because we needed
          them at work, not because they price well.
        </li>
      </ul>
    </div>
  </section>
);

const highlights: { title: string; text: string; chips: string[] }[] = [
  {
    title: "Standalone, or fully wired into Aspire",
    text: "Both products run two ways. Reach for the tap CLI or the Studio desktop app when you want a tunnel or a request right now, with nothing else running. Or model them in your .NET Aspire AppHost — AddTap attaches an inspector and a tunnel to a resource, AddTapStudio adds the workbench pinned to a workspace folder in the repo — and allocated ports, hostnames, and service discovery are resolved for you at startup.",
    chips: ["tap run", "tap-studio", "AddTap", "AddTapStudio", "Dashboard URLs"],
  },
  {
    title: "No second AI subscription",
    text: "Studio's assistant spawns the coding CLI already installed and signed in on your machine — Claude Code or the GitHub Copilot CLI — so it runs under the subscription you are paying for anyway. There is no AI seat to buy from us, no second API key to manage, and no tokens billed through a middleman. Agents reach the workspace on the same terms, through MCP.",
    chips: ["Claude Code", "GitHub Copilot CLI", "MCP", "Your login"],
  },
  {
    title: "Rides the free services you already have",
    text: "Tap does not run infrastructure and then rent it back to you. Tunnels ride a free Cloudflare account or a free Tailscale tailnet. Secrets come out of Azure Key Vault or 1Password through the sign-in you already use — DefaultAzureCredential, the op CLI — and identity through Microsoft Entra, Azure CLI, or the GitHub App you already registered.",
    chips: ["Cloudflare", "Tailscale", "Azure Key Vault", "Microsoft Entra", "1Password"],
  },
];

const Highlights = () => (
  <section className="section" id="highlights">
    <SectionHeading
      kicker="What that buys you"
      title="Free, because it runs on what you already have."
    >
      Tap is not a free tier propped up by a paid one. It is a local tool that drives your own
      accounts, your own machine, and the subscriptions already on your invoice — which is why
      there is nothing left for us to charge for.
    </SectionHeading>
    <div className="highlight-grid">
      {highlights.map((item, index) => (
        <article className="highlight-card" key={item.title}>
          <span className="highlight-index">{String(index + 1).padStart(2, "0")}</span>
          <h3>{item.title}</h3>
          <p>{item.text}</p>
          <div className="product-tags">
            {item.chips.map((chip) => (
              <span key={chip}>{chip}</span>
            ))}
          </div>
        </article>
      ))}
    </div>
  </section>
);

const ProductSplit = () => (
  <section className="section" id="products">
    <SectionHeading kicker="The product family" title="One HTTP workbench. Two focused products.">
      Sometimes the internet needs to reach your laptop and you need to see exactly what arrived.
      Other times you are the client, and you need to compose a request, authenticate properly, send
      it, and keep it somewhere your team can find next month. Tap Platform gives each job a clear
      home—Studio for outbound work and Tunnels for inbound traffic.
    </SectionHeading>
    <div className="product-split">
      <article className="product-card">
        <span className="product-index">01</span>
        <img className="product-card-icon" src="./tap-studio-icon.svg" alt="" />
        <h3>Tap Studio</h3>
        <p>
          The request and auth credential crafter: full request composition, real authentication
          flows, multi-step flows and test sets that also run headlessly in CI, an AI assistant
          running on your own machine, and a workspace that lives in your git repo as Markdown.
          Ships as a desktop app.
        </p>
        <div className="product-tags">
          <span>Desktop</span>
          <span>OAuth 2.0</span>
          <span>Entra</span>
          <span>AWS SigV4</span>
          <span>GraphQL</span>
          <span>Key Vault</span>
          <span>1Password</span>
          <span>Flows</span>
          <span>Tests</span>
          <span>CI</span>
          <span>AI</span>
        </div>
        <a className="button primary" href={href("studio")}>
          Open the Studio docs
        </a>
      </article>
      <article className="product-card">
        <span className="product-index">02</span>
        <img className="product-card-icon" src="./tap-tunnels-icon.svg" alt="" />
        <h3>Tap Tunnels</h3>
        <p>
          Tunnels with inspection built in: give localhost a URL through Cloudflare or Tailscale
          and see every request, response, SSE event, and WebSocket frame on the way through. Runs
          as the <code>tap</code> CLI or as .NET Aspire resources.
        </p>
        <div className="product-tags">
          <span>CLI</span>
          <span>Aspire</span>
          <span>Cloudflare</span>
          <span>Tailscale</span>
          <span>Replay</span>
          <span>QR</span>
        </div>
        <a className="button primary" href={href("tunnels")}>
          Open the Tunnels docs
        </a>
      </article>
    </div>
  </section>
);

const Paywall = () => (
  <section className="section" id="paywall">
    <SectionHeading kicker="What it costs" title="The subscription you don't have to buy.">
      Tunnelling and inspection are usually sold as a monthly seat — another $19 a month, per
      developer, for the version that does stable hostnames and keeps your request history. Tap
      takes a different route: it drives infrastructure you can already have for free, and the client
      that drives it is free too.
    </SectionHeading>
    <div className="compare-grid">
      <article className="compare-card">
        <span className="compare-label">The usual arrangement</span>
        <ul className="compare-list minus">
          <li>A monthly seat, per developer, billed annually</li>
          <li>Reserved domains and stable URLs on the paid tier</li>
          <li>Request inspection capped, or history trimmed</li>
          <li>SSO and audit priced as "Enterprise — contact us"</li>
          <li>Collections and history living in someone else's cloud</li>
          <li>An AI add-on with its own price per seat</li>
        </ul>
      </article>
      <article className="compare-card highlight">
        <span className="compare-label">With Tap</span>
        <ul className="compare-list plus">
          <li>
            <strong>$0.</strong> The tools are free and open source
          </li>
          <li>
            A <strong>free Cloudflare account</strong> — or a free Tailscale tailnet — carries the
            tunnel
          </li>
          <li>Your own domain, if you want stable hostnames (a .dev is about $10 a year)</li>
          <li>Unlimited tunnels, unlimited capture, nothing trimmed</li>
          <li>Entra ID, Key Vault, and 1Password in the same build as everything else</li>
          <li>The AI assistant runs on the coding CLI you already have</li>
        </ul>
      </article>
    </div>
    <div className="callout">
      <strong>The whole bill, itemised</strong>
      <p>
        Tap: free. Cloudflare Tunnel or Tailscale: free tier is enough for development. A domain, if
        you want a hostname that doesn't change: roughly $10 a year, and you keep it. That is the
        complete list — there is no line item from us.
      </p>
    </div>
  </section>
);

const Pricing = () => (
  <section className="section" id="pricing">
    <SectionHeading kicker="Pricing" title="One plan. Everything is in it.">
      This is the entire pricing page. There is no second column, no feature matrix with grey ticks,
      and no "starting from".
    </SectionHeading>
    <div className="plan-grid">
      <article className="plan-card">
        <span className="plan-name">Tap</span>
        <div className="plan-price">
          <span className="plan-amount">$0</span>
          <span className="plan-period">forever</span>
        </div>
        <p className="plan-blurb">
          Both products, every feature, on every machine you own. Open source under the repository's
          licence.
        </p>
        <ul className="plan-list">
          <li>Tap Tunnels, complete—with inspection built in</li>
          <li>Tap Studio, complete</li>
          <li>Unlimited developers — there are no seats to count</li>
          <li>Enterprise identity and secret managers included</li>
          <li>CI runs on as many pipelines as you like</li>
        </ul>
        <a className="button primary" href={`${repoUrl}/releases/latest`}>
          Download
        </a>
      </article>
      <article className="plan-card muted">
        <span className="plan-name">What we don't do</span>
        <ul className="plan-list minus">
          <li>No paid tier that unlocks the good parts</li>
          <li>No per-seat billing, ever</li>
          <li>No request or retention caps</li>
          <li>No "contact sales" for SSO</li>
          <li>No account, no cloud workspace, no telemetry</li>
          <li>No selling your API traffic back to you as insight</li>
        </ul>
        <p className="plan-note">
          The local tools stay free. If something ever runs on our infrastructure rather than yours,
          it will be a separate, clearly-labelled thing — and it will not take features out of this
          column.
        </p>
      </article>
    </div>
  </section>
);

const Included = () => (
  <section className="flow-band" id="included">
    <SectionHeading kicker="What's included" title="The list is just… the feature list.">
      Everything below ships in the build you can download right now. It is worth writing out,
      because several of these are the exact lines that usually appear under a heading called
      Enterprise.
    </SectionHeading>
    <ul className="included-grid">
      {included.map(([name, note]) => (
        <li className="included-item" key={name}>
          <span className="included-check" aria-hidden="true">
            ✓
          </span>
          <span>
            <strong>{name}</strong>
            <span className="included-note">{note}</span>
          </span>
        </li>
      ))}
    </ul>
  </section>
);

const Principles = () => (
  <section className="section" id="principles">
    <SectionHeading kicker="Shared foundations" title="Different jobs, same rules.">
      The two products share a philosophy more than they share code. Everything is local, everything
      is inspectable, and nothing quietly leaves your machine.
    </SectionHeading>
    <FeatureGrid features={principles} />
  </section>
);

const Together = () => (
  <section className="flow-band" id="together">
    <SectionHeading kicker="Used together" title="Craft in Studio. Send through Tunnels. Inspect what arrived.">
      They are separate products with separate installs, and each is useful on its own. Together they
      close the loop: Studio sends a request through Tap Tunnels' Inspector, so you can
      compare what you meant to send with what actually arrived.
    </SectionHeading>
    <FlowDiagram
      label="Tap Platform request round trip"
      steps={["Studio crafts", "Auth resolved", "Tunnels routes", "Inspector captures", "Your service"]}
    />
    <div className="mode-grid section-gap">
      <article className="mode-card">
        <strong>Debug a webhook end to end</strong>
        <p>
          Point the provider at your tunnel, watch the delivery land in the Inspector, then rebuild
          it as a Studio request with assertions so the next regression is a failing test rather than
          a surprise.
        </p>
      </article>
      <article className="mode-card">
        <strong>One AppHost, both tools</strong>
        <p>
          <code>AddTap</code> attaches an inspector and a tunnel to a resource;{" "}
          <code>AddTapStudio</code> adds the workbench pinned to a workspace folder in the repo. The
          whole loop is part of the app model.
        </p>
      </article>
    </div>
  </section>
);

const FinalCta = () => (
  <section className="final-cta" id="start">
    <div className="final-cta-panel">
      <div>
        <span className="kicker">Get started</span>
        <h2>Install it, use all of it, pay nothing.</h2>
        <p>
          Both halves are free, local, and open source. Install the CLI, grab the desktop app, or read
          the code — and if the free version turns out to be missing the feature you needed, tell us,
          because that is a bug rather than a business model.
        </p>
      </div>
      <div className="final-cta-actions">
        <a className="button primary" href={`${repoUrl}/releases/latest`}>
          Download
        </a>
        <a className="button ghost" href={repoUrl}>
          View on GitHub
        </a>
      </div>
    </div>
  </section>
);
