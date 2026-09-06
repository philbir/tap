import React from "react";
import type { Feature } from "../data/features";
import type { ShipGroup } from "../data/shipped";
import { channels, marks } from "./icons";

export type DocSection = {
  id: string;
  eyebrow: string;
  title: string;
  body: string;
  content?: () => React.ReactNode;
};

export const SectionHeading = ({
  kicker,
  title,
  children,
  className,
}: {
  kicker: string;
  title: string;
  children?: React.ReactNode;
  className?: string;
}) => (
  <div className={className ? `section-heading ${className}` : "section-heading"}>
    <span className="kicker">{kicker}</span>
    <h2>{title}</h2>
    {children ? <p>{children}</p> : null}
  </div>
);

export const FeatureGrid = ({ features }: { features: Feature[] }) => (
  <div className="feature-grid">
    {features.map((feature) => (
      <article className="feature-card" key={feature.title}>
        <span className="glyph">{feature.glyph}</span>
        <h3>{feature.title}</h3>
        <p>{feature.text}</p>
      </article>
    ))}
  </div>
);

export const ModeGrid = ({ items, gap }: { items: string[][]; gap?: boolean }) => (
  <div className={gap ? "mode-grid section-gap" : "mode-grid"}>
    {items.map(([name, body]) => (
      <article className="mode-card" key={name}>
        <strong>{name}</strong>
        <p>{body}</p>
      </article>
    ))}
  </div>
);

export const ConfigTable = ({
  label,
  rows,
  gap,
}: {
  label: string;
  rows: string[][];
  gap?: boolean;
}) => (
  <div className={gap ? "config-table section-gap" : "config-table"} role="table" aria-label={label}>
    {rows.map(([key, value]) => (
      <div className="config-row" role="row" key={key}>
        <code role="cell">{key}</code>
        <span role="cell">{value}</span>
      </div>
    ))}
  </div>
);

export const Callout = ({
  title,
  tone,
  children,
}: {
  title: string;
  tone?: "warning" | "danger";
  children: React.ReactNode;
}) => (
  <div
    className={tone ? `callout ${tone}-callout` : "callout"}
    role={tone === "danger" ? "alert" : undefined}
  >
    <strong>{title}</strong>
    <p>{children}</p>
  </div>
);

export const DocHeader = ({ doc }: { doc: DocSection }) => (
  <div className="doc-header">
    <span className="kicker">{doc.eyebrow}</span>
    <h2>{doc.title}</h2>
    <p>{doc.body}</p>
  </div>
);

export const DocBlock = ({ doc }: { doc: DocSection }) => (
  <section className="doc-block" id={doc.id}>
    <DocHeader doc={doc} />
    {doc.content ? doc.content() : null}
  </section>
);

export const DocList = ({ docs }: { docs: DocSection[] }) => (
  <div className="docs-content">
    {docs.map((doc) => (
      <DocBlock doc={doc} key={doc.id} />
    ))}
  </div>
);

export const ProviderDetail = ({
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
    {settings.length > 0 ? <ConfigTable label={`${name} settings`} rows={settings} /> : null}
    {children}
  </article>
);

export const MiniPanel = ({ title, children }: { title: string; children: React.ReactNode }) => (
  <article className="mini-panel">
    <h3>{title}</h3>
    <p>{children}</p>
  </article>
);

export const CodeBlock = ({ title, code }: { title: string; code: string }) => (
  <figure className="code-block">
    <figcaption>{title}</figcaption>
    <pre>
      <code>{code}</code>
    </pre>
  </figure>
);

export const Screenshot = ({ src, alt, caption }: { src: string; alt: string; caption: string }) => (
  <figure className="screenshot">
    <img src={src} alt={alt} loading="lazy" />
    <figcaption>{caption}</figcaption>
  </figure>
);

export const FlowDiagram = ({ steps, label }: { steps: string[]; label: string }) => (
  <div className="flow" aria-label={label}>
    {steps.map((step, index) => (
      <React.Fragment key={step}>
        <div className="flow-node">{step}</div>
        {index < steps.length - 1 ? <div className="flow-link" /> : null}
      </React.Fragment>
    ))}
  </div>
);

export const PublicTunnelInlineWarning = ({ provider }: { provider: string }) => (
  <Callout tone="danger" title={`${provider} public exposure needs auth or edge rules`}>
    Avoid running public tunnels without Tap auth, Cloudflare Access, or Cloudflare WAF rules.
    Automated probes often arrive seconds after the hostname is live, and every attempt is visible
    in the Inspector request log.
  </Callout>
);

/**
 * One published channel — the desktop bundles, the CLIs, the NuGet packages, or
 * the images — rendered from its `ShipGroup`. The home page lists every group;
 * the download page pulls single groups out of the same array, so the two never
 * describe the same artifact differently.
 */
export const ShipGroupCard = ({ group }: { group: ShipGroup }) => {
  const ChannelMark = channels[group.channel];
  const SourceMark = group.source ? channels[group.source.channel] : null;
  const NoteMark = group.noteMark ? marks[group.noteMark].Mark : null;

  return (
    <article className="ship-group" id={group.id}>
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
              {NoteMark ? (
                <span className="ship-note-mark" aria-hidden="true">
                  <NoteMark />
                </span>
              ) : null}
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
};
