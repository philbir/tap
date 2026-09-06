import React, { useEffect, useState } from "react";
import { href, useActiveSection } from "../router";
import { pages, productOrder, repoUrl, version, type PageId } from "../site";
import { DownloadArrow, GitHubMark } from "./icons";

export const Shell = ({ page, children }: { page: PageId; children: React.ReactNode }) => {
  const meta = pages[page];
  const active = useActiveSection(
    meta.nav.map((item) => item.id),
    page,
  );
  const [menuOpen, setMenuOpen] = useState(false);

  // The rail is a drawer below 1080px; a route change is the end of navigating.
  useEffect(() => setMenuOpen(false), [page]);

  return (
    <div className="page">
      <a className="skip-link" href="#content">
        Skip to content
      </a>

      <header className="site-header">
        <a
          className={page === "home" ? "brand current" : "brand"}
          href={href("home")}
          aria-current={page === "home" ? "page" : undefined}
        >
          <img src={pages.home.icon} alt="" />
          <span className="brand-copy">
            <strong>Tap Platform</strong>
            <span>Studio + Tunnels</span>
          </span>
          <span className="brand-version">{version}</span>
        </a>

        <nav className="product-switch" aria-label="Products">
          {productOrder.map((id) => {
            const product = pages[id];
            return (
              <a
                key={id}
                className={id === page ? "product-pill current" : "product-pill"}
                href={href(id)}
                aria-current={id === page ? "page" : undefined}
              >
                <img src={product.icon} alt="" />
                <span className="product-pill-text">
                  <strong className="product-pill-long">{product.name}</strong>
                  <strong className="product-pill-short">{product.shortName}</strong>
                  <span>{product.tagline}</span>
                </span>
              </a>
            );
          })}
        </nav>

        <div className="header-actions">
          {/* The two things a reader arrives looking for. Download is a page;
              pricing is a section of the overview, so it keeps its own route. */}
          <a className="header-link" href={href("home", "pricing")}>
            Pricing
          </a>
          <a
            className="button primary header-download"
            href={href("download")}
            aria-label="Download"
            aria-current={page === "download" ? "page" : undefined}
          >
            <DownloadArrow />
            <span className="header-download-text">Download</span>
          </a>
          <a className="button ghost header-cta" href={repoUrl} aria-label="Tap on GitHub">
            <GitHubMark />
            <span className="header-cta-text">GitHub</span>
          </a>
          <button
            type="button"
            className="rail-toggle"
            aria-expanded={menuOpen}
            aria-controls="rail-nav"
            onClick={() => setMenuOpen((open) => !open)}
          >
            {menuOpen ? "Close" : "Contents"}
          </button>
        </div>
      </header>

      <div className="app">
        {/* Below 1080px this is a drawer over the content, so every link in it
            has to close it — including one pointing at the current section. */}
        <aside
          className={menuOpen ? "rail open" : "rail"}
          id="rail-nav"
          onClick={() => setMenuOpen(false)}
        >
          {/* The header actions, repeated for the widths where the header has
              had to drop them. Hidden while the rail is a sidebar. */}
          <nav className="rail-quick" aria-label="Quick links">
            <a href={href("download")}>Download</a>
            <a href={href("home", "pricing")}>Pricing</a>
            <a href={repoUrl}>GitHub</a>
          </nav>
          <nav className="rail-nav" aria-label={meta.navLabel}>
            <span className="rail-label">{meta.navLabel}</span>
            {meta.nav.map((item) => (
              <a
                key={item.id || "top"}
                href={href(page, item.id || undefined)}
                className={item.id === active ? "current" : undefined}
                aria-current={item.id === active ? "true" : undefined}
              >
                {item.label}
              </a>
            ))}
          </nav>
          <p className="rail-note">Free forever. No account, no seats, no telemetry.</p>
        </aside>

        <div className="app-main">
          <main id="content">{children}</main>
          <SiteFooter />
        </div>
      </div>
    </div>
  );
};

const SiteFooter = () => (
  <footer className="site-footer">
    <span>Tap Platform</span>
    <span className="mono">
      {version} · the local-first HTTP workbench · Studio + Tunnels
    </span>
    <a href={href("download")}>Download</a>
    <a href={href("home", "pricing")}>Pricing</a>
    <a href={repoUrl}>GitHub</a>
  </footer>
);
