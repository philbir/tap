import { Button, Stack, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconDownload, IconRefresh } from '@tabler/icons-react'
import { invoke } from '@tauri-apps/api/core'
import { useEffect, useState } from 'react'

/**
 * Auto-update wiring for the Tauri desktop shell.
 *
 * The desktop webview is pointed at the sidecar's `http://127.0.0.1:<port>` origin,
 * so these `invoke()` calls reach the Rust commands only because the desktop
 * capability (`src/desktop/src-tauri/capabilities/default.json`) lists that origin
 * under `remote.urls` and grants `allow-check-for-update` / `allow-install-update`.
 * In a plain browser (Vite dev, Docker, the inspector) `isDesktop()` is false and
 * none of this runs.
 */

/** Shape returned by the Rust `check_for_update` command (see desktop lib.rs). */
type UpdateInfo = {
  version: string
  current_version: string
  body: string | null
}

/** True only when running inside the Tauri desktop shell. */
export function isDesktop(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window
}

/**
 * Open an OAuth login URL. A Tauri webview can't drive an external sign-in via a
 * `window.open` popup, so in the desktop shell we hand the URL to the system
 * browser (Rust `open_external` command); a plain browser keeps the popup.
 * Either way completion is handled by the caller's token polling, not postMessage.
 */
export function openLoginUrl(url: string) {
  if (isDesktop()) {
    void invoke('open_external', { url }).catch((e) =>
      console.error('[desktop] open_external failed', e),
    )
  } else {
    window.open(url, 'tap-auth', 'width=520,height=720,menubar=no,toolbar=no')
  }
}

const NOTIFICATION_ID = 'desktop-update'

/** Notification body: release notes (if any) + an install-and-restart button. */
function UpdateMessage({ info }: { info: UpdateInfo }) {
  const [installing, setInstalling] = useState(false)

  async function install() {
    setInstalling(true)
    try {
      // Downloads, installs, and restarts the app — control normally does not
      // return past this call. If it throws, surface it and re-enable the button.
      await invoke('install_update')
    } catch (e) {
      setInstalling(false)
      notifications.show({
        title: 'Update failed',
        message: e instanceof Error ? e.message : String(e),
        color: 'red',
      })
    }
  }

  return (
    <Stack gap="xs">
      {info.body ? (
        <Text size="sm" c="dimmed" lineClamp={4}>
          {info.body}
        </Text>
      ) : null}
      <Button
        size="xs"
        w="fit-content"
        leftSection={<IconRefresh size={14} />}
        loading={installing}
        onClick={install}
      >
        Install &amp; restart
      </Button>
    </Stack>
  )
}

/**
 * Fire-once update check on mount. Shows a persistent notification with an
 * install button when a newer release is available. No-op outside the desktop
 * shell, and silent (logged only) if the check fails so a flaky network never
 * blocks the app.
 */
export function useDesktopUpdater() {
  useEffect(() => {
    if (!isDesktop()) return
    let cancelled = false
    void (async () => {
      try {
        const info = await invoke<UpdateInfo | null>('check_for_update')
        if (cancelled || !info) return
        notifications.show({
          id: NOTIFICATION_ID,
          title: `Update available — v${info.version}`,
          message: <UpdateMessage info={info} />,
          color: 'tap',
          icon: <IconDownload size={16} />,
          autoClose: false,
          withCloseButton: true,
        })
      } catch (e) {
        console.error('[desktop] update check failed', e)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [])
}
