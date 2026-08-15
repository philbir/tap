import {
  Alert, Badge, Button, Code, Group, Loader, Modal, ScrollArea, Stack, Table, Text, TextInput, UnstyledButton,
} from '@mantine/core'
import { IconAlertCircle, IconSearch, IconShieldLock } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { OnePasswordVault } from '../api/types'

/**
 * Vault browser for the 1Password provider's `vault` field — the counterpart to
 * {@link AzureVaultPicker}. Auth is whatever session the local `op` CLI already has
 * (desktop-app integration, `op signin`, or a configured service-account token); a missing
 * CLI or a signed-out account surfaces as a 400 whose message we show verbatim, because
 * `op`'s own wording already names the fix.
 *
 * The draft `settings` ride along so the listing honours the `cliPath` / `account` /
 * `serviceAccountToken` being edited right now, not only what was last saved.
 */
export function OnePasswordVaultPicker({
  opened, onClose, onSelect, providerName, settings,
}: {
  opened: boolean
  onClose: () => void
  onSelect: (vault: OnePasswordVault) => void
  providerName: string | null
  settings: Record<string, string | null>
}) {
  const [vaults, setVaults] = useState<OnePasswordVault[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<OnePasswordVault | null>(null)
  const [filter, setFilter] = useState('')
  const [reloadKey, setReloadKey] = useState(0)

  // Reset on close so reopening always re-lists — vaults change outside Tap, and a stale
  // list is worse than a one-second spawn.
  useEffect(() => {
    if (!opened) {
      setVaults(null); setError(null); setSelected(null); setFilter('')
      return
    }
    let cancelled = false
    setVaults(null); setError(null)
    api.onePasswordVaults(providerName, settings)
      .then((v) => { if (!cancelled) setVaults(v) })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : String(e))
      })
    return () => { cancelled = true }
    // `settings` is intentionally not a dep: it's a fresh object each render and would
    // re-fetch forever. Reopening (or Retry) is what re-reads it.
  }, [opened, providerName, reloadKey]) // eslint-disable-line react-hooks/exhaustive-deps

  const visible = useMemo(() => {
    if (!vaults) return null
    const f = filter.trim().toLowerCase()
    if (!f) return vaults
    return vaults.filter((v) => v.name.toLowerCase().includes(f) || v.id.toLowerCase().includes(f))
  }, [vaults, filter])

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size="lg"
      title={(
        <Group gap="xs">
          <IconShieldLock size={18} color="var(--mantine-color-blue-5)" />
          <Text fw={600}>Select a 1Password vault</Text>
        </Group>
      )}
    >
      <Stack gap="sm">
        <Text size="xs" c="dimmed">
          Uses the local <Code fz="xs">op</Code> CLI's existing sign-in. Only vault names and
          item counts are read — never item contents.
        </Text>

        {error ? (
          <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
            <Text size="sm">{error}</Text>
            <Button
              size="xs" variant="light" color="red" mt="xs"
              onClick={() => { setError(null); setReloadKey((k) => k + 1) }}
            >
              Retry
            </Button>
          </Alert>
        ) : vaults === null ? (
          <Group gap="xs" py="sm">
            <Loader size="xs" />
            <Text size="sm" c="dimmed">Listing vaults with the 1Password CLI…</Text>
          </Group>
        ) : (
          <>
            <TextInput
              size="xs"
              placeholder="Filter by name or ID…"
              leftSection={<IconSearch size={13} />}
              value={filter}
              onChange={(e) => setFilter(e.currentTarget.value)}
              disabled={vaults.length === 0}
            />

            {visible && visible.length === 0 ? (
              <Text size="sm" c="dimmed" ta="center" py="md">
                {vaults.length > 0 ? 'No vault matches the filter.' : 'This account has no vaults.'}
              </Text>
            ) : visible && (
              <ScrollArea.Autosize mah={320} type="auto">
                <Table verticalSpacing={4} horizontalSpacing="sm" withRowBorders={false} highlightOnHover>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Vault</Table.Th>
                      <Table.Th>Items</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {visible.map((v) => (
                      <Table.Tr
                        key={v.id || v.name}
                        style={{
                          cursor: 'pointer',
                          background: selected?.id === v.id ? 'var(--mantine-color-blue-light)' : undefined,
                        }}
                      >
                        <Table.Td p={0} colSpan={2}>
                          <UnstyledButton
                            w="100%" px="sm" py={6}
                            onClick={() => setSelected(v)}
                            onDoubleClick={() => { onSelect(v); onClose() }}
                          >
                            <Group gap="sm" wrap="nowrap">
                              <Text size="sm" style={{ flex: 1 }} truncate>{v.name}</Text>
                              <Badge size="sm" variant="light" color="gray" style={{ textTransform: 'none' }}>
                                {v.items} item{v.items === 1 ? '' : 's'}
                              </Badge>
                            </Group>
                          </UnstyledButton>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </ScrollArea.Autosize>
            )}
          </>
        )}

        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" size="xs" onClick={onClose}>Cancel</Button>
          <Button
            size="xs"
            disabled={!selected}
            onClick={() => { if (selected) { onSelect(selected); onClose() } }}
          >
            Use vault
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
