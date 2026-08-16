import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

const resolveVersion = () => {
  if (process.env.GITHUB_REF_TYPE === "tag" && process.env.GITHUB_REF_NAME) {
    return process.env.GITHUB_REF_NAME;
  }

  try {
    return execSync("git describe --tags --abbrev=0", {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    }).trim();
  } catch {
    const pkg = JSON.parse(
      readFileSync(new URL("./package.json", import.meta.url), "utf8"),
    ) as { version?: string };
    return `v${pkg.version ?? "0.0.0"}`;
  }
};

export default defineConfig(() => {
  // Aspire allocates the port and passes it as PORT (same contract src/ui-studio uses).
  // The fallback matches the docs-site entry in .claude/launch.json.
  const port = Number(process.env["PORT"] ?? 5397);

  return {
    base: "./",
    plugins: [react()],
    define: {
      "import.meta.env.VITE_TAP_VERSION": JSON.stringify(resolveVersion()),
      "import.meta.env.VITE_TAP_REPO_URL": JSON.stringify("https://github.com/philbir/tap"),
    },
    server: {
      port,
      strictPort: true,
    },
    build: {
      outDir: "dist",
      emptyOutDir: true,
    },
  };
});
