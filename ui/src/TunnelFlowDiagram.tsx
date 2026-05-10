interface Props {
  mode: string
  publicUrl: string | null
  tunnelName: string | null
  proxyPort: number
  upstream: string
}

interface Node {
  label: string
  sub?: string | null
  icon: 'user' | 'cf' | 'cloudflared' | 'ts' | 'tailscaled' | 'inspector' | 'upstream'
  highlight?: boolean
}

export function TunnelFlowDiagram({ mode, publicUrl, tunnelName, proxyPort, upstream }: Props) {
  const nodes: Node[] = (() => {
    const inspector: Node = { icon: 'inspector', label: 'Inspector', sub: `localhost:${proxyPort}` }
    const upstreamNode: Node = { icon: 'upstream', label: 'Upstream', sub: stripScheme(upstream) }
    if (mode === 'standalone' || !mode) {
      return [
        { icon: 'user', label: 'You', sub: 'http client' },
        inspector,
        upstreamNode,
      ]
    }
    if (mode === 'tailscale-system' || mode === 'tailscale-ephemeral') {
      return [
        { icon: 'user', label: 'You', sub: stripScheme(publicUrl ?? '') || 'http client' },
        { icon: 'ts', label: 'Tailscale', sub: publicUrl ? new URL(publicUrl).host : null, highlight: true },
        { icon: 'tailscaled', label: 'tailscaled', sub: mode === 'tailscale-ephemeral' ? 'userspace · ephemeral' : 'system daemon' },
        inspector,
        upstreamNode,
      ]
    }
    const edgeLabel =
      mode === 'quick' ? 'TryCloudflare' :
      mode === 'token' ? 'Cloudflare Edge' :
      'Cloudflare Edge'
    const cloudflaredSub =
      mode === 'token' ? '--token …' :
      mode === 'quick' ? '--url …' :
      'config.yml'
    return [
      { icon: 'user', label: 'You', sub: stripScheme(publicUrl ?? '') || 'http client' },
      { icon: 'cf', label: edgeLabel, sub: publicUrl ? new URL(publicUrl).host : null, highlight: true },
      { icon: 'cloudflared', label: 'cloudflared', sub: cloudflaredSub },
      inspector,
      upstreamNode,
    ]
  })()

  // Layout: equal width per node, lines between.
  const NODE_W = 140
  const NODE_H = 78
  const GAP = 22
  const w = nodes.length * NODE_W + (nodes.length - 1) * GAP
  const h = NODE_H + 40

  return (
    <div style={{ width: '100%', overflowX: 'auto', padding: '8px 0' }}>
      <svg width={w} height={h} viewBox={`0 0 ${w} ${h}`} style={{ display: 'block', margin: '0 auto' }}>
        <defs>
          <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto">
            <path d="M0,0 L10,5 L0,10 Z" fill="var(--accent)" />
          </marker>
          <style>{`
            @keyframes dash { to { stroke-dashoffset: -20; } }
            .flow-line { stroke: var(--accent); stroke-width: 1.5; stroke-dasharray: 5 5; animation: dash 1.2s linear infinite; opacity: 0.85; }
          `}</style>
        </defs>

        {nodes.map((_, i) => {
          if (i === nodes.length - 1) return null
          const x1 = (i + 1) * NODE_W + i * GAP
          const x2 = x1 + GAP
          const y = h / 2
          return (
            <line key={i} className="flow-line" x1={x1} y1={y} x2={x2 - 2} y2={y} markerEnd="url(#arrow)" />
          )
        })}

        {nodes.map((node, i) => {
          const x = i * (NODE_W + GAP)
          const y = (h - NODE_H) / 2
          return (
            <g key={i} transform={`translate(${x}, ${y})`}>
              <rect width={NODE_W} height={NODE_H} rx={8} ry={8}
                fill={node.highlight ? 'color-mix(in srgb, #f6821f 12%, var(--bg-input))' : 'var(--bg-input)'}
                stroke={node.highlight ? '#f6821f' : 'var(--border)'} strokeWidth={1} />
              <foreignObject x={0} y={0} width={NODE_W} height={NODE_H}>
                <div style={{
                  height: '100%', display: 'flex', flexDirection: 'column',
                  alignItems: 'center', justifyContent: 'center', gap: '4px',
                  padding: '6px', boxSizing: 'border-box', textAlign: 'center',
                }}>
                  <NodeIcon kind={node.icon} highlight={!!node.highlight} />
                  <div style={{ fontSize: '11.5px', fontWeight: 500, color: 'var(--text)' }}>{node.label}</div>
                  {node.sub && (
                    <div style={{
                      fontSize: '9.5px', fontFamily: 'SF Mono, Menlo, monospace',
                      color: 'var(--text-muted)',
                      maxWidth: NODE_W - 16, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }} title={node.sub}>
                      {node.sub}
                    </div>
                  )}
                </div>
              </foreignObject>
            </g>
          )
        })}
      </svg>
      <div style={{ textAlign: 'center', fontSize: '10px', color: 'var(--text-muted)', marginTop: '4px' }}>
        {tunnelName ? `Tunnel: ${tunnelName}` : 'No tunnel — direct local capture'}
      </div>
    </div>
  )
}

function NodeIcon({ kind, highlight }: { kind: Node['icon']; highlight: boolean }) {
  const color = highlight ? '#f6821f' : 'var(--accent)'
  const size = 18
  if (kind === 'cf') return <img src="/cloudflare.svg" alt="" height={14} />
  if (kind === 'ts') return <img src="/tailscale-mark.png" alt="" height={18} />
  if (kind === 'tailscaled') return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={1.7}>
      <circle cx="6" cy="6" r="1.6" /><circle cx="12" cy="6" r="1.6" /><circle cx="18" cy="6" r="1.6" />
      <circle cx="6" cy="12" r="1.6" /><circle cx="12" cy="12" r="1.6" /><circle cx="18" cy="12" r="1.6" />
      <circle cx="6" cy="18" r="1.6" /><circle cx="12" cy="18" r="1.6" /><circle cx="18" cy="18" r="1.6" />
    </svg>
  )
  if (kind === 'user') return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={1.7}>
      <circle cx="12" cy="8" r="3.6" /><path d="M4 21c1.5-4 4.5-6 8-6s6.5 2 8 6" strokeLinecap="round" />
    </svg>
  )
  if (kind === 'cloudflared') return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={1.7}>
      <rect x="3" y="5" width="18" height="14" rx="2" /><path d="M7 9h10M7 13h7M7 17h5" strokeLinecap="round" />
    </svg>
  )
  if (kind === 'inspector') return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={1.7}>
      <circle cx="11" cy="11" r="6" /><path d="M16 16l5 5" strokeLinecap="round" />
    </svg>
  )
  // upstream
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={1.7}>
      <rect x="3" y="4" width="18" height="6" rx="1.5" /><rect x="3" y="14" width="18" height="6" rx="1.5" />
      <circle cx="7" cy="7" r="0.9" fill={color} /><circle cx="7" cy="17" r="0.9" fill={color} />
    </svg>
  )
}

function stripScheme(url: string): string {
  return url.replace(/^https?:\/\//, '')
}
