export const version = import.meta.env.VITE_TAP_VERSION ?? "v0.1.0";
export const repoUrl = import.meta.env.VITE_TAP_REPO_URL ?? "https://github.com/philbir/tap";

export type PageId = "home" | "inspector" | "studio";

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
export const productOrder: PageId[] = ["inspector", "studio"];

export const pages: Record<PageId, PageMeta> = {
  home: {
    id: "home",
    name: "Overview",
    shortName: "Overview",
    tagline: "The whole story",
    title: "Tap — free, local-first HTTP tunnels, inspection, and a request workbench",
    navLabel: "On this page",
    nav: [
      { id: "", label: "Overview" },
      { id: "promise", label: "The promise" },
      { id: "highlights", label: "What that buys you" },
      { id: "products", label: "Two products" },
      { id: "paywall", label: "The subscription you skip" },
      { id: "pricing", label: "One plan" },
      { id: "included", label: "What's included" },
      { id: "principles", label: "Shared foundations" },
      { id: "together", label: "Used together" },
      { id: "start", label: "Get started" },
    ],
  },
  inspector: {
    id: "inspector",
    name: "Tunnel + Inspector",
    shortName: "Inspector",
    tagline: "Traffic arriving at your machine",
    icon: "./tap-mark.svg",
    title: "Tap Tunnel + Inspector — public URLs for localhost, with every request captured",
    navLabel: "Inspector docs",
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
    tagline: "Crafting, sending, and testing",
    icon: "./tap-studio-icon.svg",
    title: "Tap Studio — an HTTP workbench with real auth, tests in CI, and a git-native workspace",
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
};

export const pageOrder: PageId[] = ["home", "inspector", "studio"];

/**
 * Anchors from the previous one-page site. Everything used to live under a bare
 * `#section-id`; those links are in READMEs, release notes, and other people's
 * bookmarks, so they are mapped onto the page that now owns the section.
 */
export const legacyAnchors: Record<string, string> = {
  top: "/home",
  products: "/home/products",
  principles: "/home/principles",
  inspector: "/inspector",
  "inspector-features": "/inspector/inspector-features",
  "use-cases": "/inspector/use-cases",
  quickstart: "/inspector/quickstart",
  "entry-points": "/inspector/entry-points",
  cli: "/inspector/cli",
  cloudflare: "/inspector/cloudflare",
  tailscale: "/inspector/tailscale",
  auth: "/inspector/auth",
  modes: "/inspector/modes",
  architecture: "/inspector/architecture",
  config: "/inspector/config",
  "inspector-docs": "/inspector",
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
