import type { ChannelId, MarkId, PlatformId } from "../components/icons";
import { href } from "../router";
import { repoUrl } from "../site";

const nuget = (id: string) => `https://www.nuget.org/packages/${id}`;

export type ShipLink = { label: string; href: string };

export type ShipItem = {
  name: string;
  /** Which product the artifact belongs to, when the name doesn't say it. */
  product?: string;
  note: string;
  /** Leading mark, for a row that already names its platform or its host. */
  mark?: MarkId;
  /** Platform chips, in reading order. */
  platforms?: PlatformId[];
  /** Chips beyond the platforms — what else runs this artifact for you. */
  badges?: MarkId[];
  /** File extensions the artifact arrives as. */
  formats?: string[];
  /** One command that gets it. */
  cmd?: string;
  link?: ShipLink;
};

export type ShipGroup = {
  id: string;
  channel: ChannelId;
  title: string;
  blurb: string;
  /** Where the artifacts are published, when the whole group shares one place. */
  source?: ShipLink & { channel: ChannelId };
  /** How to install it, in prose docs. */
  install?: ShipLink;
  items: ShipItem[];
  /** Commands that belong to the group rather than to one item. */
  cmds?: string[];
  note?: string;
  /** Mark shown against the note, when the note is about one particular host. */
  noteMark?: MarkId;
};

/**
 * Everything the platform publishes, grouped by the shape it arrives in. All of
 * it is built from one tag by the workflows in `.github/workflows/`, so a
 * version means the same commit whichever channel you took it from.
 */
export const shipGroups: ShipGroup[] = [
  {
    id: "ship-desktop",
    channel: "desktop",
    title: "Desktop app",
    blurb:
      "Tap Studio as a native application — the workbench UI wrapped around a self-contained backend, so there is no runtime to install first. Attached to every release.",
    source: { channel: "github", label: "GitHub Releases", href: `${repoUrl}/releases/latest` },
    install: { label: "Read the install guide", href: href("studio", "studio-install") },
    items: [
      {
        name: "macOS",
        mark: "macos",
        formats: [".dmg", ".app"],
        note: "Apple Silicon build, signed and notarised. Intel Macs run it under Rosetta.",
        link: { label: "Download", href: href("download", "desktop") },
      },
      {
        name: "Windows",
        mark: "windows",
        formats: [".msi", "setup.exe"],
        note: "An MSI installer and an NSIS setup executable, x64. Pre-release builds ship NSIS only.",
        link: { label: "Download", href: href("download", "desktop") },
      },
      {
        name: "Linux",
        mark: "linux",
        formats: [".deb"],
        note: "A Debian package for x64 — Ubuntu, Debian, and derivatives.",
        link: { label: "Download", href: href("download", "desktop") },
      },
    ],
    note: "Each release also publishes a signed update manifest, so the app keeps itself current instead of asking you to come back here.",
  },
  {
    id: "ship-cli",
    channel: "cli",
    title: "Command-line tools",
    blurb:
      "Two commands, one per product, installable side by side. Each carries the same engine its UI runs on and nothing that serves a UI, so they stay small enough for a CI runner.",
    source: { channel: "github", label: "Release binaries", href: `${repoUrl}/releases/latest` },
    items: [
      {
        name: "tap",
        product: "Tap Tunnels",
        platforms: ["macos", "linux", "windows"],
        note: "Opens a tunnel and an inspector in front of any local port — a quick Cloudflare tunnel, your own hostname, or Tailscale. Self-contained archives for macOS and Linux (x64 and arm64) are attached to each release, with a SHA256SUMS file; on Windows it installs as a .NET tool.",
        cmd: "dotnet tool install -g Tap",
        link: { label: "CLI reference", href: href("tunnels", "cli") },
      },
      {
        name: "tap-studio",
        product: "Tap Studio",
        platforms: ["macos", "windows", "linux"],
        note: "Runs requests, flows, and test sets headlessly, with JUnit, TRX, JSON, and Markdown reports. Also carries the agent surface — list, describe, call — and an MCP server over stdio.",
        cmd: "dotnet tool install -g Tap.Studio.Cli",
        link: { label: "CLI reference", href: href("studio", "studio-cli") },
      },
    ],
    cmds: [
      "curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh   # macOS + Linux, self-contained",
      "irm https://raw.githubusercontent.com/philbir/tap/main/install.ps1 | iex        # Windows",
    ],
    note: "The install scripts fetch the release build of tap; the .NET tool route needs the .NET 10 SDK on PATH. Both land the same command.",
  },
  {
    id: "ship-nuget",
    channel: "nuget",
    title: "NuGet packages",
    blurb:
      "Six packages on NuGet.org: the two CLIs as .NET global tools, the Aspire integration you call from your own AppHost, and the libraries the rest of the platform is built from. MIT, with symbols and Source Link.",
    items: [
      {
        name: "Tap",
        product: "Tunnels CLI",
        note: "The Tunnels CLI as a .NET global tool. Installs the tap command.",
        cmd: "dotnet tool install -g Tap",
        link: { label: "nuget.org", href: nuget("Tap") },
      },
      {
        name: "Tap.Studio.Cli",
        product: "Studio CLI",
        note: "The Studio CLI as a .NET global tool. Installs tap-studio for local runs, pipelines, and MCP.",
        cmd: "dotnet tool install -g Tap.Studio.Cli",
        link: { label: "nuget.org", href: nuget("Tap.Studio.Cli") },
      },
      {
        name: "Tap.Aspire.Hosting",
        mark: "aspire",
        product: "Aspire",
        note: "AppHost integration for both products: AddTap attaches an inspector and a tunnel to a resource, AddTapStudio adds the workbench pinned to a workspace folder in the repo — or AddTapStudioContainer runs the image instead, with nothing to build.",
        cmd: "dotnet add package Tap.Aspire.Hosting",
        link: { label: "nuget.org", href: nuget("Tap.Aspire.Hosting") },
      },
      {
        name: "Tap.Execution",
        product: "Library",
        note: "The execution engine as a library — HTTP transport, auth, assertion evaluation, value extraction, and the run engine behind both the Studio UI and the CLI.",
        cmd: "dotnet add package Tap.Execution",
        link: { label: "nuget.org", href: nuget("Tap.Execution") },
      },
      {
        name: "Tap.Workspace",
        product: "Library",
        note: "Parses, validates, and renders the .tap workspace format — the source of truth for what a request, collection, auth profile, or environment means on disk.",
        cmd: "dotnet add package Tap.Workspace",
        link: { label: "nuget.org", href: nuget("Tap.Workspace") },
      },
      {
        name: "Tap.Internals",
        product: "Internal",
        note: "Shared internals for the CLI and the Aspire integration. Pulled in for you — install Tap.Aspire.Hosting rather than referencing this directly.",
        link: { label: "nuget.org", href: nuget("Tap.Internals") },
      },
    ],
    note: "Package versions track the release tag, so Tap.Aspire.Hosting 0.7.0 and the 0.7.0 desktop app are the same build of the same commit.",
  },
  {
    id: "ship-docker",
    channel: "docker",
    title: "Docker images",
    blurb:
      "One image per product, on the official ASP.NET Core runtime. Both are multi-arch (linux/amd64 and linux/arm64), tagged latest for stable releases alongside the exact version and major.minor.",
    source: {
      channel: "github",
      label: "GitHub Container Registry",
      href: "https://github.com/philbir/tap?tab=packages",
    },
    items: [
      {
        name: "ghcr.io/philbir/tap",
        product: "Tap Tunnels",
        platforms: ["linux"],
        badges: ["aspire"],
        note: "The inspector: capture middleware, reverse proxy, the live SSE feed, and the bundled UI. Serves the UI on port 5298 and the capture proxy on 5299; every Inspector__* setting is an environment variable. AddTapContainer runs it as a resource of your AppHost — Aspire pulls it as part of starting up.",
        cmd: "docker pull ghcr.io/philbir/tap:latest",
        link: { label: "Run it from Aspire", href: href("tunnels", "entry-points") },
      },
      {
        name: "ghcr.io/philbir/tap-studio",
        product: "Tap Studio",
        platforms: ["linux"],
        badges: ["aspire"],
        note: "The whole workbench — UI, REST API, and execution engine — with nothing to install first. Listens on 8080 and takes the workspace as a bind mount at /workspace, because Studio writes the files it opens and they belong in your repository. AddTapStudioContainer is the AppHost call, and it pulls the same way. What a container cannot do is reach your machine: no AI coding CLI to spawn, no browser to open for an interactive sign-in, no op or az on PATH.",
        cmd: "docker pull ghcr.io/philbir/tap-studio:latest",
        link: { label: "Run it from Aspire", href: href("studio", "studio-aspire") },
      },
    ],
    noteMark: "aspire",
    note: "So `docker pull` is only for running one by hand: in an AppHost both are container resources, and Aspire fetches whichever tag you named. The default pull policy is Missing, which means a locally-built tag of the same name wins — the way to try a change to an image itself.",
  },
];
