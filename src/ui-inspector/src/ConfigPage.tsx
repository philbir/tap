import { useEffect, useState } from 'react'
import type { TunnelProfile, TunnelMode } from './profileTypes'
import { TUNNEL_MODES, emptyProfile } from './profileTypes'

type Tab = 'profiles' | 'cloudflare' | 'tailscale'

/**
 * What the server sends instead of a stored credential. Echoing it back on save means
 * "unchanged", so a field the user never touched must round-trip this string verbatim —
 * anything else overwrites the secret on disk.
 */
export const REDACTED_PLACEHOLDER = '__tap_redacted__'

/** Cleartext credentials, only ever returned by POST /api/profiles/{name}/reveal. */
interface ProfileSecrets {
  token?: string | null
  apiToken?: string | null
  tailscaleAuthKey?: string | null
  oidcClientSecret?: string | null
}

export function ConfigPage() {
  const [tab, setTab] = useState<Tab>('profiles')

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
        <PageTab label="Tunnel profiles" active={tab === 'profiles'} onClick={() => setTab('profiles')} />
        <PageTab label="Cloudflare" active={tab === 'cloudflare'} onClick={() => setTab('cloudflare')} />
        <PageTab label="Tailscale" active={tab === 'tailscale'} onClick={() => setTab('tailscale')} />
      </div>

      <div style={{ flex: 1, overflow: 'auto' }}>
        {tab === 'profiles' && <ProfilesSection />}
        {tab === 'cloudflare' && <IntroSection />}
        {tab === 'tailscale' && <TailscaleSection />}
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
      <h1 style={{ marginTop: 0, fontSize: 22 }}>Cloudflare with Tap Tunnels</h1>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55 }}>
        tap can route traffic from a public Cloudflare hostname through the inspector to your local
        upstream. Cloudflare runs <code>cloudflared</code> as the connector — tap installs and
        manages the process for you. Pick the mode that matches what you have:
      </p>

      <SecurityCallout />

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

const TS_ACCENT = '#5e64f4'

function SecurityCallout() {
  return (
    <div
      role="alert"
      style={{
        marginTop: 16,
        padding: '12px 14px',
        borderRadius: 8,
        background: 'color-mix(in srgb, #d97706 10%, transparent)',
        border: '1px solid color-mix(in srgb, #d97706 35%, transparent)',
        color: 'var(--text)',
        lineHeight: 1.55,
        fontSize: 13,
      }}
    >
      <b style={{ color: '#b45309' }}>⚠ Security: public tunnels are scanned within minutes.</b>
      <p style={{ margin: '6px 0 0', color: 'var(--text-muted)' }}>
        The moment a public hostname's TLS certificate appears in a public CT log (which happens
        within seconds of <code>tailscale funnel</code> or any Cloudflare tunnel coming up),
        opportunistic scanners hit it looking for admin endpoints, exposed debug routes, leaked
        secrets, dependency-confusion paths, default credentials, and known-CVE banners. This is
        not theoretical — your tap will receive scanner traffic before your first real test request.
      </p>
      <ul style={{ margin: '8px 0 0 18px', color: 'var(--text-muted)' }}>
        <li>
          <b>Default to private.</b> Use <code>WithTailscaleServe(...)</code> (or no
          <code>--tailscale-public</code> flag in the CLI) so the tap is reachable only from your
          tailnet. Switch to <code>WithTailscaleFunnel(...)</code> only when you actually need
          internet access.
        </li>
        <li>
          <b>Always pair public tunnels with auth.</b> tap supports header-based, CIDR/country
          allowlists, and OIDC out of the box — see the auth section in the README.
        </li>
        <li>
          <b>Don't run unauthenticated public tunnels for internal admin services.</b>{' '}
          Even short demos benefit from header-auth — it stops 99% of scanner traffic.
        </li>
      </ul>
    </div>
  )
}

function TailscaleSection() {
  return (
    <div style={{ maxWidth: 880, margin: '0 auto', padding: '28px 24px 64px' }}>
      <h1 style={{ marginTop: 0, fontSize: 22 }}>Tailscale Serve & Funnel with tap</h1>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55 }}>
        Tailscale exposes a tailnet node's HTTPS endpoint at <code>https://&lt;machine&gt;.&lt;tailnet&gt;.ts.net</code>{' '}
        in two flavors: <code>serve</code> (reachable only from your tailnet — the safe default)
        and <code>funnel</code> (publicly reachable on the internet). One node = one upstream;
        for multiple upstreams, register multiple resources. Only ports <code>443</code>,{' '}
        <code>8443</code>, and <code>10000</code> are allowed.
      </p>

      <SecurityCallout />

      <TailscalePrerequisites />

      <h2 style={{ fontSize: 17, marginTop: 32, marginBottom: 4 }}>Modes</h2>

      <ModeCard
        title="System daemon (default)"
        slug="system"
        when="You already have Tailscale installed and signed in on this machine."
        bullets={[
          'Reuses the host’s tailscaled and existing tailnet node — public hostname is fixed across runs.',
          'No auth key needed; the node’s existing tailnet membership and ACL caps drive everything.',
          'On AppHost shutdown, the bootstrapper removes only the path-specific rule (`tailscale serve --set-path=/ off`), so other manual rules on the same port survive.',
        ]}
        cli={`var tap = builder.AddTap<Projects.Tap_Server>("tap")
    .WithTailscaleFunnel("tap-funnel", t => t.WithSystemDaemon());
api.WithTap(tap);`}
        accent={TS_ACCENT}
      />

      <ModeCard
        title="Ephemeral userspace daemon"
        slug="ephemeral"
        when="Want a fresh tailnet node per run, no host tailscale install required."
        bullets={[
          'Spins up its own tailscaled with --tun=userspace-networking and a per-session state dir under /tmp.',
          'Joins the tailnet via the supplied auth key; node disappears when the AppHost stops.',
          'Each run consumes one use of the auth key — use a Reusable key, or generate a fresh one each run.',
          'macOS/Linux only — Windows ephemeral mode is not yet supported (the GUI service model gets in the way).',
        ]}
        cli={`var tap = builder.AddTap<Projects.Tap_Server>("tap")
    .WithTailscaleFunnel("tap-funnel", t => t
        .WithEphemeralDaemon(authKey)
        .WithFunnelPort(8443)); // 443 (default), 8443, or 10000
api.WithTap(tap);`}
        accent={TS_ACCENT}
      />

      <h2 style={{ fontSize: 17, marginTop: 32, marginBottom: 4 }}>How it actually flows</h2>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55, fontSize: 13 }}>
        Internet → Tailscale Funnel ingress → <code>tailscaled</code> on this machine →
        the inspector's proxy port → your upstream. Every request is captured and shows
        up live on the Inspector tab; the daemon and active rules are surfaced in the
        Tunnel detail dialog (click the Tunnel chip in the inspector header when running
        a Tailscale tap).
      </p>
    </div>
  )
}

function TailscalePrerequisites() {
  return (
    <section style={{ marginTop: 18 }}>
      <h2 style={{ fontSize: 17, marginTop: 0, marginBottom: 8 }}>Prerequisites</h2>
      <p style={{ color: 'var(--text-muted)', lineHeight: 1.55, marginTop: 0 }}>
        tap calls the <code>tailscale</code> CLI directly. Make sure the CLI is on{' '}
        <code>PATH</code>, HTTPS is enabled on your tailnet, and the node has the{' '}
        <code>funnel</code> ACL capability.
      </p>

      <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', marginTop: 10 }}>
        <PrereqCard
          title="Install Tailscale"
          forModes={['System', 'Ephemeral']}
          summary="The tailscale CLI must be on PATH. tap fails fast at AppHost startup if it isn't found."
          steps={[
            <>
              macOS: <code>brew install tailscale</code>, or install the App Store /
              standalone client and run <i>Install CLI</i> from its menu so{' '}
              <code>/usr/local/bin/tailscale</code> is created.
            </>,
            <>
              Linux: one-line installer at{' '}
              <a href="https://tailscale.com/download/linux" target="_blank" rel="noreferrer">
                tailscale.com/download/linux
              </a>
              , then <code>sudo systemctl enable --now tailscaled</code>.
            </>,
            <>
              Windows: GUI installer from{' '}
              <a href="https://tailscale.com/download" target="_blank" rel="noreferrer">
                tailscale.com/download
              </a>
              . System mode only — ephemeral mode is not yet supported on Windows.
            </>,
            <>
              Sign in once for system mode: <code>tailscale up</code>.
            </>,
          ]}
          notes={[
            'Verify with `tailscale version`. tap shells out to whatever resolves on PATH.',
          ]}
          link={{
            label: 'Tailscale download page',
            href: 'https://tailscale.com/download',
          }}
          accent={TS_ACCENT}
        />

        <PrereqCard
          title="Enable HTTPS + grant funnel"
          forModes={['System', 'Ephemeral']}
          summary="Funnel requires HTTPS to be enabled on the tailnet, and the node must have the funnel capability granted via ACL."
          steps={[
            <>
              Admin console →{' '}
              <a href="https://login.tailscale.com/admin/dns" target="_blank" rel="noreferrer">
                DNS
              </a>{' '}
              → enable <b>HTTPS Certificates</b> (one-time per tailnet).
            </>,
            <>
              Admin console →{' '}
              <a href="https://login.tailscale.com/admin/acls" target="_blank" rel="noreferrer">
                Access Controls
              </a>{' '}
              → add <code>nodeAttrs</code>:
            </>,
            <pre style={{ margin: '4px 0 0', padding: '6px 8px', background: 'var(--bg-input)', border: '1px solid var(--border)', borderRadius: 4, fontSize: 11 }}>
{`"nodeAttrs": [
  { "target": ["*"], "attr": ["funnel"] }
]`}
            </pre>,
            <>
              Scope by tag (e.g. <code>"target": ["tag:dev"]</code>) and tag your node from
              the Machines page if you don't want to grant funnel to everything.
            </>,
            <>
              Verify: <code>tailscale status --json | grep -i funnel</code> should show{' '}
              <code>"funnel"</code> in your node's <code>CapMap</code>.
            </>,
          ]}
          notes={[
            'Funnel is allowed only on ports 443, 8443, 10000 (enforced by Tailscale).',
            'After ACL changes, run `tailscale down && tailscale up` so the cap propagates immediately.',
          ]}
          link={{
            label: 'Open admin console',
            href: 'https://login.tailscale.com/admin/',
          }}
          accent={TS_ACCENT}
        />

        <PrereqCard
          title="Auth key (ephemeral only)"
          forModes={['Ephemeral']}
          summary="Required for ephemeral mode. tap uses the key to spawn a fresh userspace tailscaled per AppHost run; the node is removed at shutdown."
          steps={[
            <>
              Admin console →{' '}
              <a href="https://login.tailscale.com/admin/settings/keys" target="_blank" rel="noreferrer">
                Settings → Keys
              </a>{' '}
              → <b>Generate auth key</b>.
            </>,
            <>
              Mark <b>Reusable</b> if you'll run the AppHost more than once with the
              same key, and <b>Ephemeral</b> so the node is automatically removed when offline.
            </>,
            <>
              Add the ACL tags that grant <code>funnel</code> (e.g. <code>tag:dev</code>) so
              the new node lands in the right slice of your ACL.
            </>,
            <>
              Stash it in user-secrets:
              <code style={{ display: 'block', marginTop: 4 }}>
                dotnet user-secrets set Tailscale:AuthKey tskey-… --project samples/Sample.AppHost
              </code>
            </>,
          ]}
          notes={[
            "Treat the key like a password — it's authenticated tailnet membership.",
            "Reusable keys can be revoked from the same admin page if leaked.",
          ]}
          link={{
            label: 'Generate auth key',
            href: 'https://login.tailscale.com/admin/settings/keys',
          }}
          accent={TS_ACCENT}
        />
      </div>
    </section>
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
  accent = '#f6821f',
}: {
  title: string
  forModes: string[]
  summary: string
  steps: React.ReactNode[]
  notes?: string[]
  link?: { label: string; href: string }
  accent?: string
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
              color: accent,
              border: `1px solid color-mix(in srgb, ${accent} 35%, transparent)`,
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
  accent = '#f6821f',
}: {
  title: string
  slug: string
  when: string
  bullets: string[]
  cli: string
  link?: { label: string; href: string }
  accent?: string
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
            color: accent,
            border: `1px solid color-mix(in srgb, ${accent} 35%, transparent)`,
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
    profile.tunnelMode === 'Dynamic' ? profile.dynamicZone || '—'
      : profile.tunnelMode === 'Tailscale' ? '<machine>.<tailnet>.ts.net'
      : profile.hostname || '—'

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
  const accent = mode === 'Tailscale' ? '#5e64f4' : '#f6821f'
  return (
    <span
      style={{
        fontSize: 10,
        textTransform: 'uppercase',
        letterSpacing: '0.06em',
        color: accent,
        border: `1px solid color-mix(in srgb, ${accent} 35%, transparent)`,
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
  const [revealing, setRevealing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = <K extends keyof TunnelProfile>(key: K, value: TunnelProfile[K]) =>
    setDraft((d) => ({ ...d, [key]: value }))

  // One POST returns every credential at once, so revealing one field unmasks the others
  // too. Fields the user has already replaced keep what they typed.
  const revealSecrets = async () => {
    setRevealing(true)
    setError(null)
    try {
      const res = await fetch(`/api/profiles/${encodeURIComponent(profile.name)}/reveal`, {
        method: 'POST',
      })
      if (!res.ok) throw new Error(`reveal failed: ${res.status}`)
      const s: ProfileSecrets = await res.json()
      setDraft((d) => ({
        ...d,
        token: unmask(d.token, s.token),
        apiToken: unmask(d.apiToken, s.apiToken),
        tailscaleAuthKey: unmask(d.tailscaleAuthKey, s.tailscaleAuthKey),
        oidcClientSecret: unmask(d.oidcClientSecret, s.oidcClientSecret),
      }))
    } catch (e) {
      setError(`Could not reveal stored secrets: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      setRevealing(false)
    }
  }

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
        <SecretField
          label="Cloudflare tunnel token"
          hint="From the Zero Trust dashboard tunnel."
          placeholder="ey…"
          value={draft.token ?? ''}
          onChange={(v) => set('token', v)}
          onReveal={revealSecrets}
          revealing={revealing}
          canReveal={!isNew}
        />
      )}

      {(mode === 'ApiManaged' || mode === 'Dynamic') && (
        <>
          <SecretField
            label="API token"
            hint="Account · Cloudflare Tunnel: Edit (and Zone · DNS: Edit if hostname/dynamic-zone is set)."
            placeholder="cf-api-token"
            value={draft.apiToken ?? ''}
            onChange={(v) => set('apiToken', v)}
            onReveal={revealSecrets}
            revealing={revealing}
            canReveal={!isNew}
          />
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

      {mode === 'Tailscale' && (
        <>
          <Field label="Exposure" hint="Default: tailnet-only (`tailscale serve`). Switch to public when you need an internet-facing URL — pair with auth.">
            <select
              value={draft.tailscalePublic ? 'public' : 'tailnet'}
              onChange={(e) => set('tailscalePublic', e.target.value === 'public')}
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
              <option value="tailnet">tailnet-only — `tailscale serve` (default; private)</option>
              <option value="public">public — `tailscale funnel` (internet-exposed)</option>
            </select>
          </Field>
          <Field label="Daemon mode" hint="System reuses the host's tailscaled. Ephemeral spawns a per-session userspace daemon (host process or Docker, requires auth key).">
            <select
              value={draft.tailscaleDaemonMode ?? 'system'}
              onChange={(e) => set('tailscaleDaemonMode', e.target.value as 'system' | 'ephemeral')}
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
              <option value="system">system (default — uses existing tailscaled)</option>
              <option value="ephemeral">ephemeral (needs auth key + tailscaled or Docker)</option>
            </select>
          </Field>
          <Field label="Funnel port" hint="Tailscale only allows 443, 8443, or 10000.">
            <select
              value={draft.tailscaleFunnelPort ?? 443}
              onChange={(e) => set('tailscaleFunnelPort', Number(e.target.value))}
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
              <option value={443}>443 (default)</option>
              <option value={8443}>8443</option>
              <option value={10000}>10000</option>
            </select>
          </Field>
          {draft.tailscaleDaemonMode === 'ephemeral' && (
            <>
              <SecretField
                label="Auth key"
                hint="Required for ephemeral mode. Generate at admin.tailscale.com → Settings → Keys."
                placeholder="tskey-…"
                value={draft.tailscaleAuthKey ?? ''}
                onChange={(v) => set('tailscaleAuthKey', v)}
                onReveal={revealSecrets}
                revealing={revealing}
                canReveal={!isNew}
              />
              <Field label="Login server (optional)" hint="Override for self-hosted Headscale, etc. Leave blank for the public Tailscale coordination server.">
                <input
                  value={draft.tailscaleLoginServer ?? ''}
                  onChange={(e) => set('tailscaleLoginServer', e.target.value)}
                  placeholder="https://controlplane.tailscale.com"
                  style={{ width: '100%' }}
                />
              </Field>
              <div style={{ marginTop: 8, padding: '8px 10px', borderRadius: 6, background: 'color-mix(in srgb, #5e64f4 10%, transparent)', border: '1px solid color-mix(in srgb, #5e64f4 30%, transparent)', fontSize: 11.5, color: 'var(--text-muted)' }}>
                <b style={{ color: 'var(--text)' }}>Heads up:</b> ephemeral mode is honored by both the Aspire AppHost
                (<code>WithEphemeralDaemon</code>) and the CLI (<code>tap run … --tailscale-authkey</code> or
                <code>TAILSCALE_AUTHKEY</code> env). On Windows, ephemeral process mode is unsupported — pair the auth
                key with <code>--docker</code> (or <code>hostMode: TailscaleHostMode.Docker</code>) to run
                <code>tailscale/tailscale</code> in a container instead.
              </div>
            </>
          )}
        </>
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
          <SecretField
            label="OIDC client secret"
            value={draft.oidcClientSecret ?? ''}
            onChange={(v) => set('oidcClientSecret', v)}
            onReveal={revealSecrets}
            revealing={revealing}
            canReveal={!isNew}
          />
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

/** Only swap in the cleartext where the field is still showing the mask. */
function unmask(current: string | null | undefined, revealed: string | null | undefined) {
  return current === REDACTED_PLACEHOLDER ? revealed ?? '' : current
}

const SMALL_BUTTON: React.CSSProperties = { fontSize: 11, padding: '2px 8px' }

/**
 * Password input that understands the redaction placeholder. While the field still holds the
 * mask the dots on screen are not the secret, so typing into them would write a mangled
 * credential to disk on save — the input stays read-only until the user picks Reveal (fetch
 * the real values) or Replace (clear it and enter a new one). Left alone, the placeholder is
 * PUT back unchanged and the server keeps what it already has.
 */
function SecretField({
  label,
  hint,
  placeholder,
  value,
  onChange,
  onReveal,
  revealing,
  canReveal,
}: {
  label: string
  hint?: string
  placeholder?: string
  value: string
  onChange: (v: string) => void
  onReveal: () => void
  revealing: boolean
  canReveal: boolean
}) {
  const stored = value === REDACTED_PLACEHOLDER
  return (
    <>
      <Field label={label} hint={hint}>
        <input
          type="password"
          value={value}
          readOnly={stored}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          style={{ width: '100%' }}
        />
      </Field>
      {stored && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 5 }}>
          <span style={{ flex: 1, fontSize: 11, color: 'var(--text-muted)' }}>
            Stored — leave unchanged to keep it.
          </span>
          {canReveal && (
            <button type="button" onClick={onReveal} disabled={revealing} style={SMALL_BUTTON}>
              {revealing ? 'Revealing…' : 'Reveal'}
            </button>
          )}
          <button type="button" onClick={() => onChange('')} style={SMALL_BUTTON}>
            Replace
          </button>
        </div>
      )}
    </>
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
