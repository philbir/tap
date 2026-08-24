import { api } from '../api/client'
import type { EnvCollection, EnvDetail, EnvSpec } from '../api/types'

/**
 * Projects a loaded environment onto the spec the server round-trips. Shared because two
 * editors write env files: the environment editor itself, and the collection editor, whose
 * Environments tab edits the assignment this collection holds in someone else's file.
 */
export function envSpecFromDetail(d: EnvDetail, path: string = d.path): EnvSpec {
  // Flatten the VarSpec map: keep the default value in vars, and collect the names of
  // secret vars in a separate `secrets` array (the wire shape the emitter expects).
  const vars: Record<string, string> = {}
  const secrets: string[] = []
  for (const [k, spec] of Object.entries(d.vars ?? {})) {
    if (spec?.default != null) vars[k] = spec.default
    if (spec?.secret) secrets.push(k)
  }
  return {
    path,
    id: d.id,
    name: d.name,
    vars: Object.keys(vars).length > 0 ? vars : undefined,
    secrets: secrets.length > 0 ? secrets : undefined,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
    collections: d.collections.length > 0 ? d.collections : undefined,
    defaultVariableProvider: d.defaultVariableProvider ?? undefined,
    providerAliases: d.providerAliases && Object.keys(d.providerAliases).length > 0
      ? d.providerAliases
      : undefined,
    strictVariables: d.strictVariables ? true : undefined,
  }
}

/**
 * Rewrites one environment's assignment to `slug` and saves the file.
 *
 * <p>Re-reads the environment first rather than patching a listing row: the caller holds an
 * `EnvSummary`, which carries the assignments but none of the variables, provider bindings, or
 * docs — and a PUT replaces the whole file. Reading, patching, and writing back is what keeps
 * an edit made from the collection side from deleting the rest of the environment.</p>
 *
 * @param binding The assignment to store, or `null` to unassign the collection entirely.
 */
export async function saveEnvAssignment(
  envPath: string,
  slug: string,
  binding: EnvCollection | null,
): Promise<void> {
  const spec = envSpecFromDetail(await api.envDetail(envPath))
  const rest = (spec.collections ?? []).filter((b) => b.collection !== slug)
  const next = binding === null ? rest : [...rest, binding]
  await api.saveEnvSpec({ ...spec, collections: next.length > 0 ? next : undefined })
}
