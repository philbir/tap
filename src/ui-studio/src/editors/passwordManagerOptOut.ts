/**
 * Attribute bag that tells password managers (1Password, LastPass, Bitwarden, Dashlane,
 * generic browser autofill) to leave a field alone. Spread it onto any `<input>` whose
 * purpose is to hold a URL template, header value, query parameter, env-var value, or any
 * other not-a-credential string.
 *
 * Why each one is needed:
 *   - `autoComplete="off"` — browser-native opt-out. Modern Chrome largely ignores it on
 *     login-like forms, but it still helps in non-login contexts.
 *   - `autoCorrect="off"` / `autoCapitalize="off"` — keep iOS Safari from rewriting tokens.
 *   - `data-1p-ignore` — 1Password's documented escape hatch.
 *   - `data-bwignore` — Bitwarden's equivalent.
 *   - `data-lpignore` — LastPass's equivalent.
 *   - `data-form-type="other"` — RoboForm / Dashlane heuristic respects this.
 *   - randomized `name` — last-line defense against name-pattern heuristics (e.g. anything
 *     containing "password" / "user" / "email"). The input is React-controlled, so the
 *     attribute is never used to read or submit a value.
 *
 * Pure attribute object, no React state needed. Re-export from a single place so we don't
 * have a different drift between editors.
 */
export const passwordManagerOptOut = {
  autoComplete: 'off',
  autoCorrect: 'off',
  autoCapitalize: 'off',
  'data-1p-ignore': 'true',
  'data-bwignore': 'true',
  'data-lpignore': 'true',
  'data-form-type': 'other',
  spellCheck: false,
} as const

/** Generate a random `name` attribute for fields that the browser shouldn't try to
 *  associate with stored credentials. Call at component-mount time. */
export function randomFieldName(prefix = 'tap'): string {
  return `${prefix}-${Math.random().toString(36).slice(2, 10)}`
}
