export type TunnelMode = 'None' | 'Quick' | 'Token' | 'ApiManaged' | 'Dynamic'

export const TUNNEL_MODES: TunnelMode[] = ['None', 'Quick', 'Token', 'ApiManaged', 'Dynamic']

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
