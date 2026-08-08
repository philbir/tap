import {
  Alert, Anchor, Badge, Box, Button, Code, Group, Modal, PasswordInput, ScrollArea,
  SimpleGrid, Stack, Stepper, TagsInput, Text, TextInput, Textarea, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertCircle, IconBrandAws, IconBrandAzure, IconBrandGithub, IconBrandOauth,
  IconBrandWindows, IconKey, IconLock, IconShieldCheck, IconTerminal2, IconWand,
  type Icon as TablerIcon,
} from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { AuthSpec, WorkspaceFileKind } from '../api/types'
import { useTapStore } from '../store'

/**
 * Provider-pick + required-fields wizard for creating an auth profile. Sits between
 * <CreateNewDialog/> (which used to PUT a bearer profile inline) and <AuthEditor/>
 * (where the user fine-tunes once the profile exists).
 *
 * Templates are flat — one tile per real scenario rather than picking an auth type and
 * then a mode. So GitHub shows four tiles (PAT / gh CLI / App / OAuth) instead of one
 * "GitHub" tile with a sub-picker; same for Azure (CLI / Entra OAuth). That cuts a step.
 * The on-disk YAML is still the canonical type+mode pair — only the UI flattens.
 */
interface AuthTemplate {
  /** Unique key, surfaced as the wizard's local state. */
  key: string
  label: string
  description: string
  icon: TablerIcon
  color: string
  badge?: string
  /** Builds the AuthSpec from the user's name + the template's fixed shape + the user
   *  inputs collected in step 2. */
  build: (name: string, path: string, fields: Record<string, string | string[]>) => AuthSpec
  /** Schema of the fields collected in step 2. */
  inputs: AuthInput[]
  /** Intro shown above the field form so the user knows what they're configuring. */
  intro?: React.ReactNode
}

interface AuthInput {
  name: string
  label: string
  placeholder?: string
  /** `password` masks the value, `tags` renders TagsInput (for scopes), `textarea` for long
   *  PEM blobs, `text` (default) for everything else. */
  kind?: 'text' | 'password' | 'tags' | 'textarea'
  hint?: string
  optional?: boolean
}

const TEMPLATES: AuthTemplate[] = [
  {
    key: 'github-pat',
    label: 'GitHub Personal Access Token',
    description: 'Classic or fine-grained PAT. Quickest way to call the GitHub REST/GraphQL API.',
    icon: IconBrandGithub,
    color: 'dark',
    badge: 'GitHub',
    intro: (
      <Text size="xs" c="dimmed">
        Create one at{' '}
        <Anchor href="https://github.com/settings/tokens" target="_blank" rel="noopener noreferrer">
          github.com/settings/tokens
        </Anchor>
        . Tap stores a <Code>{'{{var}}'}</Code>-friendly token field; secrets are easiest to keep out
        of the workspace file via a variable provider.
      </Text>
    ),
    inputs: [
      { name: 'token', label: 'Token', kind: 'password', placeholder: 'ghp_… or github_pat_…',
        hint: 'You can also reference a workspace variable: {{env:GITHUB_TOKEN}}' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'github', githubMode: 'pat',
      token: (f.token as string) || undefined,
    }),
  },
  {
    key: 'github-cli',
    label: 'GitHub CLI (gh auth token)',
    description: 'Reuses whichever account `gh auth login` is signed in as on this machine.',
    icon: IconTerminal2,
    color: 'dark',
    badge: 'GitHub',
    intro: (
      <Text size="xs" c="dimmed">
        Tap shells out to <Code>gh auth token</Code>. Run <Code>gh auth login</Code> first.
        No fields to fill in — the runner picks up whichever account is currently active.
      </Text>
    ),
    inputs: [],
    build: (name, path) => ({
      path, id: null, name, type: 'github', githubMode: 'gh-cli',
    }),
  },
  {
    key: 'github-app',
    label: 'GitHub App',
    description: 'Mints an RS256 JWT from your App private key, exchanges for an installation token.',
    icon: IconShieldCheck,
    color: 'dark',
    badge: 'GitHub',
    intro: (
      <Text size="xs" c="dimmed">
        Download the App's <em>.pem</em> private key from the App's settings page. Tap signs a 9-minute
        client-assertion JWT and POSTs it to <Code>/app/installations/{'{id}'}/access_tokens</Code>; the
        returned installation token is cached until it expires.
      </Text>
    ),
    inputs: [
      { name: 'appId', label: 'App ID', placeholder: '123456 or client_id',
        hint: 'Numeric App ID or the new-style client_id from the App settings page.' },
      { name: 'installationId', label: 'Installation ID', placeholder: '12345678',
        hint: 'Visible on the installation page URL: /settings/installations/<id>.' },
      { name: 'privateKey', label: 'Private key (PEM)', kind: 'textarea',
        placeholder: '-----BEGIN RSA PRIVATE KEY-----\n…\n-----END RSA PRIVATE KEY-----',
        hint: 'Paste the .pem file contents, or use a {{var}} ref so the key never lands in the workspace.' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'github', githubMode: 'app',
      githubAppId: (f.appId as string) || undefined,
      githubInstallationId: (f.installationId as string) || undefined,
      githubPrivateKey: (f.privateKey as string) || undefined,
    }),
  },
  {
    key: 'github-oauth',
    label: 'GitHub OAuth App',
    description: 'Interactive popup sign-in against github.com — for tools that act as a user.',
    icon: IconBrandOauth,
    color: 'dark',
    badge: 'GitHub',
    intro: (
      <Text size="xs" c="dimmed">
        Register at <Code>Developer Settings → OAuth Apps</Code>. After saving, click Execute on the
        profile to see the redirect URL to register as the OAuth App's callback. Endpoints are preset.
      </Text>
    ),
    inputs: [
      { name: 'clientId', label: 'Client ID', placeholder: 'Iv1.…', kind: 'password' },
      { name: 'clientSecret', label: 'Client Secret', kind: 'password', optional: true,
        hint: 'Optional for PKCE-only public clients.' },
      { name: 'scopes', label: 'Scopes', kind: 'tags', placeholder: 'repo, read:org…', optional: true },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'github', githubMode: 'oauth',
      clientId: (f.clientId as string) || undefined,
      clientSecret: (f.clientSecret as string) || undefined,
      scopes: ((f.scopes as string[] | undefined)?.length ?? 0) > 0 ? (f.scopes as string[]) : undefined,
    }),
  },
  {
    key: 'bearer',
    label: 'Bearer token',
    description: 'Sends a static Authorization: Bearer … header.',
    icon: IconKey,
    color: 'tap',
    inputs: [
      { name: 'token', label: 'Token', kind: 'password', placeholder: '{{env:MY_TOKEN}}', optional: true,
        hint: 'Leave empty to fill in later. A {{var}} reference keeps the value out of the file.' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'bearer',
      token: (f.token as string) || '{{env:MY_TOKEN}}',
    }),
  },
  {
    key: 'basic',
    label: 'Basic auth',
    description: 'Username + password, base64-encoded into the Authorization header.',
    icon: IconLock,
    color: 'gray',
    inputs: [
      { name: 'username', label: 'Username', placeholder: 'admin' },
      { name: 'password', label: 'Password', kind: 'password', placeholder: '{{env:BASIC_PASSWORD}}' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'basic',
      username: (f.username as string) || undefined,
      password: (f.password as string) || undefined,
    }),
  },
  {
    key: 'apiKey',
    label: 'API key',
    description: 'Injects a fixed header (e.g. X-API-Key) on every request.',
    icon: IconKey,
    color: 'teal',
    inputs: [
      { name: 'apiKeyName', label: 'Header name', placeholder: 'X-API-Key' },
      { name: 'apiKeyValue', label: 'Value', kind: 'password', placeholder: '{{env:MY_API_KEY}}' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'apiKey', in: 'header',
      apiKeyName: (f.apiKeyName as string) || undefined,
      apiKeyValue: (f.apiKeyValue as string) || undefined,
    }),
  },
  {
    key: 'oauth2',
    label: 'OAuth 2.0 / OIDC',
    description: 'Generic OAuth flow — authorize/token URLs configured by hand or via discovery.',
    icon: IconBrandOauth,
    color: 'violet',
    inputs: [
      { name: 'authority', label: 'Authority', placeholder: 'https://login.example.com/realms/main',
        hint: 'Root URL of the identity provider. Used for .well-known discovery in the editor.' },
      { name: 'clientId', label: 'Client ID', placeholder: 'tap-studio', kind: 'password' },
      { name: 'scopes', label: 'Scopes', kind: 'tags', placeholder: 'openid, profile', optional: true },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'oauth2',
      flow: 'authorization_code_pkce',
      useDiscovery: true,
      authority: (f.authority as string) || undefined,
      clientId: (f.clientId as string) || undefined,
      scopes: ((f.scopes as string[] | undefined)?.length ?? 0) > 0 ? (f.scopes as string[]) : undefined,
    }),
  },
  {
    key: 'entra',
    label: 'Microsoft Entra (Azure AD)',
    description: 'Pre-fills the AAD v2 authority — you just provide tenant + client id.',
    icon: IconBrandWindows,
    color: 'blue',
    badge: 'Microsoft',
    inputs: [
      { name: 'tenant', label: 'Tenant', placeholder: 'contoso.onmicrosoft.com or tenant GUID' },
      { name: 'clientId', label: 'Client ID', kind: 'password', placeholder: '00000000-0000-…' },
      { name: 'scopes', label: 'Scopes', kind: 'tags', placeholder: 'https://graph.microsoft.com/.default', optional: true },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'oauth2',
      flow: 'authorization_code_pkce',
      useDiscovery: true,
      authority: `https://login.microsoftonline.com/${(f.tenant as string) || '{{TENANT_ID}}'}/v2.0`,
      clientId: (f.clientId as string) || undefined,
      scopes: ((f.scopes as string[] | undefined)?.length ?? 0) > 0 ? (f.scopes as string[]) : undefined,
    }),
  },
  {
    key: 'azure-cli',
    label: 'Azure CLI',
    description: 'Shells out to `az account get-access-token`. Resource or scope as audience.',
    icon: IconBrandAzure,
    color: 'blue',
    badge: 'Azure',
    inputs: [
      { name: 'scope', label: 'Scope', placeholder: 'https://graph.microsoft.com/.default',
        hint: 'v2 scope passed to `az --scope`. Leave empty if you set a Resource instead.', optional: true },
      { name: 'resource', label: 'Resource (v1)', placeholder: 'https://management.azure.com/',
        hint: 'Alternative to Scope. Use one or the other.', optional: true },
      { name: 'tenant', label: 'Tenant', optional: true },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'azure-cli', azureFlow: 'direct',
      scope: (f.scope as string) || undefined,
      resource: (f.resource as string) || undefined,
      tenantId: (f.tenant as string) || undefined,
    }),
  },
  {
    key: 'jwt',
    label: 'Signed JWT',
    description: 'Mints a self-signed JWT each call. Useful for service-to-service patterns.',
    icon: IconShieldCheck,
    color: 'grape',
    inputs: [
      { name: 'jwtIssuer', label: 'Issuer (iss)', placeholder: 'urn:tap-studio', optional: true },
      { name: 'jwtAudience', label: 'Audience (aud)', placeholder: 'https://api.example.com', optional: true },
      { name: 'jwtKey', label: 'Signing key', kind: 'textarea',
        placeholder: 'HMAC secret or PEM-encoded private key',
        hint: 'HS256 by default — flip to RS256/ES256 in the editor for PEM keys.' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'jwt',
      jwtAlgorithm: 'HS256',
      // iss/aud are plain claims — seed the payload with whatever was filled in, then the
      // editor's JSON payload pane owns them from here on.
      jwtPayload: jsonClaims({ iss: f.jwtIssuer as string | undefined, aud: f.jwtAudience as string | undefined }),
      jwtKey: (f.jwtKey as string) || undefined,
    }),
  },
  {
    key: 'aws',
    label: 'AWS Signature V4',
    description: 'Signs requests with AWS access keys for direct API calls.',
    icon: IconBrandAws,
    color: 'orange',
    badge: 'AWS',
    inputs: [
      { name: 'region', label: 'Region', placeholder: 'us-east-1' },
      { name: 'service', label: 'Service', placeholder: 'execute-api' },
      { name: 'accessKeyId', label: 'Access Key ID', kind: 'password' },
      { name: 'secretAccessKey', label: 'Secret Access Key', kind: 'password' },
    ],
    build: (name, path, f) => ({
      path, id: null, name, type: 'aws-sigv4',
      region: (f.region as string) || undefined,
      service: (f.service as string) || undefined,
      accessKeyId: (f.accessKeyId as string) || undefined,
      secretAccessKey: (f.secretAccessKey as string) || undefined,
    }),
  },
  {
    key: 'custom',
    label: 'Custom headers',
    description: 'Free-form header injection — fill in headers/cookies in the editor.',
    icon: IconWand,
    color: 'gray',
    inputs: [],
    build: (name, path) => ({
      path, id: null, name, type: 'custom',
    }),
  },
]

/** Build a JWT payload JSON object from the claims the user actually filled in. */
function jsonClaims(claims: Record<string, string | undefined>): string | undefined {
  const entries = Object.entries(claims).filter(([, v]) => v && v.trim().length > 0)
  return entries.length > 0 ? JSON.stringify(Object.fromEntries(entries), null, 2) : undefined
}

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  /** Pre-seeded display name from the create dialog. Editable in step 2. */
  initialName: string
  /** Workspace-relative directory the profile is written to — `auth` for a shared profile,
   *  `collections/<slug>` for one owned by a collection. Chosen in the create dialog. */
  targetDir?: string
  onCreated: (path: string, kind: WorkspaceFileKind) => void
}

export function AuthWizard({ open, onOpenChange, initialName, targetDir = 'auth', onCreated }: Props) {
  const reload = useTapStore((s) => s.reload)
  const [step, setStep] = useState(0)
  const [templateKey, setTemplateKey] = useState<string | null>(null)
  const [name, setName] = useState(initialName)
  const [fields, setFields] = useState<Record<string, string | string[]>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Reset internal state whenever the dialog is re-opened with a new name — keeps the
  // wizard usable for back-to-back auth creations without leaking field values.
  useEffect(() => {
    if (open) {
      setStep(0); setTemplateKey(null); setName(initialName); setFields({}); setError(null)
    }
  }, [open, initialName])

  const template = TEMPLATES.find((t) => t.key === templateKey) ?? null
  const slug = nameToSlug(name)
  const targetPath = slug ? `${targetDir}/${slug}.auth.md` : ''

  function pickTemplate(key: string) {
    setTemplateKey(key)
    const tpl = TEMPLATES.find((t) => t.key === key)
    if (tpl?.inputs.length === 0) {
      // Skip the field step entirely for templates with no required inputs.
      setStep(2)
    } else {
      setStep(1)
    }
  }

  async function create() {
    if (!template || !slug) return
    setBusy(true); setError(null)
    try {
      const spec = template.build(name, targetPath, fields)
      await api.saveAuthSpec(spec)
      await reload()
      onCreated(targetPath, 'auth')
      onOpenChange(false)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      size="xl"
      title={
        <Group gap={6}>
          <IconShieldCheck size={16} />
          <Text fw={600}>New auth profile</Text>
          {template && <Badge size="sm" variant="light" color={template.color}>{template.label}</Badge>}
        </Group>
      }
    >
      <Stepper active={step} onStepClick={(s) => template && setStep(s)} size="xs" mb="md">
        <Stepper.Step label="Provider" description="Pick a template" />
        <Stepper.Step label="Configure" description="Required fields" />
        <Stepper.Step label="Review" description="Confirm + create" />
      </Stepper>

      {step === 0 && (
        <ScrollArea h={520} type="auto" scrollbarSize={8}>
          <SimpleGrid cols={2} spacing="sm" pr="sm">
            {TEMPLATES.map((t) => {
              const TIcon = t.icon
              const active = templateKey === t.key
              return (
                <UnstyledButton
                  key={t.key}
                  onClick={() => pickTemplate(t.key)}
                  p="sm"
                  style={{
                    border: `1px solid ${active ? `var(--mantine-color-${t.color}-filled)` : 'var(--mantine-color-default-border)'}`,
                    borderRadius: 8,
                    background: active ? `var(--mantine-color-${t.color}-light)` : 'transparent',
                  }}
                >
                  <Group gap="sm" align="flex-start" wrap="nowrap">
                    <TIcon size={22} color={`var(--mantine-color-${t.color}-6)`} style={{ flexShrink: 0 }} />
                    <Stack gap={2} flex={1}>
                      <Group gap={6}>
                        <Text fw={600} size="sm">{t.label}</Text>
                        {t.badge && <Badge size="xs" variant="dot" color={t.color}>{t.badge}</Badge>}
                      </Group>
                      <Text size="xs" c="dimmed">{t.description}</Text>
                    </Stack>
                  </Group>
                </UnstyledButton>
              )
            })}
          </SimpleGrid>
        </ScrollArea>
      )}

      {step === 1 && template && (
        <Stack gap="md">
          {template.intro && (
            <Alert variant="light" color={template.color} icon={<IconShieldCheck size={14} />}>
              {template.intro}
            </Alert>
          )}
          <TextInput
            label="Name"
            description="Shown in pickers and tabs. The file slug is derived from this."
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            placeholder="e.g. GitHub – production"
            autoFocus
          />
          {template.inputs.map((input) => (
            <FieldEditor
              key={input.name}
              input={input}
              value={fields[input.name]}
              onChange={(v) => setFields((cur) => ({ ...cur, [input.name]: v }))}
            />
          ))}
          {slug && (
            <Text size="xs" c="dimmed">
              Will be created at <Code fz="xs">.tap/{targetPath}</Code>
            </Text>
          )}
          <Group justify="space-between" mt="sm">
            <Button variant="default" onClick={() => setStep(0)} disabled={busy}>Back</Button>
            <Button onClick={() => setStep(2)} disabled={!slug || !template}>Review</Button>
          </Group>
        </Stack>
      )}

      {step === 2 && template && (
        <Stack gap="md">
          <TextInput
            label="Name"
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            disabled={busy}
            placeholder="e.g. GitHub – production"
          />
          <Box>
            <Text size="xs" fw={600} tt="uppercase" c="dimmed" lts={0.5} mb={4}>Template</Text>
            <Group gap={6}>
              <template.icon size={16} color={`var(--mantine-color-${template.color}-6)`} />
              <Text size="sm">{template.label}</Text>
            </Group>
          </Box>
          {template.inputs.length > 0 && (
            <Box>
              <Text size="xs" fw={600} tt="uppercase" c="dimmed" lts={0.5} mb={4}>Configured fields</Text>
              <Stack gap={4}>
                {template.inputs.map((input) => {
                  const v = fields[input.name]
                  const display = Array.isArray(v) ? v.join(' ') : (v ?? '')
                  const hidden = input.kind === 'password' && typeof v === 'string' && v.length > 0
                  return (
                    <Group key={input.name} gap={6} align="center">
                      <Text size="xs" c="dimmed" w={140}>{input.label}</Text>
                      <Code fz="xs">{hidden ? '••••••••' : (display || '—')}</Code>
                    </Group>
                  )
                })}
              </Stack>
            </Box>
          )}
          {slug && (
            <Text size="xs" c="dimmed">
              Created at <Code fz="xs">.tap/{targetPath}</Code>. You can fine-tune everything in the editor afterwards.
            </Text>
          )}
          {error && (
            <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />}>{error}</Alert>
          )}
          <Group justify="space-between" mt="sm">
            <Button variant="default" onClick={() => setStep(template.inputs.length === 0 ? 0 : 1)} disabled={busy}>Back</Button>
            <Button onClick={create} loading={busy} disabled={!slug}>Create profile</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  )
}

function FieldEditor({ input, value, onChange }: {
  input: AuthInput
  value: string | string[] | undefined
  onChange: (v: string | string[]) => void
}) {
  const kind = input.kind ?? 'text'
  if (kind === 'password') {
    return (
      <PasswordInput
        label={input.label}
        placeholder={input.placeholder}
        description={input.hint}
        value={(value as string) ?? ''}
        onChange={(e) => onChange(e.currentTarget.value)}
        withAsterisk={!input.optional}
      />
    )
  }
  if (kind === 'tags') {
    return (
      <TagsInput
        label={input.label}
        placeholder={input.placeholder}
        description={input.hint}
        value={(value as string[]) ?? []}
        onChange={(v) => onChange(v)}
        clearable
        acceptValueOnBlur
      />
    )
  }
  if (kind === 'textarea') {
    return (
      <Textarea
        label={input.label}
        placeholder={input.placeholder}
        description={input.hint}
        value={(value as string) ?? ''}
        onChange={(e) => onChange(e.currentTarget.value)}
        autosize
        minRows={4}
        maxRows={10}
        withAsterisk={!input.optional}
        styles={{ input: { fontFamily: 'var(--mono)', fontSize: 12 } }}
      />
    )
  }
  return (
    <TextInput
      label={input.label}
      placeholder={input.placeholder}
      description={input.hint}
      value={(value as string) ?? ''}
      onChange={(e) => onChange(e.currentTarget.value)}
      withAsterisk={!input.optional}
    />
  )
}

function nameToSlug(name: string): string {
  return name.trim().toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .replace(/-+/g, '-')
    .slice(0, 60)
}
