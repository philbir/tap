import { ActionIcon, Box, Paper, Portal, Stack, Text, Tooltip } from '@mantine/core'
import { IconBraces, IconEye, IconEyeOff, IconFlask, IconKey, IconRefresh } from '@tabler/icons-react'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import type { Variable, VariableContext } from '../api/types'
import { useVariableView, variableMap } from '../workspace/useVariables'
import { passwordManagerOptOut, randomFieldName } from './passwordManagerOptOut'
import styles from './VariableInput.module.css'

/**
 * Variable-aware text input. Mirrors dreamr's approach: a raw <input> renders the user's
 * text exactly as typed, while a behind-the-input overlay paints colored boxes (chips) on
 * top of each `{{name}}` token. Chip positions are computed in pixels by measuring the
 * substring before the token in a hidden mirror span — the input's font/padding match the
 * mirror so the chip lines up under the token character-for-character.
 *
 * Triggering autocomplete works from the input's `onChange` handler using
 * `e.target.selectionStart`. No more Mantine `Popover` wrapping a TextInput — that approach
 * fought caret tracking + native caret rendering. The dropdown is portaled with a fixed
 * position computed from the input's bounding rect + the caret mirror's width.
 */
export interface VariableInputProps {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  /** Default `true` — use the monospace font so chip widths are stable across glyphs. */
  monospace?: boolean
  /** Context drives the autocomplete suggestion source. When omitted, no autocomplete. */
  context?: VariableContext | null
  /** Click handler for the flask icon — wire to open the Variables panel. */
  onOpenVariables?: () => void
  /** Disable the input. */
  disabled?: boolean
  /** Visual size. `sm` (default) matches Mantine TextInput sm; `xs` is for table cells. */
  size?: 'xs' | 'sm' | 'md'
  /** Static, full-value suggestions shown when the input is focused and the user is NOT
   *  in the middle of typing a `{{var}}` token. Picking one replaces the entire value. */
  staticSuggestions?: string[]
}

// Matches the single interpolation form:
//   {{var}}             — looked up against the scope cascade (any provider)
//   {{provider:name}}   — explicit provider lookup (env, file, azkv, …)
// Sensitivity is a per-variable property (resolved via the view), not a syntax marker —
// there is no `${{…}}` form. Secret-bearing names are painted with the secret chip
// whether the user wrote `{{API_KEY}}` or `{{env:API_KEY}}`.
const TOKEN_REGEX = /\{\{([^}]*)\}\}/g

export function VariableInput({
  value, onChange, placeholder, monospace = true, context, onOpenVariables, disabled, size = 'sm',
  staticSuggestions,
}: VariableInputProps) {
  const inputRef = useRef<HTMLInputElement | null>(null)
  const measureRef = useRef<HTMLSpanElement | null>(null)
  const caretMirrorRef = useRef<HTMLSpanElement | null>(null)
  const wrapperRef = useRef<HTMLDivElement | null>(null)

  const view = useVariableView(context ?? null)
  const vars = useMemo(() => variableMap(view), [view])
  // Distinct provider names visible in this context — sourced from the layered sets so
  // an unused-by-cascade provider still surfaces in `{{<prefix>` autocomplete. The default
  // provider is hoisted to the front so it's the natural top suggestion.
  const providers = useMemo(() => {
    if (!view) return [] as string[]
    const seen = new Set<string>()
    for (const s of view.sets) if (s.providerName) seen.add(s.providerName)
    // Env-scoped aliases (e.g. `confix` -> `confix-d`) are valid `{{alias:name}}`
    // qualifiers too — surface them as their own prefix hints alongside real provider names.
    for (const alias of Object.keys(view.aliases ?? {})) seen.add(alias)
    const all = Array.from(seen).sort()
    const def = view.defaultProvider
    if (def && all.includes(def)) return [def, ...all.filter((p) => p !== def)]
    return all
  }, [view])
  const defaultProvider = view?.defaultProvider ?? null
  // While the view is still loading we don't know yet whether each {{name}} resolves —
  // painting them red would falsely accuse them. Gray pending chips signal "working on it"
  // until the view lands. `context` is the gate: if there's no context, autocomplete is
  // disabled by design and we never fetch a view, so any unknown token IS truly unknown.
  const isLoading = !!context && view === null
  // -- Chip positions (recomputed on value or variable-set change) -----------------------
  const [boxes, setBoxes] = useState<ChipBox[]>([])
  useLayoutEffect(() => {
    if (!measureRef.current) { setBoxes([]); return }
    const tokens = findTokens(value)
    const out: ChipBox[] = []
    for (const t of tokens) {
      measureRef.current.textContent = value.slice(0, t.start)
      const left = measureRef.current.offsetWidth
      measureRef.current.textContent = value.slice(t.start, t.end)
      const width = measureRef.current.offsetWidth

      const known = resolveToken(t, view, vars)
      let kind: ChipBox['kind']
      if (known) kind = known.isSensitive ? 'secret' : 'valid'
      else if (isLoading) kind = 'pending'
      else kind = 'unknown'
      // For bare `{{name}}` chips, the chip tooltip should also hint at the default
      // provider (since that's where the lookup hits first at execute time).
      const provider = known?.providerName ?? t.provider ?? (t.provider === null ? defaultProvider : null) ?? undefined
      out.push({
        left, width,
        kind,
        value: known?.isSensitive ? '***' : (known?.value ?? null),
        scope: known?.scope,
        provider: provider ?? undefined,
        isDefaultProvider: !t.provider && !!provider && provider === defaultProvider,
        label: t.provider ? `${t.provider}:${t.name}` : t.name,
      })
    }
    setBoxes(out)
  }, [value, view, vars, isLoading])

  // -- Preview mode ----------------------------------------------------------------------
  const [previewOn, setPreviewOn] = useState(false)
  const renderedValue = previewOn ? renderWithValues(value, vars) : value

  // -- Autocomplete state ----------------------------------------------------------------
  // Two distinct dropdown modes share the same popup: `variable` shows {{var}} matches
  // from the workspace cascade; `static` shows free-form value suggestions (e.g. common
  // values for a known header). Variable mode wins when the user is mid-`{{…` token.
  const [showSuggestions, setShowSuggestions] = useState(false)
  const [suggestionMode, setSuggestionMode] = useState<'variable' | 'static'>('variable')
  const [suggestions, setSuggestions] = useState<VariableSuggestion[]>([])
  const [staticMatches, setStaticMatches] = useState<string[]>([])
  const [highlightedIdx, setHighlightedIdx] = useState(0)
  const [dropdownPos, setDropdownPos] = useState<{ left: number; top: number } | null>(null)
  const [scrollLeft, setScrollLeft] = useState(0)
  const suggestionMouseDownRef = useRef(false)

  const activeCount = suggestionMode === 'variable' ? suggestions.length : staticMatches.length

  // Static suggestions are only meaningful when the user has NOT already inserted a
  // `{{var}}` token — replacing the whole value with a static pick would clobber the
  // template. Restrict to plain-text values.
  const canShowStatic = !!staticSuggestions && staticSuggestions.length > 0 && !/\{\{/.test(value)

  function filterStatic(q: string): string[] {
    if (!staticSuggestions) return []
    if (!q) return staticSuggestions.slice(0, 8)
    const lc = q.toLowerCase()
    return staticSuggestions.filter((s) => s.toLowerCase().includes(lc)).slice(0, 8)
  }

  function recomputeDropdownPos() {
    if (!inputRef.current || !caretMirrorRef.current || !wrapperRef.current) return
    const rect = inputRef.current.getBoundingClientRect()
    const caretX = caretMirrorRef.current.offsetWidth
    setDropdownPos({
      left: rect.left + caretX + SIZE_PADDING_LEFT[size] - scrollLeft,
      top: rect.bottom + 4,
    })
  }

  function handleInput(e: React.ChangeEvent<HTMLInputElement>) {
    const val = e.target.value
    onChange(val)
    const caret = e.target.selectionStart ?? val.length
    const before = val.slice(0, caret)
    const match = /\{\{([^}]*)$/.exec(before)
    if (match && context && view) {
      const matches = buildVariableSuggestions(match[1], view, providers)
      setSuggestionMode('variable')
      setSuggestions(matches)
      setHighlightedIdx(0)
      setShowSuggestions(matches.length > 0)
      return
    }
    // No `{{` in flight → consider static value suggestions (e.g. header values).
    if (staticSuggestions && !/\{\{/.test(val)) {
      const matches = filterStatic(val)
      setSuggestionMode('static')
      setStaticMatches(matches)
      setHighlightedIdx(0)
      setShowSuggestions(matches.length > 0)
      return
    }
    setShowSuggestions(false)
  }

  function handleFocus() {
    if (!canShowStatic || !inputRef.current) return
    const caret = inputRef.current.selectionStart ?? value.length
    const before = value.slice(0, caret)
    // Don't pop static suggestions while the user is mid-`{{var}}` typing.
    if (/\{\{[^}]*$/.test(before)) return
    const matches = filterStatic(value)
    if (matches.length === 0) return
    setSuggestionMode('static')
    setStaticMatches(matches)
    setHighlightedIdx(0)
    setShowSuggestions(true)
  }

  // The caret-mirror tracks the substring before the caret so the dropdown sits under it.
  // We update it whenever the value or selection changes.
  useLayoutEffect(() => {
    if (!caretMirrorRef.current || !inputRef.current) return
    const caret = inputRef.current.selectionStart ?? value.length
    caretMirrorRef.current.textContent = value.slice(0, caret)
    if (showSuggestions) recomputeDropdownPos()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, showSuggestions, scrollLeft])

  function applySuggestion(s: VariableSuggestion) {
    if (!inputRef.current) return
    const caret = inputRef.current.selectionStart ?? value.length
    const before = value.slice(0, caret)
    const after = value.slice(caret)
    const match = /\{\{[^}]*$/.exec(before)
    if (!match) return
    if (s.kind === 'provider') {
      // Provider prefix is a partial token — keep the dropdown open so the user can
      // pick the variable next. Insert `{{provider:` (no closing braces) and re-run the
      // input handler to recompute suggestions for the now-narrower prefix.
      const next = before.slice(0, match.index) + `{{${s.name}:` + after
      const pos = match.index + `{{${s.name}:`.length
      onChange(next)
      queueMicrotask(() => {
        const el = inputRef.current
        if (!el) return
        el.focus(); el.setSelectionRange(pos, pos)
        // Re-trigger autocomplete now that the prefix is provider-qualified.
        const synthetic = new Event('input', { bubbles: true })
        el.dispatchEvent(synthetic)
      })
      return
    }
    // Replace the open `{{prefix` with `{{name}}` (or `{{provider:name}}`). Strip a
    // leading `}}` from `after` to avoid `{{name}}}}` when the user already had one.
    const cleanedAfter = after.replace(/^\}\}/, '')
    const token = s.provider ? `{{${s.provider}:${s.name}}}` : `{{${s.name}}}`
    const next = before.slice(0, match.index) + token + cleanedAfter
    const pos = match.index + token.length
    onChange(next)
    setShowSuggestions(false)
    queueMicrotask(() => {
      const el = inputRef.current
      if (el) { el.focus(); el.setSelectionRange(pos, pos) }
    })
  }

  function pickStatic(s: string) {
    onChange(s)
    setShowSuggestions(false)
    queueMicrotask(() => {
      const el = inputRef.current
      if (el) { el.focus(); el.setSelectionRange(s.length, s.length) }
    })
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (!showSuggestions || activeCount === 0) return
    if (e.key === 'ArrowDown') { e.preventDefault(); setHighlightedIdx((i) => (i + 1) % activeCount) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlightedIdx((i) => (i - 1 + activeCount) % activeCount) }
    else if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault()
      if (suggestionMode === 'variable') {
        const pick = suggestions[highlightedIdx]
        if (pick) applySuggestion(pick)
      } else {
        const pick = staticMatches[highlightedIdx]
        if (pick) pickStatic(pick)
      }
    } else if (e.key === 'Escape') {
      e.preventDefault()
      setShowSuggestions(false)
    }
  }

  // Close dropdown when clicking outside the input + portal.
  useEffect(() => {
    if (!showSuggestions) return
    function onDocMouseDown(e: MouseEvent) {
      const t = e.target as Node | null
      if (!t) return
      if (inputRef.current?.contains(t)) return
      const portalNode = document.getElementById(`vi-portal-${idRef.current}`)
      if (portalNode?.contains(t)) return
      setShowSuggestions(false)
    }
    document.addEventListener('mousedown', onDocMouseDown)
    return () => document.removeEventListener('mousedown', onDocMouseDown)
  }, [showSuggestions])

  // Stable id for the portal node so click-outside can find it.
  const idRef = useRef(Math.random().toString(36).slice(2))
  // Stable randomized `name` for the input so password managers can't pattern-match it.
  const fieldName = useRef(randomFieldName())

  const hasToken = TOKEN_REGEX.test(value)
  // Reset lastIndex on the shared regex so subsequent .test() calls aren't stateful.
  TOKEN_REGEX.lastIndex = 0

  const inputStyle: CSSProperties = {
    fontFamily: monospace ? 'var(--mono)' : undefined,
  }

  return (
    <div ref={wrapperRef} className={styles.wrapper} data-mono={monospace} data-size={size}>
      {/* Chip overlay — absolutely-positioned colored boxes sitting BEHIND the input. */}
      {!previewOn && (
        <div className={styles.overlay} aria-hidden="true">
          {boxes.map((b, i) => (
            <Tooltip
              key={i}
              label={
                b.kind === 'pending'
                  ? 'Resolving variables…'
                  : b.scope
                    ? `${b.value ?? '(no value)'} — ${b.scope}${b.provider ? ` (${b.provider}${b.isDefaultProvider ? ', default' : ''})` : ''}`
                    : `Unknown variable: {{${b.label}}}`
              }
              withArrow position="top"
              disabled={!b.value && b.kind !== 'unknown' && b.kind !== 'secret' && b.kind !== 'pending' && !b.scope}
            >
              <span
                className={`${styles.chip} ${b.kind === 'valid' ? styles.chipValid : b.kind === 'secret' ? styles.chipSecret : b.kind === 'pending' ? styles.chipPending : styles.chipUnknown}`}
                style={{ left: b.left + SIZE_PADDING_LEFT[size] - scrollLeft, width: b.width }}
              >
                &nbsp;
              </span>
            </Tooltip>
          ))}
          {/* Hidden measurement span — same font/padding as the input so getOffsetWidth
              matches the actual rendered character widths. */}
          <span ref={measureRef} className={styles.measure} style={inputStyle} />
          <span ref={caretMirrorRef} className={styles.measure} style={inputStyle} />
        </div>
      )}

      {/* The actual text input. Visible normally; chips live behind it. */}
      <input
        ref={inputRef}
        type="text"
        className={styles.input}
        value={renderedValue}
        placeholder={placeholder}
        onChange={previewOn ? undefined : handleInput}
        onKeyDown={previewOn ? undefined : handleKeyDown}
        onFocus={previewOn ? undefined : handleFocus}
        onScroll={(e) => setScrollLeft(e.currentTarget.scrollLeft)}
        onSelect={() => {
          if (!caretMirrorRef.current || !inputRef.current) return
          caretMirrorRef.current.textContent = value.slice(0, inputRef.current.selectionStart ?? 0)
          if (showSuggestions) recomputeDropdownPos()
        }}
        onBlur={() => {
          // If the blur was triggered by clicking a suggestion, swallow it; the click handler
          // will restore focus after inserting. Otherwise close the dropdown.
          if (suggestionMouseDownRef.current) {
            queueMicrotask(() => { suggestionMouseDownRef.current = false })
            return
          }
          setShowSuggestions(false)
        }}
        readOnly={previewOn}
        disabled={disabled}
        // 1Password & co. otherwise inject their "unlock" floating button into this input
        // and swallow keystrokes — see `passwordManagerOptOut` for the full opt-out story.
        {...passwordManagerOptOut}
        name={fieldName.current}
        style={inputStyle}
      />

      {/* Right-side actions — preview toggle + flask (open Variables panel) + token indicator. */}
      <div className={styles.actions}>
        {hasToken && (
          <Tooltip label="Field accepts {{variables}}" withArrow>
            <IconBraces size={13} color="var(--mantine-color-tap-6)" />
          </Tooltip>
        )}
        {context && hasToken && (
          <Tooltip label={previewOn ? 'Edit mode' : 'Preview resolved values'} withArrow>
            <ActionIcon
              size="sm" variant="subtle" color={previewOn ? 'tap' : 'gray'}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => setPreviewOn((p) => !p)}
              aria-label="Toggle preview"
            >
              {previewOn ? <IconEyeOff size={13} /> : <IconEye size={13} />}
            </ActionIcon>
          </Tooltip>
        )}
        {onOpenVariables && (
          <Tooltip label="Open variables panel" withArrow>
            <ActionIcon
              size="sm" variant="subtle" color="gray"
              onMouseDown={(e) => e.preventDefault()}
              onClick={onOpenVariables}
              aria-label="Variables panel"
            >
              <IconFlask size={13} />
            </ActionIcon>
          </Tooltip>
        )}
      </div>

      {/* Suggestions popup — portaled with fixed position so it escapes any clipping. */}
      {showSuggestions && dropdownPos && (
        <Portal>
          <div id={`vi-portal-${idRef.current}`}>
            <Paper
              shadow="md" radius="md" withBorder p={4}
              style={{
                position: 'fixed',
                left: dropdownPos.left,
                top: dropdownPos.top,
                minWidth: 240,
                maxWidth: 420,
                zIndex: 2147483647,
              }}
            >
              <Stack gap={1}>
                {suggestionMode === 'variable' && suggestions.map((s, i) => (
                  <SuggestionRow
                    key={`${s.kind}:${s.provider ?? ''}:${s.name}`}
                    suggestion={s}
                    active={i === highlightedIdx}
                    onPick={() => applySuggestion(s)}
                    onHover={() => setHighlightedIdx(i)}
                    onMouseDownGrab={() => { suggestionMouseDownRef.current = true }}
                  />
                ))}
                {suggestionMode === 'static' && staticMatches.map((s, i) => (
                  <StaticSuggestionRow
                    key={s}
                    text={s}
                    active={i === highlightedIdx}
                    onPick={() => pickStatic(s)}
                    onHover={() => setHighlightedIdx(i)}
                    onMouseDownGrab={() => { suggestionMouseDownRef.current = true }}
                  />
                ))}
              </Stack>
              <div className={styles.popupFooter}>
                <Tooltip label="Refresh from workspace" withArrow>
                  <ActionIcon
                    size="sm" variant="subtle" color="tap"
                    onMouseDown={(e) => e.preventDefault()}
                    aria-label="Refresh"
                  ><IconRefresh size={12} /></ActionIcon>
                </Tooltip>
                {onOpenVariables && (
                  <Tooltip label="Open variables panel" withArrow>
                    <ActionIcon
                      size="sm" variant="subtle" color="gray"
                      onMouseDown={(e) => e.preventDefault()}
                      onClick={onOpenVariables}
                      aria-label="Variables panel"
                    ><IconBraces size={12} /></ActionIcon>
                  </Tooltip>
                )}
              </div>
            </Paper>
          </div>
        </Portal>
      )}
    </div>
  )
}

function StaticSuggestionRow({ text, active, onPick, onHover, onMouseDownGrab }: {
  text: string
  active: boolean
  onPick: () => void
  onHover: () => void
  onMouseDownGrab: () => void
}) {
  return (
    <Box
      onMouseEnter={onHover}
      onMouseDown={(e) => { e.preventDefault(); onMouseDownGrab(); onPick() }}
      px="sm" py={6}
      style={{
        cursor: 'pointer',
        borderRadius: 4,
        background: active ? 'var(--mantine-color-tap-light)' : undefined,
        color: active ? 'var(--mantine-color-tap-light-color)' : undefined,
      }}
    >
      <Text size="sm" ff="var(--mono)" truncate>{text}</Text>
    </Box>
  )
}

function SuggestionRow({ suggestion, active, onPick, onHover, onMouseDownGrab }: {
  suggestion: VariableSuggestion
  active: boolean
  onPick: () => void
  onHover: () => void
  onMouseDownGrab: () => void
}) {
  const isProvider = suggestion.kind === 'provider'
  const isDefaultProvider = isProvider && suggestion.sourceProvider === '__default__'
  const Icon = isProvider ? IconBraces : (suggestion.isSensitive ? IconKey : IconBraces)
  const iconColor = isProvider
    ? (isDefaultProvider ? 'var(--mantine-color-tap-6)' : 'var(--mantine-color-gray-6)')
    : (suggestion.isSensitive ? 'var(--mantine-color-yellow-6)' : 'var(--mantine-color-tap-6)')
  const display = isProvider
    ? `${suggestion.name}:`
    : (suggestion.provider ? `${suggestion.provider}:${suggestion.name}` : suggestion.name)
  // Provider-hint rows: label "PROVIDER", or "PROVIDER · default" for the workspace's
  // default provider — bare `{{name}}` lookups hit that one first at execute time.
  // Variable rows:
  //   - If the user typed the provider qualifier (`{{env:…`), suppress the tail —
  //     `env:` is already in the display.
  //   - Else if the variable is sourced from a provider, show the provider name —
  //     more useful than the literal scope `provider`.
  //   - Otherwise show the cascade scope (workspace/collection/env/…).
  const trailing = isProvider
    ? (isDefaultProvider ? 'PROVIDER · default' : 'PROVIDER')
    : suggestion.provider
      ? ''
      : (suggestion.sourceProvider ?? suggestion.scope ?? '')
  return (
    <Box
      onMouseEnter={onHover}
      onMouseDown={(e) => { e.preventDefault(); onMouseDownGrab(); onPick() }}
      px="sm" py={6}
      style={{
        cursor: 'pointer',
        borderRadius: 4,
        background: active ? 'var(--mantine-color-tap-light)' : undefined,
        color: active ? 'var(--mantine-color-tap-light-color)' : undefined,
      }}
    >
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          <Icon size={12} color={iconColor} />
          <Text size="sm" ff="var(--mono)" truncate>{display}</Text>
        </div>
        {trailing && <Text size="xs" c="dimmed" tt="uppercase">{trailing}</Text>}
      </div>
    </Box>
  )
}

// -----------------------------------------------------------------------------------------
// Internals
// -----------------------------------------------------------------------------------------

interface ChipBox {
  left: number
  width: number
  kind: 'valid' | 'secret' | 'unknown' | 'pending'
  value: string | null
  scope?: Variable['scope']
  /** Provider that backed the resolution (for {{provider:name}}) or the workspace
   *  default for bare {{name}} tokens. Surfaced in the tooltip. */
  provider?: string
  /** True when this chip is bare-named (no provider qualifier) and the resolved
   *  provider matches the workspace's default. Lets the tooltip mark it explicitly. */
  isDefaultProvider?: boolean
  /** Inner token text — `name` for {{name}}, or `provider:name` for {{provider:name}}.
   *  Used by the tooltip to give the user something readable to copy/paste. */
  label?: string
}

/** Variable suggestion shown in the autocomplete popup.
 *  - `provider` rows insert a `{{provider:` prefix and re-open the popup narrowed.
 *  - `variable` rows insert the full `{{name}}` or `{{provider:name}}` token.
 *    `provider` is the qualifier to write into the token (null = bare `{{name}}`).
 *    `sourceProvider` is informational — the provider that actually owns the variable,
 *    shown in the trailing label. They're distinct so a bare-prefix pick (e.g. `{{D` →
 *    `{{DEMO_API_KEY}}`) can still surface "env" in the row tail. */
type VariableSuggestion =
  | { kind: 'variable'; name: string; provider: string | null; sourceProvider: string | null; isSensitive: boolean; scope: Variable['scope'] }
  | { kind: 'provider'; name: string; provider: null; sourceProvider: string | null; isSensitive: false; scope: null }

// Must match `.input` padding in the CSS module — used to offset chip `left` so the chip
// box lines up with the token's first glyph. Keep in sync with VariableInput.module.css.
const SIZE_PADDING_LEFT: Record<'xs' | 'sm' | 'md', number> = { xs: 8, sm: 10, md: 12 }

interface ParsedToken {
  start: number
  end: number
  /** Bare variable name (the part after `provider:` if present, else the whole inner). */
  name: string
  /** Optional provider qualifier when the token is `{{provider:name}}`. */
  provider: string | null
}

function findTokens(text: string): ParsedToken[] {
  const out: ParsedToken[] = []
  const re = /\{\{([^}]*)\}\}/g
  let m: RegExpExecArray | null
  while ((m = re.exec(text)) !== null) {
    const inner = m[1].trim()
    const colon = inner.indexOf(':')
    // Provider qualifier must look like an identifier (letters/digits/_-) — anything
    // else (e.g. accidental `:` inside the variable name) falls through as a plain name.
    if (colon > 0 && /^[a-zA-Z][a-zA-Z0-9_-]*$/.test(inner.slice(0, colon))) {
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

/** Resolve a parsed token against the view: explicit `{{provider:name}}` searches the
 *  matching provider's set; bare `{{name}}` falls back to the merged cascade. Returns
 *  `null` when nothing matches (caller paints the unknown chip). The qualifier may be
 *  an env-scoped alias (`view.aliases`) rather than a literal provider name — resolve
 *  it first so `{{kv:secret}}` still hits `kv-dev`'s set when `kv` is an alias. */
function resolveToken(
  t: ParsedToken,
  view: import('../api/types').VariableView | null,
  vars: Map<string, Variable>,
): Variable | null {
  if (t.provider && view) {
    const provider = view.aliases?.[t.provider] ?? t.provider
    for (const set of view.sets) {
      if (set.providerName !== provider) continue
      const hit = set.variables.find((v) => v.name === t.name)
      if (hit) return hit
    }
    return null
  }
  return vars.get(t.name) ?? null
}

function buildVariableSuggestions(
  prefix: string,
  view: import('../api/types').VariableView,
  providers: string[],
): VariableSuggestion[] {
  const defaultProvider = view.defaultProvider
  const trimmed = prefix.trim()
  const colon = trimmed.indexOf(':')
  // Provider-qualified prefix: filter to that provider's vars by name. The qualifier may
  // be an env-scoped alias — resolve it to the real provider name before matching sets.
  if (colon >= 0 && /^[a-zA-Z][a-zA-Z0-9_-]*$/.test(trimmed.slice(0, colon))) {
    const qualifier = trimmed.slice(0, colon)
    const provider = view.aliases?.[qualifier] ?? qualifier
    const q = trimmed.slice(colon + 1).toLowerCase()
    const out: VariableSuggestion[] = []
    for (const set of view.sets) {
      if (set.providerName !== provider) continue
      for (const v of set.variables) {
        if (v.name.toLowerCase().startsWith(q)) {
          out.push({
            kind: 'variable', name: v.name, provider: qualifier, sourceProvider: provider,
            isSensitive: v.isSensitive, scope: v.scope,
          })
        }
      }
    }
    return out.slice(0, 8)
  }
  // Bare prefix: show cascade matches first, then provider-prefix shortcuts. Providers
  // that don't start with the query are still useful as hints — but only when the user
  // has typed at least one character that matches a provider name.
  const q = trimmed.toLowerCase()
  const vars: VariableSuggestion[] = view.result
    .filter((v) => v.name.toLowerCase().startsWith(q))
    .slice(0, 8)
    .map((v) => ({
      kind: 'variable', name: v.name, provider: null, sourceProvider: v.providerName,
      isSensitive: v.isSensitive, scope: v.scope,
    }))
  const providerHints: VariableSuggestion[] = providers
    .filter((p) => p.toLowerCase().startsWith(q))
    .map((p) => ({
      kind: 'provider', name: p, provider: null,
      // Stash the default-provider marker in sourceProvider so the row can label it
      // "(default)". No other code path reads sourceProvider for provider rows.
      sourceProvider: p === defaultProvider ? '__default__' : null,
      isSensitive: false, scope: null,
    }))
  // Bare-prefix variable suggestions ranked default-provider first, then others by name.
  const rankedVars = defaultProvider
    ? [...vars].sort((a, b) => {
        const ad = a.sourceProvider === defaultProvider ? 0 : 1
        const bd = b.sourceProvider === defaultProvider ? 0 : 1
        return ad - bd
      })
    : vars
  return [...providerHints, ...rankedVars].slice(0, 10)
}

function renderWithValues(text: string, vars: Map<string, Variable>): string {
  // Preview mode replaces each `{{…}}` with the resolved value (or `***` for secrets).
  // Provider-qualified tokens fall through to the cascade map by bare name — close enough
  // for a preview; the server-side renderer is the source of truth at execute time.
  return text.replace(/\{\{\s*([^}]+?)\s*\}\}/g, (_, raw: string) => {
    const inner = raw.trim()
    const colon = inner.indexOf(':')
    const name = (colon > 0 && /^[a-zA-Z][a-zA-Z0-9_-]*$/.test(inner.slice(0, colon)))
      ? inner.slice(colon + 1).trim()
      : inner
    const v = vars.get(name)
    if (!v) return `{{${inner}}}`
    return v.isSensitive ? '***' : (v.value ?? '')
  })
}
