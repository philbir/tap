import { Group, Select } from '@mantine/core'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { BrowserOption } from '../api/types'
import { openLoginUrl } from '../desktop/desktopUpdater'

const PREF_KEY = 'tap-studio.auth-browser'

export interface BrowserPref {
  browser: string | null
  profile: string | null
}

function loadPref(): BrowserPref {
  try {
    const raw = localStorage.getItem(PREF_KEY)
    if (raw) return JSON.parse(raw) as BrowserPref
  } catch { /* ignore malformed */ }
  return { browser: null, profile: null }
}

function savePref(p: BrowserPref) {
  try { localStorage.setItem(PREF_KEY, JSON.stringify(p)) } catch { /* ignore */ }
}

/**
 * Loads the host's browsers once and manages the persisted browser/profile choice used for
 * OAuth sign-in. `openLogin` launches a URL honoring that choice: a specific browser+profile is
 * opened server-side (`/api/browsers/open`), while "System default" falls back to the normal
 * open (window.open in a browser, the desktop shell's system browser).
 */
export function useBrowserLaunch() {
  const [browsers, setBrowsers] = useState<BrowserOption[]>([])
  const [pref, setPrefState] = useState<BrowserPref>(loadPref)

  useEffect(() => {
    let cancelled = false
    api.browsers().then((b) => { if (!cancelled) setBrowsers(b) }).catch(() => { /* discovery best-effort */ })
    return () => { cancelled = true }
  }, [])

  function setPref(p: BrowserPref) {
    setPrefState(p)
    savePref(p)
  }

  async function openLogin(url: string): Promise<void> {
    if (!pref.browser) {
      openLoginUrl(url)
      return
    }
    await api.openInBrowser(url, pref.browser, pref.profile)
  }

  return { browsers, pref, setPref, openLogin }
}

/** Compact two-select picker: browser + (when the browser supports them) profile. */
export function BrowserPicker({ browsers, pref, onChange }: {
  browsers: BrowserOption[]
  pref: BrowserPref
  onChange: (p: BrowserPref) => void
}) {
  const available = browsers.filter((b) => b.available)
  if (available.length === 0) return null

  const selected = available.find((b) => b.id === pref.browser) ?? null

  return (
    <Group gap="xs" grow align="flex-start">
      <Select
        size="xs"
        label="Browser"
        allowDeselect={false}
        value={pref.browser ?? ''}
        data={[{ value: '', label: 'System default' }, ...available.map((b) => ({ value: b.id, label: b.label }))]}
        onChange={(v) => onChange({ browser: v || null, profile: null })}
      />
      {selected?.supportsProfiles && (
        <Select
          size="xs"
          label="Profile"
          allowDeselect={false}
          value={pref.profile ?? ''}
          data={[
            { value: '', label: 'Default profile' },
            ...selected.profiles.map((p) => ({ value: p.key, label: p.label })),
          ]}
          onChange={(v) => onChange({ ...pref, profile: v || null })}
        />
      )}
    </Group>
  )
}
