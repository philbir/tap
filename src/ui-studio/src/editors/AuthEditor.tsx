import {
  ActionIcon, Alert, Box, Button, Checkbox, Code, Collapse, Divider, Group, ScrollArea,
  Select, Stack, Switch, Tabs, TagsInput, Text, TextInput, Textarea, Tooltip,
} from '@mantine/core'
import { useClipboard, useDebouncedValue, useDisclosure } from '@mantine/hooks'
import {
  IconAlertCircle, IconCheck, IconChevronDown, IconChevronRight, IconCode, IconCopy,
  IconExternalLink, IconKey, IconFileText, IconPlayerPlayFilled, IconRefresh, IconShieldCheck, IconTrash,
} from '@tabler/icons-react'
import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { AuthDetail, AuthExecuteResponse, AuthSpec, VariableContext } from '../api/types'
import { BrowserPicker, useBrowserLaunch } from './BrowserPicker'
import { useActiveEnv, useTapStore } from '../store'
import { useTagDictionary } from '../workspace/useTagDictionary'
import { decodeJwt } from '../workspace/jwt'
import { DocsEditor } from './DocsEditor'
import { EditorShell, TabDot } from './EditorShell'
import { KvTable } from './KvTable'
import { COMMON_HEADER_NAMES, valuesForHeader } from './headerSuggestions'
import { SourceTab } from './SourceTab'
import { VariableInput } from './VariableInput'
import { VariablesPanel } from './VariablesPanel'

interface Props {
  path: string
}

type AuthType = AuthSpec['type']

const AUTH_TYPES: { value: AuthType; label: string }[] = [
  { value: 'none',      label: 'None' },
  { value: 'basic',     label: 'Basic' },
  { value: 'bearer',    label: 'Bearer' },
  { value: 'apiKey',    label: 'API Key' },
  { value: 'oauth2',    label: 'OAuth 2.0 / OIDC' },
  { value: 'aws-sigv4', label: 'AWS SigV4' },
  // Azure CLI's two modes (plain + OBO) live behind a single type with a flow selector,
  // matching how OAuth2 picks its grant.
  { value: 'azure-cli', label: 'Azure CLI' },
  { value: 'jwt',       label: 'JWT (signed)' },
  { value: 'github',    label: 'GitHub' },
  { value: 'custom',    label: 'Custom' },
]

export function AuthEditor({ path }: Props) {
  const generation = useTapStore((s) => s.generation)
  const tagSuggestions = useTagDictionary()
  const activeEnv = useActiveEnv()
  const [varsOpened, setVarsOpened] = useState(false)
  const variableContext = useMemo<VariableContext>(() => ({
    envPath: activeEnv ?? undefined,
  }), [activeEnv])

  const [detail, setDetail] = useState<AuthDetail | null>(null)
  const [spec, setSpec] = useState<AuthSpec | null>(null)
  const [savedSpec, setSavedSpec] = useState<AuthSpec | null>(null)
  const [tab, setTab] = useState<string | null>('config')
  const [saving, setSaving] = useState(false)
  const [errorMessage, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setError(null)
    api.authDetail(path).then((d) => {
      if (cancelled) return
      setDetail(d)
      const initial = specFromDetail(d, path)
      setSpec(initial)
      setSavedSpec(initial)
    }).catch((e: Error) => !cancelled && setError(e.message))
    return () => { cancelled = true }
  }, [path, generation])

  const dirty = useMemo(() => JSON.stringify(spec) !== JSON.stringify(savedSpec), [spec, savedSpec])

  function update<K extends keyof AuthSpec>(key: K, value: AuthSpec[K]) {
    setSpec((cur) => cur ? { ...cur, [key]: value } : cur)
  }

  function setType(next: AuthType) {
    setSpec((cur) => cur ? trimToType({ ...cur, type: next }) : cur)
  }

  async function save() {
    if (!spec) return
    setSaving(true); setError(null)
    try {
      await api.saveAuthSpec(spec)
      setSavedSpec(spec)
    } catch (e) { setError(e instanceof ApiError ? e.message : String(e)) }
    finally { setSaving(false) }
  }

  if (!detail || !spec) {
    return (
      <EditorShell
        title={detail?.name ?? basename(path)}
        kindLabel="Auth"
        dirty={false} saving={saving} errorMessage={errorMessage}
        onSave={save}
      >
        <Text c="dimmed">Loading…</Text>
      </EditorShell>
    )
  }

  return (
    <AuthVarContext.Provider value={{ context: variableContext, onOpenVariables: () => setVarsOpened(true) }}>
    <EditorShell
      title={spec.name || basename(path)}
      kindLabel="Auth"
      dirty={dirty} saving={saving} errorMessage={errorMessage}
      onSave={save}
      onDiscard={() => setSpec(savedSpec)}
      onTitleChange={(n) => update('name', n)}
      rightPane={<AuthExecutePanel path={path} dirty={dirty} type={spec.type} />}
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List mb="md">
          <Tabs.Tab value="config" leftSection={<IconShieldCheck size={14} />}>Configuration</Tabs.Tab>
          <Tabs.Tab value="docs" leftSection={<IconFileText size={14} />}>
            Docs <TabDot active={!!spec.body && spec.body.trim().length > 0} />
          </Tabs.Tab>
          <Tabs.Tab value="source" leftSection={<IconCode size={14} />}>Source</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="config">
          <Stack gap="md" maw={880}>
            <Group gap="md" align="flex-start" wrap="nowrap">
              <Select
                label="Type"
                value={spec.type}
                data={AUTH_TYPES}
                onChange={(v) => v && setType(v as AuthType)}
                allowDeselect={false}
                maw={240}
                flex="0 0 240px"
              />
              <TagsInput
                label="Tags"
                placeholder={(spec.tags?.length ?? 0) === 0 ? 'Add tag…' : ''}
                data={tagSuggestions}
                value={spec.tags ?? []}
                onChange={(v) => update('tags', v.length > 0 ? v : undefined)}
                acceptValueOnBlur
                clearable
                flex={1}
              />
            </Group>

            <Divider variant="dashed" />

            {spec.type === 'bearer' && (
              <VariableField
                label="Token"
                value={spec.token ?? ''}
                onChange={(v) => update('token', v || undefined)}
                hint="May be a {{var}} reference (e.g. {{env:MY_TOKEN}})."
              />
            )}

            {spec.type === 'basic' && (
              <>
                <VariableField label="Username" value={spec.username ?? ''} onChange={(v) => update('username', v || undefined)} />
                <VariableField label="Password" value={spec.password ?? ''} onChange={(v) => update('password', v || undefined)} secret />
              </>
            )}

            {spec.type === 'apiKey' && (
              <>
                <Select
                  label="Location"
                  value={spec.in ?? 'header'}
                  onChange={(v) => v && update('in', v as AuthSpec['in'])}
                  data={[
                    { value: 'header', label: 'Header' },
                    { value: 'query', label: 'Query string' },
                    { value: 'cookie', label: 'Cookie' },
                  ]}
                  allowDeselect={false}
                  maw={240}
                />
                <VariableField
                  label="Key name"
                  value={spec.apiKeyName ?? ''}
                  onChange={(v) => update('apiKeyName', v || undefined)}
                  hint="Header / query parameter / cookie name (e.g. 'X-API-Key')."
                />
                <VariableField
                  label="Key value"
                  value={spec.apiKeyValue ?? ''}
                  onChange={(v) => update('apiKeyValue', v || undefined)}
                  secret
                />
              </>
            )}

            {spec.type === 'oauth2' && <OAuth2Fields spec={spec} update={update} />}

            {spec.type === 'aws-sigv4' && (
              <>
                <VariableField label="Region" value={spec.region ?? ''} onChange={(v) => update('region', v || undefined)} />
                <VariableField label="Service" value={spec.service ?? ''} onChange={(v) => update('service', v || undefined)} />
                <VariableField label="Access Key ID" value={spec.accessKeyId ?? ''} onChange={(v) => update('accessKeyId', v || undefined)} secret />
                <VariableField label="Secret Access Key" value={spec.secretAccessKey ?? ''} onChange={(v) => update('secretAccessKey', v || undefined)} secret />
                <VariableField label="Session Token" value={spec.sessionToken ?? ''} onChange={(v) => update('sessionToken', v || undefined)} hint="Optional, for temporary STS credentials." secret />
              </>
            )}

            {spec.type === 'azure-cli' && <AzureCliPanel spec={spec} update={update} setSpec={setSpec} />}
            {spec.type === 'jwt' && <JwtFields spec={spec} update={update} />}
            {spec.type === 'github' && <GithubPanel spec={spec} update={update} setSpec={setSpec} />}

            {spec.type === 'custom' && (
              <Stack gap="xs">
                <Text size="sm" fw={500}>Headers</Text>
                <Text size="xs" c="dimmed">
                  Inject arbitrary headers. Values may contain <Code>${'{{provider:path}}'}</Code> refs.
                </Text>
                <KvTable
                  rows={Object.entries(spec.headers ?? {}).map(([k, v]) => ({ key: k, value: v }))}
                  onChange={(rows) => {
                    const obj: Record<string, string> = {}
                    for (const r of rows) if (r.key) obj[r.key] = r.value
                    update('headers', Object.keys(obj).length > 0 ? obj : undefined)
                  }}
                  keyPlaceholder="Header-Name"
                  valuePlaceholder="value or {{var}}"
                  keySuggestions={COMMON_HEADER_NAMES}
                  getValueSuggestions={valuesForHeader}
                />
              </Stack>
            )}

            {spec.type === 'none' && (
              <Alert variant="light" color="gray" icon={<IconAlertCircle size={14} />}>
                No auth applied. Useful for explicitly opting out at the request level.
              </Alert>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="docs">
          <DocsEditor
            value={spec.body ?? ''}
            onChange={(v) => setSpec((cur) => cur ? { ...cur, body: v.trim().length > 0 ? v : undefined } : cur)}
            emptyHint="No docs yet. Describe how this auth profile works and how to set its variables."
          />
        </Tabs.Panel>

        <Tabs.Panel value="source">
          <SourceTab path={path} source={detail.source} />
        </Tabs.Panel>
      </Tabs>
    </EditorShell>
    <VariablesPanel opened={varsOpened} onClose={() => setVarsOpened(false)} context={variableContext} />
    </AuthVarContext.Provider>
  )
}

// ---- Per-type field helpers ----------------------------------------------------------

/**
 * AuthEditor injects its variable context + "open Variables panel" callback through this
 * context so every `VariableField` and `VariableInput` underneath picks them up without
 * the parent having to prop-drill to 14 call sites. Other editors using `VariableInput`
 * directly can pass props; this is purely an AuthEditor-local ergonomic.
 */
const AuthVarContext = createContext<{ context?: VariableContext; onOpenVariables?: () => void }>({})

function VariableField({ label, value, onChange, hint, secret }: {
  label: string
  value: string
  onChange: (v: string) => void
  hint?: string
  secret?: boolean
}) {
  const { context, onOpenVariables } = useContext(AuthVarContext)
  return (
    <Box>
      <Group gap={6} mb={4}>
        <Text size="sm" fw={500}>{label}</Text>
        {secret && <Tooltip label="Secret-bearing — use a {{var}} reference (e.g. {{env:NAME}}); the variable's `secret: true` flag keeps the value masked"><IconKey size={12} color="var(--mantine-color-yellow-7)" /></Tooltip>}
      </Group>
      <VariableInput value={value} onChange={onChange} context={context} onOpenVariables={onOpenVariables} />
      {hint && <Text size="xs" c="dimmed" mt={4}>{hint}</Text>}
    </Box>
  )
}

/**
 * Authority gets its own wrapper because we want a custom placeholder + no key icon, but
 * still need the AuthVarContext plumbing the rest of the editor relies on for variable
 * resolution. Without `context`, the highlighter has no scope to look up against and
 * paints every {{var}} ref as unresolved.
 */
function AuthorityInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const { context, onOpenVariables } = useContext(AuthVarContext)
  return (
    <VariableInput
      value={value}
      onChange={onChange}
      context={context}
      onOpenVariables={onOpenVariables}
      placeholder="https://login.microsoftonline.com/{tenant}/v2.0"
    />
  )
}

// ---- Spec mapping ---------------------------------------------------------------------

function specFromDetail(d: AuthDetail, path: string): AuthSpec {
  const type = ((d.type as AuthType | undefined) ?? 'none')
  const f = d.fields ?? {}
  const useDiscovery = String(f['useDiscovery'] ?? '').toLowerCase() === 'true'

  const base: AuthSpec = {
    path,
    id: d.id,
    name: d.name,
    type,
    tags: d.tags && d.tags.length > 0 ? d.tags : undefined,
    body: d.body && d.body.trim().length > 0 ? d.body : undefined,
  }

  switch (type) {
    case 'basic':    return { ...base, username: f['username'] ?? undefined, password: f['password'] ?? undefined }
    case 'bearer':   return { ...base, token: f['token'] ?? undefined }
    case 'apiKey':   return {
      ...base,
      in: (f['in'] as AuthSpec['in']) ?? 'header',
      apiKeyName: f['apiKeyName'] ?? undefined,
      apiKeyValue: f['apiKeyValue'] ?? f['value'] ?? undefined,
    }
    case 'oauth2':   return {
      ...base,
      flow: (f['flow'] as AuthSpec['flow']) ?? 'authorization_code_pkce',
      useDiscovery: useDiscovery || undefined,
      authority: f['authority'] ?? undefined,
      authorizeUrl: f['authorizeUrl'] ?? undefined,
      tokenUrl: f['tokenUrl'] ?? undefined,
      deviceAuthorizationUrl: f['deviceAuthorizationUrl'] ?? undefined,
      clientId: f['clientId'] ?? undefined,
      clientSecret: f['clientSecret'] ?? undefined,
      scopes: d.scopes && d.scopes.length > 0 ? d.scopes : undefined,
      audience: f['audience'] ?? undefined,
      // redirectUri is intentionally dropped from the editor view: Studio computes it
      // at runtime from its own base URL. Any value previously written to the file is
      // ignored on load and removed on the next save.
      redirectUri: undefined,
      // ROPC bits — only relevant when flow === 'password'. Read back from the
      // generic username/password keys; the spec mirrors them under oauth* names
      // to avoid colliding with the basic-auth fields in the same union.
      oauthUsername: f['username'] ?? undefined,
      oauthPassword: f['password'] ?? undefined,
    }
    case 'azure-cli': {
      // Flow picks between az's own token (direct) and OBO. We accept either spelling
      // (`on_behalf_of` / `obo`) so hand-edited workspaces stay readable.
      const rawFlow = (f['flow'] ?? 'direct').trim()
      const azureFlow = rawFlow === 'obo' || rawFlow === 'on_behalf_of' ? 'on_behalf_of' : 'direct'
      return {
        ...base,
        azureFlow,
        tenantId: f['tenant'] ?? f['tenantId'] ?? undefined,
        subscription: f['subscription'] ?? undefined,
        resource: f['resource'] ?? undefined,
        scope: f['scope'] ?? undefined,
        userResource: f['userResource'] ?? undefined,
        userScope: f['userScope'] ?? undefined,
        tokenUrl: f['tokenUrl'] ?? undefined,
        clientId: f['clientId'] ?? undefined,
        clientSecret: f['clientSecret'] ?? undefined,
        scopes: d.scopes && d.scopes.length > 0 ? d.scopes : undefined,
        audience: f['audience'] ?? undefined,
      }
    }
    case 'jwt': return {
      ...base,
      jwtAlgorithm: f['algorithm'] ?? 'HS256',
      jwtIssuer: f['issuer'] ?? undefined,
      jwtAudience: f['audience'] ?? undefined,
      jwtSubject: f['subject'] ?? undefined,
      jwtKeyId: f['keyId'] ?? f['kid'] ?? undefined,
      jwtKey: f['key'] ?? undefined,
      jwtExpiresIn: f['expiresIn'] ? Number(f['expiresIn']) : undefined,
      jwtPayload: f['payload'] ?? undefined,
    }
    case 'aws-sigv4': return {
      ...base,
      region: f['region'] ?? undefined,
      service: f['service'] ?? undefined,
      accessKeyId: f['accessKeyId'] ?? undefined,
      secretAccessKey: f['secretAccessKey'] ?? undefined,
      sessionToken: f['sessionToken'] ?? undefined,
    }
    case 'custom':   return {
      ...base,
      headers: Object.keys(d.headers ?? {}).length > 0 ? d.headers : undefined,
      query: Object.keys(d.query ?? {}).length > 0 ? d.query : undefined,
    }
    case 'github': {
      const rawMode = (f['mode'] ?? 'pat').trim()
      const githubMode = (['pat', 'gh-cli', 'app', 'oauth'].includes(rawMode) ? rawMode : 'pat') as AuthSpec['githubMode']
      return {
        ...base,
        githubMode,
        token: f['token'] ?? undefined,
        githubAppId: f['appId'] ?? undefined,
        githubInstallationId: f['installationId'] ?? undefined,
        githubPrivateKey: f['privateKey'] ?? undefined,
        clientId: f['clientId'] ?? undefined,
        clientSecret: f['clientSecret'] ?? undefined,
        scopes: d.scopes && d.scopes.length > 0 ? d.scopes : undefined,
      }
    }
    default: return base
  }
}

function trimToType(spec: AuthSpec): AuthSpec {
  const base: AuthSpec = { path: spec.path, id: spec.id, name: spec.name, type: spec.type, tags: spec.tags, body: spec.body }
  switch (spec.type) {
    case 'basic':    return { ...base, username: spec.username, password: spec.password }
    case 'bearer':   return { ...base, token: spec.token }
    case 'apiKey':   return { ...base, in: spec.in ?? 'header', apiKeyName: spec.apiKeyName, apiKeyValue: spec.apiKeyValue }
    case 'oauth2':   return {
      ...base,
      flow: spec.flow ?? 'authorization_code_pkce',
      useDiscovery: spec.useDiscovery,
      authority: spec.authority,
      authorizeUrl: spec.authorizeUrl,
      tokenUrl: spec.tokenUrl,
      deviceAuthorizationUrl: spec.deviceAuthorizationUrl,
      clientId: spec.clientId,
      clientSecret: spec.clientSecret,
      scopes: spec.scopes,
      audience: spec.audience,
      // redirectUri intentionally not persisted — see specFromDetail's note.
      redirectUri: undefined,
      oauthUsername: spec.oauthUsername,
      oauthPassword: spec.oauthPassword,
    }
    case 'azure-cli': {
      const azureFlow = spec.azureFlow ?? 'direct'
      // Trim the spec down to the fields the chosen flow actually consumes — keeps the
      // emitted YAML clean when the user toggles between modes.
      return azureFlow === 'on_behalf_of'
        ? {
            ...base, azureFlow,
            tenantId: spec.tenantId, subscription: spec.subscription,
            userResource: spec.userResource, userScope: spec.userScope,
            tokenUrl: spec.tokenUrl,
            clientId: spec.clientId, clientSecret: spec.clientSecret,
            scopes: spec.scopes, audience: spec.audience,
          }
        : {
            ...base, azureFlow,
            tenantId: spec.tenantId, subscription: spec.subscription,
            resource: spec.resource, scope: spec.scope,
          }
    }
    case 'jwt': return {
      ...base,
      jwtAlgorithm: spec.jwtAlgorithm ?? 'HS256',
      jwtIssuer: spec.jwtIssuer,
      jwtAudience: spec.jwtAudience,
      jwtSubject: spec.jwtSubject,
      jwtKeyId: spec.jwtKeyId,
      jwtKey: spec.jwtKey,
      jwtExpiresIn: spec.jwtExpiresIn,
      jwtPayload: spec.jwtPayload,
    }
    case 'aws-sigv4': return {
      ...base,
      region: spec.region, service: spec.service,
      accessKeyId: spec.accessKeyId, secretAccessKey: spec.secretAccessKey, sessionToken: spec.sessionToken,
    }
    case 'custom':   return { ...base, headers: spec.headers, query: spec.query }
    case 'github': {
      const mode = spec.githubMode ?? 'pat'
      switch (mode) {
        case 'pat':    return { ...base, githubMode: mode, token: spec.token }
        case 'gh-cli': return { ...base, githubMode: mode }
        case 'app':    return {
          ...base, githubMode: mode,
          githubAppId: spec.githubAppId,
          githubInstallationId: spec.githubInstallationId,
          githubPrivateKey: spec.githubPrivateKey,
        }
        case 'oauth':  return {
          ...base, githubMode: mode,
          clientId: spec.clientId, clientSecret: spec.clientSecret,
          scopes: spec.scopes,
        }
      }
      return { ...base, githubMode: 'pat' }
    }
    default: return base
  }
}

function basename(p: string): string { return p.split('/').pop() ?? p }

// ---- OAuth2 fields --------------------------------------------------------------------

const OAUTH2_GRANT_TYPES = [
  { value: 'authorization_code_pkce', label: 'Authorization Code (With PKCE)' },
  { value: 'authorization_code',      label: 'Authorization Code' },
  { value: 'client_credentials',      label: 'Client Credentials' },
  { value: 'password',                label: 'Password (ROPC)' },
  { value: 'device_code',             label: 'Device Code (RFC 8628)' },
] as const

function OAuth2Fields({ spec, update }: { spec: AuthSpec; update: <K extends keyof AuthSpec>(k: K, v: AuthSpec[K]) => void }) {
  const [advancedOpened, advancedControls] = useDisclosure(false)
  const [discoveryBusy, setDiscoveryBusy] = useState(false)
  const [discoveryErr, setDiscoveryErr] = useState<string | null>(null)
  const [debouncedAuthority] = useDebouncedValue(spec.authority ?? '', 350)
  // Studio owns the redirect URI — it derives one from its own base URL on every run so
  // the value follows the live Aspire port. The user just needs to know what it is to
  // register with their identity provider, hence the read-only field below.
  const [callbackUri, setCallbackUri] = useState<string>('')
  useEffect(() => {
    let cancelled = false
    api.authCallbackUri()
      .then((r) => { if (!cancelled) setCallbackUri(r.redirectUri) })
      .catch(() => { /* fall back to silent — the server will still pick the right value */ })
    return () => { cancelled = true }
  }, [])
  const flow = spec.flow ?? 'authorization_code_pkce'

  useEffect(() => {
    if (!spec.useDiscovery || !debouncedAuthority.trim()) return
    let cancelled = false
    setDiscoveryBusy(true); setDiscoveryErr(null)
    api.oidcDiscovery(debouncedAuthority.trim()).then((doc) => {
      if (cancelled) return
      update('tokenUrl', doc.tokenEndpoint)
      update('authorizeUrl', doc.authorizationEndpoint)
    }).catch((e: Error) => {
      if (!cancelled) setDiscoveryErr(e.message)
    }).finally(() => { if (!cancelled) setDiscoveryBusy(false) })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [spec.useDiscovery, debouncedAuthority])

  return (
    <Stack gap="md">
      <Group grow align="flex-start" gap="md">
        <Select
          label="Grant type"
          value={flow}
          data={OAUTH2_GRANT_TYPES as unknown as { value: string; label: string }[]}
          onChange={(v) => v && update('flow', v as AuthSpec['flow'])}
          allowDeselect={false}
        />
        <Checkbox
          label={<Group gap={4}>Use Discovery <Code fz="xs">.well-known</Code></Group>}
          checked={!!spec.useDiscovery}
          onChange={(e) => update('useDiscovery', e.currentTarget.checked || undefined)}
          mt={24}
        />
      </Group>

      <Box>
        <Text size="sm" fw={500} mb={4}>Authority</Text>
        {/*
          NOTE: forward the AuthVarContext explicitly here. Every other auth field goes
          through <VariableField/> which pulls context from useContext(AuthVarContext);
          this raw <VariableInput/> wasn't, so the highlighter painted {{DEMO_API_URL}}
          (a System-scope var) as unresolved. Same pattern works for any workspace /
          env variable referenced inside the authority URL.
        */}
        <AuthorityInput
          value={spec.authority ?? ''}
          onChange={(v) => update('authority', v || undefined)}
        />
        <Text size="xs" c="dimmed" mt={4}>
          {spec.useDiscovery
            ? 'We fetch /.well-known/openid-configuration to auto-fill Token + Authorize URLs.'
            : 'Optional — fill in Token / Authorize URLs below, or enable Use Discovery.'}
        </Text>
        {discoveryBusy && <Text size="xs" c="dimmed" mt={2}>Fetching discovery document…</Text>}
        {discoveryErr && <Text size="xs" c="red" mt={2}>{discoveryErr}</Text>}
      </Box>

      <VariableField label="Token URL" value={spec.tokenUrl ?? ''} onChange={(v) => update('tokenUrl', v || undefined)} />
      {(flow === 'authorization_code' || flow === 'authorization_code_pkce') && (
        <VariableField label="Authorize URL" value={spec.authorizeUrl ?? ''} onChange={(v) => update('authorizeUrl', v || undefined)} />
      )}
      {flow === 'device_code' && (
        <VariableField
          label="Device Authorization URL"
          value={spec.deviceAuthorizationUrl ?? ''}
          onChange={(v) => update('deviceAuthorizationUrl', v || undefined)}
          hint="Required unless Use Discovery is on and the authority advertises device_authorization_endpoint."
        />
      )}
      {flow === 'password' && (
        <>
          <VariableField
            label="Username"
            value={spec.oauthUsername ?? ''}
            onChange={(v) => update('oauthUsername', v || undefined)}
            hint="ROPC is non-interactive — credentials live in the auth profile (use {{var}} refs)."
          />
          <VariableField
            label="Password"
            value={spec.oauthPassword ?? ''}
            onChange={(v) => update('oauthPassword', v || undefined)}
            secret
          />
        </>
      )}

      <VariableField label="Client ID" value={spec.clientId ?? ''} onChange={(v) => update('clientId', v || undefined)} secret />
      <VariableField
        label="Client Secret"
        value={spec.clientSecret ?? ''}
        onChange={(v) => update('clientSecret', v || undefined)}
        hint={flow === 'authorization_code_pkce' ? 'Omit for public clients (PKCE handles it).' : 'Server-to-server clients require this.'}
        secret
      />

      <TagsInput
        label="Scope"
        placeholder="Type scope and press Enter"
        value={spec.scopes ?? []}
        onChange={(values) => update('scopes', values.length > 0 ? values : undefined)}
        clearable
      />

      {(flow === 'authorization_code' || flow === 'authorization_code_pkce') && (
        <Box>
          <Text size="sm" fw={500} mb={4}>Redirect URL</Text>
          <TextInput
            value={callbackUri || spec.redirectUri || 'Resolving…'}
            readOnly
            styles={{ input: { fontFamily: 'var(--mono)', fontSize: 12, color: 'var(--mantine-color-dimmed)' } }}
          />
          <Text size="xs" c="dimmed" mt={4}>
            Studio derives this from its own base URL. Register it with your identity
            provider's client configuration before running the flow.
          </Text>
        </Box>
      )}

      <Box>
        <Button
          variant="subtle"
          size="xs"
          color="tap"
          leftSection={advancedOpened ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
          onClick={advancedControls.toggle}
          px={0}
        >
          Advanced Settings
        </Button>
        <Collapse expanded={advancedOpened}>
          <Stack gap="md" pt="sm">
            <VariableField label="Audience" value={spec.audience ?? ''} onChange={(v) => update('audience', v || undefined)} />
          </Stack>
        </Collapse>
      </Box>

      {(flow === 'authorization_code' || flow === 'authorization_code_pkce') && callbackUri && spec.clientId && (
        <Alert
          variant="light"
          color="tap"
          icon={<IconAlertCircle size={14} />}
          title="Client configuration"
        >
          <Text size="sm">
            Allow <Code>{callbackUri}</Code> as redirect URL on client: <Code fw={600}>{spec.clientId}</Code>
          </Text>
        </Alert>
      )}
    </Stack>
  )
}

// ---- Azure CLI fields -----------------------------------------------------------------

/**
 * Single editor for both Azure CLI modes. Resource / Scope / Tenant / Subscription are
 * shared between modes — the OBO toggle just layers an extra exchange step on top.
 *
 * On the wire the runner reads `userResource` / `userScope` for the OBO az-cli call and
 * `resource` / `scope` for the direct call. The UI hides that split: a single Resource /
 * Scope pair is shown, and the toggle handler migrates values between the two field
 * pairs so users never see "where did my value go?" when flipping the switch.
 */
function AzureCliPanel({ spec, update, setSpec }: {
  spec: AuthSpec
  update: <K extends keyof AuthSpec>(k: K, v: AuthSpec[K]) => void
  setSpec: React.Dispatch<React.SetStateAction<AuthSpec | null>>
}) {
  const flow = spec.azureFlow ?? 'direct'
  const isObo = flow === 'on_behalf_of'

  const resource = isObo ? spec.userResource : spec.resource
  const scope = isObo ? spec.userScope : spec.scope
  const setResource = (v: string) => update(isObo ? 'userResource' : 'resource', v || undefined)
  const setScope = (v: string) => update(isObo ? 'userScope' : 'scope', v || undefined)

  function toggleObo(next: boolean) {
    setSpec((cur) => {
      if (!cur) return cur
      if (next) {
        return {
          ...cur,
          azureFlow: 'on_behalf_of',
          userResource: cur.userResource ?? cur.resource,
          userScope: cur.userScope ?? cur.scope,
          resource: undefined,
          scope: undefined,
        }
      }
      return {
        ...cur,
        azureFlow: 'direct',
        resource: cur.resource ?? cur.userResource,
        scope: cur.scope ?? cur.userScope,
        userResource: undefined,
        userScope: undefined,
      }
    })
  }

  return (
    <Stack gap="md">
      <Alert color="tap" variant="light" icon={<IconShieldCheck size={14} />}>
        <Text size="xs">
          Tap shells out to <Code>az account get-access-token</Code>. Run <Code>az login</Code> on
          this machine first. Set either <b>Resource</b> (v1) <em>or</em> <b>Scope</b> (v2).
        </Text>
      </Alert>

      <VariableField label="Tenant" value={spec.tenantId ?? ''} onChange={(v) => update('tenantId', v || undefined)} hint={isObo ? 'Pinned on the az-cli call; also used to build the AAD v2 token URL.' : 'Optional — tenant id or domain.'} />
      <VariableField label="Subscription" value={spec.subscription ?? ''} onChange={(v) => update('subscription', v || undefined)} hint="Optional — subscription id or name." />
      <VariableField label="Resource (v1)" value={resource ?? ''} onChange={setResource} hint={isObo ? 'App ID URI of the middle-tier API the user token must target.' : 'e.g. https://management.azure.com/ or https://graph.microsoft.com/'} />
      <VariableField label="Scope (v2)" value={scope ?? ''} onChange={setScope} hint={isObo ? 'Mutually exclusive with Resource.' : 'e.g. https://graph.microsoft.com/.default'} />

      <Divider variant="dashed" />

      <Switch
        label="On-Behalf-Of (OBO)"
        description={<><Code>az</Code> mints a user token for the middle-tier API, then this profile exchanges it for a downstream API token via the <Code>urn:ietf:params:oauth:grant-type:jwt-bearer</Code> grant.</>}
        checked={isObo}
        onChange={(e) => toggleObo(e.currentTarget.checked)}
      />

      {isObo && (
        <>
          <Text size="xs" fw={600} tt="uppercase" c="dimmed" lts={0.5}>OBO exchange</Text>
          <VariableField label="Token URL" value={spec.tokenUrl ?? ''} onChange={(v) => update('tokenUrl', v || undefined)} hint="Defaults to https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token when Tenant is set." />
          <VariableField label="Client ID" value={spec.clientId ?? ''} onChange={(v) => update('clientId', v || undefined)} secret />
          <VariableField label="Client Secret" value={spec.clientSecret ?? ''} onChange={(v) => update('clientSecret', v || undefined)} secret hint="Required for confidential middle-tier apps." />
          <TagsInput
            label="Downstream Scopes"
            placeholder="https://downstream-api.example.com/.default"
            value={spec.scopes ?? []}
            onChange={(values) => update('scopes', values.length > 0 ? values : undefined)}
            clearable
          />
        </>
      )}
    </Stack>
  )
}

// ---- JWT fields -----------------------------------------------------------------------

const JWT_ALGORITHMS = [
  { value: 'HS256', label: 'HS256 (HMAC-SHA256)' },
  { value: 'HS384', label: 'HS384 (HMAC-SHA384)' },
  { value: 'HS512', label: 'HS512 (HMAC-SHA512)' },
  { value: 'RS256', label: 'RS256 (RSA-SHA256)' },
  { value: 'RS384', label: 'RS384 (RSA-SHA384)' },
  { value: 'RS512', label: 'RS512 (RSA-SHA512)' },
  { value: 'PS256', label: 'PS256 (RSA-PSS-SHA256)' },
  { value: 'PS384', label: 'PS384 (RSA-PSS-SHA384)' },
  { value: 'PS512', label: 'PS512 (RSA-PSS-SHA512)' },
  { value: 'ES256', label: 'ES256 (ECDSA P-256)' },
  { value: 'ES384', label: 'ES384 (ECDSA P-384)' },
  { value: 'ES512', label: 'ES512 (ECDSA P-521)' },
]

/**
 * Editor for the self-signed JWT auth type. Mirrors dreamr's CreateRawJwtTokenRequest:
 * algorithm + standard claims (iss/aud/sub/exp) + a JSON blob of extra claims that
 * override the auto-filled set. The signing key is always treated as secret — typically
 * a `{{var}}` reference to a secret-flagged workspace variable.
 */
function JwtFields({ spec, update }: { spec: AuthSpec; update: <K extends keyof AuthSpec>(k: K, v: AuthSpec[K]) => void }) {
  const isHmac = (spec.jwtAlgorithm ?? 'HS256').startsWith('HS')
  return (
    <Stack gap="md">
      <Alert color="tap" variant="light" icon={<IconShieldCheck size={14} />}>
        <Text size="xs">
          Mints a self-signed JWT each time the profile runs and stamps it as
          <Code> Authorization: Bearer …</Code>. Useful for service-to-service "client
          assertion" patterns, dev-mode JWT-bearer endpoints, or signed webhooks.
        </Text>
      </Alert>

      <Group grow align="flex-start" gap="md">
        <Select
          label="Algorithm"
          value={spec.jwtAlgorithm ?? 'HS256'}
          data={JWT_ALGORITHMS}
          onChange={(v) => v && update('jwtAlgorithm', v)}
          allowDeselect={false}
        />
        <TextInput
          label="Expires in (seconds)"
          type="number"
          value={spec.jwtExpiresIn ?? 3600}
          onChange={(e) => update('jwtExpiresIn', Number(e.currentTarget.value) || undefined)}
        />
      </Group>

      <VariableField label="Issuer (iss)" value={spec.jwtIssuer ?? ''} onChange={(v) => update('jwtIssuer', v || undefined)} />
      <VariableField label="Audience (aud)" value={spec.jwtAudience ?? ''} onChange={(v) => update('jwtAudience', v || undefined)} />
      <VariableField label="Subject (sub)" value={spec.jwtSubject ?? ''} onChange={(v) => update('jwtSubject', v || undefined)} />
      <VariableField label="Key ID (kid)" value={spec.jwtKeyId ?? ''} onChange={(v) => update('jwtKeyId', v || undefined)} hint="Optional. For HMAC, we derive a stable kid from the key hash when unset." />

      <VariableField
        label="Signing key"
        value={spec.jwtKey ?? ''}
        onChange={(v) => update('jwtKey', v || undefined)}
        hint={isHmac
          ? 'HMAC: raw shared secret. Use a {{var}} reference to keep it out of the file.'
          : 'RSA / ECDSA: PEM-encoded private key (-----BEGIN PRIVATE KEY-----). Use a {{var}} ref.'}
        secret
      />

      <Box>
        <Text size="sm" fw={500} mb={4}>Payload (extra claims)</Text>
        <Textarea
          value={spec.jwtPayload ?? ''}
          onChange={(e) => update('jwtPayload', e.currentTarget.value || undefined)}
          placeholder={'{\n  "scope": "read:items",\n  "roles": ["admin"]\n}'}
          autosize
          minRows={4}
          styles={{ input: { fontFamily: 'var(--mono)', fontSize: 12 } }}
        />
        <Text size="xs" c="dimmed" mt={4}>
          Optional JSON object. Merged onto the auto-filled <Code>iss / aud / sub / exp / iat / jti</Code> claims — same-named entries override.
        </Text>
      </Box>
    </Stack>
  )
}

// ---- GitHub fields --------------------------------------------------------------------

const GITHUB_MODES = [
  { value: 'pat',    label: 'Personal Access Token' },
  { value: 'gh-cli', label: 'gh CLI (gh auth token)' },
  { value: 'app',    label: 'GitHub App (private key)' },
  { value: 'oauth',  label: 'OAuth App (interactive)' },
] as const

/**
 * Single editor for all four GitHub modes. Mode switcher at the top trims unused fields
 * (mirrors AzureCliPanel's approach). Per-mode field set below.
 *
 *  - PAT: literal `Authorization: Bearer <token>`; the only field is the token itself.
 *  - gh-cli: shells out to `gh auth token` on the host; no persisted fields.
 *  - App: mints an RS256 client-assertion JWT from app id + PEM private key, exchanges
 *    it for an installation access token at `/app/installations/{id}/access_tokens`.
 *  - OAuth: delegates to the oauth2 path with github.com authorize/token endpoints
 *    preset and PKCE on. Scope lives in `scopes:` like every other OAuth2 profile.
 */
function GithubPanel({ spec, update, setSpec }: {
  spec: AuthSpec
  update: <K extends keyof AuthSpec>(k: K, v: AuthSpec[K]) => void
  setSpec: React.Dispatch<React.SetStateAction<AuthSpec | null>>
}) {
  const mode = spec.githubMode ?? 'pat'
  function setMode(next: AuthSpec['githubMode']) {
    setSpec((cur) => {
      if (!cur) return cur
      return { ...cur, githubMode: next }
    })
  }
  return (
    <Stack gap="md">
      <Select
        label="Mode"
        value={mode}
        data={GITHUB_MODES as unknown as { value: string; label: string }[]}
        onChange={(v) => v && setMode(v as AuthSpec['githubMode'])}
        allowDeselect={false}
        maw={320}
      />

      {mode === 'pat' && (
        <VariableField
          label="Personal access token"
          value={spec.token ?? ''}
          onChange={(v) => update('token', v || undefined)}
          hint="Classic PAT or fine-grained token. Use a {{var}} ref to keep it out of the file."
          secret
        />
      )}

      {mode === 'gh-cli' && (
        <Alert color="gray" variant="light">
          <Text size="xs">
            Tap shells out to <Code>gh auth token</Code> on this machine. Install the GitHub CLI
            and run <Code>gh auth login</Code> first. No fields to fill in here — the runner picks
            up whichever account is currently active.
          </Text>
        </Alert>
      )}

      {mode === 'app' && (
        <>
          <VariableField
            label="App ID"
            value={spec.githubAppId ?? ''}
            onChange={(v) => update('githubAppId', v || undefined)}
            hint="The App's numeric ID (or the new-style client_id from the app settings page)."
          />
          <VariableField
            label="Installation ID"
            value={spec.githubInstallationId ?? ''}
            onChange={(v) => update('githubInstallationId', v || undefined)}
            hint="Visible on the installation's URL: /settings/installations/<id>."
          />
          <VariableField
            label="Private key (PEM)"
            value={spec.githubPrivateKey ?? ''}
            onChange={(v) => update('githubPrivateKey', v || undefined)}
            hint="PEM-encoded RSA private key downloaded from the GitHub App page. Use a {{var}} ref."
            secret
          />
        </>
      )}

      {mode === 'oauth' && (
        <>
          <Alert color="gray" variant="light">
            <Text size="xs">
              Standard OAuth2 against github.com — endpoints are preset; you just need a Client ID and Client Secret
              from your <Code>Developer Settings → OAuth Apps</Code> page. Register the Studio's redirect URL
              (shown when you Execute the profile) as the OAuth App's Authorization callback URL.
            </Text>
          </Alert>
          <VariableField
            label="Client ID"
            value={spec.clientId ?? ''}
            onChange={(v) => update('clientId', v || undefined)}
            secret
          />
          <VariableField
            label="Client Secret"
            value={spec.clientSecret ?? ''}
            onChange={(v) => update('clientSecret', v || undefined)}
            secret
          />
          <TagsInput
            label="Scopes"
            placeholder="repo, read:org, …"
            value={spec.scopes ?? []}
            onChange={(values) => update('scopes', values.length > 0 ? values : undefined)}
            clearable
          />
        </>
      )}
    </Stack>
  )
}

// ---- Execute (right-side) panel -------------------------------------------------------

function AuthExecutePanel({ path, dirty, type }: { path: string; dirty: boolean; type: string }) {
  const [result, setResult] = useState<AuthExecuteResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Tracks whether the user has chosen how to consume the login URL. While null we
  // render the open/copy chooser; once set we render the "waiting" view with hints
  // tailored to the chosen path.
  const [launchMode, setLaunchMode] = useState<'open' | 'copy' | null>(null)
  const pollRef = useRef<number | null>(null)
  const clipboard = useClipboard({ timeout: 2000 })
  const { browsers, pref, setPref, openLogin } = useBrowserLaunch()

  useEffect(() => {
    const onMessage = (ev: MessageEvent) => {
      const data = ev.data as { type?: string; state?: string } | undefined
      if (data?.type !== 'tap-auth-callback') return
      if (data.state) void pollOnce(data.state)
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  useEffect(() => () => { if (pollRef.current !== null) window.clearInterval(pollRef.current) }, [])

  async function pollOnce(flowId: string) {
    try {
      const r = await api.authFlow(flowId)
      // The flow-poll endpoint omits loginUrl/userCode (they only come back from
      // /api/auth/execute), so merge them forward — otherwise the chooser/device-code
      // alert disappears on the first poll tick.
      setResult((prev) => ({
        ...r,
        loginUrl: r.loginUrl ?? prev?.loginUrl ?? null,
        userCode: r.userCode ?? prev?.userCode ?? null,
        verificationUri: r.verificationUri ?? prev?.verificationUri ?? null,
        verificationUriComplete: r.verificationUriComplete ?? prev?.verificationUriComplete ?? null,
      }))
      if (r.status !== 'pending') stopPolling()
    } catch { /* keep polling */ }
  }
  function startPolling(flowId: string) {
    stopPolling()
    pollRef.current = window.setInterval(() => { void pollOnce(flowId) }, 800)
  }
  function stopPolling() {
    if (pollRef.current !== null) { window.clearInterval(pollRef.current); pollRef.current = null }
  }

  async function execute(force = false) {
    setBusy(true); setError(null); setResult(null); setLaunchMode(null); stopPolling()
    try {
      const r = await api.executeAuth(path, force)
      setResult(r)
      if (r.status === 'pending' && r.flowId) {
        // Server has already created the flow record, so we can poll right away.
        // For auth-code flows we wait for the user to pick open-vs-copy below;
        // for device-code (no loginUrl, has userCode) the existing alert handles UX.
        startPolling(r.flowId)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally { setBusy(false) }
  }

  function openAuthWindow(loginUrl: string) {
    void openLogin(loginUrl).catch((e) => setError(e instanceof Error ? e.message : String(e)))
    setLaunchMode('open')
  }
  function copyLoginUrl(loginUrl: string) {
    clipboard.copy(loginUrl)
    setLaunchMode('copy')
  }

  const isOAuth = type === 'oauth2'
  return (
    <ScrollArea h="100%" type="auto" scrollbarSize={8}>
      <Stack p="lg" gap="md">
        <Group justify="space-between">
          <Text fw={600} size="sm" tt="uppercase" c="dimmed" lts={0.5}>Try it</Text>
          <Group gap="xs">
            {result?.accessToken && (
              <Tooltip label="Force re-authentication">
                <ActionIcon variant="default" onClick={() => execute(true)} disabled={busy} aria-label="Refresh">
                  <IconRefresh size={14} />
                </ActionIcon>
              </Tooltip>
            )}
            <Tooltip label="Clear cached token (~/.tap/auth-tokens.json)">
              <ActionIcon
                variant="default"
                color="red"
                onClick={async () => {
                  setError(null); setBusy(true); stopPolling()
                  try { await api.clearAuthToken(path); setResult(null); setLaunchMode(null) }
                  catch (e) { setError(e instanceof Error ? e.message : String(e)) }
                  finally { setBusy(false) }
                }}
                disabled={busy}
                aria-label="Clear cached token"
              >
                <IconTrash size={14} />
              </ActionIcon>
            </Tooltip>
            <Button
              leftSection={<IconPlayerPlayFilled size={12} />}
              size="xs"
              onClick={() => execute(false)}
              disabled={dirty || busy}
              loading={busy}
            >
              Execute
            </Button>
          </Group>
        </Group>

        {dirty && (
          <Alert color="gray" variant="light" p="xs"><Text size="xs">Save your changes before executing.</Text></Alert>
        )}
        {error && <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}><Text size="xs">{error}</Text></Alert>}

        {!result && !error && !dirty && (
          <Text size="xs" c="dimmed">
            {isOAuth
              ? 'Click Execute to obtain an access token. For Authorization Code grants we open a browser popup; for Client Credentials we exchange synchronously.'
              : 'Click Execute to evaluate the headers/params this profile injects.'}
          </Text>
        )}

        {result?.status === 'pending' && result.userCode && (
          // Device-code grant — show the code and URL the user needs to type.
          <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
            <Stack gap={6}>
              <Text size="sm">Enter this code on the verification URL:</Text>
              <Code fw={700} fz="md" style={{ letterSpacing: 2 }}>{result.userCode}</Code>
              {result.verificationUriComplete ? (
                <Text size="xs">
                  <a href={result.verificationUriComplete} target="_blank" rel="noopener noreferrer">
                    {result.verificationUriComplete}
                  </a>
                </Text>
              ) : result.verificationUri ? (
                <Text size="xs">
                  <a href={result.verificationUri} target="_blank" rel="noopener noreferrer">
                    {result.verificationUri}
                  </a>
                </Text>
              ) : null}
              <Text size="xs" c="dimmed">Polling for completion…</Text>
            </Stack>
          </Alert>
        )}

        {result?.status === 'pending' && !result.userCode && result.loginUrl && launchMode === null && (
          // Chooser — we've created the flow and started polling; let the user decide
          // whether to open the auth window here, or copy the URL and sign in elsewhere.
          <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
            <Stack gap="xs">
              <Text size="sm" fw={500}>Sign in to continue</Text>
              <Text size="xs" c="dimmed">
                Open the auth window here, or copy the URL and paste it into another browser.
                We'll keep listening for the callback either way.
              </Text>
              <BrowserPicker browsers={browsers} pref={pref} onChange={setPref} />
              <Group gap="xs" mt={4}>
                <Button
                  size="xs"
                  leftSection={<IconExternalLink size={12} />}
                  onClick={() => openAuthWindow(result.loginUrl!)}
                >
                  {pref.browser ? 'Open in browser' : 'Open auth window'}
                </Button>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={clipboard.copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
                  onClick={() => copyLoginUrl(result.loginUrl!)}
                >
                  {clipboard.copied ? 'Copied' : 'Copy URL'}
                </Button>
              </Group>
            </Stack>
          </Alert>
        )}

        {result?.status === 'pending' && !result.userCode && launchMode !== null && (
          <Alert color="tap" variant="light" icon={<IconExternalLink size={14} />}>
            <Stack gap={4}>
              <Text size="sm">
                {launchMode === 'open'
                  ? 'Waiting for sign-in… (the popup should be open)'
                  : 'URL copied — waiting for sign-in in your other browser…'}
              </Text>
              {result.loginUrl && (
                <Group gap="md">
                  <Text size="xs">
                    <a href={result.loginUrl} target="_blank" rel="noopener noreferrer">
                      {launchMode === 'open' ? 'Open URL manually' : 'Open URL here'}
                    </a>
                  </Text>
                  <Text
                    size="xs"
                    c="tap"
                    style={{ cursor: 'pointer' }}
                    onClick={() => copyLoginUrl(result.loginUrl!)}
                  >
                    {clipboard.copied ? 'Copied' : 'Copy URL again'}
                  </Text>
                </Group>
              )}
            </Stack>
          </Alert>
        )}

        {result?.status === 'failed' && (
          <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>
            <Text size="xs">{result.error ?? 'Authentication failed.'}</Text>
          </Alert>
        )}

        {result?.status === 'completed' && <TokenResultView result={result} />}
      </Stack>
    </ScrollArea>
  )
}

function TokenResultView({ result }: { result: AuthExecuteResponse }) {
  const decoded = result.accessToken ? decodeJwt(result.accessToken) : null

  return (
    <Stack gap="md">
      {result.fromCache && (
        <Alert color="gray" variant="light" p="xs"><Text size="xs">Token loaded from local cache.</Text></Alert>
      )}

      {result.headers && Object.keys(result.headers).length > 0 && (
        <TokenSection label="Headers">{JSON.stringify(result.headers, null, 2)}</TokenSection>
      )}

      {result.accessToken && (
        <Box>
          <Text size="xs" fw={600} tt="uppercase" c="tap" lts={0.5} mb={4}>Access token</Text>
          <Code block fz="xs" style={{ wordBreak: 'break-all', maxHeight: 220, overflow: 'auto' }}>
            {result.accessToken.length > 200 ? result.accessToken.slice(0, 200) + '…' : result.accessToken}
          </Code>
          {result.expiresAt && (
            <Text size="xs" c="dimmed" mt={4}>Expires {new Date(result.expiresAt).toLocaleString()}</Text>
          )}
        </Box>
      )}

      {decoded && <TokenSection label="Decoded payload">{JSON.stringify(decoded.payload, null, 2)}</TokenSection>}

      {result.idToken && (
        <TokenSection label="ID token">{result.idToken.slice(0, 200) + (result.idToken.length > 200 ? '…' : '')}</TokenSection>
      )}
    </Stack>
  )
}

function TokenSection({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Box>
      <Text size="xs" fw={600} tt="uppercase" c="tap" lts={0.5} mb={4}>{label}</Text>
      <Code block fz="xs" style={{ wordBreak: 'break-all', maxHeight: 220, overflow: 'auto' }}>{children}</Code>
    </Box>
  )
}
