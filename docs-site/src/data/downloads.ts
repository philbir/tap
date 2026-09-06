import type { PlatformId } from "../components/icons";

/**
 * What the download page offers, and how each row finds itself in a release.
 *
 * Every pattern here matches an asset name the release workflows actually
 * upload — the desktop bundles come from `.github/workflows/desktop.yml`
 * (tauri-action names them) and the `tap` archives from
 * `.github/workflows/release-binaries.yml` (`tap-<version>-<rid>.tar.gz`).
 * Change a name there and the matching pattern has to move with it.
 */
export type AssetSpec = {
  label: string;
  /** Matched against the release asset name. */
  pattern: RegExp;
  /** The one to offer first for this platform. */
  primary?: boolean;
};

export type DesktopTarget = {
  platform: PlatformId;
  /** What the reader needs to know before clicking. */
  note: string;
  /** Requirement or caveat, shown under the note. */
  detail: string;
  assets: AssetSpec[];
};

/** Tap Studio, as a native app. One card per platform, in install-share order. */
export const desktopTargets: DesktopTarget[] = [
  {
    platform: "macos",
    note: "Apple silicon build, signed and notarised.",
    detail: "Intel Macs run it under Rosetta. The .app archive is what the updater consumes.",
    assets: [
      { label: "Disk image (.dmg)", pattern: /_aarch64\.dmg$/, primary: true },
      { label: "App archive (.app.tar.gz)", pattern: /_aarch64\.app\.tar\.gz$/ },
    ],
  },
  {
    platform: "windows",
    note: "x64, as an MSI or an NSIS setup executable.",
    detail: "Either installs the same app; pre-release builds ship the NSIS setup only.",
    assets: [
      { label: "Installer (.msi)", pattern: /_x64_en-US\.msi$/, primary: true },
      { label: "Setup (.exe)", pattern: /_x64-setup\.exe$/ },
    ],
  },
  {
    platform: "linux",
    note: "A Debian package for x64.",
    detail: "Ubuntu, Debian, and derivatives. Install with apt: sudo apt install ./<file>.deb",
    assets: [{ label: "Debian package (.deb)", pattern: /_amd64\.deb$/, primary: true }],
  },
];

export type CliArchive = {
  label: string;
  platform: PlatformId;
  pattern: RegExp;
};

/**
 * Self-contained `tap` builds — no .NET runtime needed. Windows is absent on
 * purpose: it installs as a .NET tool, which is what install.ps1 does.
 */
export const cliArchives: CliArchive[] = [
  { label: "macOS · Apple silicon", platform: "macos", pattern: /^tap-.*-osx-arm64\.tar\.gz$/ },
  { label: "macOS · Intel", platform: "macos", pattern: /^tap-.*-osx-x64\.tar\.gz$/ },
  { label: "Linux · x64", platform: "linux", pattern: /^tap-.*-linux-x64\.tar\.gz$/ },
  { label: "Linux · arm64", platform: "linux", pattern: /^tap-.*-linux-arm64\.tar\.gz$/ },
];

/** The checksum file the binaries job publishes next to those archives. */
export const checksumsPattern = /^SHA256SUMS$/;

/** The signed manifest the desktop updater polls. */
export const updateManifestPattern = /^latest\.json$/;

export const installCommands = {
  cliUnix: `curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh`,
  cliWindows: `irm https://raw.githubusercontent.com/philbir/tap/main/install.ps1 | iex`,
  cliDotnet: `dotnet tool install -g Tap`,
  studioCliDotnet: `dotnet tool install -g Tap.Studio.Cli`,
  studioCliCi: `- run: dotnet tool install --global Tap.Studio.Cli
- run: tap-studio test --workspace ./tap --junit results.xml`,
  verify: `# macOS + Linux, from the folder you downloaded into
shasum -a 256 -c SHA256SUMS --ignore-missing`,
};

/**
 * The platform to offer first. Deliberately a hint rather than a gate: every
 * platform stays one scroll away, and an unrecognised agent simply gets none.
 */
export const detectPlatform = (): PlatformId | null => {
  if (typeof navigator === "undefined") return null;
  const ua = navigator.userAgent;
  // Android and iOS both carry a desktop-looking token; neither has a build.
  if (/Android|iPhone|iPad|iPod/i.test(ua)) return null;
  if (/Mac|Darwin/i.test(ua)) return "macos";
  if (/Win/i.test(ua)) return "windows";
  if (/Linux|X11|CrOS/i.test(ua)) return "linux";
  return null;
};
