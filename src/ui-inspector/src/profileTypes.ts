export type TunnelMode = 'None' | 'Quick' | 'Token' | 'ApiManaged' | 'Dynamic' | 'Tailscale'

export const TUNNEL_MODES: TunnelMode[] = ['None', 'Quick', 'Token', 'ApiManaged', 'Dynamic', 'Tailscale']

/** Subset for the Tailscale daemon-mode field. CLI runtime supports `system` only;
 *  `ephemeral` is AppHost-only (the AppHost lifecycle hook spawns a userspace tailscaled). */
export type TailscaleDaemonMode = 'system' | 'ephemeral'

export interface TunnelProfile {
  name: string
  upstream?: string | null
  proxyPort?: number | null
  uiPort?: number | null
  tunnelMode: TunnelMode
  token?: string | null
  apiToken?: string | null
  accountId?: string | null
  apiManagedTunnelName?: string | null
  dynamicZone?: string | null
  hostname?: string | null
  docker?: boolean
  autoInstall?: boolean
  // Tailscale Funnel fields (used when tunnelMode === 'Tailscale').
  tailscaleDaemonMode?: TailscaleDaemonMode | null
  tailscaleAuthKey?: string | null
  tailscaleLoginServer?: string | null
  tailscaleFunnelPort?: number | null  // 443 (default) | 8443 | 10000
  /** True = `tailscale funnel` (public). False / unset = `tailscale serve` (tailnet-only, default). */
  tailscalePublic?: boolean | null
  authHeader?: string | null
  authCidrs?: string[] | null
  authCountries?: string[] | null
  oidcAuthority?: string | null
  oidcClientId?: string | null
  oidcClientSecret?: string | null
}

export const emptyProfile = (name = ''): TunnelProfile => ({
  name,
  upstream: '',
  tunnelMode: 'None',
})
