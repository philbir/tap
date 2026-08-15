import {
  Alert, Badge, Button, Code, Group, Loader, Modal, ScrollArea, Select, Stack, Table, Text, TextInput, UnstyledButton,
} from '@mantine/core'
import { IconAlertCircle, IconBrandAzure, IconSearch } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { AzureKeyVault, AzureSubscription } from '../api/types'

/**
 * Subscription → Key Vault browser for the azkv provider's `vaultName` field.
 * Auth is the host's Azure CLI credential (`az login`); the server surfaces a clear
 * remediation message when that's missing, shown verbatim in the dialog.
 *
 * `onSelect` receives the chosen vault plus its subscription so the caller can also
 * back-fill `tenantId` when it's still empty.
 */
export function AzureVaultPicker({
  opened, onClose, onSelect,
}: {
  opened: boolean
  onClose: () => void
  onSelect: (vault: AzureKeyVault, subscription: AzureSubscription) => void
}) {
  const [subs, setSubs] = useState<AzureSubscription[] | null>(null)
  const [subsError, setSubsError] = useState<string | null>(null)
  const [subscriptionId, setSubscriptionId] = useState<string | null>(null)

  const [vaults, setVaults] = useState<AzureKeyVault[] | null>(null)
  const [vaultsError, setVaultsError] = useState<string | null>(null)
  const [vaultsLoading, setVaultsLoading] = useState(false)
  const [selected, setSelected] = useState<AzureKeyVault | null>(null)
  const [filter, setFilter] = useState('')

  // Load subscriptions when the dialog opens; reset transient state on close.
  useEffect(() => {
    if (!opened) {
      setVaults(null); setVaultsError(null); setSelected(null); setFilter('')
      return
    }
    if (subs !== null) return
    let cancelled = false
    api.azureSubscriptions()
      .then((s) => {
        if (cancelled) return
        setSubs(s)
        setSubsError(null)
        if (s.length === 1) setSubscriptionId(s[0].subscriptionId)
      })
      .catch((e: unknown) => {
        if (!cancelled) setSubsError(e instanceof ApiError ? e.message : String(e))
      })
    return () => { cancelled = true }
  }, [opened, subs])

  // Load vaults whenever a subscription is picked.
  useEffect(() => {
    if (!opened || !subscriptionId) return
    let cancelled = false
    setVaultsLoading(true); setVaults(null); setVaultsError(null); setSelected(null)
    api.azureKeyVaults(subscriptionId)
      .then((v) => { if (!cancelled) { setVaults(v); setVaultsLoading(false) } })
      .catch((e: unknown) => {
        if (!cancelled) {
          setVaultsError(e instanceof ApiError ? e.message : String(e))
          setVaultsLoading(false)
        }
      })
    return () => { cancelled = true }
  }, [opened, subscriptionId])

  const subscription = subs?.find((s) => s.subscriptionId === subscriptionId) ?? null

  const visibleVaults = useMemo(() => {
    if (!vaults) return null
    const f = filter.trim().toLowerCase()
    if (!f) return vaults
    return vaults.filter((v) =>
      v.name.toLowerCase().includes(f) || v.resourceGroup.toLowerCase().includes(f))
  }, [vaults, filter])

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size="lg"
      title={(
        <Group gap="xs">
          <IconBrandAzure size={18} color="var(--mantine-color-blue-6)" />
          <Text fw={600}>Select an Azure Key Vault</Text>
        </Group>
      )}
    >
      <Stack gap="sm">
        <Text size="xs" c="dimmed">
          Uses your Azure CLI sign-in (<Code fz="xs">az login</Code>) to list subscriptions and vaults.
        </Text>

        {subsError ? (
          <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
            <Text size="sm">{subsError}</Text>
            <Button
              size="xs" variant="light" color="red" mt="xs"
              onClick={() => { setSubs(null); setSubsError(null) }}
            >
              Retry
            </Button>
          </Alert>
        ) : subs === null ? (
          <Group gap="xs" py="sm">
            <Loader size="xs" />
            <Text size="sm" c="dimmed">Signing in with the Azure CLI credential…</Text>
          </Group>
        ) : (
          <Select
            label="Subscription"
            placeholder={subs.length === 0 ? 'No subscriptions visible to az login' : 'Pick a subscription'}
            data={subs.map((s) => ({ value: s.subscriptionId, label: s.displayName }))}
            value={subscriptionId}
            onChange={setSubscriptionId}
            renderOption={({ option }) => {
              const s = subs.find((x) => x.subscriptionId === option.value)
              return (
                <Stack gap={0}>
                  <Text size="sm">{option.label}</Text>
                  <Text size="xs" c="dimmed" ff="var(--mono)">{s?.subscriptionId}</Text>
                </Stack>
              )
            }}
            searchable
            allowDeselect={false}
            disabled={subs.length === 0}
          />
        )}

        {subscriptionId && (
          <>
            <TextInput
              size="xs"
              placeholder="Filter by name or resource group…"
              leftSection={<IconSearch size={13} />}
              value={filter}
              onChange={(e) => setFilter(e.currentTarget.value)}
              disabled={!vaults || vaults.length === 0}
            />

            {vaultsError && (
              <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
                <Text size="sm">{vaultsError}</Text>
              </Alert>
            )}

            {vaultsLoading ? (
              <Group gap="xs" py="sm">
                <Loader size="xs" />
                <Text size="sm" c="dimmed">Listing Key Vaults…</Text>
              </Group>
            ) : visibleVaults && visibleVaults.length === 0 ? (
              <Text size="sm" c="dimmed" ta="center" py="md">
                {vaults && vaults.length > 0 ? 'No vault matches the filter.' : 'No Key Vaults in this subscription.'}
              </Text>
            ) : visibleVaults && (
              <ScrollArea.Autosize mah={320} type="auto">
                <Table verticalSpacing={4} horizontalSpacing="sm" withRowBorders={false} highlightOnHover>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Name</Table.Th>
                      <Table.Th>Resource group</Table.Th>
                      <Table.Th>Location</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {visibleVaults.map((v) => {
                      const isSelected = selected?.name === v.name && selected.resourceGroup === v.resourceGroup
                      return (
                        <Table.Tr
                          key={v.resourceGroup + '/' + v.name}
                          style={{
                            cursor: 'pointer',
                            background: isSelected ? 'var(--mantine-color-blue-light)' : undefined,
                          }}
                        >
                          <Table.Td p={0} colSpan={3}>
                            <UnstyledButton
                              w="100%" px="sm" py={6}
                              onClick={() => setSelected(v)}
                              onDoubleClick={() => { onSelect(v, subscription!); onClose() }}
                            >
                              <Group gap="sm" wrap="nowrap">
                                <Text size="sm" ff="var(--mono)" style={{ flex: '0 0 40%' }} truncate>{v.name}</Text>
                                <Badge size="sm" variant="light" color="gray" style={{ textTransform: 'none' }}>
                                  {v.resourceGroup}
                                </Badge>
                                {v.location && <Text size="xs" c="dimmed">{v.location}</Text>}
                              </Group>
                            </UnstyledButton>
                          </Table.Td>
                        </Table.Tr>
                      )
                    })}
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
            disabled={!selected || !subscription}
            onClick={() => {
              if (selected && subscription) { onSelect(selected, subscription); onClose() }
            }}
          >
            Use vault
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
