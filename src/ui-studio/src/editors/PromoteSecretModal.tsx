import {
  Alert, Button, Code, Group, Loader, Modal, Select, Stack, Text, TextInput,
} from '@mantine/core'
import { IconAlertTriangle, IconKey, IconLock } from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { ApiError, api } from '../api/client'
import type { EncryptionKeyStatus, ProviderSummary } from '../api/types'
import { ProviderTypeIcon } from './providerMeta'

/**
 * Asked when a variable is marked secret while holding a literal value.
 *
 * <p>Marking something secret used to change one flag and leave the value sitting in the
 * workspace file, in clear text, on its way to Git — the mark said "sensitive" while the file
 * said otherwise. So the mark now moves the value: it goes into a writable provider, and the
 * file keeps a <Code>{'{{provider:key}}'}</Code> reference in its place.</p>
 *
 * <p>Resolving that token is ordinary variable resolution, so nothing downstream changes —
 * the request renders the same value it always did, from a place that isn't the repository.</p>
 */
export interface PromoteSecretRequest {
  /** The variable's name in the file — the default key inside the provider. */
  name: string
  /** The literal value about to be moved out of the file. */
  value: string
  /** Active env path, so the write lands on that env's provider binding. */
  envPath: string | null
}

export interface PromoteSecretModalProps {
  request: PromoteSecretRequest | null
  /** Called with the token to put in the file, or null when the user backs out. Returning
   *  `{ inline: true }` means "leave the literal where it is and just set the flag". */
  onResolve: (outcome: { token: string } | { inline: true } | null) => void
}

export function PromoteSecretModal({ request, onResolve }: PromoteSecretModalProps) {
  const [providers, setProviders] = useState<ProviderSummary[] | null>(null)
  const [keyStatus, setKeyStatus] = useState<EncryptionKeyStatus | null>(null)
  const [target, setTarget] = useState<string | null>(null)
  const [key, setKey] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const envPath = request?.envPath ?? null

  useEffect(() => {
    if (!request) return
    setError(null)
    setKey(request.name)
    setProviders(null)
    api.listVariableProviders(envPath)
      .then((all) => {
        const writable = all.filter((p) => p.mode === 'readwrite')
        setProviders(writable)
        setTarget((cur) => cur && writable.some((p) => p.name === cur) ? cur : writable[0]?.name ?? null)
      })
      .catch((e) => {
        setProviders([])
        setError(e instanceof ApiError ? e.message : String(e))
      })
    api.encryptionKey().then(setKeyStatus).catch(() => setKeyStatus(null))
  }, [request, envPath])

  const chosen = providers?.find((p) => p.name === target) ?? null
  // Only the file provider encrypts with the machine key; the others carry their own
  // protection (a vault, the user profile), so a missing key doesn't concern them.
  const needsKey = chosen?.type === 'file' && keyStatus !== null && !keyStatus.configured
  const token = target && key.trim() ? `{{${target}:${key.trim()}}}` : ''

  async function store() {
    if (!request || !target || !key.trim()) return
    setBusy(true)
    setError(null)
    try {
      await api.setVariable({
        name: key.trim(),
        value: request.value,
        isSecret: true,
        variableProvider: target,
        envPath,
      })
      onResolve({ token })
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  async function generateKey() {
    setBusy(true)
    setError(null)
    try {
      setKeyStatus(await api.generateEncryptionKey())
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const noWritableProvider = providers !== null && providers.length === 0

  return (
    <Modal
      opened={request !== null}
      onClose={() => onResolve(null)}
      title={<Group gap="xs"><IconLock size={16} /><Text fw={600}>Move this value out of the file</Text></Group>}
      size="lg"
    >
      {request && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            <Code fz="xs">{request.name}</Code> is about to be marked secret. Its value is
            currently written into this file in clear text — store it in a provider instead and
            the file will reference it.
          </Text>

          {providers === null ? (
            <Group justify="center" p="md"><Loader size="sm" /></Group>
          ) : noWritableProvider ? (
            <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />}>
              No writable variable provider is configured, so there is nowhere to put this
              value. Add a <Code fz="xs">file</Code> provider in Settings, or keep the literal in
              the file and mark it secret anyway — which masks it in the UI but does not take it
              out of the repository.
            </Alert>
          ) : (
            <>
              <Select
                label="Store it in"
                data={providers.map((p) => ({ value: p.name, label: `${p.name} — ${p.typeDisplayName ?? p.type}` }))}
                value={target}
                onChange={setTarget}
                leftSection={<ProviderTypeIcon icon={chosen?.icon ?? null} size={14} />}
                allowDeselect={false}
              />
              <TextInput
                label="Name inside the provider"
                value={key}
                onChange={(e) => setKey(e.currentTarget.value)}
                styles={{ input: { fontFamily: 'var(--mono)' } }}
              />
              <div>
                <Text size="xs" c="dimmed" mb={4}>The file will contain:</Text>
                <Code block fz="xs">{token || '—'}</Code>
              </div>
              {needsKey && (
                <Alert color="yellow" variant="light" icon={<IconKey size={16} />}>
                  <Stack gap="xs">
                    <Text size="sm">
                      This machine has no encryption key, so the file provider can't store a
                      secret yet. Set <Code fz="xs">{keyStatus?.envVarName}</Code>, or generate one.
                    </Text>
                    <Group>
                      <Button size="xs" loading={busy} onClick={() => void generateKey()} leftSection={<IconKey size={14} />}>
                        Generate a key
                      </Button>
                    </Group>
                  </Stack>
                </Alert>
              )}
            </>
          )}

          {error && <Text size="sm" c="red">{error}</Text>}

          <Group justify="space-between">
            <Button variant="subtle" color="gray" onClick={() => onResolve({ inline: true })}>
              Keep it in the file
            </Button>
            <Group gap="xs">
              <Button variant="default" onClick={() => onResolve(null)}>Cancel</Button>
              <Button
                loading={busy}
                disabled={!target || !key.trim() || needsKey || noWritableProvider}
                onClick={() => void store()}
              >
                Store &amp; reference
              </Button>
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  )
}
