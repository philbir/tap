import { useEffect, useState } from "react";
import { repoUrl } from "../site";

/**
 * The latest published release, read at runtime so every download link points at
 * an asset the release actually carries.
 *
 * The asset names are versioned (`Tap.Studio_0.7.6_aarch64.dmg`), so GitHub's
 * `/releases/latest/download/<name>` shortcut cannot address them and a baked-in
 * URL would silently serve the version the docs were built from rather than the
 * current one. The release workflow derives its own download table the same way,
 * for the same reason: derive names, never invent them.
 */
export type ReleaseAsset = {
  name: string;
  /** Direct download URL — GitHub's `browser_download_url`. */
  url: string;
  /** Size in bytes, as GitHub reports it. */
  size: number;
};

export type LatestRelease = {
  tag: string;
  htmlUrl: string;
  assets: ReleaseAsset[];
};

export type ReleaseState =
  | { status: "loading" }
  | { status: "ready"; release: LatestRelease }
  | { status: "unavailable" };

/** `https://github.com/philbir/tap` -> the REST endpoint for its latest release. */
const latestReleaseEndpoint = () => {
  const repoPath = new URL(repoUrl).pathname.replace(/^\/+|\/+$/g, "");
  return `https://api.github.com/repos/${repoPath}/releases/latest`;
};

type ApiRelease = {
  tag_name?: string;
  html_url?: string;
  assets?: { name?: string; browser_download_url?: string; size?: number }[];
};

const REQUEST_TIMEOUT_MS = 8000;

const fetchLatestRelease = async (): Promise<ReleaseState> => {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  try {
    const response = await fetch(latestReleaseEndpoint(), {
      headers: { Accept: "application/vnd.github+json" },
      signal: controller.signal,
    });
    if (!response.ok) return { status: "unavailable" };

    // `/releases/latest` skips drafts and pre-releases, which is exactly the set
    // a download page should offer.
    const body = (await response.json()) as ApiRelease;
    const assets = (body.assets ?? []).flatMap<ReleaseAsset>((asset) =>
      asset.name && asset.browser_download_url
        ? [{ name: asset.name, url: asset.browser_download_url, size: asset.size ?? 0 }]
        : [],
    );
    if (!body.tag_name || assets.length === 0) return { status: "unavailable" };

    return {
      status: "ready",
      release: {
        tag: body.tag_name,
        htmlUrl: body.html_url ?? `${repoUrl}/releases/latest`,
        assets,
      },
    };
  } catch {
    return { status: "unavailable" };
  } finally {
    window.clearTimeout(timer);
  }
};

// One request per session. A success is cached for the life of the page so
// navigating away and back is free; a failure is not, so a reader who arrives
// during a rate-limit window gets another go on the next visit.
let cached: LatestRelease | null = null;
let inFlight: Promise<ReleaseState> | null = null;

export const useLatestRelease = (): ReleaseState => {
  const [state, setState] = useState<ReleaseState>(() =>
    cached ? { status: "ready", release: cached } : { status: "loading" },
  );

  useEffect(() => {
    if (cached) return;

    let alive = true;
    const request = (inFlight ??= fetchLatestRelease());
    void request.then((next) => {
      if (next.status === "ready") cached = next.release;
      else inFlight = null;
      if (alive) setState(next);
    });

    return () => {
      alive = false;
    };
  }, []);

  return state;
};

export const findAsset = (release: LatestRelease, pattern: RegExp) =>
  release.assets.find((asset) => pattern.test(asset.name)) ?? null;

/** Byte counts as GitHub reports them; MB is the only unit a download needs. */
export const formatSize = (bytes: number) =>
  bytes >= 1024 * 1024
    ? `${(bytes / (1024 * 1024)).toFixed(1)} MB`
    : `${Math.max(1, Math.round(bytes / 1024))} KB`;
