#!/usr/bin/env node
// Sync the desktop-bundle version across the four files tauri-action reads
// from. Used by .github/workflows/desktop.yml right before bundling so the
// asset filenames (Tap-Studio_X.Y.Z_*) always match the git tag — even when a
// release PR forgets to bump these files. Run locally:
//   node apps/tap-studio-desktop/scripts/sync-version.mjs 0.2.0
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const version = process.argv[2];
if (!version || !/^[0-9]+\.[0-9]+\.[0-9]+([-+].+)?$/.test(version)) {
  console.error(`Usage: sync-version.mjs <X.Y.Z>\nGot: ${JSON.stringify(version)}`);
  process.exit(1);
}

const here = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(here, "..");
const tauriDir = resolve(desktopRoot, "src-tauri");

const updates = [];

// 1. apps/tap-studio-desktop/package.json
// 2. apps/tap-studio-desktop/src-tauri/tauri.conf.json
for (const file of [
  resolve(desktopRoot, "package.json"),
  resolve(tauriDir, "tauri.conf.json"),
]) {
  const text = readFileSync(file, "utf8");
  const doc = JSON.parse(text);
  const before = doc.version;
  if (before === version) {
    updates.push({ file, before, after: version, changed: false });
    continue;
  }
  doc.version = version;
  writeFileSync(file, JSON.stringify(doc, null, 2) + "\n");
  updates.push({ file, before, after: version, changed: true });
}

// 3. apps/tap-studio-desktop/src-tauri/Cargo.toml — patch only the [package]
//    block's version line. Anchor on `[package]` so we don't touch deps.
const cargoTomlPath = resolve(tauriDir, "Cargo.toml");
{
  const text = readFileSync(cargoTomlPath, "utf8");
  let inPackage = false;
  let changed = false;
  let before = null;
  const out = text.split("\n").map((line) => {
    if (/^\[package\]/.test(line)) {
      inPackage = true;
      return line;
    }
    if (inPackage && /^\[/.test(line)) inPackage = false;
    if (inPackage) {
      const m = line.match(/^version\s*=\s*"([^"]+)"\s*$/);
      if (m) {
        before = m[1];
        if (m[1] === version) return line;
        changed = true;
        return `version = "${version}"`;
      }
    }
    return line;
  });
  if (changed) writeFileSync(cargoTomlPath, out.join("\n"));
  updates.push({ file: cargoTomlPath, before, after: version, changed });
}

// 4. apps/tap-studio-desktop/src-tauri/Cargo.lock — patch ONLY the
//    tap-studio-desktop entry. Each `[[package]]` block has `name = ...`
//    followed (after blanks) by `version = ...`.
const cargoLockPath = resolve(tauriDir, "Cargo.lock");
{
  const text = readFileSync(cargoLockPath, "utf8");
  const lines = text.split("\n");
  let changed = false;
  let before = null;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].trim() !== 'name = "tap-studio-desktop"') continue;
    for (let j = i + 1; j < lines.length; j++) {
      if (lines[j].trim() === "") continue;
      const m = lines[j].match(/^version\s*=\s*"([^"]+)"\s*$/);
      if (m) {
        before = m[1];
        if (m[1] !== version) {
          lines[j] = `version = "${version}"`;
          changed = true;
        }
      }
      break;
    }
    break;
  }
  if (changed) writeFileSync(cargoLockPath, lines.join("\n"));
  updates.push({ file: cargoLockPath, before, after: version, changed });
}

for (const u of updates) {
  const rel = u.file.replace(resolve(desktopRoot, "..", ".."), ".");
  if (u.changed) {
    console.log(`updated  ${rel}: ${u.before} → ${u.after}`);
  } else {
    console.log(`unchanged ${rel}: ${u.before}`);
  }
}
