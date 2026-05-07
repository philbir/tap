import { useEffect, useState } from 'react'
import type { TunnelProfile, TunnelMode } from './profileTypes'
import { TUNNEL_MODES, emptyProfile } from './profileTypes'

type Tab = 'intro' | 'profiles'

export function CloudflarePage() {
  const [tab, setTab] = useState<Tab>('intro')

  return (
    <div style={{ flex: 1, overflow: 'auto', display: 'flex', flexDirection: 'column' }}>
      <div
        style={{
          display: 'flex',
          gap: '4px',
          padding: '8px 16px 0',
          borderBottom: '1px solid var(--border)',
          background: 'var(--bg-raised)',
        }}
      >
        <PageTab label="Cloudflare guide" active={tab === 'intro'} onClick={() => setTab('intro')} />
        <PageTab label="Tunnel profiles" active={tab === 'profiles'} onClick={() => setTab('profiles')} />
      </div>

      <div style={{ flex: 1, overflow: 'auto' }}>
        {tab === 'intro' ? <IntroSection /> : <ProfilesSection />}
      </div>
    </div>
  )
}

function PageTab({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      style={{
        background: 'transparent',
        border: 'none',
        borderBottom: active ? '2px solid var(--accent)' : '2px solid transparent',
        borderRadius: 0,
        color: active ? 'var(--accent)' : 'var(--text-muted)',
        padding: '8px 14px',
        fontSize: '13px',
        fontWeight: 600,
        cursor: 'pointer',
      }}
    >
      {label}
    </button>
  )
}

function IntroSection() {
  return (
    <div style={{ maxWidth: 880, margin: '0 auto', padding: '28px 24px 64px' }}>
      <h1 style={{ marginTop: 0, fontSize: 22 }}>Cloudflare Tunnels with tap</h1>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55 }}>
        tap can route traffic from a public Cloudflare hostname through the inspector to your local
        upstream. Cloudflare runs <code>cloudflared</code> as the connector — tap installs and
        manages the process for you. Pick the mode that matches what you have:
      </p>

      <Prerequisites />

      <h2 style={{ fontSize: 17, marginTop: 32, marginBottom: 4 }}>Tunnel modes</h2>

      <ModeCard
        title="Quick (TryCloudflare)"
        slug="quick"
        when="No Cloudflare account, just want a public URL for a few minutes."
        bullets={[
          'Random *.trycloudflare.com hostname, no auth on Cloudflare side.',
          'Best for ad-hoc demos and webhook testing.',
          'No persistence — the URL changes every run.',
        ]}
        cli="tap run http://localhost:3000 --quick"
      />

      <ModeCard
        title="Token (existing dashboard tunnel)"
        slug="token"
        when="You already created a tunnel in the Zero Trust dashboard."
        bullets={[
          'You configure ingress rules in the dashboard pointing at http://localhost:<proxyPort>.',
          'tap launches cloudflared with the token; hostname stays whatever you set in the UI.',
          'Token is sensitive — keep it in a profile or env var, not in shell history.',
        ]}
        cli="tap run http://localhost:3000 --token <CF_TUNNEL_TOKEN>"
        link={{
          label: 'Cloudflare dashboard → Networks → Tunnels',
          href: 'https://one.dash.cloudflare.com/?to=/:account/networks/tunnels',
        }}
      />

      <ModeCard
        title="API-managed"
        slug="api-managed"
        when="You want tap to create/manage the tunnel for you."
        bullets={[
          'tap calls the Cloudflare API to create a named tunnel and write its credentials locally.',
          'Set --hostname to attach a known FQDN; tap creates the DNS CNAME automatically.',
          'Requires an API token with Account → Cloudflare Tunnel: Edit (and Zone → DNS: Edit if you use --hostname).',
        ]}
        cli="tap run http://localhost:3000 --api-managed my-tunnel --account <ID> --api-token <TOK> --hostname app.example.com"
      />

      <ModeCard
        title="Dynamic hostname"
        slug="dynamic"
        when="You want a fresh, predictable hostname every run on a zone you own."
        bullets={[
          'tap picks a hostname like tap-<random>.<zone> and sets up the CNAME for the duration of the run.',
          'Same API token requirements as API-managed.',
          'Hostname is removed when tap shuts down.',
        ]}
        cli="tap run http://localhost:3000 --dynamic-zone example.com --account <ID> --api-token <TOK>"
      />

    </div>
  )
}

function Prerequisites() {
  return (
    <section style={{ marginTop: 18 }}>
      <h2 style={{ fontSize: 17, marginTop: 0, marginBottom: 8 }}>Prerequisites</h2>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55, marginTop: 0 }}>
        <b style={{ color: 'var(--text)' }}>Quick</b> mode needs nothing — tap and{' '}
        <code>cloudflared</code> only. The other modes need credentials from your Cloudflare
        account, listed below. <code>cloudflared</code> itself can be installed automatically with{' '}
        <code>tap install-cloudflared</code> or by passing <code>--auto-install</code>.
      </p>

      <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', marginTop: 10 }}>
        <PrereqCard
          title="Tunnel token"
          forModes={['Token']}
          summary="One opaque token that authenticates a specific tunnel created in the dashboard. Used in --token mode."
          steps={[
            <>
              Go to{' '}
              <a href="https://one.dash.cloudflare.com/?to=/:account/networks/tunnels" target="_blank" rel="noreferrer">
                Zero Trust → Networks → Tunnels
              </a>
              .
            </>,
            <>Click <b>Create a tunnel</b>, choose <b>Cloudflared</b>, give it a name.</>,
            <>
              On the <b>Install connector</b> step, copy the token from the install command (the
              long string after <code>--token</code>). That is the value you pass to{' '}
              <code>--token</code> or save in a profile.
            </>,
            <>
              Add public hostnames in the dashboard pointing at{' '}
              <code>http://localhost:&lt;proxy-port&gt;</code> (default <code>4444</code>).
            </>,
          ]}
          notes={[
            'Treat the token like a password — it grants connector access to that tunnel.',
            'Rotate from the dashboard if it leaks; the old token stops working immediately.',
          ]}
          link={{
            label: 'Open dashboard → Networks → Tunnels',
            href: 'https://one.dash.cloudflare.com/?to=/:account/networks/tunnels',
          }}
        />

        <PrereqCard
          title="API token + Account ID"
          forModes={['ApiManaged', 'Dynamic']}
          summary="Used by tap to create/reuse tunnels and manage DNS via the Cloudflare API. Required for --api-managed and --dynamic."
          steps={[
            <>
              Open the{' '}
              <a href="https://dash.cloudflare.com/profile/api-tokens" target="_blank" rel="noreferrer">
                API tokens page
              </a>{' '}
              and click <b>Create Token → Custom token</b>.
            </>,
            <>
              Add permission <code>Account → Cloudflare Tunnel → Edit</code> (required for
              api-managed/dynamic).
            </>,
            <>
              Add permission <code>Zone → DNS → Edit</code> for the zone you want public hostnames
              under (required for <code>--hostname</code> and <code>--dynamic</code>).
            </>,
            <>Scope to the specific account/zone you'll use; then create and copy the token.</>,
            <>
              Find your <b>Account ID</b> in{' '}
              <a href="https://dash.cloudflare.com/" target="_blank" rel="noreferrer">
                dash.cloudflare.com
              </a>{' '}
              — sidebar of any zone overview, or in the URL after <code>/</code>.
            </>,
          ]}
          notes={[
            'Pass via --api-token / --account, env CLOUDFLARE_API_TOKEN / CLOUDFLARE_ACCOUNT_ID, or save into a profile.',
            'The token is stored in plaintext on disk in profiles — keep the profiles directory private.',
          ]}
          link={{
            label: 'Create an API token',
            href: 'https://dash.cloudflare.com/profile/api-tokens',
          }}
        />
      </div>
    </section>
  )
}

function PrereqCard({
  title,
  forModes,
  summary,
  steps,
  notes,
  link,
}: {
  title: string
  forModes: string[]
  summary: string
  steps: React.ReactNode[]
  notes?: string[]
  link?: { label: string; href: string }
}) {
  return (
    <article
      style={{
        padding: '14px 16px',
        border: '1px solid var(--border)',
        borderRadius: 8,
        background: 'var(--bg-raised)',
      }}
    >
      <header style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 8, marginBottom: 6 }}>
        <h3 style={{ margin: 0, fontSize: 14 }}>{title}</h3>
        {forModes.map((m) => (
          <span
            key={m}
            style={{
              fontSize: 9.5,
              textTransform: 'uppercase',
              letterSpacing: '0.06em',
              color: '#f6821f',
              border: '1px solid color-mix(in srgb, #f6821f 35%, transparent)',
              padding: '1px 5px',
              borderRadius: 3,
              background: '#fff',
            }}
          >
            {m === 'ApiManaged' ? 'api-managed' : m.toLowerCase()}
          </span>
        ))}
      </header>
      <p style={{ margin: '4px 0 8px', color: 'var(--text-muted)', fontSize: 12.5, lineHeight: 1.5 }}>{summary}</p>
      <ol style={{ margin: '4px 0 8px', paddingLeft: 18, color: 'var(--text-muted)', fontSize: 12.5, lineHeight: 1.55 }}>
        {steps.map((s, i) => (
          <li key={i}>{s}</li>
        ))}
      </ol>
      {notes && notes.length > 0 && (
        <ul style={{ margin: '4px 0 8px', paddingLeft: 18, color: 'var(--text-muted)', fontSize: 12, lineHeight: 1.5 }}>
          {notes.map((n, i) => (
            <li key={i} style={{ listStyleType: 'square' }}>{n}</li>
          ))}
        </ul>
      )}
      {link && (
        <a href={link.href} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)', fontSize: 12 }}>
          {link.label} ↗
        </a>
      )}
    </article>
  )
}

function ModeCard({
  title,
  slug,
  when,
  bullets,
  cli,
  link,
}: {
  title: string
  slug: string
  when: string
  bullets: string[]
  cli: string
  link?: { label: string; href: string }
}) {
  return (
    <section
      style={{
        marginTop: 16,
        padding: '14px 16px',
        border: '1px solid var(--border)',
        borderRadius: 8,
        background: 'var(--bg-raised)',
      }}
    >
      <header style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 6 }}>
        <span
          style={{
            fontSize: 10,
            textTransform: 'uppercase',
            letterSpacing: '0.06em',
            color: '#f6821f',
            border: '1px solid color-mix(in srgb, #f6821f 35%, transparent)',
            padding: '1px 6px',
            borderRadius: 3,
            background: '#fff',
          }}
        >
          {slug}
        </span>
        <h3 style={{ margin: 0, fontSize: 15 }}>{title}</h3>
      </header>
      <p style={{ margin: '4px 0 8px', color: 'var(--text-muted)', fontSize: 13 }}>
        <b style={{ color: 'var(--text)' }}>When to use:</b> {when}
      </p>
      <ul style={{ margin: '4px 0 10px', paddingLeft: 18, color: 'var(--text-muted)', lineHeight: 1.5 }}>
        {bullets.map((b, i) => (
          <li key={i}>{b}</li>
        ))}
      </ul>
      <pre
        style={{
          background: 'var(--bg-input)',
          border: '1px solid var(--border)',
          borderRadius: 6,
          padding: '8px 10px',
          fontSize: 12,
        }}
      >
        {cli}
      </pre>
      {link && (
        <a href={link.href} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)', fontSize: 12 }}>
          {link.label} ↗
        </a>
      )}
    </section>
  )
}

interface ProfileDirInfo {
  root: string
}

function ProfilesSection() {
  const [profiles, setProfiles] = useState<TunnelProfile[] | null>(null)
  const [dir, setDir] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<TunnelProfile | null>(null)
  const [creatingNew, setCreatingNew] = useState(false)

  const reload = async () => {
    try {
      const [pRes, dRes] = await Promise.all([
        fetch('/api/profiles'),
        fetch('/api/profiles/dir'),
      ])
      if (!pRes.ok) throw new Error(`profiles: ${pRes.status}`)
      const list: TunnelProfile[] = await pRes.json()
      const di: ProfileDirInfo = await dRes.json()
      setProfiles(list)
      setDir(di.root)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  useEffect(() => {
    reload()
  }, [])

  const startCreate = () => {
    setCreatingNew(true)
    setEditing(emptyProfile())
  }

  const onSaved = async () => {
    setEditing(null)
    setCreatingNew(false)
    await reload()
  }

  if (editing) {
    return (
      <ProfileEditor
        profile={editing}
        isNew={creatingNew}
        existingNames={profiles?.map((p) => p.name) ?? []}
        onCancel={() => {
          setEditing(null)
          setCreatingNew(false)
        }}
        onSaved={onSaved}
      />
    )
  }

  return (
    <div style={{ maxWidth: 980, margin: '0 auto', padding: '24px 24px 64px' }}>
      <header style={{ display: 'flex', alignItems: 'baseline', gap: 12, marginBottom: 14 }}>
        <h1 style={{ margin: 0, fontSize: 20 }}>Tunnel profiles</h1>
        <span style={{ flex: 1 }} />
        <button onClick={startCreate}>+ New profile</button>
      </header>

      <p style={{ color: 'var(--text-muted)', fontSize: 12, lineHeight: 1.5, marginTop: 0 }}>
        Profiles are JSON files on disk{dir ? <> in <code>{dir}</code></> : null}. Run a saved
        profile from the CLI with <code>tap run --name &lt;name&gt;</code>.
      </p>

      {error && <ErrorBanner message={error} onRetry={reload} />}

      {profiles && profiles.length === 0 && (
        <div
          style={{
            padding: '24px',
            border: '1px dashed var(--border)',
            borderRadius: 8,
            color: 'var(--text-muted)',
            textAlign: 'center',
          }}
        >
          No saved profiles yet. Click <b>New profile</b> to create one, or run{' '}
          <code>tap save &lt;name&gt; &lt;upstream&gt; …</code>.
        </div>
      )}

      {profiles && profiles.length > 0 && (
        <table
          style={{
            width: '100%',
            borderCollapse: 'collapse',
            fontSize: 13,
          }}
        >
          <thead>
            <tr style={{ textAlign: 'left', color: 'var(--text-muted)', fontSize: 11, textTransform: 'uppercase' }}>
              <th style={{ padding: '8px 6px' }}>Name</th>
              <th style={{ padding: '8px 6px' }}>Upstream</th>
              <th style={{ padding: '8px 6px' }}>Mode</th>
              <th style={{ padding: '8px 6px' }}>Hostname / Zone</th>
              <th style={{ padding: '8px 6px' }}>Auth</th>
              <th style={{ padding: '8px 6px', width: 1 }} />
            </tr>
          </thead>
          <tbody>
            {profiles.map((p) => (
              <ProfileRow key={p.name} profile={p} onEdit={setEditing} onChanged={reload} />
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

function ProfileRow({
  profile,
  onEdit,
  onChanged,
}: {
  profile: TunnelProfile
  onEdit: (p: TunnelProfile) => void
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)

  const onDelete = async () => {
    if (!confirm(`Delete profile '${profile.name}'?`)) return
    setBusy(true)
    try {
      const res = await fetch(`/api/profiles/${encodeURIComponent(profile.name)}`, { method: 'DELETE' })
      if (!res.ok && res.status !== 204) throw new Error(`delete failed: ${res.status}`)
      onChanged()
    } catch (e) {
      alert(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const auth = []
  if (profile.authHeader) auth.push('header')
  if (profile.authCidrs && profile.authCidrs.length) auth.push(`cidr×${profile.authCidrs.length}`)
  if (profile.authCountries && profile.authCountries.length) auth.push(`country×${profile.authCountries.length}`)
  if (profile.oidcAuthority) auth.push('oidc')

  const hostOrZone =
    profile.tunnelMode === 'Dynamic' ? profile.dynamicZone || '—' : profile.hostname || '—'

  return (
    <tr style={{ borderTop: '1px solid var(--border)' }}>
      <td style={{ padding: '10px 6px', fontWeight: 600 }}>{profile.name}</td>
      <td style={{ padding: '10px 6px', fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}>
        {profile.upstream || '—'}
      </td>
      <td style={{ padding: '10px 6px' }}>
        <ModeBadge mode={profile.tunnelMode} />
      </td>
      <td style={{ padding: '10px 6px', fontFamily: 'SF Mono, Menlo, monospace', fontSize: 12 }}>
        {hostOrZone}
      </td>
      <td style={{ padding: '10px 6px', color: 'var(--text-muted)', fontSize: 12 }}>
        {auth.length ? auth.join(' + ') : '—'}
      </td>
      <td style={{ padding: '10px 6px', whiteSpace: 'nowrap' }}>
        <button onClick={() => onEdit(profile)} disabled={busy} style={{ marginRight: 6 }}>
          Edit
        </button>
        <button onClick={onDelete} disabled={busy}>
          Delete
        </button>
      </td>
    </tr>
  )
}

function ModeBadge({ mode }: { mode: TunnelMode }) {
  if (mode === 'None') {
    return <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>standalone</span>
  }
  const label = mode === 'ApiManaged' ? 'api-managed' : mode.toLowerCase()
  return (
    <span
      style={{
        fontSize: 10,
        textTransform: 'uppercase',
        letterSpacing: '0.06em',
        color: '#f6821f',
        border: '1px solid color-mix(in srgb, #f6821f 35%, transparent)',
        padding: '1px 6px',
        borderRadius: 3,
        background: '#fff',
      }}
    >
      {label}
    </span>
  )
}

function ErrorBanner({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div
      style={{
        padding: '10px 12px',
        marginBottom: 12,
        background: 'color-mix(in srgb, var(--err) 12%, transparent)',
        border: '1px solid var(--err)',
        borderRadius: 6,
        color: 'var(--err)',
        fontSize: 12,
        display: 'flex',
        gap: 10,
        alignItems: 'center',
      }}
    >
      <span style={{ flex: 1 }}>Failed to load profiles: {message}</span>
      <button onClick={onRetry}>Retry</button>
    </div>
  )
}

function ProfileEditor({
  profile,
  isNew,
  existingNames,
  onCancel,
  onSaved,
}: {
  profile: TunnelProfile
  isNew: boolean
  existingNames: string[]
  onCancel: () => void
  onSaved: () => void
}) {
  const [draft, setDraft] = useState<TunnelProfile>({ ...profile })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = <K extends keyof TunnelProfile>(key: K, value: TunnelProfile[K]) =>
    setDraft((d) => ({ ...d, [key]: value }))

  const onSave = async () => {
    setError(null)
    if (!draft.name || /[\\\/:*?"<>|]/.test(draft.name)) {
      setError('Name is required and must be a valid filename.')
      return
    }
    if (isNew && existingNames.includes(draft.name)) {
      setError(`A profile named '${draft.name}' already exists.`)
      return
    }
    setSaving(true)
    try {
      const res = await fetch(`/api/profiles/${encodeURIComponent(draft.name)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(draft),
      })
      if (!res.ok) {
        const j = await res.json().catch(() => ({}))
        throw new Error(j.error || `save failed: ${res.status}`)
      }
      onSaved()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const mode = draft.tunnelMode

  return (
    <div style={{ maxWidth: 720, margin: '0 auto', padding: '24px 24px 64px' }}>
      <header style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
        <h1 style={{ margin: 0, fontSize: 20 }}>{isNew ? 'New profile' : `Edit '${profile.name}'`}</h1>
      </header>

      {error && (
        <div
          style={{
            padding: '8px 12px',
            marginBottom: 12,
            background: 'color-mix(in srgb, var(--err) 12%, transparent)',
            border: '1px solid var(--err)',
            borderRadius: 6,
            color: 'var(--err)',
            fontSize: 12,
          }}
        >
          {error}
        </div>
      )}

      <Field label="Name" hint="Filename used under the profiles directory.">
        <input
          value={draft.name}
          disabled={!isNew}
          onChange={(e) => set('name', e.target.value)}
          placeholder="my-api"
          style={{ width: '100%' }}
        />
      </Field>

      <Field label="Upstream URL" hint="Local URL the tunnel forwards to.">
        <input
          value={draft.upstream ?? ''}
          onChange={(e) => set('upstream', e.target.value)}
          placeholder="http://localhost:3000"
          style={{ width: '100%' }}
        />
      </Field>

      <Row>
        <Field label="Proxy port" hint="Defaults to 4444.">
          <input
            type="number"
            value={draft.proxyPort ?? ''}
            onChange={(e) => set('proxyPort', e.target.value === '' ? null : Number(e.target.value))}
            placeholder="4444"
            style={{ width: '100%' }}
          />
        </Field>
        <Field label="UI port" hint="Defaults to 4445.">
          <input
            type="number"
            value={draft.uiPort ?? ''}
            onChange={(e) => set('uiPort', e.target.value === '' ? null : Number(e.target.value))}
            placeholder="4445"
            style={{ width: '100%' }}
          />
        </Field>
      </Row>

      <Field label="Tunnel mode">
        <select
          value={draft.tunnelMode}
          onChange={(e) => set('tunnelMode', e.target.value as TunnelMode)}
          style={{
            width: '100%',
            background: 'var(--bg-input)',
            color: 'var(--text)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            padding: '5px 8px',
            fontFamily: 'inherit',
            fontSize: 'inherit',
          }}
        >
          {TUNNEL_MODES.map((m) => (
            <option key={m} value={m}>
              {m === 'None' ? 'None (standalone)' : m === 'ApiManaged' ? 'ApiManaged (API-managed)' : m}
            </option>
          ))}
        </select>
      </Field>

      {mode === 'Token' && (
        <Field label="Cloudflare tunnel token" hint="From the Zero Trust dashboard tunnel.">
          <input
            type="password"
            value={draft.token ?? ''}
            onChange={(e) => set('token', e.target.value)}
            placeholder="ey…"
            style={{ width: '100%' }}
          />
        </Field>
      )}

      {(mode === 'ApiManaged' || mode === 'Dynamic') && (
        <>
          <Field label="API token" hint="Account · Cloudflare Tunnel: Edit (and Zone · DNS: Edit if hostname/dynamic-zone is set).">
            <input
              type="password"
              value={draft.apiToken ?? ''}
              onChange={(e) => set('apiToken', e.target.value)}
              placeholder="cf-api-token"
              style={{ width: '100%' }}
            />
          </Field>
          <Field label="Account ID">
            <input
              value={draft.accountId ?? ''}
              onChange={(e) => set('accountId', e.target.value)}
              placeholder="32-char hex"
              style={{ width: '100%' }}
            />
          </Field>
        </>
      )}

      {mode === 'ApiManaged' && (
        <>
          <Field label="Tunnel name" hint="tap creates this tunnel via the API if it doesn't exist.">
            <input
              value={draft.apiManagedTunnelName ?? ''}
              onChange={(e) => set('apiManagedTunnelName', e.target.value)}
              placeholder="my-tunnel"
              style={{ width: '100%' }}
            />
          </Field>
          <Field label="Hostname" hint="Optional — FQDN to attach (a CNAME is created in the matching zone).">
            <input
              value={draft.hostname ?? ''}
              onChange={(e) => set('hostname', e.target.value)}
              placeholder="app.example.com"
              style={{ width: '100%' }}
            />
          </Field>
        </>
      )}

      {mode === 'Dynamic' && (
        <Field label="Zone" hint="A zone you own; tap creates a tap-<random>.<zone> hostname per run.">
          <input
            value={draft.dynamicZone ?? ''}
            onChange={(e) => set('dynamicZone', e.target.value)}
            placeholder="example.com"
            style={{ width: '100%' }}
          />
        </Field>
      )}

      <Row>
        <Field label="Run cloudflared in Docker">
          <Toggle checked={!!draft.docker} onChange={(v) => set('docker', v)} />
        </Field>
        <Field label="Auto-install cloudflared if missing">
          <Toggle checked={!!draft.autoInstall} onChange={(v) => set('autoInstall', v)} />
        </Field>
      </Row>

      <details style={{ marginTop: 16, border: '1px solid var(--border)', borderRadius: 6, padding: '8px 12px' }}>
        <summary style={{ cursor: 'pointer', fontWeight: 600 }}>Auth (optional)</summary>
        <Field label="Header check (Name=Value)" hint="e.g. X-Auth-Header=secret">
          <input
            value={draft.authHeader ?? ''}
            onChange={(e) => set('authHeader', e.target.value)}
            placeholder="X-Header=value"
            style={{ width: '100%' }}
          />
        </Field>
        <Field label="Allowed CIDRs (comma-separated)">
          <input
            value={(draft.authCidrs ?? []).join(',')}
            onChange={(e) =>
              set(
                'authCidrs',
                e.target.value
                  .split(',')
                  .map((s) => s.trim())
                  .filter(Boolean),
              )
            }
            placeholder="10.0.0.0/8,1.2.3.4/32"
            style={{ width: '100%' }}
          />
        </Field>
        <Field label="Allowed countries (comma-separated ISO-3166)">
          <input
            value={(draft.authCountries ?? []).join(',')}
            onChange={(e) =>
              set(
                'authCountries',
                e.target.value
                  .split(',')
                  .map((s) => s.trim())
                  .filter(Boolean),
              )
            }
            placeholder="US,DE"
            style={{ width: '100%' }}
          />
        </Field>
        <Field label="OIDC authority">
          <input
            value={draft.oidcAuthority ?? ''}
            onChange={(e) => set('oidcAuthority', e.target.value)}
            placeholder="https://issuer.example.com"
            style={{ width: '100%' }}
          />
        </Field>
        <Row>
          <Field label="OIDC client id">
            <input
              value={draft.oidcClientId ?? ''}
              onChange={(e) => set('oidcClientId', e.target.value)}
              style={{ width: '100%' }}
            />
          </Field>
          <Field label="OIDC client secret">
            <input
              type="password"
              value={draft.oidcClientSecret ?? ''}
              onChange={(e) => set('oidcClientSecret', e.target.value)}
              style={{ width: '100%' }}
            />
          </Field>
        </Row>
      </details>

      <div style={{ marginTop: 24, display: 'flex', gap: 8 }}>
        <button onClick={onCancel} disabled={saving}>
          Cancel
        </button>
        <span style={{ flex: 1 }} />
        <button
          onClick={onSave}
          disabled={saving}
          style={{
            background: 'var(--accent-solid)',
            color: '#fff',
            borderColor: 'var(--accent-solid)',
          }}
        >
          {saving ? 'Saving…' : isNew ? 'Create profile' : 'Save changes'}
        </button>
      </div>
    </div>
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'block', marginTop: 12 }}>
      <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4, fontWeight: 600 }}>{label}</div>
      {children}
      {hint && <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>{hint}</div>}
    </label>
  )
}

function Row({ children }: { children: React.ReactNode }) {
  return <div style={{ display: 'flex', gap: 12, marginTop: 0 }}>{Array.isArray(children) ? children.map((c, i) => <div key={i} style={{ flex: 1 }}>{c}</div>) : children}</div>
}

function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      type="button"
      onClick={() => onChange(!checked)}
      style={{
        width: 44,
        height: 22,
        borderRadius: 999,
        position: 'relative',
        background: checked ? 'var(--accent-solid)' : 'var(--bg-input)',
        border: '1px solid ' + (checked ? 'var(--accent-solid)' : 'var(--border)'),
        padding: 0,
      }}
    >
      <span
        style={{
          position: 'absolute',
          top: 1,
          left: checked ? 22 : 1,
          width: 18,
          height: 18,
          borderRadius: '50%',
          background: '#fff',
          transition: 'left 0.12s',
        }}
      />
    </button>
  )
}
