/**
 * Saving something the UI is already holding. Two callers so far — the response panel and
 * the TLS report — and they agree on the boring parts: an anchor click rather than a
 * `window.open` (which a popup blocker eats), and filenames reduced to characters every
 * filesystem accepts.
 */

/** Click a synthetic anchor. `href` may be an object URL, a `data:` URL, or an API path —
 *  the caller owns whichever it made, including revoking an object URL afterwards. */
export function triggerDownload(href: string, filename: string): void {
  const a = document.createElement('a')
  a.href = href
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
}

/** Save text as a file. The object URL is revoked on a delay rather than immediately —
 *  revoking it in the same tick can beat the browser to reading it. */
export function downloadText(text: string, filename: string, mime = 'text/plain;charset=utf-8'): void {
  const url = URL.createObjectURL(new Blob([text], { type: mime }))
  triggerDownload(url, filename)
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/** Reduce a name to filesystem-safe characters: anything outside `[A-Za-z0-9._-]` becomes
 *  `_`, runs collapse, and leading/trailing separators are trimmed. Capped so a verbose
 *  name can't produce an unwieldy filename. */
export function sanitizeFilenamePart(name: string): string {
  return name
    .normalize('NFKD')
    .replace(/[^A-Za-z0-9._-]+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^[_.]+|[_.]+$/g, '')
    .slice(0, 80)
}
