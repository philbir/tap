export const version = import.meta.env.VITE_TAP_VERSION ?? "v0.1.0";
export const repoUrl = import.meta.env.VITE_TAP_REPO_URL ?? "https://github.com/philbir/tap";

export type PageId = "home" | "tunnels" | "studio" | "download";

export type NavItem = {
  /** Section id on the page. Empty means the top of the page. */
  id: string;
  label: string;
};

export type PageMeta = {
  id: PageId;
  /** Label in the product switcher. */
  name: string;
  /** Switcher label once the header runs out of room. */
  shortName: string;
  /** One line under the switcher label. */
  tagline: string;
  /** Product icon in the header switcher. Absent on the overview page. */
  icon?: string;
  /** Document title for this page. */
  title: string;
  /** Heading above the section list in the rail. */
  navLabel: string;
  nav: NavItem[];
};

/** The two product pages, in the order the header switcher lists them. */
export const productOrder: PageId[] = ["studio", "tunnels"];

export const pages: Record<PageId, PageMeta> = {
  home: {
    id: "home",
    name: "Tap Platform",
    shortName: "Platform",
    tagline: "Studio + Tunnels",
    icon: "./tap-platform-icon.svg",
    title: "Tap Platform — the local-first HTTP workbench",
    navLabel: "Platform guide",
    nav: [
      { id: "", label: "Overview" },
      { id: "use-cases", label: "Use cases" },
      { id: "promise", label: "The promise" },
      { id: "highlights", label: "What that buys you" },
      { id: "products", label: "Studio + Tunnels" },
      { id: "paywall", label: "The subscription you skip" },
      { id: "pricing", label: "One plan" },
      { id: "included", label: "What's included" },
      { id: "ships", label: "What ships" },
      { id: "principles", label: "Shared foundations" },
      { id: "together", label: "Used together" },
      { id: "start", label: "Get started" },
    ],
  },
  tunnels: {
    id: "tunnels",
    name: "Tap Tunnels",
    shortName: "Tunnels",
    tagline: "Tunnels with inspection",
    icon: "./tap-tunnels-icon.svg",
    title: "Tap Tunnels — public URLs for localhost with inspection built in",
    navLabel: "Tunnels docs",
    nav: [
      { id: "", label: "Overview" },
      { id: "inspector-features", label: "What it does" },
      { id: "flow", label: "Traffic flow" },
      { id: "use-cases", label: "Use cases" },
      { id: "quickstart", label: "Quick start" },
      { id: "entry-points", label: "CLI or Aspire" },
      { id: "cli", label: "CLI reference" },
      { id: "cloudflare", label: "Cloudflare" },
      { id: "tailscale", label: "Tailscale" },
      { id: "auth", label: "Proxy authentication" },
      { id: "modes", label: "Tunnel modes" },
      { id: "architecture", label: "Architecture" },
      { id: "config", label: "Configuration" },
    ],
  },
  studio: {
    id: "studio",
    name: "Tap Studio",
    shortName: "Studio",
    tagline: "Requests + auth credentials",
    icon: "./tap-studio-icon.svg",
    title: "Tap Studio — the HTTP request and auth credential crafter",
    navLabel: "Studio docs",
    nav: [
      { id: "", label: "Overview" },
      { id: "studio-features", label: "What it does" },
      { id: "studio-compose", label: "The request composer" },
      { id: "studio-auth", label: "Authentication flows" },
      { id: "studio-testing", label: "Flows and test sets" },
      { id: "studio-cli", label: "The tap-studio CLI" },
      { id: "studio-agents", label: "Agents and MCP" },
      { id: "studio-ai", label: "AI assistance" },
      { id: "studio-workspace", label: "The workspace format" },
      { id: "studio-variables", label: "Variables and secrets" },
      { id: "studio-providers", label: "Variable providers" },
      { id: "studio-aspire", label: "Run from your AppHost" },
      { id: "studio-install", label: "Get Tap Studio" },
    ],
  },
  download: {
    id: "download",
    name: "Download",
    shortName: "Download",
    tagline: "Every channel",
    title: "Download Tap — desktop app, CLI, NuGet, and containers",
    navLabel: "Download",
    nav: [
      { id: "", label: "Overview" },
      { id: "desktop", label: "Desktop app" },
      { id: "cli", label: "Command line" },
      { id: "nuget", label: "NuGet packages" },
      { id: "docker", label: "Docker images" },
      { id: "verify", label: "Checksums and updates" },
    ],
  },
};

export const pageOrder: PageId[] = ["home", "studio", "tunnels", "download"];

/**
 * Anchors from the previous one-page site. Everything used to live under a bare
 * `#section-id`; those links are in READMEs, release notes, and other people's
 * bookmarks, so they are mapped onto the page that now owns the section.
 */
export const legacyAnchors: Record<string, string> = {
  top: "/home",
  products: "/home/products",
  principles: "/home/principles",
  inspector: "/tunnels",
  tunnels: "/tunnels",
  "inspector-features": "/tunnels/inspector-features",
  "use-cases": "/tunnels/use-cases",
  quickstart: "/tunnels/quickstart",
  "entry-points": "/tunnels/entry-points",
  cli: "/tunnels/cli",
  cloudflare: "/tunnels/cloudflare",
  tailscale: "/tunnels/tailscale",
  auth: "/tunnels/auth",
  modes: "/tunnels/modes",
  architecture: "/tunnels/architecture",
  config: "/tunnels/config",
  "inspector-docs": "/tunnels",
  studio: "/studio",
  "studio-features": "/studio/studio-features",
  "studio-compose": "/studio/studio-compose",
  "studio-auth": "/studio/studio-auth",
  "studio-testing": "/studio/studio-testing",
  "studio-cli": "/studio/studio-cli",
  "studio-ai": "/studio/studio-ai",
  "studio-workspace": "/studio/studio-workspace",
  "studio-variables": "/studio/studio-variables",
  "studio-providers": "/studio/studio-providers",
  "studio-aspire": "/studio/studio-aspire",
  "studio-install": "/studio/studio-install",
  "studio-docs": "/studio",
  "provider-env": "/studio/provider-env",
  "provider-file": "/studio/provider-file",
  "provider-azkv": "/studio/provider-azkv",
  "provider-1password": "/studio/provider-1password",
  "provider-system": "/studio/provider-system",
};
