#!/usr/bin/env node
// Compile the Tap.Studio ASP.NET app into a single self-contained binary using
// `dotnet publish`, then stage it (plus its wwwroot) in the Tauri sidecar slot
// using the target-triple naming convention tauri-action's externalBin reads.
//
// Usage:
//   node src/desktop/scripts/compile-server.mjs              # host triple
//   node src/desktop/scripts/compile-server.mjs aarch64-apple-darwin
//   TAP_VERSION=0.7.3 node src/desktop/scripts/compile-server.mjs   # stamped build
//
// Requires: .NET 10 SDK (matches global.json), Node 20+. No bash required —
// the GitHub Actions Windows runner can invoke this without WSL.

import { spawnSync } from "node:child_process";
import {
  chmodSync,
  cpSync,
  existsSync,
  mkdirSync,
  readdirSync,
  rmSync,
  statSync,
} from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(here, "..");
const repoRoot = resolve(desktopRoot, "..", "..");
const csproj = resolve(repoRoot, "src/backend/Tap.Studio/Tap.Studio.csproj");
const sidecarDir = resolve(desktopRoot, "src-tauri/binaries");

// Tauri target triple → (.NET RID, executable extension)
const TRIPLE_MAP = {
  "aarch64-apple-darwin": { rid: "osx-arm64", ext: "" },
  "x86_64-apple-darwin": { rid: "osx-x64", ext: "" },
  "x86_64-unknown-linux-gnu": { rid: "linux-x64", ext: "" },
  "aarch64-unknown-linux-gnu": { rid: "linux-arm64", ext: "" },
  "x86_64-pc-windows-msvc": { rid: "win-x64", ext: ".exe" },
};

const detectHostTriple = () => {
  // Prefer rustc -vV when available — matches what tauri-action will use.
  const r = spawnSync("rustc", ["-vV"], { encoding: "utf8" });
  if (r.status === 0) {
    const m = r.stdout.match(/^host:\s*(\S+)/m);
    if (m) return m[1];
  }
  if (process.platform === "darwin") {
    return process.arch === "arm64" ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
  }
  if (process.platform === "linux") {
    return process.arch === "arm64" ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
  }
  if (process.platform === "win32") return "x86_64-pc-windows-msvc";
  throw new Error(`Unsupported host: ${process.platform}/${process.arch}`);
};

/**
 * Version stamped into the sidecar assembly, or null to leave it at the
 * Directory.Build.props fallback (`0.1.0-local`).
 *
 * The sidecar *is* the server the desktop app talks to, so its
 * AssemblyInformationalVersion is what `/api/workspace` reports and what the brand
 * menu's version line shows. Without this the shipped app always claimed
 * `0.1.0-local`, because sync-version.mjs only touches the Tauri-side files and
 * runs in the bundle job, long after this one has published the binary.
 *
 * Non-SemVer values are ignored rather than passed through: the desktop workflow
 * hands us `github.ref_name`, which is a branch name on workflow_dispatch and
 * would fail the build as an assembly version.
 */
const SEMVER = /^[0-9]+\.[0-9]+\.[0-9]+([-+].+)?$/;
const resolveVersion = () => {
  const requested = process.env.TAP_VERSION?.trim();
  if (!requested) return null;
  if (SEMVER.test(requested)) return requested;
  console.log(`[tap-studio] ignoring non-SemVer TAP_VERSION=${requested}; using the local fallback`);
  return null;
};

const triple = process.argv[2] ?? detectHostTriple();
const entry = TRIPLE_MAP[triple];
if (!entry) {
  console.error(`Unsupported triple: ${triple}`);
  console.error(`Supported: ${Object.keys(TRIPLE_MAP).join(", ")}`);
  process.exit(2);
}

const version = resolveVersion();
const publishDir = resolve(repoRoot, `artifacts/studio/${entry.rid}`);
const sidecarName = `tap-studio-${triple}${entry.ext}`;
const sidecarPath = resolve(sidecarDir, sidecarName);
const stagedWwwroot = resolve(sidecarDir, "wwwroot");

console.log(`[tap-studio] publishing sidecar`);
console.log(`             triple : ${triple}`);
console.log(`             rid    : ${entry.rid}`);
console.log(`             output : ${sidecarPath}`);
console.log(`             version: ${version ?? "0.1.0-local (unstamped)"}`);

// 1. dotnet publish
mkdirSync(publishDir, { recursive: true });
const dotnet = spawnSync(
  "dotnet",
  [
    "publish",
    csproj,
    "--configuration",
    "Release",
    "--runtime",
    entry.rid,
    "--output",
    publishDir,
    ...(version ? [`-p:Version=${version}`] : []),
  ],
  { stdio: "inherit" },
);
if (dotnet.error) {
  console.error("Could not invoke `dotnet`. Install .NET 10 SDK.");
  process.exit(2);
}
if (dotnet.status !== 0) process.exit(dotnet.status ?? 1);

// 2. Resolve the produced binary. PublishSingleFile drops it at <publishDir>/Tap.Studio[.exe].
const producedExeCandidates = readdirSync(publishDir).filter((f) =>
  f === `Tap.Studio${entry.ext}` || f.startsWith("Tap.Studio.") === false && f.startsWith("Tap.Studio"),
);
const exeName = `Tap.Studio${entry.ext}`;
const producedExe = resolve(publishDir, exeName);
if (!existsSync(producedExe)) {
  console.error(
    `Could not find published binary at ${producedExe}.\n` +
      `Publish dir contents: ${readdirSync(publishDir).join(", ")}`,
  );
  process.exit(1);
}

// 3. Stage to src-tauri/binaries/tap-studio-<triple>[.exe]
mkdirSync(sidecarDir, { recursive: true });
cpSync(producedExe, sidecarPath);
if (process.platform !== "win32") chmodSync(sidecarPath, 0o755);

// 4. Stage wwwroot alongside. Replace any previous copy so a stale UI doesn't
//    survive into the next bundle.
const producedWwwroot = resolve(publishDir, "wwwroot");
if (!existsSync(producedWwwroot) || !statSync(producedWwwroot).isDirectory()) {
  console.error(
    `wwwroot missing in publish output (${producedWwwroot}). ` +
      `The BuildStudioUi target probably did not run — check src/ui-studio/ builds.`,
  );
  process.exit(1);
}
if (existsSync(stagedWwwroot)) rmSync(stagedWwwroot, { recursive: true, force: true });
cpSync(producedWwwroot, stagedWwwroot, { recursive: true });

console.log(`[tap-studio] staged sidecar:  ${sidecarPath}`);
console.log(`[tap-studio] staged wwwroot:  ${stagedWwwroot}`);
