import type { KvRow } from './KvTable'

/**
 * Variable-row helpers used by every editor that surfaces a vars table. The two shapes are:
 *   - Workspace / collection / stage: `vars: Record<string, VarSpec>` (rich VarSpec map).
 *   - Env / request override: `vars: Record<string, string>` + `secrets: string[]` (flat).
 *
 * Both ultimately render as `KvRow[]`, but each editor used to spell out its own
 * conversion. Centralizing here keeps the secret-toggle and key-ordering behavior
 * identical across editors — fixing it once fixes it everywhere.
 */

/** Convert a flat `(vars, secrets)` pair into rows for `KvTable`. */
export function flatVarsToRows(
  vars: Record<string, string> | undefined | null,
  secrets: readonly string[] | undefined | null,
): KvRow[] {
  const secretSet = new Set(secrets ?? [])
  return Object.entries(vars ?? {}).map(([key, value]) => ({
    key,
    value,
    secret: secretSet.has(key),
  }))
}

/** Inverse of {@link flatVarsToRows}. Drops empty-key rows. Returns `undefined` instead of
 *  empty maps/arrays so the JSON shape matches a fresh detail-fetch (which omits empties). */
export function rowsToFlatVars(rows: readonly KvRow[]): {
  vars: Record<string, string> | undefined
  secrets: string[] | undefined
} {
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const r of rows) {
    if (!r.key) continue
    vars[r.key] = r.value
    if (r.secret) secrets.push(r.key)
  }
  return {
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
  }
}

/**
 * Shape used by the workspace/collection/stage editors — `VarSpec` carries default +
 * secret + description + required + example. We expose the `default` in the row and the
 * `secret` flag via the inline toggle; the other VarSpec fields are preserved verbatim on
 * write-back so they survive an edit even when not shown.
 */
export interface VarSpecLike {
  default: string | null
  description: string | null
  required: boolean
  example: string | null
  secret: boolean
}

export function specVarsToRows(
  vars: Record<string, VarSpecLike> | undefined | null,
): KvRow[] {
  return Object.entries(vars ?? {}).map(([key, spec]) => ({
    key,
    value: spec?.default ?? '',
    secret: !!spec?.secret,
  }))
}

/** Inverse of {@link specVarsToRows}. Preserves description/required/example from the
 *  original map when a row's key still matches. */
export function rowsToSpecVars(
  rows: readonly KvRow[],
  previous: Record<string, VarSpecLike> | undefined | null,
): Record<string, VarSpecLike> {
  const next: Record<string, VarSpecLike> = {}
  for (const r of rows) {
    if (!r.key) continue
    const prev = previous?.[r.key]
    next[r.key] = {
      default: r.value === '' ? null : r.value,
      description: prev?.description ?? null,
      required: prev?.required === true,
      example: prev?.example ?? null,
      secret: r.secret === true,
    }
  }
  return next
}
