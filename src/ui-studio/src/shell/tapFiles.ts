/**
 * Filename conventions for Tap workspace files — the client-side mirror of `KindResolver`
 * on the server.
 *
 * Since 0.7.0 there are two extension families: canonical `.tap` and the legacy `.md` it
 * replaced. Both are *read*; only `.tap` is ever *created*. That split is why this module
 * exists at all — the suffix rule used to be copy-pasted as a regex literal at a dozen call
 * sites, which had already drifted (one copy never learned about `.flow`/`.test`, another
 * lost its case-insensitive flag). Doubling each of those for a second family would have
 * doubled the drift, so they all route through here now.
 */

export const TAP_EXTENSION = '.tap'
export const LEGACY_EXTENSION = '.md'

/** The portable request format (Visual Studio, REST Client, JetBrains, httpyac). Read and sent
 *  but never rewritten by Tap, and outside both families above — a `.http` file has no kind
 *  infix because the format is not Tap's. */
export const HTTP_EXTENSION = '.http'

export const MANIFEST_FILE = `workspace${TAP_EXTENSION}`
export const COLLECTION_FILE = `_collection${TAP_EXTENSION}`
export const LEGACY_COLLECTION_FILE = `_collection${LEGACY_EXTENSION}`

/** The kind-bearing infix of a suffixed workspace file: `orders.req.tap` → `req`. */
export type TapFileKind = 'req' | 'auth' | 'env' | 'flow' | 'test'

/** Every suffixed family member, in both spellings. Anchored, so it only ever strips a
 *  trailing suffix and leaves dots inside the stem (`v2.orders.req.tap`) alone. */
const SUFFIX_RE = /\.(req|auth|env|flow|test)\.(?:tap|md)$/i

/** `orders.req.tap` → `orders`. Names that aren't workspace files come back untouched, so
 *  this is safe to apply to any filename. */
export function stripTapSuffix(fileName: string): string {
  return fileName.replace(SUFFIX_RE, '')
}

/** The display label for a workspace path: its basename without the Tap suffix. */
export function labelForPath(path: string): string {
  const name = path.split('/').pop() ?? path
  return stripTapSuffix(name)
}

/** The canonical filename for a file being created. New files are always canonical — the
 *  legacy spelling is read-only, and renaming existing files is `tap-studio migrate`'s job. */
export function fileNameFor(kind: TapFileKind, slug: string): string {
  return `${slug}.${kind}${TAP_EXTENSION}`
}

/** The filename for a new portable `.http` file. */
export function httpFileNameFor(slug: string): string {
  return `${slug}${HTTP_EXTENSION}`
}

/** True for a portable `.http` file. Mirrors `KindResolver.IsHttpFileName`. */
export function isHttpFileName(fileName: string): boolean {
  return fileName.toLowerCase().endsWith(HTTP_EXTENSION)
}

/** Splits a possibly-fragmented path (`orders.http#get-order`) into its file part and
 *  fragment. Mirrors `HttpFragment.Split`; safe on paths that carry no fragment. */
export function splitHttpFragment(path: string): { path: string; fragment: string | null } {
  const cut = path.indexOf('#')
  return cut < 0
    ? { path, fragment: null }
    : { path: path.slice(0, cut), fragment: path.slice(cut + 1) }
}

/** True when this path addresses a request that lives inside a `.http` file. Such a request
 *  has no spec form on disk — the raw file is the document, and Tap never rewrites it — so
 *  structured editors can show one but must not try to save one. */
export function isHttpBackedRequest(path: string): boolean {
  return isHttpFileName(splitHttpFragment(path).path)
}

const LEGACY_SUFFIX_RE = /\.(req|auth|env|flow|test)\.md$/i

/**
 * The path this file has after `tap-studio migrate`. Paths that are already canonical, or that
 * aren't workspace files, come back unchanged.
 *
 * Used to heal state we persisted against pre-migration paths — open tabs and the selected
 * environment — so migrating a workspace doesn't greet the user with a screen of "not found".
 */
export function toCanonicalPath(path: string): string {
  const cut = path.lastIndexOf('/')
  const dir = cut < 0 ? '' : path.slice(0, cut + 1)
  const name = cut < 0 ? path : path.slice(cut + 1)
  const lower = name.toLowerCase()

  if (lower === `tap${LEGACY_EXTENSION}`) return dir + MANIFEST_FILE
  if (lower === LEGACY_COLLECTION_FILE) return dir + COLLECTION_FILE
  return LEGACY_SUFFIX_RE.test(name) ? dir + name.replace(LEGACY_SUFFIX_RE, `.$1${TAP_EXTENSION}`) : path
}

/** True for a collection metadata file in either family. */
export function isCollectionFile(fileName: string): boolean {
  const lower = fileName.toLowerCase()
  return lower === COLLECTION_FILE || lower === LEGACY_COLLECTION_FILE
}

/** Where a collection's metadata file could live inside `dirPath`, canonical first. Callers
 *  that need the one that actually exists should probe these in order. */
export function collectionFileCandidates(dirPath: string): string[] {
  return [`${dirPath}/${COLLECTION_FILE}`, `${dirPath}/${LEGACY_COLLECTION_FILE}`]
}

/** The collection file inside `dirPath` that `exists` accepts, falling back to the canonical
 *  name so a not-yet-created collection still gets a sensible target path. */
export function resolveCollectionFile(dirPath: string, exists: (path: string) => boolean): string {
  const candidates = collectionFileCandidates(dirPath)
  return candidates.find(exists) ?? candidates[0]
}
