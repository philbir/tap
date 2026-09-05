import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { Variable, VariableContext, VariableView } from '../api/types'
import { useTapStore } from '../store'

/**
 * Fetches the layered + merged variable view for an editor context. Re-runs whenever the
 * context changes OR the workspace generation ticks (FS save propagates). Components use
 * this to power autocomplete in `VariableInput` and to render the cascade in `VariablesPanel`.
 *
 * Pass `null` to suspend the fetch — useful when the editor doesn't have a context yet.
 */
export function useVariableView(context: VariableContext | null): VariableView | null {
  const generation = useTapStore((s) => s.generation)
  const [view, setView] = useState<VariableView | null>(null)

  // Serialize the context so we don't refetch on every reference-changed object.
  const key = context ? JSON.stringify(context) : null

  useEffect(() => {
    if (!key) { setView(null); return }
    let cancelled = false
    api.variablesView(JSON.parse(key) as VariableContext)
      .then((v) => { if (!cancelled) setView(v) })
      .catch(() => { if (!cancelled) setView(null) })
    return () => { cancelled = true }
  }, [key, generation])

  // Lookup-friendly map: name → Variable. Memoized so consumers don't rebuild it.
  return useMemo(() => view, [view])
}

/** Build a lookup map by name from a view's merged `result`. */
export function variableMap(view: VariableView | null): Map<string, VariableView['result'][number]> {
  const out = new Map<string, VariableView['result'][number]>()
  if (!view) return out
  for (const v of view.result) out.set(v.name, v)
  return out
}

/**
 * One `{{…}}` occurrence in a template. `provider` is the explicit qualifier the user wrote
 * (`{{aspire:demo-api}}` → `"aspire"`), or null for the bare `{{name}}` form.
 */
export interface ParsedToken {
  /** Index of the opening `{` in the source string. */
  start: number
  /** Index one past the closing `}`. */
  end: number
  name: string
  provider: string | null
}

/** Provider qualifiers look like identifiers; anything else is part of the variable name. */
const QUALIFIER = /^[a-zA-Z][a-zA-Z0-9_-]*$/

/**
 * Parse every `{{name}}` / `{{provider:name}}` token in `text`, in source order.
 *
 * Shared so the chip painter, the value preview and the collection chip all agree on where a
 * token starts and what part of it is the qualifier — three copies of this regex is how
 * `{{aspire:demo-api}}` ended up resolving in one place and not the others.
 */
export function parseTokens(text: string): ParsedToken[] {
  const out: ParsedToken[] = []
  const re = /\{\{([^}]*)\}\}/g
  let m: RegExpExecArray | null
  while ((m = re.exec(text)) !== null) {
    const inner = m[1].trim()
    const colon = inner.indexOf(':')
    if (colon > 0 && QUALIFIER.test(inner.slice(0, colon))) {
      out.push({
        start: m.index, end: m.index + m[0].length,
        provider: inner.slice(0, colon),
        name: inner.slice(colon + 1).trim(),
      })
    } else {
      out.push({ start: m.index, end: m.index + m[0].length, name: inner, provider: null })
    }
  }
  return out
}

/**
 * Resolve a parsed token against the view. Explicit `{{provider:name}}` searches that
 * provider's own set, because the merged-by-name map drops the entry a cascade layer
 * shadowed — the same reason the server's `VariableCompiler` searches the layered sets.
 * Bare `{{name}}` falls back to the merged cascade. Returns null when nothing matches.
 *
 * The qualifier may be an env-scoped alias (`view.aliases`) rather than a literal provider
 * name, so `{{kv:secret}}` still hits `kv-dev`'s set when `kv` is bound to it.
 */
export function resolveToken(
  token: ParsedToken,
  view: VariableView | null,
  vars: Map<string, Variable>,
): Variable | null {
  if (token.provider && view) {
    const provider = view.aliases?.[token.provider] ?? token.provider
    for (const set of view.sets) {
      if (set.providerName !== provider) continue
      const hit = set.variables.find((v) => v.name === token.name)
      if (hit) return hit
    }
    return null
  }
  return vars.get(token.name) ?? null
}

/**
 * Substitute every resolvable token in `text` with its value (`***` for a sensitive one),
 * leaving unresolvable tokens as written so the reader can see what is still missing.
 */
export function resolveTemplate(text: string, view: VariableView | null, vars: Map<string, Variable>): string {
  let out = ''
  let cursor = 0
  for (const token of parseTokens(text)) {
    out += text.slice(cursor, token.start)
    const v = resolveToken(token, view, vars)
    out += v ? (v.isSensitive ? '***' : (v.value ?? '')) : text.slice(token.start, token.end)
    cursor = token.end
  }
  return out + text.slice(cursor)
}
