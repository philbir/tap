import { useEffect, useState } from "react";
import { legacyAnchors, pages, type PageId } from "./site";

export type Route = {
  page: PageId;
  /** Section to scroll to, or null for the top of the page. */
  section: string | null;
};

const isPageId = (value: string): value is PageId => value in pages;

const parsePath = (path: string): Route | null => {
  const [head, ...rest] = path.replace(/^\/+/, "").split("/");
  if (!isPageId(head)) return null;
  const section = rest.join("/");
  return { page: head, section: section === "" ? null : section };
};

/** `#/inspector/cli` -> { page: "inspector", section: "cli" } */
export const parseHash = (hash: string): Route => {
  const raw = hash.replace(/^#/, "").replace(/^\/+/, "");
  if (raw === "") return { page: "home", section: null };

  // A bare `#section-id` from the old one-page site is looked up once —
  // deliberately not recursively, so a mapping that points at another legacy
  // anchor is a dead link rather than a blown stack.
  const mapped = legacyAnchors[raw];
  return parsePath(raw) ?? (mapped ? parsePath(mapped) : null) ?? { page: "home", section: null };
};

export const href = (page: PageId, section?: string) =>
  section ? `#/${page}/${section}` : page === "home" ? "#/" : `#/${page}`;

/** Offset from the top of the document, independent of the current scroll. */
const documentTop = (el: HTMLElement) => {
  let y = 0;
  let node: HTMLElement | null = el;
  while (node) {
    y += node.offsetTop;
    node = node.offsetParent as HTMLElement | null;
  }
  return y;
};

export const useRoute = (): Route => {
  const [route, setRoute] = useState<Route>(() => parseHash(window.location.hash));

  useEffect(() => {
    // The hash decides where the reader lands, so let it — otherwise a reload
    // restores the old offset first and the page visibly jumps twice.
    if ("scrollRestoration" in history) history.scrollRestoration = "manual";

    const onHashChange = () => setRoute(parseHash(window.location.hash));
    window.addEventListener("hashchange", onHashChange);
    return () => window.removeEventListener("hashchange", onHashChange);
  }, []);

  useEffect(() => {
    document.title = pages[route.page].title;
  }, [route.page]);

  const { page, section } = route;
  useEffect(() => {
    // This runs after React has committed the new page, so the target exists.
    // Deliberately not requestAnimationFrame: a deep link opened in a
    // background tab would sit unscrolled until the tab is focused.
    if (!section) {
      window.scrollTo({ top: 0, behavior: "instant" });
      return;
    }

    // scrollTo with an absolute offset rather than scrollIntoView: the second
    // call below has to be idempotent, and a relative scroll doubles up if the
    // rect it reads is a frame behind.
    const jump = () => {
      const target = document.getElementById(section);
      if (!target) return;
      const margin = Number.parseFloat(getComputedStyle(target).scrollMarginTop) || 0;
      window.scrollTo({ top: Math.max(0, documentTop(target) - margin), behavior: "instant" });
    };
    jump();

    // Screenshots above the target are lazy and have no height until they load,
    // so the first jump can land short. Correct once — but only if the reader
    // has not already scrolled away from where we put them.
    const landed = window.scrollY;
    const timer = window.setTimeout(() => {
      if (Math.abs(window.scrollY - landed) < 4) jump();
    }, 400);
    return () => window.clearTimeout(timer);
  }, [page, section]);

  return route;
};

/**
 * Highlights the section the reader is actually looking at. Deliberately does
 * not rewrite the hash — that would fight the router on every scroll.
 */
export const useActiveSection = (ids: string[], deps: unknown) => {
  const [active, setActive] = useState<string>("");

  useEffect(() => {
    const targets = ids
      .filter((id) => id !== "")
      .map((id) => document.getElementById(id))
      .filter((el): el is HTMLElement => el !== null);

    if (targets.length === 0) {
      setActive("");
      return;
    }

    const visible = new Set<string>();
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) visible.add(entry.target.id);
          else visible.delete(entry.target.id);
        }
        const first = targets.find((el) => visible.has(el.id));
        if (first) setActive(first.id);
      },
      // Bias the band towards the top of the viewport, so the highlighted entry
      // is the heading you just scrolled past rather than one three screens down.
      { rootMargin: "-88px 0px -70% 0px", threshold: 0 },
    );

    for (const target of targets) observer.observe(target);
    const onTop = () => {
      if (window.scrollY < 120) setActive("");
    };
    window.addEventListener("scroll", onTop, { passive: true });
    onTop();

    return () => {
      observer.disconnect();
      window.removeEventListener("scroll", onTop);
    };
    // `ids` is derived from the page, which is what `deps` carries.
  }, [deps]);

  return active;
};
