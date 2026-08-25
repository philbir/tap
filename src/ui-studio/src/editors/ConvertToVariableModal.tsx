import {
  Alert, Button, Code, Group, Loader, Modal, Select, Stack, Switch, Text, TextInput, Tooltip,
} from '@mantine/core'
import { IconAlertTriangle, IconKey, IconLock, IconVariable } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { ApiError, api } from '../api/client'
import type {
  DeclareVariableResult, EncryptionKeyStatus, ProviderSummary, VariableContext, VariableScope,
  VariableTarget,
} from '../api/types'
import { LEGACY_EXTENSION, MANIFEST_FILE } from '../shell/tapFiles'
import { MANIFEST_TAB_PATH, useTapStore } from '../store'
import { ProviderTypeIcon } from './providerMeta'

/**
 * Turns a literal typed into a field into a declared variable.
 *
 * <p>Two decisions, and they are not the same one. <b>Scope</b> is where the variable is
 * <i>declared</i> — the cascade tier whose <code>vars:</code> block gets the entry, which is what
 * decides who else can spell <code>{'{{name}}'}</code>. <b>Provider</b> is where the value
 * physically <i>lands</i>, and only comes up for a secret: a secret must not sit in a file bound
 * for Git, so it goes to a writable provider and the declaration keeps a
 * <code>{'{{provider:key}}'}</code> reference in its place (§12.6 of the workspace format).</p>
 *
 * <p>The field itself always ends up with the bare <code>{'{{name}}'}</code> either way, which is
 * the point of routing through a declaration rather than referencing the provider directly: the
 * request reads the same in every environment, and swapping vaults is an edit to one line of one
 * file rather than to every field that spells the secret out.</p>
 *
 * <p>See {@link PromoteSecretModal} for the narrower sibling — that one fires when an
 * already-declared variable is <i>marked</i> secret, and has no scope to choose because the
 * declaration already exists.</p>
 */
export interface ConvertToVariableRequest {
  /** The literal about to become a variable. */
  value: string
  /** Seed for the name field — the field's own key, where the caller knows one. */
  nameHint?: string
  /** Editor context. Decides which tiers are on offer and which env's provider binding applies. */
  context: VariableContext | null
}

export interface ConvertToVariableModalProps {
  request: ConvertToVariableRequest | null
  /** The declaration that landed, or null when the user backed out. */
  onResolve: (result: DeclareVariableResult | null) => void
}

/** Tier labels + the order the picker offers them, weakest first — the cascade's own order,
 *  so the list reads as "how widely does this apply". */
const SCOPE_LABEL: Partial<Record<VariableScope, string>> = {
  workspace: 'Workspace — every collection',
  collection: 'Collection — every request in it',
  env: 'Environment — this environment only',
  request: 'Request — this request only',
}

/** Which tier to land on when the panel opens. A field being converted is nearly always
 *  collection-shaped configuration; workspace is the fallback when nothing narrower is in play. */
const DEFAULT_SCOPE_ORDER: VariableScope[] = ['collection', 'workspace', 'env', 'request']

export function ConvertToVariableModal({ request, onResolve }: ConvertToVariableModalProps) {
  const [targets, setTargets] = useState<VariableTarget[] | null>(null)
  const [providers, setProviders] = useState<ProviderSummary[] | null>(null)
  const [keyStatus, setKeyStatus] = useState<EncryptionKeyStatus | null>(null)
  const [scope, setScope] = useState<VariableScope | null>(null)
  const [name, setName] = useState('')
  const [secret, setSecret] = useState(false)
  const [provider, setProvider] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const declareVar = useTapStore((s) => s.declareVar)
  const reload = useTapStore((s) => s.reload)

  const context = request?.context ?? null
  const envPath = context?.envPath ?? null
  // Serialized so the effect keys on the context's *contents* — the callers rebuild the object
  // on every render, and depending on the identity would refetch in a loop.
  const contextKey = context ? JSON.stringify(context) : null

  useEffect(() => {
    if (!request) return
    setError(null)
    setBusy(false)
    setSecret(false)
    setName(suggestName(request.nameHint))
    setTargets(null)
    setProviders(null)

    api.variableDeclareTargets(context ?? {})
      .then((all) => {
        setTargets(all)
        const usable = all.filter((t) => t.path !== null).map((t) => t.scope)
        setScope(DEFAULT_SCOPE_ORDER.find((s) => usable.includes(s)) ?? usable[0] ?? null)
      })
      .catch((e) => { setTargets([]); setError(messageOf(e)) })

    api.listVariableProviders(envPath)
      .then((all) => {
        const writable = all.filter((p) => p.mode === 'readwrite')
        setProviders(writable)
        setProvider((cur) => (cur && writable.some((p) => p.name === cur) ? cur : writable[0]?.name ?? null))
      })
      .catch((e) => { setProviders([]); setError(messageOf(e)) })

    api.encryptionKey().then(setKeyStatus).catch(() => setKeyStatus(null))
    // eslint-disable-next-line react-hooks/exhaustive-deps -- contextKey stands in for context.
  }, [request, contextKey, envPath])

  const chosenTarget = targets?.find((t) => t.scope === scope) ?? null
  const chosenProvider = providers?.find((p) => p.name === provider) ?? null
  const trimmedName = name.trim()

  // The token grammar of §3.2: `{{name}}` stops at the first `}`, and a leading `word:` would
  // read as a provider qualifier. Caught here so the field says so before the server does.
  const nameError = trimmedName && /[:{}]/.test(trimmedName)
    ? "A variable name cannot contain ':', '{' or '}'."
    : null

  const noWritableProvider = providers !== null && providers.length === 0
  // Only the file provider encrypts with the machine key; the others carry their own protection.
  // Not a blocker — the server creates the key while storing the first secret. This only decides
  // whether to say so, since the file that appears is the user's to back up.
  const willCreateKey = secret && chosenProvider?.type === 'file' && keyStatus !== null && !keyStatus.configured

  const declaredValue = useMemo(() => {
    if (!request) return ''
    return secret && provider && trimmedName ? `{{${provider}:${trimmedName}}}` : request.value
  }, [request, secret, provider, trimmedName])

  const canSubmit = !!request && !!scope && !!chosenTarget?.path && !!trimmedName && !nameError
    && (!secret || (!!provider && !noWritableProvider))

  async function declare() {
    if (!request || !scope || !canSubmit) return
    setBusy(true)
    setError(null)
    try {
      const result = await api.declareVariable({
        name: trimmedName,
        value: request.value,
        scope,
        isSecret: secret,
        variableProvider: secret ? provider : null,
        requestPath: context?.requestPath ?? null,
        collectionPath: context?.collectionPath ?? null,
        envPath,
      })
      // The declaration is on disk, but an editor for that file may be open — and its next
      // save would write a spec that predates this entry, silently undoing it. Recording it
      // here is what stops that: `restoreDraft` folds it into whatever that editor seeds from,
      // on the reload below and on every seed until the editor saves.
      declareVar(tabPathFor(result.path), trimmedName, { value: result.declaredValue, secret })
      // Bumps `generation`, which repaints the token this field is about to hold as a known
      // chip instead of an unknown one, and re-seeds any editor on the file just written. The
      // file watcher gets there too, eventually — this just doesn't make the user watch it.
      void reload()
      onResolve(result)
    } catch (e) {
      setError(messageOf(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      opened={request !== null}
      onClose={() => onResolve(null)}
      title={<Group gap="xs"><IconVariable size={16} /><Text fw={600}>Convert to variable</Text></Group>}
      size="lg"
    >
      {request && (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            This field holds a literal value. Declare it as a variable and the field references
            it instead — so the value lives in one place, and an environment can change it.
          </Text>

          {targets === null ? (
            <Group justify="center" p="md"><Loader size="sm" /></Group>
          ) : (
            <>
              <Select
                label="Declare it in"
                description={chosenTarget?.path
                  ? `Writes to ${chosenTarget.path}`
                  : 'Choose where the declaration lives.'}
                data={targets.map((t) => ({
                  value: t.scope,
                  label: t.label
                    ? `${SCOPE_LABEL[t.scope] ?? t.scope} · ${t.label}`
                    : `${SCOPE_LABEL[t.scope] ?? t.scope} — ${t.unavailable}`,
                  disabled: t.path === null,
                }))}
                value={scope}
                onChange={(v) => setScope(v as VariableScope | null)}
                allowDeselect={false}
              />

              <TextInput
                label="Name"
                description="What the field will spell — {{name}}."
                value={name}
                onChange={(e) => setName(e.currentTarget.value)}
                error={nameError}
                styles={{ input: { fontFamily: 'var(--mono)' } }}
              />

              <Tooltip
                label="No writable variable provider is configured, so there is nowhere to put a secret."
                disabled={!noWritableProvider}
                withArrow
              >
                <Switch
                  label="This is a secret"
                  description="Keeps the value out of the workspace file — it goes to a provider, and the declaration references it."
                  checked={secret}
                  disabled={noWritableProvider}
                  onChange={(e) => setSecret(e.currentTarget.checked)}
                />
              </Tooltip>

              {secret && (noWritableProvider ? (
                <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />}>
                  No writable variable provider is configured, so there is nowhere to put this
                  value. Add a <Code fz="xs">file</Code> provider in Settings first.
                </Alert>
              ) : (
                <Select
                  label="Store the value in"
                  data={(providers ?? []).map((p) => ({
                    value: p.name,
                    label: `${p.name} — ${p.typeDisplayName ?? p.type}`,
                  }))}
                  value={provider}
                  onChange={setProvider}
                  leftSection={<ProviderTypeIcon icon={chosenProvider?.icon ?? null} size={14} />}
                  allowDeselect={false}
                />
              ))}

              <div>
                <Text size="xs" c="dimmed" mb={4}>
                  {chosenTarget?.path ?? 'The file'} will declare:
                </Text>
                <Code block fz="xs">
                  {trimmedName && !nameError
                    ? secret
                      ? `vars:\n  ${trimmedName}:\n    default: '${declaredValue}'\n    secret: true`
                      : `vars:\n  ${trimmedName}: ${declaredValue}`
                    : '—'}
                </Code>
                <Text size="xs" c="dimmed" mt={6}>
                  The field becomes{' '}
                  <Code fz="xs">{trimmedName && !nameError ? `{{${trimmedName}}}` : '—'}</Code>
                </Text>
              </div>

              {willCreateKey && (
                <Alert color="blue" variant="light" icon={<IconKey size={16} />}>
                  <Text size="sm">
                    This machine has no encryption key yet — one will be created at{' '}
                    <Code fz="xs">{keyStatus?.keyFilePath}</Code> when you store this.{' '}
                    <Text span size="sm" fw={600}>Back that file up:</Text> it is the only thing
                    that can decrypt what it encrypts.
                  </Text>
                </Alert>
              )}
            </>
          )}

          {error && <Text size="sm" c="red">{error}</Text>}

          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => onResolve(null)}>Cancel</Button>
            <Button
              loading={busy}
              disabled={!canSubmit}
              leftSection={secret ? <IconLock size={14} /> : undefined}
              onClick={() => void declare()}
            >
              {secret ? 'Store & declare' : 'Declare'}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  )
}

/**
 * The tab path an editor for `path` would key its draft by. Every kind built on
 * `useSpecEditor` uses the file path itself; the manifest has no path of its own and uses a
 * sentinel.
 */
function tabPathFor(path: string): string {
  return path === MANIFEST_FILE || path === `tap${LEGACY_EXTENSION}` ? MANIFEST_TAB_PATH : path
}

/** Turns a field's own key into a usable variable name: `X-Api-Key` → `xApiKey`. Anything that
 *  survives as empty leaves the field blank rather than guessing from the value, which is a
 *  password or a URL as often as it is a word. */
function suggestName(hint: string | undefined): string {
  if (!hint) return ''
  const parts = hint.split(/[^A-Za-z0-9]+/).filter(Boolean)
  if (parts.length === 0) return ''
  const [first, ...rest] = parts
  return first[0].toLowerCase() + first.slice(1)
    + rest.map((p) => p[0].toUpperCase() + p.slice(1)).join('')
}

function messageOf(e: unknown): string {
  return e instanceof ApiError ? e.message : String(e)
}
