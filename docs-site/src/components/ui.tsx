import React from "react";
import type { Feature } from "../data/features";

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
