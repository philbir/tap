import { useEffect, useMemo, useRef, useState } from 'react'
import QRCode from 'qrcode'
import type { IngressEntry, InspectorConfig } from './types'

interface Props {
  config: InspectorConfig | null
}

interface QrTarget {
  hostname: string
  url: string
  publicExpose: boolean
  tunnelMode: string | null
}

export function QrPage({ config }: Props) {
  const targets = useMemo<QrTarget[]>(() => {
    if (!config) return []
    return collectTargets(config)
  }, [config])

  if (!config) {
    return <Centered>Loading inspector config…</Centered>
  }

  if (targets.length === 0) {
    return (
      <Centered>
        <div style={{ maxWidth: 460, textAlign: 'center', color: 'var(--text-muted)', lineHeight: 1.55 }}>
          <h2 style={{ fontSize: 18, color: 'var(--text)' }}>No URL yet</h2>
          <p>
            This tap doesn't have a hostname to point a phone at yet. If you just started the
            AppHost or CLI, wait a few seconds and refresh:
          </p>
          <ul style={{ textAlign: 'left', margin: '8px auto', maxWidth: 380 }}>
            <li>Cloudflare quick tunnel — cloudflared is still printing the <code>*.trycloudflare.com</code> address.</li>
            <li>Tailscale — the daemon is registering MagicDNS for the new node.</li>
          </ul>
          <p>
            For Tailscale <code>serve</code> (tailnet-only), the URL only works from your other
            tailnet-connected devices — opening it from a phone on cellular won't work, that's
            what <code>tailscale funnel</code> (public, opt-in) is for.
          </p>
          <p>
            Local proxy: <code>{guessLocalUrl(config)}</code>
          </p>
        </div>
      </Centered>
    )
  }

  return (
    <div style={{ flex: 1, overflow: 'auto', padding: '32px 24px 64px' }}>
      <div style={{ maxWidth: 1100, margin: '0 auto' }}>
        <h1 style={{ marginTop: 0, fontSize: 22 }}>Open on phone</h1>
        <p style={{ color: 'var(--text-muted)', lineHeight: 1.55, marginTop: 0, maxWidth: 720 }}>
          Scan with your phone's camera to open the public URL. Use this for quick mobile-app or
          webhook-callback testing without typing the hostname. Most camera apps detect the QR and
          offer to open the link directly.
        </p>

        <div
          style={{
            display: 'grid',
            gap: 24,
            gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
            marginTop: 24,
          }}
        >
          {targets.map((t) => (
            <QrCard key={t.url} target={t} />
          ))}
        </div>
      </div>
    </div>
  )
}

function collectTargets(config: InspectorConfig): QrTarget[] {
  const seen = new Set<string>()
  const out: QrTarget[] = []
  for (const e of config.ingress) {
    const url = pickUrl(e)
    if (!url || seen.has(url)) continue
    seen.add(url)
    out.push({
      hostname: e.hostname || new URL(url).host,
      url,
      publicExpose: !!e.publicExpose,
      tunnelMode: e.tunnelMode ?? null,
    })
  }
  return out
}

function pickUrl(e: IngressEntry): string | null {
  if (e.publicUrl) return e.publicUrl
  if (e.hostname) return `https://${e.hostname}`
  return null
}

function guessLocalUrl(config: InspectorConfig): string {
  return `http://localhost:${config.proxyPort}`
}

function QrCard({ target }: { target: QrTarget }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    QRCode.toCanvas(canvas, target.url, {
      errorCorrectionLevel: 'M',
      width: 320,
      margin: 1,
      color: { dark: '#17142b', light: '#ffffff' },
    }).catch((e) => setErr(String(e?.message ?? e)))
  }, [target.url])

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(target.url)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard may be denied; ignore */
    }
  }

  const accent = isTailscale(target.tunnelMode) ? '#5e64f4' : '#f6821f'

  return (
    <article
      style={{
        padding: '20px',
        border: '1px solid var(--border)',
        borderRadius: 12,
        background: 'var(--bg-raised)',
        boxShadow: '0 6px 24px rgba(72, 58, 149, 0.10)',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        gap: 12,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, alignSelf: 'stretch' }}>
        <span
          style={{
            fontSize: 9.5,
            padding: '2px 7px',
            borderRadius: 3,
            color: target.publicExpose ? '#b45309' : 'var(--ok)',
            border: target.publicExpose
              ? '1px solid color-mix(in srgb, #d97706 40%, transparent)'
              : '1px solid color-mix(in srgb, var(--ok) 40%, transparent)',
            background: target.publicExpose
              ? 'color-mix(in srgb, #d97706 12%, transparent)'
              : 'color-mix(in srgb, var(--ok) 10%, transparent)',
            textTransform: 'uppercase',
            letterSpacing: '0.06em',
          }}
        >
          {target.publicExpose ? 'public' : 'tailnet-only'}
        </span>
        {target.tunnelMode && (
          <span
            style={{
              fontSize: 9.5,
              padding: '2px 7px',
              borderRadius: 3,
              color: accent,
              border: `1px solid color-mix(in srgb, ${accent} 35%, transparent)`,
              background: '#fff',
              textTransform: 'uppercase',
              letterSpacing: '0.06em',
            }}
          >
            {target.tunnelMode}
          </span>
        )}
        <span style={{ flex: 1 }} />
      </div>

      {err ? (
        <div style={{ color: 'var(--err)', fontSize: 12, textAlign: 'center', padding: '40px 0' }}>
          QR generation failed: {err}
        </div>
      ) : (
        <canvas
          ref={canvasRef}
          style={{
            width: 320,
            height: 320,
            maxWidth: '100%',
            background: '#fff',
            borderRadius: 8,
            padding: 8,
            boxSizing: 'border-box',
          }}
        />
      )}

      <div
        style={{
          fontFamily: 'SF Mono, Menlo, monospace',
          fontSize: 13,
          color: 'var(--text)',
          wordBreak: 'break-all',
          textAlign: 'center',
          maxWidth: '100%',
        }}
      >
        {target.url}
      </div>

      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', justifyContent: 'center' }}>
        <a
          href={target.url}
          target="_blank"
          rel="noreferrer"
          style={{
            padding: '6px 12px',
            borderRadius: 6,
            background: accent,
            color: '#fff',
            textDecoration: 'none',
            fontSize: 12,
            fontWeight: 500,
          }}
        >
          Open ↗
        </a>
        <button onClick={onCopy} style={{ fontSize: 12 }}>
          {copied ? 'Copied!' : 'Copy URL'}
        </button>
      </div>
    </article>
  )
}

function isTailscale(mode: string | null): boolean {
  return mode === 'tailscale-system' || mode === 'tailscale-ephemeral'
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <div
      style={{
        flex: 1,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: 32,
      }}
    >
      {children}
    </div>
  )
}
