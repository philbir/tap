import { channels, marks, platforms, type PlatformId } from "../components/icons";
import { Callout, CodeBlock, SectionHeading, ShipGroupCard } from "../components/ui";
import {
  checksumsPattern,
  cliArchives,
  desktopTargets,
  detectPlatform,
  installCommands,
  updateManifestPattern,
  type AssetSpec,
  type DesktopTarget,
} from "../data/downloads";
import { findAsset, formatSize, useLatestRelease, type ReleaseState } from "../data/release";
import { shipGroups } from "../data/shipped";
import { href } from "../router";
import { repoUrl } from "../site";

/** Read once: the reader's platform cannot change while the page is open. */
const detected = detectPlatform();

const shipGroup = (id: string) => shipGroups.find((group) => group.id === id) ?? null;

export const DownloadPage = () => {
  const state = useLatestRelease();

  return (
    <>
      <Hero state={state} />
      <Desktop state={state} />
      <CommandLine state={state} />
      <Packages />
      <Images />
      <Verify state={state} />
    </>
  );
};

/**
 * One downloadable file. Until the release answers — and if it never does — the
 * row points at the release page instead, which carries every asset. There is
 * no state in which this renders a dead link.
 */
const AssetLink = ({
  spec,
  state,
  mark,
}: {
  spec: AssetSpec;
  state: ReleaseState;
  mark?: PlatformId;
}) => {
  const asset = state.status === "ready" ? findAsset(state.release, spec.pattern) : null;
  const Mark = mark ? marks[mark].Mark : null;

  return (
    <a
      className={spec.primary ? "dl-asset primary" : "dl-asset"}
      href={asset ? asset.url : `${repoUrl}/releases/latest`}
    >
      {Mark ? (
        <span className="dl-asset-mark" aria-hidden="true">
          <Mark />
        </span>
      ) : null}
      <span className="dl-asset-label">{spec.label}</span>
      {asset ? <span className="dl-asset-meta">{formatSize(asset.size)}</span> : null}
      {state.status === "unavailable" ? <span className="dl-asset-meta">on GitHub</span> : null}
    </a>
  );
};

/** The line under the hero buttons: which build, how big, and where it came from. */
const heroMeta = (state: ReleaseState, assetName: string | null) => {
  if (state.status === "loading") return "Resolving the latest release…";
  if (state.status === "unavailable") {
    return "GitHub did not answer just now — the release page carries every file below.";
  }
  return assetName
    ? `Tap Studio ${state.release.tag} · ${assetName}`
    : `Latest release ${state.release.tag}. Pick a platform below.`;
};

const Hero = ({ state }: { state: ReleaseState }) => {
  const target = desktopTargets.find((entry) => entry.platform === detected);
  const spec = target?.assets.find((asset) => asset.primary) ?? target?.assets[0];
  const asset =
    state.status === "ready" && spec ? findAsset(state.release, spec.pattern) : null;
  const Mark = target ? platforms[target.platform].Mark : null;

  return (
    <section className="download-hero" id="top">
      <span className="kicker">Download</span>
      <h1>Everything Tap publishes, in one place.</h1>
      <p className="lead">
        One release tag, four channels: the <strong>Tap Studio</strong> desktop app, the two
        command-line tools, the NuGet packages they are built from, and a container image per
        product. All of it is free — the whole bill is itemised on the{" "}
        <a href={href("home", "pricing")}>pricing page</a>.
      </p>
      <div className="hero-actions">
        {target && asset && Mark ? (
          <a className="button primary dl-cta" href={asset.url}>
            <Mark />
            Download for {platforms[target.platform].label}
          </a>
        ) : (
          <a className="button primary" href={`${repoUrl}/releases/latest`}>
            Download the latest release
          </a>
        )}
        <a className="button ghost" href={href("download", "desktop")}>
          All platforms
        </a>
      </div>
      <p className="download-meta">{heroMeta(state, asset?.name ?? null)}</p>
      <div className="proof-row">
        <span>$0</span>
        <span>No account</span>
        <span>Open source</span>
        <span>macOS</span>
        <span>Windows</span>
        <span>Linux</span>
      </div>
    </section>
  );
};

const DesktopCard = ({ target, state }: { target: DesktopTarget; state: ReleaseState }) => {
  const { label, Mark } = platforms[target.platform];

  return (
    <article className="dl-card">
      <header className="dl-card-head">
        <span className="dl-card-mark" aria-hidden="true">
          <Mark />
        </span>
        <h3>{label}</h3>
      </header>
      <p>{target.note}</p>
      <div className="dl-assets">
        {target.assets.map((spec) => (
          <AssetLink key={spec.label} spec={spec} state={state} />
        ))}
      </div>
      <p className="dl-detail">{target.detail}</p>
    </article>
  );
};

const Desktop = ({ state }: { state: ReleaseState }) => (
  <section className="section" id="desktop">
    <SectionHeading kicker="Desktop app" title="Tap Studio, as a native application.">
      The workbench UI wrapped around a self-contained backend, so there is no runtime to install
      first. Every link below points straight at a file the latest release carries — the names are
      read from the release itself rather than assembled here, so they never drift from what is
      actually published.
    </SectionHeading>
    <div className="dl-grid">
      {desktopTargets.map((target) => (
        <DesktopCard key={target.platform} target={target} state={state} />
      ))}
    </div>
    <Callout title="It keeps itself current">
      Each release also publishes a signed update manifest, so the app updates in place instead of
      asking you to come back here. Installing it once is the last visit this page needs.
    </Callout>
  </section>
);

const CommandLine = ({ state }: { state: ReleaseState }) => {
  const PromptMark = channels.cli;

  return (
    <section className="section" id="cli">
      <SectionHeading kicker="Command line" title="Two commands, one per product.">
        Each carries the same engine its UI runs on and nothing that serves a UI, so they stay small
        enough for a CI runner. They install side by side and neither needs the other.
      </SectionHeading>

      <article className="dl-tool">
        <header className="dl-tool-head">
          <span className="dl-tool-icon" aria-hidden="true">
            <PromptMark />
          </span>
          <div className="dl-tool-copy">
            <div className="ship-item-head">
              <strong>tap</strong>
              <span className="ship-product">Tap Tunnels</span>
            </div>
            <p>
              Opens a tunnel and an inspector in front of any local port — a quick Cloudflare
              tunnel, your own hostname, or Tailscale.
            </p>
          </div>
          <a className="ship-source" href={href("tunnels", "cli")}>
            CLI reference
          </a>
        </header>

        <div className="code-grid">
          <CodeBlock title="macOS + Linux — self-contained" code={installCommands.cliUnix} />
          <CodeBlock title="Windows — .NET tool" code={installCommands.cliWindows} />
        </div>
        <p className="doc-note">
          The install script fetches the release build; the .NET tool route (
          <code>{installCommands.cliDotnet}</code>) needs the .NET 10 SDK on PATH. Both land the same{" "}
          <code>tap</code> command.
        </p>

        <div className="dl-assets row">
          {cliArchives.map((archive) => (
            <AssetLink
              key={archive.label}
              spec={archive}
              state={state}
              mark={archive.platform}
            />
          ))}
        </div>
        <p className="dl-detail">
          Prefer to unpack it yourself? These are the self-contained archives the install script
          downloads. Windows has none: there it installs as a .NET tool.
        </p>
      </article>

      <article className="dl-tool">
        <header className="dl-tool-head">
          <span className="dl-tool-icon" aria-hidden="true">
            <PromptMark />
          </span>
          <div className="dl-tool-copy">
            <div className="ship-item-head">
              <strong>tap-studio</strong>
              <span className="ship-product">Tap Studio</span>
            </div>
            <p>
              Runs requests, flows, and test sets headlessly, with JUnit, TRX, JSON, and Markdown
              reports. Also carries the agent surface — list, describe, call — and an MCP server
              over stdio.
            </p>
          </div>
          <a className="ship-source" href={href("studio", "studio-cli")}>
            CLI reference
          </a>
        </header>

        <div className="code-grid">
          <CodeBlock title="Install the tool" code={installCommands.studioCliDotnet} />
          <CodeBlock title="In a pipeline" code={installCommands.studioCliCi} />
        </div>
        <p className="doc-note">
          Ships as a .NET global tool on every platform, so it needs the .NET 10 SDK on PATH — which
          a CI runner building this repository already has.
        </p>
      </article>
    </section>
  );
};

const Packages = () => {
  const group = shipGroup("ship-nuget");

  return (
    <section className="section" id="nuget">
      {/* No body copy: the group card below carries the same description, and
          saying it twice in a row reads as a stutter. */}
      <SectionHeading kicker="NuGet packages" title="Install the tools, or build on the engine." />
      {group ? (
        <div className="ship-groups">
          <ShipGroupCard group={group} />
        </div>
      ) : null}
    </section>
  );
};

const Images = () => {
  const group = shipGroup("ship-docker");

  return (
    <section className="section" id="docker">
      <SectionHeading kicker="Docker images" title="Run either product without installing it." />
      {group ? (
        <div className="ship-groups">
          <ShipGroupCard group={group} />
        </div>
      ) : null}
    </section>
  );
};

const Verify = ({ state }: { state: ReleaseState }) => (
  <section className="section" id="verify">
    <SectionHeading kicker="Checksums and updates" title="Check what you downloaded.">
      The binaries job publishes a SHA256SUMS file next to the <code>tap</code> archives, and the
      desktop job publishes a signed update manifest next to the bundles. Both are ordinary release
      assets.
    </SectionHeading>
    <div className="code-grid">
      <CodeBlock title="Verify the tap archives" code={installCommands.verify} />
    </div>
    <div className="dl-assets row section-gap">
      <AssetLink spec={{ label: "SHA256SUMS", pattern: checksumsPattern }} state={state} />
      <AssetLink spec={{ label: "latest.json — update manifest", pattern: updateManifestPattern }} state={state} />
      <a className="dl-asset" href={`${repoUrl}/releases`}>
        <span className="dl-asset-label">All releases and notes</span>
      </a>
    </div>
    <p className="doc-note">
      The macOS bundle is signed and notarised by Apple, so Gatekeeper opens it without a
      right-click. The Windows installers are unsigned today; SmartScreen will warn on first run.
    </p>
  </section>
);
