import { EditorSelection, StateEffect, StateField, RangeSetBuilder, type Extension } from '@codemirror/state'
import { Decoration, EditorView, type DecorationSet } from '@codemirror/view'
import { findRanges, type MatchRange } from './resultSearch'

/**
 * Find-in-document for `CodeBlock`, driven from outside rather than by CodeMirror's own
 * search panel. Two reasons it isn't the stock panel: the panel is CodeMirror-styled in a
 * Mantine app, and it only exists inside one editor — while the response panel wants one
 * query that also filters the events, frames and header tabs.
 *
 * Matches are recomputed from the editor's *own* document. `CodeBlock` pretty-prints JSON, so
 * offsets computed against the raw response body would land in the wrong place.
 */
export interface CodeSearchSpec {
  /** `RegExp` source — already escaped upstream when the user is not in regex mode. */
  source: string
  /** Must include `g`. */
  flags: string
  /** Zero-based index of the match to emphasise, or -1 for "highlight all, single out none". */
  active: number
}

export const setCodeSearch = StateEffect.define<CodeSearchSpec | null>()

interface CodeSearchValue {
  spec: CodeSearchSpec | null
  matches: MatchRange[]
  deco: DecorationSet
}

const EMPTY: CodeSearchValue = { spec: null, matches: [], deco: Decoration.none }

const matchMark = Decoration.mark({ class: 'cm-tapMatch' })
const activeMark = Decoration.mark({ class: 'cm-tapMatch cm-tapMatchActive' })

export const codeSearchField = StateField.define<CodeSearchValue>({
  create: () => EMPTY,
  update(value, tr) {
    let spec = value.spec
    let reset = false
    for (const e of tr.effects) {
      if (e.is(setCodeSearch)) { spec = e.value; reset = true }
    }
    if (!reset && !tr.docChanged) return value
    if (!spec) return EMPTY

    // Only the active index moved — reuse the ranges rather than rescanning the document on
    // every press of the next-match arrow.
    const reusable = !tr.docChanged
      && value.spec
      && value.spec.source === spec.source
      && value.spec.flags === spec.flags
    const matches = reusable
      ? value.matches
      : safeRanges(tr.state.doc.toString(), spec.source, spec.flags)

    return { spec, matches, deco: buildDeco(matches, spec.active) }
  },
  provide: (f) => EditorView.decorations.from(f, (v) => v.deco),
})

/** Ranges currently matched in `view`'s document, in document order. */
export function codeSearchMatches(view: EditorView): MatchRange[] {
  return view.state.field(codeSearchField, false)?.matches ?? []
}

/** Scroll the nth match into view. No-op when the index is out of range. */
export function scrollToCodeMatch(view: EditorView, index: number): void {
  const m = codeSearchMatches(view)[index]
  if (!m) return
  view.dispatch({ effects: EditorView.scrollIntoView(EditorSelection.range(m.from, m.to), { y: 'center' }) })
}

/**
 * The highlight colours. `CodeBlock` pins the VSCode *light* theme regardless of the app's
 * colour scheme, so these are chosen against a light editor background — and Mantine's palette
 * shades don't flip with the scheme, which is what makes the tokens safe to use here.
 */
const codeSearchTheme = EditorView.theme({
  '.cm-tapMatch': {
    backgroundColor: 'var(--mantine-color-yellow-2)',
    outline: '1px solid var(--mantine-color-yellow-5)',
    borderRadius: '2px',
  },
  '.cm-tapMatchActive': {
    backgroundColor: 'var(--mantine-color-orange-4)',
    outline: '1px solid var(--mantine-color-orange-7)',
  },
})

export const codeSearch: Extension = [codeSearchField, codeSearchTheme]

function buildDeco(matches: MatchRange[], active: number): DecorationSet {
  const b = new RangeSetBuilder<Decoration>()
  matches.forEach((m, i) => b.add(m.from, m.to, i === active ? activeMark : matchMark))
  return b.finish()
}

function safeRanges(text: string, source: string, flags: string): MatchRange[] {
  try {
    return findRanges(text, new RegExp(source, flags))
  } catch {
    // The bar reports the compile error; the editor just shows nothing highlighted.
    return []
  }
}
