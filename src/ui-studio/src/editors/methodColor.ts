/**
 * The one method → Mantine colour mapping. A verb has to read the same in the request list,
 * the import dialogs, the `.http` editor and the method picker, so there is exactly one table
 * — the previous three near-copies had already drifted (`PATCH` orange in one, grape in the
 * next), which is the failure mode a shared table exists to prevent.
 *
 * The scheme is read-safety, not rainbow: teal = safe/idempotent read, blue = create,
 * orange = replace/modify, red = destroy, gray = protocol-level.
 */
export const METHOD_COLOR: Record<string, string> = {
  GET: 'teal',
  POST: 'blue',
  PUT: 'orange',
  PATCH: 'grape',
  DELETE: 'red',
  HEAD: 'gray',
  OPTIONS: 'gray',
  /** Not an HTTP verb — the WebSocket upgrade, which the request editor offers alongside them. */
  WS: 'violet',
}

/** Mantine colour name for `method`, falling back to gray for anything unrecognised. */
export function methodColor(method: string): string {
  return METHOD_COLOR[method.toUpperCase()] ?? 'gray'
}

/**
 * A CSS colour for `method` as *text* on a plain surface. Mantine's `-text` token is the one
 * tuned for that job — it lightens in the dark scheme, where the `-filled` background token
 * would go too dark to read.
 */
export function methodTextColor(method: string): string {
  return `var(--mantine-color-${methodColor(method)}-text)`
}
