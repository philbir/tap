import { Alert, Anchor, Group, NumberInput, Select, Stack, Text } from '@mantine/core'
import { IconAlertTriangle, IconLock } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { HistoryOptions, HistoryStatus } from '../api/types'

interface Props {
  /** What this scope declares, or null/undefined when it declares nothing. */
  value: HistoryOptions | null | undefined
  onChange: (next: HistoryOptions | undefined) => void
  /** The merged value from the scopes above, shown as the placeholder behind each unset
   *  field so "inherited" is visible rather than guessed at. */
  inherited?: HistoryOptions | null
  /** Workspace scope also owns how long an orphaned request's history is kept. */
  showOrphanRetention?: boolean
  /** Name of the scope above, for the inherit labels ("Inherit from Demo"). */
  inheritedFrom?: string
}

/**
 * The `history:` block, at whichever scope is editing it.
 *
 * <p>Every control has three states, not two: <b>Inherit</b>, on, and off. That is the whole
 * point of the cascade — a collection turns recording on for everything under it, and one noisy
 * request turns it back off, without either of them restating what the other said. A two-state
 * toggle would have to pick a value on every save, which silently pins whatever was inherited at
 * the time and severs the link.</p>
 */
export function HistorySettings({
  value, onChange, inherited, showOrphanRetention = false, inheritedFrom,
}: Props) {
  const [status, setStatus] = useState<HistoryStatus | null>(null)

  // Only matters when encryption is in play, but it is cheap and the answer decides whether the
  // encrypt option can be honoured at all.
  useEffect(() => { api.historyStatus().then(setStatus).catch(() => setStatus(null)) }, [])

  /** Patches one key, collapsing to `undefined` once nothing is declared — a scope that says
   *  nothing must emit no `history:` block at all. */
  function set<K extends keyof HistoryOptions>(key: K, next: HistoryOptions[K]) {
    const merged: HistoryOptions = { ...value, [key]: next }
    const declared = Object.values(merged).some((v) => v !== undefined && v !== null)
    onChange(declared ? merged : undefined)
  }

  const encryptOn = value?.encrypt ?? inherited?.encrypt ?? false
  const inheritLabel = inheritedFrom ? `Inherit (${inheritedFrom})` : 'Inherit'
  // What recording will actually do here, once this scope and the ones above it are merged.
  // Everything below the toggle is dead configuration while this is false, so it stays hidden
  // rather than inviting someone to tune a cap that nothing reads.
  const recording = value?.enabled ?? inherited?.enabled ?? false

  return (
    <Stack gap="sm">
      <TriState
        label="Record exchanges"
        description="Writes each request and response to .tap-history/ so you can look at them later."
        value={value?.enabled}
        inherited={inherited?.enabled}
        inheritLabel={inheritLabel}
        onChange={(v) => set('enabled', v)}
      />

      {recording && (
      <>
      <Group grow align="flex-start">
        <NumberInput
          label="Entries kept"
          description="Per request. Oldest are pruned."
          placeholder={inherited?.maxEntries != null ? `${inherited.maxEntries} (inherited)` : '25 (default)'}
          value={value?.maxEntries ?? ''}
          onChange={(v) => set('maxEntries', v === '' ? undefined : Number(v))}
          min={1}
          max={1000}
          allowDecimal={false}
        />
        <NumberInput
          label="Body kept per entry (KB)"
          description="Well below the response cap — history grows unattended."
          placeholder={inherited?.maxBodyBytes != null
            ? `${Math.round(inherited.maxBodyBytes / 1024)} (inherited)`
            : '256 (default)'}
          value={value?.maxBodyBytes != null ? Math.round(value.maxBodyBytes / 1024) : ''}
          onChange={(v) => set('maxBodyBytes', v === '' ? undefined : Number(v) * 1024)}
          min={0}
          allowDecimal={false}
        />
      </Group>

      <TriState
        label="Encrypt at rest"
        description="Keeps the real headers and values — the only way history can show the token that was actually sent. The file is sealed with this machine's key."
        value={value?.encrypt}
        inherited={inherited?.encrypt}
        inheritLabel={inheritLabel}
        onChange={(v) => set('encrypt', v)}
      />

      {!encryptOn && (
        <Text size="xs" c="dimmed">
          Credential headers and every resolved secret are masked before writing. Turn on
          encryption to keep what actually went on the wire.
        </Text>
      )}

      {encryptOn && status?.hasEncryptionKey === false && (
        <Alert color="orange" variant="light" icon={<IconAlertTriangle size={15} />} p="xs">
          <Text size="xs">
            This machine has no encryption key yet. One is generated on the first recorded
            exchange; if that fails, nothing is written rather than being stored in the clear.
            You can create one now in <Anchor href="#" onClick={(e) => e.preventDefault()} inherit>Settings → Encryption</Anchor>.
          </Text>
        </Alert>
      )}

      {encryptOn && status?.hasEncryptionKey && (
        <Group gap={6}>
          <IconLock size={13} color="var(--mantine-color-dimmed)" />
          <Text size="xs" c="dimmed">Entries are sealed with this machine's key and won't open elsewhere.</Text>
        </Group>
      )}
      </>
      )}

      {/* Retention is about files already on disk, not about recording — a workspace with the
          toggle off can still be holding history a collection opted into, or history left behind
          by a scope that has since been turned off. So it stays reachable either way. */}
      {showOrphanRetention && (
        <NumberInput
          label="Keep a deleted request's history for (days)"
          description="Its entries stay readable, and re-link by themselves if the request comes back."
          placeholder="30 (default)"
          value={value?.orphanRetentionDays ?? ''}
          onChange={(v) => set('orphanRetentionDays', v === '' ? undefined : Number(v))}
          min={0}
          allowDecimal={false}
          maw={360}
        />
      )}
    </Stack>
  )
}

/** Inherit / On / Off. A plain Switch can't express the first, which is the one that keeps the
 *  cascade intact. */
function TriState({ label, description, value, inherited, inheritLabel, onChange }: {
  label: string
  description: string
  value: boolean | null | undefined
  inherited: boolean | null | undefined
  inheritLabel: string
  onChange: (next: boolean | undefined) => void
}) {
  const current = value == null ? 'inherit' : value ? 'on' : 'off'
  const inheritedSuffix = inherited == null ? '' : inherited ? ' — on' : ' — off';

  return (
    <Select
      label={label}
      description={description}
      data={[
        { value: 'inherit', label: inheritLabel + inheritedSuffix },
        { value: 'on', label: 'On' },
        { value: 'off', label: 'Off' },
      ]}
      value={current}
      onChange={(v) => onChange(v === 'inherit' ? undefined : v === 'on')}
      allowDeselect={false}
      maw={360}
    />
  )
}
